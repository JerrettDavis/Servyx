using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Domain.Connectors;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;
using Servyx.Infrastructure.Process;
using Servyx.Infrastructure.Ssh;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Infrastructure.Ssh.Tests;

/// <summary>
/// The architecture test <c>docs/architecture.md</c> and <c>docs/roadmap.md</c> M1 both promise: no
/// transport may be registered in dependency injection without the write guard in front of the execution
/// targets it hands out.
/// </summary>
/// <remarks>
/// <para>
/// It lives here for the same reason <c>CanonicalTagVocabularyTests</c> does: this is a claim about every
/// transport at once, no source project may reference another, and this is the only assembly that sees all
/// three.
/// </para>
/// <para>
/// <b>There are no exemptions left.</b> <see cref="KnownUnguardedRegistrations"/> is asserted to be
/// <em>empty</em>, so the guard assertion applies to every registration without exception. A transport added
/// tomorrow and registered unguarded fails
/// <see cref="Every_transport_implementation_in_the_solution_is_named_here"/> or
/// <see cref="Every_transport_registration_hands_out_write_guarded_sessions"/>, and the only way to make
/// either pass is to guard it — adding an entry here now fails
/// <see cref="The_exemption_list_is_empty_and_no_registration_may_rejoin_it"/> as well, so reintroducing an
/// exemption takes a deliberate, reviewable edit to that assertion rather than one quiet line.
/// </para>
/// </remarks>
public class TransportWriteGuardArchitectureTests
{
    /// <summary>Every DI extension in the solution that registers an <see cref="ITransport"/>.</summary>
    private static readonly (string Method, Action<IServiceCollection> Register)[] TransportRegistrations =
    [
        ("AddServyxDocker", services => services.AddServyxDocker()),
        ("AddServyxSsh", services => services.AddServyxSsh()),
        ("AddServyxLocalProcess", services => services.AddServyxLocalProcess()),
        ("AddServyxSshDocker", services => services.AddServyxSshDocker(SshDockerTestOptions(), NullLogger.Instance)),
    ];

