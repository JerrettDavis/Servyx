using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Backups;
using Servyx.Application.Servers;
using Servyx.Composition;
using Servyx.Domain.Backups;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Mcp.Tests.Support;
using Servyx.Mcp.Tools;

namespace Servyx.Mcp.Tests.Honesty;

/// <summary>
/// "We could not find out" and "we found out and the answer is empty/none/nobody" must never collapse into
/// the same shape anywhere in this tool surface. Each test below picks one seam named in the brief and
/// proves the two are still distinguishable outcomes.
/// </summary>
public sealed class McpEmptyVersusUnknownTests
{
    private static ServerSummary MakeSummary(string id, string name) => new(
        id, name, "unknown", ServerState.Running, ServerHealthStatus.Unknown, null, null, "local", []);

    [Fact]
    public async Task Failed_server_discovery_is_not_reported_as_an_empty_server_list()
    {
        var query = Substitute.For<IServerQueryService>();
        query.GetAdoptedServersWithStatusAsync(Arg.Any<CancellationToken>())
            .Returns(ServerListResult.Failed("daemon unreachable"));

        var result = await ServerReadTools.ListAsync(query, WritableServers.None, CancellationToken.None);

        result.Outcome.Should().Be("discovery-failed");
        result.Servers.Should().BeEmpty();
        result.FailureDetail.Should().Be("daemon unreachable");

        var genuinelyEmpty = await ServerReadTools.ListAsync(
            SubstituteQueryReturning(ServerListResult.Ok([])), WritableServers.None, CancellationToken.None);
        genuinelyEmpty.Outcome.Should().Be("listed");
        genuinelyEmpty.Outcome.Should().NotBe(result.Outcome, "a failed discovery must never read the same as a genuinely empty fleet");
    }

    private static IServerQueryService SubstituteQueryReturning(ServerListResult result)
    {
        var query = Substitute.For<IServerQueryService>();
        query.GetAdoptedServersWithStatusAsync(Arg.Any<CancellationToken>()).Returns(result);
        return query;
    }

    [Fact]
    public async Task Unconfigured_backup_provider_is_not_reported_as_found_nothing()
    {
        var composition = ComposedHost.BuildWithNoDefinitions();
        // BackupProvider capability is unavailable whenever the provisioning gate is closed — the default
        // for a composition built with no Servyx:Provisioning:Enabled override.
        composition.Capabilities.Get(ServyxCapability.BackupProvider).Available.Should().BeFalse();

        var services = new FakeServiceProvider(new Dictionary<Type, object?>());

        var result = await ArchiveTools.ListAsync("srv-1", composition, services, CancellationToken.None);

        result.Outcome.Should().Be("unavailable");
        result.ServyxOwned.Should().BeNull("an unconfigured provider must never be reported as 'found zero backups'");
        result.Foreign.Should().BeNull();
    }

