using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Domain.Common;
using Servyx.Domain.Definitions;
using Servyx.Domain.Discovery;
using Servyx.Domain.Entities;
using Servyx.Domain.Hosts;
using Servyx.Domain.Servers;

namespace Servyx.Application.Tests.Servers;

/// <summary>
/// Tests for <see cref="ServerAdoptionService"/> — the whole of Phase 1's "adopt an existing container,
/// view it, forget it" surface. Persistence is faked with a tiny in-memory <see cref="IServerRepository"/>
/// (state-carrying add/list/remove is awkward to express as a sequence of NSubstitute stubs); discovery and
/// the definition binding store, both single-call collaborators, are NSubstitute mocks.
/// </summary>
public class ServerAdoptionServiceTests
{
    private static readonly GameDefinitionRef PalworldRef =
        new("palworld", "sha256:palworld-v1", "filesystem", "definitions/palworld-docker.yaml");

    private static readonly AdoptionCriteria PalworldCriteria = new(
        GameId: "palworld",
        GameName: "Palworld Dedicated Server",
        ImageRepository: "thijsvanloef/palworld-server-docker",
        RequiredMountContainerPath: "/palworld");

    private static readonly DefinitionAdoptionCriteria PalworldAdoptionCriteria = new(PalworldCriteria, PalworldRef);

    /// <summary>A tiny in-memory <see cref="IServerRepository"/> — real add/list/remove semantics without a database.</summary>
    private sealed class InMemoryServerRepository : IServerRepository
    {
        private readonly List<Server> _servers = [];

        public Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Server>>(_servers.ToList());

        public Task<Server?> TryGetAsync(ServerId id, CancellationToken ct = default) =>
            Task.FromResult(_servers.FirstOrDefault(s => s.Id == id));

        public Task AddAsync(Server server, CancellationToken ct = default)
        {
            _servers.Add(server);
            return Task.CompletedTask;
        }

        public Task<Server?> SetWriteModeAsync(
            ServerId id,
            ServerWriteMode mode,
            string changedBy,
            DateTimeOffset changedAt,
            CancellationToken ct = default)
        {
            var existing = _servers.FirstOrDefault(s => s.Id == id);
            if (existing is null)
            {
                return Task.FromResult<Server?>(null);
            }

            existing.WriteMode = mode;
            existing.WriteModeChangedBy = changedBy;
            existing.WriteModeChangedAt = changedAt;
            return Task.FromResult<Server?>(existing);
        }

        public Task<bool> RemoveAsync(ServerId id, CancellationToken ct = default)
        {
            var existing = _servers.FirstOrDefault(s => s.Id == id);
            if (existing is null)
            {
                return Task.FromResult(false);
            }

            _servers.Remove(existing);
            return Task.FromResult(true);
        }
    }

    private static DiscoveredServer BuildDiscovered(string id = "container-1", string name = "palworld-server", string? hostKey = null) => new(
        ServerId: id,
        Name: name,
        Image: "thijsvanloef/palworld-server-docker:latest",
        ImageDigest: "sha256:abc",
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
        HostKey: hostKey);

    private static Host BuildHost(string name, HostId? id = null) => new()
    {
        Id = id ?? HostId.New(),
        Name = name,
        ConnectorId = "ssh-docker",
        Endpoint = $"ssh:user@{name}:22",
        TrustPolicy = "trustOnFirstUse",
        Enabled = true,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private sealed record Fixture(
        ServerAdoptionService Service,
        IServerDiscovery Discovery,
        InMemoryServerRepository Repository,
        IServerDefinitionBindingStore Bindings,
        IHostRepository Hosts,
        IAdoptionDefinitionCatalog Catalog);

    private static Fixture CreateFixture(IReadOnlyList<DefinitionAdoptionCriteria>? criteriaSet = null, TimeProvider? timeProvider = null)
    {
        var discovery = Substitute.For<IServerDiscovery>();
        var repository = new InMemoryServerRepository();
        var bindings = Substitute.For<IServerDefinitionBindingStore>();
        var hosts = Substitute.For<IHostRepository>();
        hosts.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Host>>([]));
        hosts.TryGetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<Host?>(null));
        var catalog = Substitute.For<IAdoptionDefinitionCatalog>();