    /// <summary>
    /// A single fully-specified ssh+docker host, enough for <see cref="SshDockerWiringOptions.Any"/> to be
    /// true and a transport to actually be registered — the same shape <c>AddServyxSshDocker</c> is a no-op
    /// without.
    /// </summary>
    private static SshDockerWiringOptions SshDockerTestOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Servyx:Hosts:testhost:Enabled"] = "true",
                ["Servyx:Hosts:testhost:Endpoint"] = "ssh:user@10.0.0.9:22",
                ["Servyx:Hosts:testhost:Container"] = "palworld-server",
            })
            .Build();

        return SshDockerWiringOptions.FromConfiguration(configuration, NullLogger.Instance);
    }

    /// <summary>
    /// Registrations that still hand out unguarded sessions. <b>Empty, and asserted to be.</b>
    /// </summary>
    /// <remarks>
    /// It last held <c>AddServyxLocalProcess</c>, which registered a bare <c>LocalProcessTransport</c>. That
    /// registration now builds a <c>WriteGuardedTransport</c> over it, the same way <c>AddServyxDocker</c> and
    /// <c>AddServyxSsh</c> do, so all three transports are covered by the assertion below with nothing carved
    /// out of it.
    /// </remarks>
    private static readonly string[] KnownUnguardedRegistrations = [];

    private static IServiceCollection Composed(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The cross-cutting services AddServyxSsh deliberately does not register itself.
        services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddSingleton(Substitute.For<IHostKeyVerifier>());

        register(services);
        return services;
    }

    /// <summary>
    /// The types an assembly can actually surface here. This test assembly binds a newer Docker.DotNet than
    /// the one <c>Servyx.Infrastructure.Docker</c> compiled against, so a handful of that project's types
    /// fail to load and <c>Assembly.GetTypes()</c> throws rather than returning the rest. None of the
    /// unloadable ones is an <see cref="ITransport"/>, and skipping them is what lets this test scan all
    /// three assemblies at once — which is the only reason it can exist.
    /// </summary>
    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static IReadOnlyList<ITransport> ResolveTransports(Action<IServiceCollection> register)
    {
        var provider = Composed(register).BuildServiceProvider();
        return provider.GetServices<ITransport>().ToList();
    }

    [Fact]
    public void Every_transport_implementation_in_the_solution_is_named_here()
    {
        // If this fails, a fourth transport exists. Add its registration to TransportRegistrations — at which
        // point the guard assertion below applies to it too, which is the entire mechanism.
        var implementations = new[]
            {
                typeof(DockerTransport).Assembly,
                typeof(SshTransport).Assembly,
                typeof(LocalProcessTransport).Assembly,
            }
            .Distinct()
            .SelectMany(LoadableTypes)
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(ITransport).IsAssignableFrom(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        implementations.Should().Equal("DockerTransport", "LocalProcessTransport", "SshDockerTransport", "SshTransport");
        TransportRegistrations.Should().HaveCount(implementations.Count);
    }

    [Theory]
    [InlineData("AddServyxDocker")]
    [InlineData("AddServyxSsh")]
    [InlineData("AddServyxLocalProcess")]
    [InlineData("AddServyxSshDocker")]
    public void Every_transport_registration_hands_out_write_guarded_sessions(string method)
    {
        var registration = TransportRegistrations.Single(r => r.Method == method);
        var transports = ResolveTransports(registration.Register);

        transports.Should().ContainSingle($"{method} must register exactly one ITransport");

        KnownUnguardedRegistrations.Should().NotContain(
            method,
            "there are no exemptions left; a registration may not rejoin the list without changing " +
            $"{nameof(The_exemption_list_is_empty_and_no_registration_may_rejoin_it)} too");

        transports[0].Should().BeOfType<WriteGuardedTransport>(
            $"{method} must not put an unguarded transport in the container — every execution target it " +
            "hands out has to come out of WriteGuardedExecutionTarget");
    }

    [Fact]
    public void The_exemption_list_is_empty_and_no_registration_may_rejoin_it()
    {
        // A list that quietly grows is not an exemption list, it is a loophole. It is empty now, and this is
        // the assertion that keeps it empty: a fourth transport registered unguarded cannot be waved through
        // by adding one line here, because that line fails this test as well as the guard theory above.
        KnownUnguardedRegistrations.Should().BeEmpty();
    }

    [Fact]
    public void A_guarded_registration_does_not_publish_the_bare_transport_under_any_service_type()
    {
        // Wrapping is worth nothing if the inner transport is also resolvable: a caller who wanted an
        // unguarded session would simply ask for that instead.
        foreach (var (method, register) in TransportRegistrations.Where(r => !KnownUnguardedRegistrations.Contains(r.Method)))
        {
            var services = Composed(register);
            var bare = services
                .Where(d => d.ServiceType == typeof(DockerTransport)
                    || d.ServiceType == typeof(SshTransport)
                    || d.ServiceType == typeof(LocalProcessTransport)
                    || d.ServiceType == typeof(SshDockerTransport))
                .ToList();

            bare.Should().BeEmpty($"{method} must expose the concrete transport under no service type at all");
        }
    }

    [Fact]
    public void A_guarded_registration_defaults_every_target_to_read_only()
    {
        // No grants registered — the state of every host that has not deliberately enabled writes for a
        // specific server, including every M1 host.
        var provider = Composed(services => services.AddServyxDocker()).BuildServiceProvider();
        var resolver = provider.GetRequiredService<IWriteModeResolver>();

        var target = new TargetDescriptor(
            "docker",
            "npipe://./pipe/docker_engine",
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = "palworld-server" });

        resolver.Resolve(target).Should().Be(WriteMode.ReadOnly);
    }

    /// <summary>Every concrete, non-abstract <see cref="IExecutionTarget"/> implementation across the three transport assemblies scanned above.</summary>
    private static IReadOnlyList<Type> ExecutionTargetImplementations() =>
        new[]
            {
                typeof(DockerTransport).Assembly,
                typeof(SshTransport).Assembly,
                typeof(LocalProcessTransport).Assembly,
            }
            .Distinct()
            .SelectMany(LoadableTypes)
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IExecutionTarget).IsAssignableFrom(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Interfaces an <see cref="IExecutionTarget"/> implementation may carry without
    /// <see cref="WriteGuardedExecutionTarget"/> needing to carry them too, because they add no method a
    /// caller could invoke to reach a target's I/O — only structure the guard-seam already covers on
    /// whichever <see cref="IExecutionTarget"/> is actually called through.
    /// </summary>
    /// <remarks>
    /// <see cref="ICompositeExecutionTarget"/> is the one example today: <c>CompositeExecutionTarget</c>
    /// implements it purely so <see cref="ExecutionTargetWriteMode.Resolve"/> can look through to whichever
    /// half would perform a mutation — that method already special-cases <see cref="ICompositeExecutionTarget"/>
    /// in its own switch expression, alongside <see cref="WriteGuardedExecutionTarget"/> itself. The interface
    /// declares only two <see cref="IExecutionTarget"/>-typed properties and no method of its own, so there is
    /// nothing reachable through it that <see cref="IExecutionTarget"/> does not already gate on whichever
    /// half a caller actually calls. Unlike <see cref="IContainerLifecycle"/>, it is not a channel.
    /// </remarks>
    private static readonly Type[] NonCapabilityInterfaces = [typeof(ICompositeExecutionTarget)];

    [Fact]
    public void Every_IExecutionTarget_implementation_in_the_solution_is_named_here()
    {
        // If this fails, a new channel exists that the completeness test below has not yet scanned by name.
        // Add it here, at which point the completeness assertion applies to whatever interfaces it carries.
        var implementations = ExecutionTargetImplementations().Select(t => t.Name).ToList();

        implementations.Should().Equal(
            "CompositeExecutionTarget",
            "DockerExecutionTarget",
            "LazyConnectingExecutionTarget",
            "LocalExecutionTarget",
            "SftpFileChannel",
            "ShellFileChannel",
            "SshDockerLifecycleSession",
            "SshExecChannel");
    }

    [Fact]
    public void Every_capability_interface_an_IExecutionTarget_implementation_carries_is_also_on_the_write_guard()
    {
        // "Add a new channel to an inner target and forget to decorate WriteGuardedExecutionTarget" is
        // exactly the hole ContainerLifecycle closed for Docker's start/stop/restart/kill: those calls have
        // no CommandSpec for ExecuteAsync's gate to inspect, so an ungated cast straight to
        // IContainerLifecycle would have reached the inner target with no refusal at all. This test is what
        // keeps that hole from reopening for whatever the next channel turns out to be: any interface a real
        // IExecutionTarget implementation carries must also appear on WriteGuardedExecutionTarget, or this
        // fails the build instead of shipping a silent bypass.
        var implementations = ExecutionTargetImplementations();
        implementations.Should().NotBeEmpty("the scan above must find real channels for this assertion to mean anything");

        var capabilityInterfaces = implementations
            .SelectMany(type => type.GetInterfaces())
            .Distinct()
            .Except(NonCapabilityInterfaces)
            .ToList();

        // Pinned by name so a future channel that drops IExecutionTarget itself (impossible today, but not
        // impossible to typo past) still fails loudly rather than shrinking this set to nothing.
        // IContainerLifecycle joined this set when SshDockerLifecycleSession added a lifecycle channel to
        // the ssh+docker session — WriteGuardedExecutionTarget already carries it (see its own remarks), so
        // this assertion is what proves that coverage rather than assuming it.
        capabilityInterfaces.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal)
            .Should().Equal("IAsyncDisposable", "IContainerLifecycle", "IExecutionTarget");

        var guardInterfaces = typeof(WriteGuardedExecutionTarget).GetInterfaces();

        foreach (var capability in capabilityInterfaces)
        {
            guardInterfaces.Should().Contain(
                capability,
                $"WriteGuardedExecutionTarget must implement {capability.Name} too, or a caller that reaches " +
                "an inner target through it bypasses the write guard entirely");
        }
    }

    [Fact]
    public void A_grant_registered_by_a_composition_root_is_what_makes_a_single_server_writable()
    {
        var provider = Composed(services =>
        {
            services.AddSingleton(new WriteModeGrant(
                WriteMode.Enabled,
                "docker",
                requiredOptions: new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = "palworld-server" }));
            services.AddServyxDocker();
        }).BuildServiceProvider();

        var resolver = provider.GetRequiredService<IWriteModeResolver>();

        TargetDescriptor Container(string name) => new(
            "docker",
            "npipe://./pipe/docker_engine",
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = name });

        resolver.Resolve(Container("palworld-server")).Should().Be(WriteMode.Enabled);
        resolver.Resolve(Container("someone-elses-container")).Should().Be(WriteMode.ReadOnly);
    }
}
