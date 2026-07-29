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
    ];

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
                    || d.ServiceType == typeof(LocalProcessTransport))
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
