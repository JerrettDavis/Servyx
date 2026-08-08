using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Lifecycle;
using Servyx.Application.Servers;
using Servyx.Composition;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Mcp.Tests.Support;
using Servyx.Mcp.Tools;

namespace Servyx.Mcp.Tests.Destructive;

/// <summary>
/// Proves the D3 plan→apply protocol <see cref="StopPlanHash"/> underlies: a plan preview is genuinely
/// side-effect-free and reports its worst-case duration and a stable hash; an apply refuses — before making
/// any lifecycle call at all — a hash that is missing, stale, minted for a different server, or minted for a
/// differently-shaped plan (a kill-plan hash presented to the stop tool); and the hash itself is computed
/// deterministically regardless of the host's current culture.
/// </summary>
public sealed class McpStopPlanHashTests
{
    private const string ServerId = "srv-1";
    private const string ContainerName = "my-container";

    private static WritableServers WritableFor(string key, Servyx.Domain.Transport.WriteMode mode)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"Servyx:Servers:{key}:WriteMode"] = mode.ToString() })
            .Build();

        return WritableServers.FromConfiguration(configuration, new ProvisioningGate(enabled: true));
    }

    private static ServerSummary MakeSummary() =>
        new(ServerId, ContainerName, "unknown", ServerState.Running, ServerHealthStatus.Unknown, null, null, "local", []);

    private static IServerQueryService MakeQuery()
    {
        var query = Substitute.For<IServerQueryService>();
        var summary = MakeSummary();
        query.GetServerDetailAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(new ServerDetail(summary, "image:latest", null, null, null, null, null, null, []));
        return query;
    }

    /// <summary>
    /// A <see cref="ServyxServerLifecycles"/> wired over an <see cref="ITransport"/> whose <c>ConnectAsync</c>
    /// is asserted never to be reached in the tests that use it — the proof that a refused apply performs no
    /// lifecycle call at all.
    /// </summary>
    private static ServyxServerLifecycles BuildLifecycles(StopPlan plan, IServerQueryService query, out ITransport transport)
    {
        transport = Substitute.For<ITransport>();
        var stateProbe = Substitute.For<IContainerStateProbe>();
        var rconResolver = Substitute.For<IRconChannelResolver>();
        var logStream = Substitute.For<ILogStream>();
        var definition = new LifecycleDefinition(Ready: [], Stop: plan, CrashDetection: []);

        return new ServyxServerLifecycles(query, transport, stateProbe, rconResolver, logStream, NullLoggerFactory.Instance, definition);
    }

    private static StopPlan MultiStagePlan() => new(
    [
        new StopStage.Rcon("shutdown", TimeSpan.FromSeconds(30)),
        new StopStage.Signal("SIGTERM", TimeSpan.FromSeconds(60)),
        new StopStage.Kill(),
    ]);

    // -----------------------------------------------------------------------------------------------------
    // Plan preview
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stop_plan_returns_a_hash_and_an_impact_statement()
    {
        var plan = MultiStagePlan();
        var query = MakeQuery();
        var lifecycles = BuildLifecycles(plan, query, out _);
        var composition = ComposedHost.BuildWithOneDefinition();

        var result = await ServerRuntimeTools.StopPlanAsync(ServerId, query, lifecycles, composition, CancellationToken.None);

        result.Outcome.Should().Be("planned");
        result.PlanHash.Should().NotBeNullOrWhiteSpace();
        result.ImpactStatement.Should().NotBeNullOrWhiteSpace();
        result.Stages.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_stop_plan_impact_statement_states_that_nothing_has_been_stopped()
    {
        var plan = MultiStagePlan();
        var query = MakeQuery();
        var lifecycles = BuildLifecycles(plan, query, out var transport);
        var composition = ComposedHost.BuildWithOneDefinition();

        var result = await ServerRuntimeTools.StopPlanAsync(ServerId, query, lifecycles, composition, CancellationToken.None);

        result.ImpactStatement.Should().Contain("Nothing has happened yet");
        await transport.DidNotReceive().ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stop_plan_reports_the_worst_case_duration_in_seconds()
    {
        var plan = MultiStagePlan(); // 30 + 60 + (10s kill-confirmation default) = 100
        var query = MakeQuery();
        var lifecycles = BuildLifecycles(plan, query, out _);
        var composition = ComposedHost.BuildWithOneDefinition();

        var result = await ServerRuntimeTools.StopPlanAsync(ServerId, query, lifecycles, composition, CancellationToken.None);

        result.WorstCaseSeconds.Should().Be(100);
    }

    [Fact]
    public async Task A_stop_plan_hash_is_stable_across_repeated_plan_calls()
    {
        var plan = MultiStagePlan();
        var query = MakeQuery();
        var lifecycles = BuildLifecycles(plan, query, out _);
        var composition = ComposedHost.BuildWithOneDefinition();

        var first = await ServerRuntimeTools.StopPlanAsync(ServerId, query, lifecycles, composition, CancellationToken.None);
        var second = await ServerRuntimeTools.StopPlanAsync(ServerId, query, lifecycles, composition, CancellationToken.None);

        second.PlanHash.Should().Be(first.PlanHash);
    }

    // -----------------------------------------------------------------------------------------------------
    // StopPlanHash itself
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_stop_plan_hash_changes_when_any_stage_changes()
    {
        var baseline = StopPlanHash.Compute(ServerId, MultiStagePlan());
        var changedTimeout = StopPlanHash.Compute(
            ServerId,
            new StopPlan(
            [
                new StopStage.Rcon("shutdown", TimeSpan.FromSeconds(31)), // was 30
                new StopStage.Signal("SIGTERM", TimeSpan.FromSeconds(60)),
                new StopStage.Kill(),
            ]));
        var changedCommand = StopPlanHash.Compute(
            ServerId,
            new StopPlan(
            [
                new StopStage.Rcon("save", TimeSpan.FromSeconds(30)), // was "shutdown"
                new StopStage.Signal("SIGTERM", TimeSpan.FromSeconds(60)),
                new StopStage.Kill(),
            ]));

        changedTimeout.Should().NotBe(baseline);
        changedCommand.Should().NotBe(baseline);
        changedCommand.Should().NotBe(changedTimeout);
    }

    [Fact]
    public void Every_StopStage_case_contributes_a_distinct_component_to_the_hash()
    {
        var rcon = StopPlanHash.Compute(ServerId, new StopPlan([new StopStage.Rcon("shutdown", TimeSpan.FromSeconds(1))]));
        var console = StopPlanHash.Compute(ServerId, new StopPlan([new StopStage.ConsoleWrite("shutdown", TimeSpan.FromSeconds(1))]));
        var signal = StopPlanHash.Compute(ServerId, new StopPlan([new StopStage.Signal("shutdown", TimeSpan.FromSeconds(1))]));
        var kill = StopPlanHash.Compute(ServerId, new StopPlan([new StopStage.Kill()]));

        var hashes = new[] { rcon, console, signal, kill };
        hashes.Should().OnlyHaveUniqueItems(
            "each StopStage case must render a structurally distinct component even when unrelated fields (like a command id reused as signal/console text) collide");
    }

    [Fact]
    public void The_hash_is_computed_under_the_invariant_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            var plan = new StopPlan([new StopStage.Rcon("save", TimeSpan.FromSeconds(1.5)), new StopStage.Kill()]);

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariantHash = StopPlanHash.Compute(ServerId, plan);

            // A comma-decimal culture: TimeSpan.TotalSeconds.ToString("0.###") under fr-FR renders "1,5" —
            // if StopPlanHash ever stopped passing InvariantCulture explicitly, this would silently change
            // the hash, passing on a French dev box and failing (or worse, drifting) elsewhere.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var frenchHash = StopPlanHash.Compute(ServerId, plan);

            frenchHash.Should().Be(invariantHash, "the hash must not depend on the host's current culture");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // -----------------------------------------------------------------------------------------------------
    // Apply-time hash validation
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stop_apply_with_a_hash_minted_for_a_different_server_is_refused()
    {
        var plan = MultiStagePlan();
        var query = MakeQuery();
        var lifecycles = BuildLifecycles(plan, query, out var transport);
        var composition = ComposedHost.BuildWithOneDefinition();
        var writable = WritableFor(ContainerName, Servyx.Domain.Transport.WriteMode.Enabled);

        var hashForAnotherServer = StopPlanHash.Compute("some-other-server", plan);

        var result = await ServerRuntimeTools.StopApplyAsync(
            ServerId, hashForAnotherServer, query, lifecycles, composition, writable, progress: null, CancellationToken.None);

        result.Outcome.Should().Be("plan-hash-mismatch");
        await transport.DidNotReceive().ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stop_apply_with_a_kill_plan_hash_is_refused_by_the_stop_tool()
    {
        var plan = MultiStagePlan();
        var query = MakeQuery();
        var lifecycles = BuildLifecycles(plan, query, out var transport);
        var composition = ComposedHost.BuildWithOneDefinition();
        var writable = WritableFor(ContainerName, Servyx.Domain.Transport.WriteMode.Enabled);

        var killPlanHash = StopPlanHash.Compute(ServerId, new StopPlan([new StopStage.Kill()]));

        var result = await ServerRuntimeTools.StopApplyAsync(
            ServerId, killPlanHash, query, lifecycles, composition, writable, progress: null, CancellationToken.None);

        result.Outcome.Should().Be("plan-hash-mismatch", "a kill plan's hash never matches the stop ladder's own plan shape");
        await transport.DidNotReceive().ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stop_apply_with_an_empty_hash_is_refused_and_never_defaults_to_proceeding()
    {
        var plan = MultiStagePlan();
        var query = MakeQuery();
        var lifecycles = BuildLifecycles(plan, query, out var transport);
        var composition = ComposedHost.BuildWithOneDefinition();
        var writable = WritableFor(ContainerName, Servyx.Domain.Transport.WriteMode.Enabled);

        var result = await ServerRuntimeTools.StopApplyAsync(
            ServerId, string.Empty, query, lifecycles, composition, writable, progress: null, CancellationToken.None);

        result.Outcome.Should().Be("plan-hash-mismatch");
        await transport.DidNotReceive().ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_stop_apply_performs_no_lifecycle_call_at_all()
    {
        var plan = MultiStagePlan();
        var query = MakeQuery();
        var lifecycles = BuildLifecycles(plan, query, out var transport);
        var composition = ComposedHost.BuildWithOneDefinition();
        var writable = WritableFor(ContainerName, Servyx.Domain.Transport.WriteMode.Enabled);

        var result = await ServerRuntimeTools.StopApplyAsync(
            ServerId, "not-a-real-hash", query, lifecycles, composition, writable, progress: null, CancellationToken.None);

        result.Outcome.Should().Be("plan-hash-mismatch");
        // ServyxServerLifecycles.GetAsync is what would open the underlying transport session; asserting the
        // transport was never connected to proves no lifecycle call — real or guarded — was ever attempted.
        await transport.DidNotReceive().ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>());
    }
}
