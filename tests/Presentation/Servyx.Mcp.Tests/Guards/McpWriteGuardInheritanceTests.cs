using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Servyx.Application.Lifecycle;
using Servyx.Application.Servers;
using Servyx.Composition;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon;
using Servyx.Mcp.Tests.Support;
using Servyx.Mcp.Tools;

namespace Servyx.Mcp.Tests.Guards;

/// <summary>
/// Proves the write guard is structural, not conventional, for every mutating tool this phase adds: a
/// refusal is a named, non-erroring result; it happens before any real I/O (no socket, no container call);
/// <see cref="Servyx.Domain.Transport.WriteMode.PreviewOnly"/> is refused exactly like <see cref="Servyx.Domain.Transport.WriteMode.ReadOnly"/>; and — via an
/// IL scan, the same technique <c>Inventory/McpWithheldOperationTests</c> uses — every mutating tool's async
/// state machine actually calls through <see cref="ToolGuard.RunAsync{T}"/> rather than each tool inventing
/// its own catch.
/// </summary>
public sealed class McpWriteGuardInheritanceTests
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

    // -----------------------------------------------------------------------------------------------------
    // RCON invoke
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a real, working RCON stack (catalogue, options, a reachability strategy) around a substitute
    /// inner <see cref="IRconSession"/>, so <see cref="ServyxRconChannels.GetSessionAsync"/> is genuinely
    /// exercised rather than stubbed away. <paramref name="reachability"/> is returned so a test can assert
    /// it was never probed — the proof that no socket was opened.
    /// </summary>
    private static ServyxRconChannels BuildRconChannels(Servyx.Domain.Transport.WriteMode mode, out IRconReachability reachability, out IRconSession innerSession)
    {
        var catalog = new RconCommandCatalog(
        [
            new RconCommand("info", "Info", ReadOnly: true),
            new RconCommand("save", "Save", ReadOnly: false),
        ]);

        var endpoint = new RconEndpoint("127.0.0.1", 25575);
        var channel = new RconChannel(ContainerName, endpoint, SecretUrn.Create("server", ContainerName, "rcon", "password"));
        var options = new RconWiringOptions([channel]);

        innerSession = Substitute.For<IRconSession>();
        innerSession.InvokeAsync("info", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new RconResponse("pong", true));

        var strategy = Substitute.For<IRconReachability>();
        strategy.StrategyId.Returns("direct-tcp");
        strategy.IsAvailableAsync(Arg.Any<RconEndpoint>(), Arg.Any<CancellationToken>()).Returns(true);
        strategy.AcquireAsync(Arg.Any<RconEndpoint>(), Arg.Any<CancellationToken>()).Returns(innerSession);
        reachability = strategy;

        var chain = new RconReachabilityChain([strategy]);
        var writable = WritableFor(ContainerName, mode);
        var client = Substitute.For<IRconClient>();
        var secrets = Substitute.For<ISecretStore>();

        return new ServyxRconChannels(options, catalog, client, secrets, writable, chainFactory: _ => chain);
    }

    [Fact]
    public async Task A_mutating_rcon_command_on_a_read_only_server_is_refused_before_any_socket_is_opened()
    {
        var channels = BuildRconChannels(Servyx.Domain.Transport.WriteMode.ReadOnly, out var reachability, out var innerSession);
        var writable = WritableFor(ContainerName, Servyx.Domain.Transport.WriteMode.ReadOnly);
        var composition = ComposedHost.BuildWithOneDefinition();
        var query = MakeQuery();

        var result = await ControlChannelTools.RconInvokeAsync(
            ServerId, "save", composition, query, channels, writable, CancellationToken.None);

        result.Outcome.Should().Be("refused-write-guard");
        await reachability.DidNotReceive().IsAvailableAsync(Arg.Any<RconEndpoint>(), Arg.Any<CancellationToken>());
        await reachability.DidNotReceive().AcquireAsync(Arg.Any<RconEndpoint>(), Arg.Any<CancellationToken>());
        await innerSession.DidNotReceive().InvokeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_read_only_rcon_command_on_a_read_only_server_is_invoked()
    {
        var channels = BuildRconChannels(Servyx.Domain.Transport.WriteMode.ReadOnly, out _, out var innerSession);
        var writable = WritableFor(ContainerName, Servyx.Domain.Transport.WriteMode.ReadOnly);
        var composition = ComposedHost.BuildWithOneDefinition();
        var query = MakeQuery();

        var result = await ControlChannelTools.RconInvokeAsync(
            ServerId, "info", composition, query, channels, writable, CancellationToken.None);

        result.Outcome.Should().Be("invoked");
        result.ResponseText.Should().Be("pong");
        result.GameReportedSuccess.Should().BeTrue();
        await innerSession.Received(1).InvokeAsync(
            "info", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(Servyx.Domain.Transport.WriteMode.ReadOnly)]
    [InlineData(Servyx.Domain.Transport.WriteMode.PreviewOnly)]
    public async Task A_preview_only_server_is_refused_exactly_as_a_read_only_server_is(Servyx.Domain.Transport.WriteMode mode)
    {
        var channels = BuildRconChannels(mode, out _, out _);
        var writable = WritableFor(ContainerName, mode);
        var composition = ComposedHost.BuildWithOneDefinition();
        var query = MakeQuery();

        var result = await ControlChannelTools.RconInvokeAsync(
            ServerId, "save", composition, query, channels, writable, CancellationToken.None);

        result.Outcome.Should().Be(
            "refused-write-guard", $"write mode '{mode}' may plan but never actually writes, exactly like ReadOnly");
    }

    [Fact]
    public async Task A_write_guard_refusal_is_reported_as_a_named_outcome_and_never_as_a_thrown_error()
    {
        var channels = BuildRconChannels(Servyx.Domain.Transport.WriteMode.ReadOnly, out _, out _);
        var writable = WritableFor(ContainerName, Servyx.Domain.Transport.WriteMode.ReadOnly);
        var composition = ComposedHost.BuildWithOneDefinition();
        var query = MakeQuery();

        var act = () => ControlChannelTools.RconInvokeAsync(
            ServerId, "save", composition, query, channels, writable, CancellationToken.None);

        var result = await act.Should().NotThrowAsync(
            "an expected write-guard refusal must be a normal result, never a thrown exception the SDK would mark IsError");
        result.Subject.Outcome.Should().Be("refused-write-guard");
    }

    [Fact]
    public async Task A_write_guard_refusal_names_both_configuration_keys_that_would_grant_the_write()
    {
        var channels = BuildRconChannels(Servyx.Domain.Transport.WriteMode.ReadOnly, out _, out _);
        var writable = WritableFor(ContainerName, Servyx.Domain.Transport.WriteMode.ReadOnly);
        var composition = ComposedHost.BuildWithOneDefinition();
        var query = MakeQuery();

        var result = await ControlChannelTools.RconInvokeAsync(
            ServerId, "save", composition, query, channels, writable, CancellationToken.None);

        result.Remediation.Should().NotBeNullOrWhiteSpace();
        result.Remediation.Should().Contain("Servyx:Provisioning:Enabled=true", "the gate, without which a grant does nothing");
        result.Remediation.Should().Contain($"Servyx:Servers:{ContainerName}:WriteMode=Enabled", "the per-server grant");
    }

    // -----------------------------------------------------------------------------------------------------
    // RCON invoke — the Outcome/GameReportedSuccess pairing this result shape exists for
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Pins the exact invariant <see cref="RconInvokeResult.GameReportedSuccess"/> exists for: a command
    /// Servyx genuinely delivered can still be rejected by the game itself, and the two facts must never
    /// collapse into one. Asserted as a single tuple comparison rather than two separate assertions, so a
    /// future refactor that folds <c>GameReportedSuccess</c> into <c>Outcome</c> fails this test on the
    /// pairing itself, not on whichever half happens to be checked first.
    /// </summary>
    [Fact]
    public async Task A_command_the_game_rejects_is_still_reported_as_delivered()
    {
        var channels = BuildRconChannels(Servyx.Domain.Transport.WriteMode.ReadOnly, out _, out var innerSession);
        var writable = WritableFor(ContainerName, Servyx.Domain.Transport.WriteMode.ReadOnly);
        var composition = ComposedHost.BuildWithOneDefinition();
        var query = MakeQuery();

        innerSession.InvokeAsync("info", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new RconResponse("ERR: unknown zone", false));

        var result = await ControlChannelTools.RconInvokeAsync(
            ServerId, "info", composition, query, channels, writable, CancellationToken.None);

        (result.Outcome, result.GameReportedSuccess).Should().Be(("invoked", (bool?)false),
            "Servyx delivered the command (Outcome == \"invoked\") even though the game rejected it "
            + "(GameReportedSuccess == false) — that pairing is the whole point of keeping the two fields apart");
        result.ResponseText.Should().Be("ERR: unknown zone");
    }

    [Fact]
    public async Task An_undeclared_command_id_is_refused_with_the_declared_command_list()
    {
        var channels = BuildRconChannels(Servyx.Domain.Transport.WriteMode.ReadOnly, out _, out var innerSession);
        var writable = WritableFor(ContainerName, Servyx.Domain.Transport.WriteMode.ReadOnly);
        var composition = ComposedHost.BuildWithOneDefinition();
        var query = MakeQuery();

        var result = await ControlChannelTools.RconInvokeAsync(
            ServerId, "teleport-everyone", composition, query, channels, writable, CancellationToken.None);

        result.Outcome.Should().Be("unknown-command");
        result.DeclaredCommandIds.Should().BeEquivalentTo(["info", "save"]);
        await innerSession.DidNotReceive().InvokeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_server_with_no_configured_control_channel_is_reported_as_unavailable()
    {
        var catalog = new RconCommandCatalog([new RconCommand("info", "Info", ReadOnly: true)]);
        var writable = WritableFor(ContainerName, Servyx.Domain.Transport.WriteMode.ReadOnly);
        var channels = new ServyxRconChannels(RconWiringOptions.Disabled, catalog, client: null, secrets: null, writable);
        var composition = ComposedHost.BuildWithOneDefinition();
        var query = MakeQuery();

        var result = await ControlChannelTools.RconInvokeAsync(
            ServerId, "info", composition, query, channels, writable, CancellationToken.None);

        result.Outcome.Should().Be("unavailable");
    }

    /// <summary>
    /// A configured channel that cannot currently be reached is a materially different fact from "no channel
    /// is configured at all" (the previous test) — <see cref="RconReachabilityChain"/> throws
    /// <see cref="RconUnreachableException"/> only after a strategy reports itself available and then fails
    /// to actually acquire a session, which is exactly what this test forces.
    /// </summary>
    [Fact]
    public async Task A_configured_but_unreachable_control_channel_is_distinguishable_from_an_unconfigured_one()
    {
        var channels = BuildRconChannels(Servyx.Domain.Transport.WriteMode.ReadOnly, out var reachability, out _);
        reachability.AcquireAsync(Arg.Any<RconEndpoint>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RconUnreachableException("No reachability strategy could reach the RCON endpoint."));
        var writable = WritableFor(ContainerName, Servyx.Domain.Transport.WriteMode.ReadOnly);
        var composition = ComposedHost.BuildWithOneDefinition();
        var query = MakeQuery();

        var result = await ControlChannelTools.RconInvokeAsync(
            ServerId, "info", composition, query, channels, writable, CancellationToken.None);

        result.Outcome.Should().Be("unreachable");
        result.Outcome.Should().NotBe(
            "unavailable", "a configured-but-unreachable channel must never read the same as an unconfigured one");
    }

    /// <summary>
    /// <see cref="ControlChannelTools.PlayersListAsync"/> draws the same unreachable/unconfigured
    /// distinction <see cref="ControlChannelTools.RconInvokeAsync"/> does, but had zero coverage of the
    /// unreachable branch anywhere in the suite before this test.
    /// </summary>
    [Fact]
    public async Task A_players_list_call_against_a_configured_but_unreachable_control_channel_reports_unreachable()
    {
        var channels = BuildRconChannels(Servyx.Domain.Transport.WriteMode.ReadOnly, out var reachability, out _);
        reachability.AcquireAsync(Arg.Any<RconEndpoint>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RconUnreachableException("No reachability strategy could reach the RCON endpoint."));
        var composition = ComposedHost.BuildWithOneDefinition();
        var query = MakeQuery();

        var result = await ControlChannelTools.PlayersListAsync(
            ServerId, composition, query, channels, CancellationToken.None);

        result.Outcome.Should().Be("unreachable");
    }

    // -----------------------------------------------------------------------------------------------------
    // Stop apply
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Wires a real <see cref="ServyxServerLifecycles"/> around substitute transport-level collaborators, so
    /// <see cref="ServerLifecycleService"/>'s real stop ladder — and the real <see cref="WriteGuardedRconSession"/>
    /// it calls into for its RCON stage — is genuinely exercised.
    /// </summary>
    private static ServyxServerLifecycles BuildLifecycles(
        Servyx.Domain.Transport.WriteMode rconMode, StopPlan plan, IServerQueryService query,
        out IContainerLifecycle containerLifecycle, out IRconSession innerRconSession)
    {
        var target = Substitute.For<IExecutionTarget, IContainerLifecycle>();
        containerLifecycle = (IContainerLifecycle)target;

        var transport = Substitute.For<ITransport>();
        transport.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>()).Returns((IExecutionTarget)target);

        var stateProbe = Substitute.For<IContainerStateProbe>();
        stateProbe.GetStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerStateSnapshot(Exited: false));

        var catalog = new RconCommandCatalog([new RconCommand("shutdown", "Shutdown", ReadOnly: false)]);
        innerRconSession = Substitute.For<IRconSession>();
        var guardedSession = new WriteGuardedRconSession(innerRconSession, catalog, rconMode, ContainerName);

        var rconResolver = Substitute.For<IRconChannelResolver>();
        rconResolver.GetSessionAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IRconSession?>(guardedSession));

        var logStream = Substitute.For<ILogStream>();
        var definition = new LifecycleDefinition(Ready: [], Stop: plan, CrashDetection: []);

        return new ServyxServerLifecycles(query, transport, stateProbe, rconResolver, logStream, NullLoggerFactory.Instance, definition);
    }

    [Fact]
    public async Task A_stop_apply_on_a_read_only_server_is_refused_before_the_lifecycle_is_called()
    {
        var plan = new StopPlan([new StopStage.Rcon("shutdown", TimeSpan.FromSeconds(1)), new StopStage.Kill()]);
        var query = MakeQuery();
        var lifecycles = BuildLifecycles(Servyx.Domain.Transport.WriteMode.ReadOnly, plan, query, out var containerLifecycle, out var innerRconSession);
        var composition = ComposedHost.BuildWithOneDefinition();
        var writable = WritableFor(ContainerName, Servyx.Domain.Transport.WriteMode.ReadOnly);
        var hash = StopPlanHash.Compute(ServerId, plan);

        var result = await ServerRuntimeTools.StopApplyAsync(
            ServerId, hash, query, lifecycles, composition, writable, progress: null, CancellationToken.None);

        result.Outcome.Should().Be("refused-write-guard");
        await innerRconSession.DidNotReceive().InvokeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
        await containerLifecycle.DidNotReceive().InvokeAsync(Arg.Any<ContainerLifecycleRequest>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------------------------------------
    // Structural: every mutating tool routes through ToolGuard
    // -----------------------------------------------------------------------------------------------------

    private static readonly IReadOnlyList<(Type DeclaringType, string MethodName)> MutatingTools =
    [
        (typeof(ServerRuntimeTools), "StartAsync"),
        (typeof(ServerRuntimeTools), "StopApplyAsync"),
        (typeof(ServerRuntimeTools), "RestartApplyAsync"),
        (typeof(ServerRuntimeTools), "KillApplyAsync"),
        (typeof(ControlChannelTools), "RconInvokeAsync"),
    ];

    public static TheoryData<string> EveryMutatingToolLabel()
    {
        var data = new TheoryData<string>();
        foreach (var (type, method) in MutatingTools)
        {
            data.Add($"{type.Name}.{method}");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryMutatingToolLabel))]
    public void Every_mutating_tool_routes_its_call_through_ToolGuard(string label)
    {
        var (declaringType, methodName) = MutatingTools.Single(t => $"{t.DeclaringType.Name}.{t.MethodName}" == label);

        // The async method's own IL only starts its compiler-generated state machine; the ToolGuard.RunAsync
        // call actually lives in that state machine's MoveNext, exactly as an await-ed call always does — so
        // this is found via the nested <MethodName>d__N type, not the declaring method's own IL body.
        var stateMachine = declaringType
            .GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(t => t.Name.StartsWith($"<{methodName}>d__", StringComparison.Ordinal));

        stateMachine.Should().NotBeNull(
            $"{label} is expected to be an async method with a compiler-generated state machine; if it was " +
            "rewritten as non-async this check must be updated to look at its own IL instead");

        var moveNext = IlScanner.DeclaredMethods(stateMachine!).Single(m => m.Name == "MoveNext");
        var calls = IlScanner.MethodCallsMadeBy(moveNext);

        calls.Should().Contain(
            call => call.Name == "RunAsync" && call.DeclaringType != null && call.DeclaringType.FullName == "Servyx.Mcp.ToolGuard",
            $"{label} must route its mutating call through ToolGuard.RunAsync rather than catching " +
            "WritesDisabledException itself");
    }
}