    [Fact]
    public async Task Failed_backup_listing_is_not_reported_as_empty_backups()
    {
        var composition = ComposedHostWithOpenGate();
        var dashboard = Substitute.For<IBackupDashboard>();
        dashboard.ProviderConfigured.Returns(true);
        dashboard.ListAsync("srv-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BackupListResult>(new BackupListResult.Failed("timed out", "TimeoutException")));

        var services = new FakeServiceProvider(new Dictionary<Type, object?> { [typeof(IBackupDashboard)] = dashboard });

        var result = await ArchiveTools.ListAsync("srv-1", composition, services, CancellationToken.None);

        result.Outcome.Should().Be("failed");
        result.ServyxOwned.Should().BeNull("a failed listing must never collapse to an empty (found-nothing) result");
        result.Detail.Should().Be("timed out");
    }

    /// <summary>
    /// Of the nine honesty invariants this tool surface documents, <c>servyx_server_metrics_get</c> had
    /// nothing pinning it before this test. "The server exists but no sample could be taken right now"
    /// (<c>not-sampled</c>, <c>Sample == null</c>) must never collapse with, nor be confused for, "the server
    /// does not exist at all" (<c>server-not-found</c>) — and a missing sample must never be synthesized as
    /// a zero-valued one.
    /// </summary>
    [Fact]
    public async Task A_server_with_no_metrics_sample_is_distinguishable_from_a_server_that_does_not_exist()
    {
        var summary = MakeSummary("srv-1", "my-container");
        var query = Substitute.For<IServerQueryService>();
        query.GetServerDetailAsync("srv-1", Arg.Any<CancellationToken>())
            .Returns(new ServerDetail(summary, "image:latest", null, null, null, null, null, null, []));
        query.GetMetricsSampleAsync("srv-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ResourceSample?>(null));
        query.GetServerDetailAsync("srv-missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ServerDetail?>(null));

        var notSampled = await ServerReadTools.MetricsAsync("srv-1", query, CancellationToken.None);
        notSampled.Outcome.Should().Be("not-sampled");
        notSampled.Sample.Should().BeNull("a failed sample attempt must never be synthesized as a zero-valued sample");

        var notFound = await ServerReadTools.MetricsAsync("srv-missing", query, CancellationToken.None);
        notFound.Outcome.Should().Be("server-not-found");
        notFound.Sample.Should().BeNull();

        notSampled.Outcome.Should().NotBe(
            notFound.Outcome, "'exists but unsampled' and 'does not exist' are different facts and must read as different outcomes");
    }

    [Fact]
    public void Unreadable_player_roster_is_reported_with_Fidelity_never_as_nobody_connected()
    {
        // ControlChannelTools.ToResult is the pure mapping PlayersListAsync applies once a session has
        // answered — exercised directly here (see its own remarks) rather than through a fully acquired
        // IRconSession, which would need a live RconReachabilityChain to stand up in a unit test.
        var snapshot = new PlayerSnapshot(DateTimeOffset.UtcNow, PlayerListSnapshot.Unresolved("reply did not parse"));

        var result = ControlChannelTools.ToResult(snapshot);

        result.Outcome.Should().Be("observed");
        result.Fidelity.Should().Be("unknown");
        result.Diagnostic.Should().Be("reply did not parse");
        // Critically: this must not be misread as "confirmed zero players" — Count is null (unestablished),
        // not 0 (established-and-empty).
        result.Count.Should().BeNull();
        result.Players.Should().BeEmpty();
    }

    [Fact]
    public void A_countonly_roster_is_distinguishable_from_a_names_and_count_roster_of_the_same_size()
    {
        var countOnly = ControlChannelTools.ToResult(
            new PlayerSnapshot(DateTimeOffset.UtcNow, PlayerListSnapshot.CountOnly(3)));
        var namesAndCount = ControlChannelTools.ToResult(
            new PlayerSnapshot(DateTimeOffset.UtcNow, PlayerListSnapshot.Roster(
                [new PlayerInfo("a", "uid-a", null), new PlayerInfo("b", "uid-b", null), new PlayerInfo("c", "uid-c", null)])));

        countOnly.Fidelity.Should().Be("count-only");
        countOnly.Players.Should().BeEmpty("count-only never fabricates names it does not have");
        countOnly.Count.Should().Be(3);

        namesAndCount.Fidelity.Should().Be("names-and-count");
        namesAndCount.Players.Should().HaveCount(3);
        namesAndCount.Fidelity.Should().NotBe(countOnly.Fidelity);
    }

    [Fact]
    public async Task Unsupported_transport_saves_result_is_distinguishable_from_a_server_with_no_saves()
    {
        var composition = ComposedHost.BuildWithOneDefinition();
        var summary = MakeSummary("srv-1", "my-container");
        var query = Substitute.For<IServerQueryService>();
        query.GetServerDetailAsync("srv-1", Arg.Any<CancellationToken>())
            .Returns(new ServerDetail(summary, "image:latest", "/host", "/data", null, null, null, null, []));

        var transport = Substitute.For<ITransport>();
        transport.Capabilities.Returns(TransportCapabilities.None);
        transport.TransportId.Returns("ssh+docker");

        // No IServerExecutionTargetResolver registered: this process never called AddServyxSshDocker, so
        // ServerReadTools resolves it optionally (see that method's own remarks) and gets null, exactly as a
        // Docker-only host would.
        var services = new ServiceCollection().BuildServiceProvider();

        var result = await ServerReadTools.SavesAsync(
            "srv-1", query, transport, composition, NullLoggerFactory.Instance, services, CancellationToken.None);

        // Save inspection may or may not be process-level available depending on whether the single loaded
        // definition declares a saves block; either way this must never silently read as "Save is null,
        // Outcome listed" (a server confirmed to have no saves) when the real fact is "the transport cannot
        // be trusted to answer this question at all".
        if (result.Outcome == "unavailable" && result.Unavailable!.ReasonCode == UnavailableReason.TransportUnsupported)
        {
            result.Save.Should().BeNull();
        }

        result.Outcome.Should().NotBe(
            "listed",
            "an unsupported-transport read must never surface as a successful (even if empty) 'listed' result");
    }

    private static ServyxCoreComposition ComposedHostWithOpenGate()
    {
        // Fully qualified for the same reason as Support/ComposedHost.cs: Servyx.Mcp.Tests.Host (Host/*Tests.cs)
        // shadows the unqualified Host class from Microsoft.Extensions.Hosting.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration["Servyx:Definitions:Path"] = Directory.CreateTempSubdirectory("servyx-mcp-tests-gate-").FullName;
        builder.Configuration["Servyx:Provisioning:Enabled"] = "true";
        return builder.AddServyxCore(NullLoggerFactory.Instance);
    }

    /// <summary>A minimal <see cref="IServiceProvider"/> double for the ArchiveTools.* optional-resolution pattern.</summary>
    private sealed class FakeServiceProvider(IReadOnlyDictionary<Type, object?> services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.GetValueOrDefault(serviceType);
    }
}
