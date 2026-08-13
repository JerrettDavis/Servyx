using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Domain.Common;
using Servyx.Domain.Definitions;
using Servyx.Domain.Discovery;
using Servyx.Domain.Entities;
using Servyx.Domain.Hosts;
using Servyx.Domain.Servers;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;
using Servyx.Infrastructure.Ssh.Tests.Provisioning;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// The exact scenario the "a registered host's containers never appear as adoption candidates" bug fix
/// targets, reaching all the way up to <see cref="ServerAdoptionService.ListCandidatesAsync"/>: an operator
/// registers a host purely through the UI/database — zero <c>Servyx:Hosts</c> configuration, ever — and that
/// host's containers must become adoption candidates without a process restart, while a plain
/// local-docker-only install (nothing ever registered) must keep seeing its local containers exactly as
/// before.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this composes the pieces directly instead of resolving through a real
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/> built by
/// <c>AddServyxSshDocker</c>.</strong> <c>HostConnectionRegistry</c>'s production <c>ITransport</c> for a
/// database-registered host is always a freshly-built real <c>SshTransport</c> (see
/// <c>SshDockerServiceCollectionExtensions.BuildSshDockerTransport</c>) — deliberately not substitutable
/// through DI, so there is no seam in the real composition root to fake the network layer without actually
/// opening a socket. This suite instead builds <see cref="HostConnectionRegistry"/>,
/// <see cref="CompositeServerDiscovery"/>, and <see cref="HostAwareServerDiscovery"/> exactly the way
/// <c>AddServyxSshDocker</c> wires them for the zero-<c>Servyx:Hosts</c> case, over a substituted
/// <see cref="ITransport"/> — the same level <see cref="CompositeServerDiscoveryTests.Zero_configured_hosts_plus_one_database_registered_host_is_still_discovered"/>
/// already tests at for the layer below this one. <see cref="Docker.SshDockerWiringTests"/> (in
/// <c>Servyx.Web.Tests</c>) is the sibling suite proving the DI registration itself resolves to the right
/// <em>type</em> — this suite proves what that type then actually DOES once wired.
/// </para>
/// </remarks>
public class HostAwareServerDiscoveryCompositionTests
{
    private const string ImageRepository = "thijsvanloef/palworld-server-docker";
    private const string RequiredMountPath = "/palworld";
    private const string PalworldContainerId = "1cae202fb5341a59ec34c72e63fbad9c33e054ce88f5b05e37bd756b729fa81e";

    private static readonly GameDefinitionRef PalworldRef =
        new("palworld", "sha256:palworld-v1", "filesystem", "definitions/palworld-docker.yaml");

    private static readonly DefinitionAdoptionCriteria PalworldCriteria = new(
        new AdoptionCriteria(
            GameId: "palworld",
            GameName: "Palworld Dedicated Server",
            ImageRepository: ImageRepository,
            RequiredMountContainerPath: RequiredMountPath),
        PalworldRef);

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    private static SshHostDouble CreatePalworldHost()
    {
        var lsOutput = ReadFixture("palworld-container-ls.jsonl");
        var inspectJson = ReadFixture("palworld-inspect.json");

        return new SshHostDouble
        {
            ExecHandler = command =>
            {
                if (command.Arguments.Contains("ls"))
                {
                    return new CommandResult(0, lsOutput, string.Empty, TimeSpan.FromMilliseconds(5));
                }

                if (command.Arguments.Contains("inspect"))
                {
                    return new CommandResult(0, inspectJson, string.Empty, TimeSpan.FromMilliseconds(5));
                }

                throw new InvalidOperationException($"Unexpected command: {command.Executable} {string.Join(' ', command.Arguments)}");
            },
        };
    }

    /// <summary>Mutable, in-memory <see cref="IHostRepository"/> — the same shape <c>HostConnectionRegistryTests</c> uses.</summary>
    private sealed class FakeHostRepository : IHostRepository
    {
        public List<Host> Rows { get; } = [];

        public Task<IReadOnlyList<Host>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Host>>(Rows);

        public Task<Host?> TryGetAsync(HostId id, CancellationToken ct = default) =>
            Task.FromResult(Rows.FirstOrDefault(h => h.Id == id));

        public Task<Host?> TryGetByNameAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(Rows.FirstOrDefault(h => h.Name == name));

        public Task AddAsync(Host host, CancellationToken ct = default)
        {
            Rows.Add(host);
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(HostId id, CancellationToken ct = default) =>
            throw new NotSupportedException("Not needed by this suite.");
    }

    private sealed class InMemoryServerRepository : IServerRepository
    {
        public Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Server>>([]);

        public Task<Server?> TryGetAsync(ServerId id, CancellationToken ct = default) =>
            Task.FromResult<Server?>(null);

        public Task AddAsync(Server server, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Server?> SetWriteModeAsync(
            ServerId id, ServerWriteMode mode, string changedBy, DateTimeOffset changedAt, CancellationToken ct = default) =>
            throw new NotSupportedException("Not needed by this suite.");

        public Task<bool> RemoveAsync(ServerId id, CancellationToken ct = default) =>
            throw new NotSupportedException("Not needed by this suite.");
    }