        var criteria = criteriaSet ?? [PalworldAdoptionCriteria];
        catalog.AllCriteria().Returns(criteria);
        catalog.TryGetRefById(PalworldRef.Id).Returns(PalworldRef);

        var service = new ServerAdoptionService(
            discovery, repository, bindings, hosts, catalog, NullLogger<ServerAdoptionService>.Instance, timeProvider);

        return new Fixture(service, discovery, repository, bindings, hosts, catalog);
    }

    private static void StubDiscovery(IServerDiscovery discovery, params DiscoveredServer[] servers) =>
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>(servers));

    // ── AdoptAsync ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdoptAsync_persists_a_server_row_with_pinned_content_hash_and_read_only_write_mode()
    {
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered());

        var result = await fixture.Service.AdoptAsync("container-1", "palworld");

        result.Outcome.Should().Be(AdoptionOutcome.Adopted);
        result.ServerId.Should().NotBeNull();

        var rows = await fixture.Repository.ListAsync();
        rows.Should().HaveCount(1);
        var row = rows[0];
        row.Id.Should().Be(result.ServerId!.Value);
        row.Name.Should().Be("palworld-server");
        row.GameDefinitionId.Should().Be("palworld");
        row.DefinitionContentHash.Should().Be("sha256:palworld-v1");
        row.AdoptionMode.Should().Be(AdoptionMode.Adopted);
        row.WriteMode.Should().Be(ServerWriteMode.ReadOnly,
            because: "adoption never grants write access on its own — that is a separate, deliberate act");
    }

    [Fact]
    public async Task AdoptAsync_records_the_definition_binding()
    {
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered());

        await fixture.Service.AdoptAsync("container-1", "palworld");

        await fixture.Bindings.Received(1).SaveAsync(
            Arg.Is<ServerDefinitionBinding>(b =>
                b != null &&
                b.ServerId == "container-1" &&
                b.State == ServerDefinitionBindingState.Bound &&
                b.Definition == PalworldRef),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdoptAsync_adopting_an_already_adopted_container_is_a_no_op_result_not_a_duplicate_row()
    {
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered());

        var first = await fixture.Service.AdoptAsync("container-1", "palworld");
        var second = await fixture.Service.AdoptAsync("container-1", "palworld");

        second.Outcome.Should().Be(AdoptionOutcome.AlreadyAdopted);
        second.ServerId.Should().Be(first.ServerId);
        (await fixture.Repository.ListAsync()).Should().HaveCount(1,
            because: "re-adopting must never create a second row");
    }

    [Fact]
    public async Task AdoptAsync_an_unknown_definition_id_returns_a_result_not_an_exception()
    {
        var fixture = CreateFixture();

        var result = await fixture.Service.AdoptAsync("container-1", "no-such-game");

        result.Outcome.Should().Be(AdoptionOutcome.UnknownDefinition);
        (await fixture.Repository.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AdoptAsync_a_definition_with_no_derivable_adoption_criteria_returns_unknown_definition()
    {
        var otherRef = new GameDefinitionRef("no-docker-profile", "sha256:x", "filesystem");
        var fixture = CreateFixture(criteriaSet: []);
        fixture.Catalog.TryGetRefById("no-docker-profile").Returns(otherRef);

        var result = await fixture.Service.AdoptAsync("container-1", "no-docker-profile");

        result.Outcome.Should().Be(AdoptionOutcome.UnknownDefinition);
    }

    [Fact]
    public async Task AdoptAsync_a_container_that_vanished_before_adoption_returns_a_result_not_an_exception()
    {
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery /* no servers */);

        var result = await fixture.Service.AdoptAsync("container-1", "palworld");

        result.Outcome.Should().Be(AdoptionOutcome.ContainerNotFound);
        (await fixture.Repository.ListAsync()).Should().BeEmpty();
    }

    // ── ForgetAsync ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ForgetAsync_removes_the_server_row()
    {
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered());
        var adopted = await fixture.Service.AdoptAsync("container-1", "palworld");

        var result = await fixture.Service.ForgetAsync(adopted.ServerId!.Value);

        result.Outcome.Should().Be(ForgetOutcome.Forgotten);
        (await fixture.Repository.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ForgetAsync_issues_no_container_command()
    {
        // ServerAdoptionService holds no ITransport/execution-target dependency of any kind — only
        // IServerDiscovery (read-only workload inventory), IServerRepository, and
        // IServerDefinitionBindingStore. That is itself structural proof Forget cannot issue a container
        // command: there is no collaborator through which one could be sent. The assertion below is the
        // executable half of that proof — discovery, the only Docker-facing dependency this service holds
        // at all, is never touched by ForgetAsync.
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered());
        var adopted = await fixture.Service.AdoptAsync("container-1", "palworld");
        fixture.Discovery.ClearReceivedCalls();

        await fixture.Service.ForgetAsync(adopted.ServerId!.Value);

        await fixture.Discovery.DidNotReceive().DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForgetAsync_forgetting_an_unknown_id_returns_not_found()
    {
        var fixture = CreateFixture();

        var result = await fixture.Service.ForgetAsync(ServerId.New());

        result.Outcome.Should().Be(ForgetOutcome.NotFound);
    }

    // ── ListCandidatesAsync / ListTrackedAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListCandidatesAsync_excludes_already_adopted_containers()
    {
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered(), BuildDiscovered("container-2", "other-server"));
        await fixture.Service.AdoptAsync("container-1", "palworld");

        var result = await fixture.Service.ListCandidatesAsync();

        result.DiscoveryFailed.Should().BeFalse();
        result.Candidates.Should().ContainSingle(c => c.ContainerId == "container-2");
    }

    [Fact]
    public async Task ListCandidatesAsync_returns_empty_when_no_definition_has_adoption_criteria()
    {
        var fixture = CreateFixture(criteriaSet: []);

        var result = await fixture.Service.ListCandidatesAsync();

        result.DiscoveryFailed.Should().BeFalse();
        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task ListCandidatesAsync_reports_discovery_failure_honestly_rather_than_an_empty_list()
    {
        // Defect 1 (candidates side) regression: a discovery failure here must never be indistinguishable
        // from "genuinely no containers to adopt" — an operator seeing "No containers available to adopt"
        // while Docker is actually unreachable is a false, and actively misleading, signal.
        var fixture = CreateFixture();
        fixture.Discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<DiscoveredServer>>>(_ => throw new InvalidOperationException("daemon unreachable"));

        var result = await fixture.Service.ListCandidatesAsync();

        result.DiscoveryFailed.Should().BeTrue();
        result.Candidates.Should().BeEmpty();
        result.FailureDetail.Should().Contain("daemon unreachable");
    }

    [Fact]
    public async Task ListTrackedAsync_reflects_adopted_servers()
    {
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered());
        await fixture.Service.AdoptAsync("container-1", "palworld");

        var result = await fixture.Service.ListTrackedAsync();

        result.TrackingFailed.Should().BeFalse();
        result.Servers.Should().ContainSingle(t => t.Name == "palworld-server" && t.GameDefinitionId == "palworld");
    }

    [Fact]
    public async Task ListTrackedAsync_reports_failure_honestly_rather_than_an_empty_list_when_the_database_is_broken()
    {
        // Defect 1 regression: a persistence failure here must never be indistinguishable from "genuinely
        // nothing tracked" — an operator seeing "Nothing tracked yet" while the database is actually broken
        // is a false, and actively misleading, signal.
        var discovery = Substitute.For<IServerDiscovery>();
        var repository = Substitute.For<IServerRepository>();
        repository.ListAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<Server>>>(
            _ => throw new InvalidOperationException("database is unwritable"));
        var bindings = Substitute.For<IServerDefinitionBindingStore>();
        var hosts = Substitute.For<IHostRepository>();
        hosts.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Host>>([]));
        var catalog = Substitute.For<IAdoptionDefinitionCatalog>();

        var service = new ServerAdoptionService(
            discovery, repository, bindings, hosts, catalog, NullLogger<ServerAdoptionService>.Instance);

        var result = await service.ListTrackedAsync();

        result.TrackingFailed.Should().BeTrue();
        result.Servers.Should().BeEmpty();
        result.FailureDetail.Should().Contain("database is unwritable");
    }

    // ── Defect regressions: container-id correlation, HostId, and orphaned bindings ────────────────────

    [Fact]
    public async Task AdoptAsync_correlates_already_adopted_by_container_id_not_by_name()
    {
        // Two different hosts can each run a container named "palworld-server" — a Name-based correlation
        // would falsely treat the second as already adopted. Container id is unique per workload regardless
        // of how many hosts share the same container name.
        var fixture = CreateFixture();
        fixture.Discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>(
                [BuildDiscovered("container-host-a", "palworld-server"), BuildDiscovered("container-host-b", "palworld-server")]));

        var first = await fixture.Service.AdoptAsync("container-host-a", "palworld");
        var second = await fixture.Service.AdoptAsync("container-host-b", "palworld");

        first.Outcome.Should().Be(AdoptionOutcome.Adopted);
        second.Outcome.Should().Be(AdoptionOutcome.Adopted,
            because: "a same-named container on a different host must not be refused as AlreadyAdopted");
        second.ServerId.Should().NotBe(first.ServerId);
        (await fixture.Repository.ListAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task AdoptAsync_never_fabricates_a_HostId()
    {
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered());

        var result = await fixture.Service.AdoptAsync("container-1", "palworld");

        var row = (await fixture.Repository.ListAsync()).Single();
        row.Id.Should().Be(result.ServerId!.Value);
        row.HostId.Should().BeNull(because: "no Host row exists for this server yet, and a random unlinked id would be a fabrication");
    }

    [Fact]
    public async Task AdoptAsync_persists_the_container_id_used_to_correlate_it()
    {
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered());

        await fixture.Service.AdoptAsync("container-1", "palworld");

        var row = (await fixture.Repository.ListAsync()).Single();
        row.ContainerId.Should().Be("container-1");
    }

    // ── Increment 7: HostId/HostName resolution from a discovered container's HostKey ─────────────────

    [Fact]
    public async Task AdoptAsync_sets_HostId_when_the_HostKey_resolves_to_a_registered_host_row()
    {
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered(hostKey: "db-host"));
        var registeredHost = BuildHost("db-host");
        fixture.Hosts.TryGetByNameAsync("db-host", Arg.Any<CancellationToken>()).Returns(Task.FromResult<Host?>(registeredHost));

        var result = await fixture.Service.AdoptAsync("container-1", "palworld");

        var row = (await fixture.Repository.ListAsync()).Single();
        row.Id.Should().Be(result.ServerId!.Value);
        row.HostId.Should().Be(registeredHost.Id);
    }

    [Fact]
    public async Task AdoptAsync_leaves_HostId_null_when_the_HostKey_names_a_configuration_declared_host_with_no_row()
    {
        // A HostKey is not proof a Host row exists: CompositeServerDiscovery tags results with the same name
        // for a configuration-declared host (Servyx:Hosts) as it does for a database-registered one, but only
        // the latter has a row IHostRepository can resolve. TryGetByNameAsync (stubbed to null by default in
        // CreateFixture) simulates exactly that gap.
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered(hostKey: "configured-host"));

        var result = await fixture.Service.AdoptAsync("container-1", "palworld");

        result.Outcome.Should().Be(AdoptionOutcome.Adopted);
        var row = (await fixture.Repository.ListAsync()).Single();
        row.HostId.Should().BeNull(
            because: "a configuration-declared host is never itself persisted as a Host row, so there is nothing to point HostId at");
    }

    [Fact]
    public async Task AdoptAsync_leaves_HostId_null_and_does_not_throw_when_the_HostKey_is_null()
    {
        // A null HostKey means discovery has no host notion at all (the local/non-SSH source) — this must
        // not throw or attempt a lookup that could fail.
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered(hostKey: null));

        var result = await fixture.Service.AdoptAsync("container-1", "palworld");

        result.Outcome.Should().Be(AdoptionOutcome.Adopted);
        var row = (await fixture.Repository.ListAsync()).Single();
        row.HostId.Should().BeNull();
    }

    [Fact]
    public async Task ListCandidatesAsync_populates_HostName_from_the_registered_host_row()
    {
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered(hostKey: "db-host"));
        var registeredHost = BuildHost("db-host");
        fixture.Hosts.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Host>>([registeredHost]));

        var result = await fixture.Service.ListCandidatesAsync();

        result.Candidates.Should().ContainSingle().Which.HostName.Should().Be("db-host");
    }

    [Fact]
    public async Task ListCandidatesAsync_falls_back_to_the_raw_HostKey_when_no_host_row_matches_it()
    {
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered(hostKey: "configured-host"));
        // Hosts.ListAsync defaults to an empty list in CreateFixture — no row named "configured-host".

        var result = await fixture.Service.ListCandidatesAsync();

        result.Candidates.Should().ContainSingle().Which.HostName.Should().Be("configured-host");
    }

    [Fact]
    public async Task ListCandidatesAsync_leaves_HostName_null_when_the_HostKey_is_null()
    {
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered(hostKey: null));

        var result = await fixture.Service.ListCandidatesAsync();

        result.Candidates.Should().ContainSingle().Which.HostName.Should().BeNull();
    }

    [Fact]
    public async Task ListCandidatesAsync_degrades_HostName_to_the_raw_HostKey_when_reading_hosts_fails()
    {
        // Same "cosmetic, best-effort" standard as the already-adopted exclusion check: a broken host-table
        // read must not fail the whole candidate listing, just fall back to showing the raw HostKey.
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered(hostKey: "db-host"));
        fixture.Hosts.ListAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Host>>>(_ => throw new InvalidOperationException("database is unwritable"));

        var result = await fixture.Service.ListCandidatesAsync();

        result.DiscoveryFailed.Should().BeFalse();
        result.Candidates.Should().ContainSingle().Which.HostName.Should().Be("db-host");
    }

    [Fact]
    public async Task ForgetAsync_leaves_the_definition_binding_recorded_at_adoption_time_in_place()
    {
        // Deliberate, documented decision (see ForgetAsync's own remarks): the ServerDefinitionBindings row
        // is ServerQueryService's own state, independent of Servyx's adoption bookkeeping. The container
        // Forget stops tracking keeps running and keeps being discovered, so deleting its binding here would
        // force a still-Bound, still-running container back through Ambiguous/NeedsRebind purely because an
        // operator clicked Forget — a side effect on an unrelated subsystem this method must not cause.
        var fixture = CreateFixture();
        StubDiscovery(fixture.Discovery, BuildDiscovered());
        var adopted = await fixture.Service.AdoptAsync("container-1", "palworld");
        fixture.Bindings.ClearReceivedCalls();

        await fixture.Service.ForgetAsync(adopted.ServerId!.Value);

        await fixture.Bindings.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForgetAsync_forgetting_an_unknown_id_never_touches_the_binding_store()
    {
        var fixture = CreateFixture();

        await fixture.Service.ForgetAsync(ServerId.New());

        await fixture.Bindings.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
