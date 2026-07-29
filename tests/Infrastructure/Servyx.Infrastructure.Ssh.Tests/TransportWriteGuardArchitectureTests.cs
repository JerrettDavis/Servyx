using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Servyx.Domain.Connectors;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;
using Servyx.Infrastructure.Process;
using Servyx.Infrastructure.Ssh;

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
/// <b>The exemption list is the point.</b> <see cref="KnownUnguardedRegistrations"/> is asserted to be
/// exactly one entry. A transport added tomorrow and registered unguarded fails
/// <see cref="Every_transport_implementation_in_the_solution_is_named_here"/> or
/// <see cref="Every_transport_registration_hands_out_write_guarded_sessions"/>, and the only way to make
/// either pass is to guard it or to add it here in writing, where a reviewer sees it.
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
    ];

    /// <summary>
    /// Registrations that still hand out unguarded sessions, each of which is a known gap rather than a
    /// design choice.
    /// </summary>
    /// <remarks>
    /// <c>AddServyxLocalProcess</c>: <c>LocalProcessTransportTests</c> and
    /// <c>ProvisionedLocalTargetHandoffTests</c> pin both this registration's concrete implementation type
    /// and <c>ConnectAsync</c>'s concrete return type (<c>LocalExecutionTarget</c>, cast and written through
    /// directly). Guarding it therefore cannot be done without rewriting those tests, which is M8's business
    /// — the milestone that brings bare process hosts in — not something M4 may do in passing while adding
    /// Docker writes.
    /// </remarks>
    private static readonly string[] KnownUnguardedRegistrations = ["AddServyxLocalProcess"];

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

        implementations.Should().Equal("DockerTransport", "LocalProcessTransport", "SshTransport");
        TransportRegistrations.Should().HaveCount(implementations.Count);
    }

    [Theory]
    [InlineData("AddServyxDocker")]
    [InlineData("AddServyxSsh")]
    [InlineData("AddServyxLocalProcess")]
    public void Every_transport_registration_hands_out_write_guarded_sessions(string method)
    {
        var registration = TransportRegistrations.Single(r => r.Method == method);
        var transports = ResolveTransports(registration.Register);

        transports.Should().ContainSingle($"{method} must register exactly one ITransport");

        if (KnownUnguardedRegistrations.Contains(method))
        {
            transports[0].Should().NotBeOfType<WriteGuardedTransport>(
                "this registration is a documented gap; if it has been guarded, remove it from " +
                "KnownUnguardedRegistrations rather than leaving a stale exemption behind");
            return;
        }

        transports[0].Should().BeOfType<WriteGuardedTransport>(
            $"{method} must not put an unguarded transport in the container — every execution target it " +
            "hands out has to come out of WriteGuardedExecutionTarget");
    }

    [Fact]
    public void The_exemption_list_holds_exactly_the_one_registration_documented_above()
    {
        // A list that quietly grows is not an exemption list, it is a loophole. Anyone adding to it has to
        // change this number and say why in the remarks.
        KnownUnguardedRegistrations.Should().Equal("AddServyxLocalProcess");
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
                .Where(d => d.ServiceType == typeof(DockerTransport) || d.ServiceType == typeof(SshTransport))
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