    /// <summary>
    /// Builds the exact object graph <c>AddServyxSshDocker</c> wires for the zero-<c>Servyx:Hosts</c> case:
    /// <see cref="HostConnectionRegistry"/> (over <paramref name="repository"/> and a substituted
    /// <see cref="ITransport"/> that always hands back <paramref name="dbHost"/>'s session),
    /// <see cref="CompositeServerDiscovery"/> over that registry, and <see cref="HostAwareServerDiscovery"/>
    /// over both plus <paramref name="localDiscovery"/> — then a real <see cref="ServerAdoptionService"/> on
    /// top, exactly as <c>ServyxCoreCompositionExtensions</c> composes it.
    /// </summary>
    private static (ServerAdoptionService Service, HostConnectionRegistry Registry, FakeHostRepository Hosts) BuildFixture(
        SshHostDouble dbHost, IServerDiscovery localDiscovery)
    {
        var repository = new FakeHostRepository();

        var transport = Substitute.For<ITransport>();
        transport.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(dbHost.Session));

        var registry = new HostConnectionRegistry(
            SshDockerWiringOptions.None, repository, transport, NullLogger<HostConnectionRegistry>.Instance);
        var composite = new CompositeServerDiscovery(registry);
        var discovery = new HostAwareServerDiscovery(registry, composite, localDiscovery);

        var bindings = Substitute.For<IServerDefinitionBindingStore>();
        var catalog = Substitute.For<IAdoptionDefinitionCatalog>();
        catalog.AllCriteria().Returns((IReadOnlyList<DefinitionAdoptionCriteria>)[PalworldCriteria]);

        var service = new ServerAdoptionService(
            discovery, new InMemoryServerRepository(), bindings, repository, catalog,
            NullLogger<ServerAdoptionService>.Instance);

        return (service, registry, repository);
    }

    private static IServerDiscovery LocalDiscoveryReturning(params DiscoveredServer[] servers)
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>(servers));
        return discovery;
    }

    private static DiscoveredServer LocalOnlyServer() => new(
        ServerId: "local-only-container",
        Name: "local-only-container",
        Image: $"{ImageRepository}:latest",
        ImageDigest: "sha256:local",
        State: "running",
        HealthStatus: "healthy",
        CreatedAt: DateTimeOffset.UnixEpoch,
        StartedAt: DateTimeOffset.UnixEpoch,
        Ports: [],
        Mounts: [],
        NetworkName: null,
        ContainerIp: null,
        MemoryLimitBytes: null,
        CpuLimit: null,
        RestartPolicy: null,
        ComposeLabels: new Dictionary<string, string>(),
        EnvironmentVariables: new Dictionary<string, string>(),
        HostKey: null);

    [Fact]
    public async Task With_nothing_registered_yet_a_plain_local_docker_install_still_lists_local_candidates()
    {
        var (service, _, _) = BuildFixture(CreatePalworldHost(), LocalDiscoveryReturning(LocalOnlyServer()));

        var result = await service.ListCandidatesAsync();

        result.DiscoveryFailed.Should().BeFalse();
        result.Candidates.Should().ContainSingle(c => c.ContainerId == "local-only-container",
            because: "a fresh, never-registered-anything install must keep discovering local containers, " +
                "not silently go blind the moment ssh+docker wiring exists in the process");
    }

    [Fact]
    public async Task Registering_a_host_purely_through_the_database_makes_its_container_an_adoption_candidate()
    {
        var dbHost = CreatePalworldHost();
        var (service, registry, hosts) = BuildFixture(dbHost, LocalDiscoveryReturning(LocalOnlyServer()));

        // Before registration: only the local container is a candidate — see the sibling test above. This
        // call also warms HostConnectionRegistry's cache with the pre-registration (empty) database state.
        (await service.ListCandidatesAsync()).Candidates.Should().ContainSingle(c => c.ContainerId == "local-only-container");

        // The exact side effect HostRegistrationService.RegisterAsync performs after persisting a Host row —
        // see IHostConnectionRefresher's remarks — with zero Servyx:Hosts configuration ever declared.
        hosts.Rows.Add(new Host
        {
            Id = HostId.New(),
            Name = "ui-registered-host",
            ConnectorId = "ssh:ui-registered-host",
            Endpoint = "ssh:user@10.0.0.50:22",
            TrustPolicy = "requirePinned",
            Enabled = true,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        registry.Invalidate();

        var result = await service.ListCandidatesAsync();

        result.DiscoveryFailed.Should().BeFalse();
        result.Candidates.Should().HaveCount(2,
            because: "registering a host through the database must ADD its container as a candidate, not " +
                "silently drop the local-only container from the same fixture — that was the regression the " +
                "reviewer flagged: HostAwareServerDiscovery's old either/or dropped local discovery entirely " +
                "the moment any host existed");
        result.Candidates.Should().Contain(c => c.ContainerId == "local-only-container",
            because: "an already-adopted local server must remain visible after a remote host is registered");
        var candidate = result.Candidates.Should().ContainSingle(c => c.ContainerId == PalworldContainerId).Subject;
        candidate.HostName.Should().Be("ui-registered-host",
            because: "ServerAdoptionService resolves the discovered container's HostKey to the registered " +
                "Host row's own display name");
    }

    [Fact]
    public async Task A_disabled_database_host_never_surfaces_a_candidate()
    {
        var dbHost = CreatePalworldHost();
        var (service, registry, hosts) = BuildFixture(dbHost, LocalDiscoveryReturning());

        hosts.Rows.Add(new Host
        {
            Id = HostId.New(),
            Name = "disabled-host",
            ConnectorId = "ssh:disabled-host",
            Endpoint = "ssh:user@10.0.0.51:22",
            TrustPolicy = "requirePinned",
            Enabled = false,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        registry.Invalidate();

        var result = await service.ListCandidatesAsync();

        result.DiscoveryFailed.Should().BeFalse();
        result.Candidates.Should().BeEmpty(
            "HostAwareServerDiscovery still routes to local discovery when the only database row is disabled, " +
            "and local discovery here reports nothing");
    }
}
