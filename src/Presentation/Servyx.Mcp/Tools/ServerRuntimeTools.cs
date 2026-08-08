using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Servyx.Application.Lifecycle;
using Servyx.Application.Servers;
using Servyx.Composition;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;
using Servyx.Mcp.Contracts;

namespace Servyx.Mcp.Tools;

/// <summary>One stage of a stop-escalation ladder, rendered for a plan preview.</summary>
/// <param name="Kind">"rcon" | "console-write" | "signal" | "kill".</param>
/// <param name="CommandId">Populated only for <c>Kind == "rcon"</c>.</param>
/// <param name="Text">Populated only for <c>Kind == "console-write"</c>.</param>
/// <param name="SignalName">Populated only for <c>Kind == "signal"</c>.</param>
/// <param name="TimeoutSeconds">
/// The stage's declared timeout, or <see langword="null"/> for a <c>kill</c> stage — <see cref="StopStage.Kill"/>
/// declares none; see <see cref="ServerRuntimeTools.StageWorstCaseSeconds"/> for the confirmation timeout used
/// in its place when budgeting <c>worstCaseSeconds</c>.
/// </param>
/// <param name="ContinueOnError">Whether this stage's own failure escalates the ladder rather than aborting it.</param>
public sealed record StopStageDto(
    string Kind, string? CommandId, string? Text, string? SignalName, double? TimeoutSeconds, bool ContinueOnError)
{
    /// <summary>Maps a domain <see cref="StopStage"/> to its wire shape.</summary>
    public static StopStageDto From(StopStage stage) => stage switch
    {
        StopStage.Rcon rcon => new StopStageDto("rcon", rcon.CommandId, null, null, rcon.Timeout.TotalSeconds, rcon.ContinueOnError),
        StopStage.ConsoleWrite console => new StopStageDto("console-write", null, console.Text, null, console.Timeout.TotalSeconds, console.ContinueOnError),
        StopStage.Signal signal => new StopStageDto("signal", null, null, signal.SignalName, signal.Timeout.TotalSeconds, signal.ContinueOnError),
        StopStage.Kill kill => new StopStageDto("kill", null, null, null, null, kill.ContinueOnError),
        _ => throw new NotSupportedException(
            $"Unrecognized {nameof(StopStage)} case '{stage.GetType().Name}'; {nameof(StopStageDto)} must be updated to render it."),
    };
}

/// <summary>
/// The result of a plan-preview tool (<c>servyx_server_stop_plan</c>, <c>_restart_plan</c>, <c>_kill_plan</c>).
/// Side-effect-free by construction — no lifecycle call is ever made to produce this response — so
/// <see cref="ImpactStatement"/> can honestly say nothing has happened yet.
/// </summary>
public sealed record StopPlanResponse(
    string Outcome, // "planned" | "unavailable" | "server-not-found"
    string? PlanHash,
    IReadOnlyList<StopStageDto>? Stages,
    double? WorstCaseSeconds,
    string? ImpactStatement,
    Unavailable? Unavailable);

/// <summary>
/// The result of every apply tool (<c>servyx_server_stop_apply</c>, <c>_restart_apply</c>, <c>_kill_apply</c>) —
/// one shape shared across all three, since each is "run the plan named by <c>planHash</c>" differing only in
/// which lifecycle member it calls and which word describes success (<c>Outcome</c> carries that word: e.g.
/// <c>"stopped"</c>, <c>"restarted"</c>, <c>"killed"</c>).
/// </summary>
public sealed record StopApplyResponse(
    string Outcome, // "stopped" | "restarted" | "killed" | "refused-write-guard" | "plan-hash-mismatch" | "unavailable" | "server-not-found"
    string Message,
    string? StageThatStopped, // e.g. "rcon:shutdown" | "console" | "signal:SIGTERM" | "kill" — null for a restart, which walks no ladder
    double? TotalDurationSeconds,
    string? WriteMode,
    string? Remediation,
    Unavailable? Unavailable);

/// <summary>The result of <see cref="ServerRuntimeTools.StartAsync"/>.</summary>
public sealed record ServerStartResult(
    string Outcome, // "started" | "started-unconfirmed" | "refused-write-guard" | "unavailable" | "server-not-found"
    string Message,
    bool? Ready,
    double? TimeToReadySeconds,
    string? ReadinessDetail,
    string? WriteMode,
    string? Remediation,
    Unavailable? Unavailable);

/// <summary>
/// The mutating half of server lifecycle control: start, and the three plan→apply pairs (stop, restart, kill).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Start needs no plan.</strong> Starting a stopped server destroys nothing — there is no connected
/// player to disconnect and no unflushed world state a start could discard — so unlike stop/restart/kill it is
/// a single call with no preview step.
/// </para>
/// <para>
/// <strong>Stop, restart and kill each disconnect every connected player</strong>, and the Kill stage
/// additionally discards unflushed world state. Each therefore requires a plan the agent has actually seen: the
/// matching <c>_plan</c> tool returns a <c>planHash</c> over the exact stages that would run, and the
/// corresponding <c>_apply</c> tool refuses — before making any lifecycle call at all — unless that same hash is
/// echoed back. A hash minted for a different server, or for a different plan shape (e.g. a kill-plan hash
/// presented to <c>servyx_server_stop_apply</c>), fails to match and is refused the same way a stale one is.
/// </para>
/// <para>
/// <strong>Every mutating call routes through <see cref="ToolGuard"/>.</strong> A <see cref="WritesDisabledException"/>
/// raised by the underlying <see cref="IServerLifecycle"/> — a read-only or preview-only server — is mapped to
/// the shared <c>refused-write-guard</c> shape, never left to propagate as an unhandled error.
/// </para>
/// <para>
/// <strong>No <c>maxWaitSeconds</c> parameter on any apply tool.</strong> Letting an agent abandon the ladder
/// between stages would leave a workload <c>Stopping</c> with nobody escalating it further. Protocol-level
/// cancellation (the MCP client's own request cancellation) is the only escape hatch, and every apply tool's
/// description says so.
/// </para>
/// </remarks>
[McpServerToolType]
public static class ServerRuntimeTools
{
    /// <summary>
    /// The <see cref="Unavailable.Capability"/> tag used by every tool here, distinct from
    /// <c>servyx_server_status_get</c>'s own <c>"server-lifecycle-status"</c> tag — this is the mutating
    /// control surface, not the read-only status observation.
    /// </summary>
    private const string LifecycleControlCapabilityTag = "server-lifecycle-control";

    [McpServerTool(Name = "servyx_server_start", UseStructuredContent = true)]
    [Description(
        "Starts a stopped server. BLOCKS until the server reports ready or the game definition's own readiness " +
        "timeout elapses, whichever comes first — that timeout is per-definition, so no fixed worst case is " +
        "quoted here, and progress notifications are emitted every 5 seconds while it runs. Unlike " +
        "stop/restart/kill this is a single call with no plan step: starting a stopped server destroys " +
        "nothing — there is no connected player to disconnect and no unflushed world state a start could " +
        "discard. A read-only or preview-only server refuses this call before any container operation runs.")]
    public static async Task<ServerStartResult> StartAsync(
        [Description("The server's discovery id.")] string serverId,
        IServerQueryService query,
        ServyxServerLifecycles lifecycles,
        ServyxCoreComposition composition,
        WritableServers writable,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return new ServerStartResult("server-not-found", "No adopted server matches this id.", null, null, null, null, null, null);
        }

        var unavailable = CheckLifecycleAvailability(composition, lifecycles);
        if (unavailable is not null)
        {
            return new ServerStartResult("unavailable", unavailable.Explanation, null, null, null, null, null, unavailable);
        }

        var lifecycle = await lifecycles.GetAsync(detail.Summary.Id, cancellationToken).ConfigureAwait(false);
        if (lifecycle is null)
        {
            // Existence was already confirmed above; a null lifecycle here means the server is not (or no
            // longer) adopted by the time this second call ran — the same narrow TOCTOU window
            // ServerReadTools.StatusAsync documents and reports honestly, rather than papered over.
            return new ServerStartResult("server-not-found", "The server is not (or no longer) adopted.", null, null, null, null, null, null);
        }

        await using var heartbeat = ProgressHeartbeat.Start(
            progress, "Waiting for the server to report ready.", worstCase: null, cancellationToken);

        return await ToolGuard.RunAsync(
            async () =>
            {
                var outcome = await lifecycle.StartAsync(cancellationToken).ConfigureAwait(false);
                return outcome.Ready
                    ? new ServerStartResult(
                        "started", "The server started and reported ready.", true,
                        outcome.TimeToReady.TotalSeconds, outcome.Signal.Detail, null, null, null)
                    : new ServerStartResult(
                        "started-unconfirmed",
                        "The start was issued, but readiness could not be confirmed within the definition's readiness timeout.",
                        false, outcome.TimeToReady.TotalSeconds, outcome.Signal.Detail, null, null, null);
            },
            ex => WithRefusal(writable, detail, ex, (writeMode, remediation, message) =>
                new ServerStartResult("refused-write-guard", message, null, null, null, writeMode, remediation, null)));
    }

    [McpServerTool(Name = "servyx_server_stop_plan", UseStructuredContent = true)]
    [Description(
        "Previews the stop-escalation ladder for a server: side-effect-free, and returns a planHash that " +
        "servyx_server_stop_apply must echo back verbatim to actually run it. Nothing is stopped by calling " +
        "this. Stopping disconnects every connected player, which is why an apply requires a plan the agent " +
        "has actually seen.")]
    public static async Task<StopPlanResponse> StopPlanAsync(
        [Description("The server's discovery id.")] string serverId,
        IServerQueryService query,
        ServyxServerLifecycles lifecycles,
        ServyxCoreComposition composition,
        CancellationToken cancellationToken)
    {
        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return new StopPlanResponse("server-not-found", null, null, null, null, null);
        }

        var unavailable = CheckLifecycleAvailability(composition, lifecycles);
        if (unavailable is not null)
        {
            return new StopPlanResponse("unavailable", null, null, null, null, unavailable);
        }

        return BuildPlanResponse(
            detail.Summary.Id, lifecycles.StopPlan!,
            "Nothing has happened yet — this is a preview only. Stopping disconnects every connected player. " +
            "Call servyx_server_stop_apply with this exact planHash to actually run the ladder.");
    }

    [McpServerTool(Name = "servyx_server_stop_apply", UseStructuredContent = true)]
    [Description(
        "BLOCKS until the stop-escalation ladder previewed by servyx_server_stop_plan finishes or the server " +
        "exits, whichever comes first — worst case is the worstCaseSeconds servyx_server_stop_plan reported, " +
        "the sum of the ladder's stage timeouts. Progress notifications are emitted every 5 seconds " +
        "while it runs. Cancelling mid-ladder leaves the server in an indeterminate state (Stopping, " +
        "escalated part-way, with nobody advancing it further), so waiting for completion is strongly " +
        "preferred over cancelling; if the ladder must be abandoned, servyx_server_kill_plan / " +
        "servyx_server_kill_apply is the documented recovery path. Requires the exact planHash " +
        "servyx_server_stop_plan returned for this server — a missing, stale, or mismatched hash is refused " +
        "before any lifecycle call is made.")]
    public static async Task<StopApplyResponse> StopApplyAsync(
        [Description("The server's discovery id.")] string serverId,
        [Description("The exact planHash returned by servyx_server_stop_plan for this server.")] string planHash,
        IServerQueryService query,
        ServyxServerLifecycles lifecycles,
        ServyxCoreComposition composition,
        WritableServers writable,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return new StopApplyResponse("server-not-found", "No adopted server matches this id.", null, null, null, null, null);
        }

        var unavailable = CheckLifecycleAvailability(composition, lifecycles);
        if (unavailable is not null)
        {
            return new StopApplyResponse("unavailable", unavailable.Explanation, null, null, null, null, unavailable);
        }

        var plan = lifecycles.StopPlan!;
        if (!HashMatches(detail.Summary.Id, plan, planHash))
        {
            return new StopApplyResponse("plan-hash-mismatch", PlanHashMismatchMessage("servyx_server_stop_plan"), null, null, null, null, null);
        }

        var lifecycle = await lifecycles.GetAsync(detail.Summary.Id, cancellationToken).ConfigureAwait(false);
        if (lifecycle is null)
        {
            return new StopApplyResponse("server-not-found", "The server is not (or no longer) adopted.", null, null, null, null, null);
        }

        await using var heartbeat = ProgressHeartbeat.Start(
            progress, "Running the stop-escalation ladder.", WorstCase(plan), cancellationToken);

        return await ToolGuard.RunAsync(
            async () =>
            {
                var outcome = await lifecycle.StopAsync(plan, cancellationToken).ConfigureAwait(false);
                var stage = FormatStageThatStopped(outcome.StageThatStopped);
                return new StopApplyResponse(
                    "stopped", $"Stopped at stage '{stage}'.", stage, outcome.TotalDuration.TotalSeconds, null, null, null);
            },
            ex => WithRefusal(writable, detail, ex, (writeMode, remediation, message) =>
                new StopApplyResponse("refused-write-guard", message, null, null, writeMode, remediation, null)));
    }

    [McpServerTool(Name = "servyx_server_restart_plan", UseStructuredContent = true)]
    [Description(
        "Previews a restart for a server: side-effect-free, and returns a planHash that " +
        "servyx_server_restart_apply must echo back verbatim to actually run it. Nothing is restarted by " +
        "calling this. Restarting disconnects every connected player, which is why an apply requires a plan " +
        "the agent has actually seen. Reports the same stop-escalation ladder servyx_server_stop_plan does, " +
        "since a restart's underlying container operation performs a stop-then-start as one step rather than " +
        "walking the ladder itself — the ladder's stage timeouts are used here purely as the worst-case " +
        "duration budget.")]
    public static async Task<StopPlanResponse> RestartPlanAsync(
        [Description("The server's discovery id.")] string serverId,
        IServerQueryService query,
        ServyxServerLifecycles lifecycles,
        ServyxCoreComposition composition,
        CancellationToken cancellationToken)
    {
        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return new StopPlanResponse("server-not-found", null, null, null, null, null);
        }

        var unavailable = CheckLifecycleAvailability(composition, lifecycles);
        if (unavailable is not null)
        {
            return new StopPlanResponse("unavailable", null, null, null, null, unavailable);
        }

        return BuildPlanResponse(
            detail.Summary.Id, lifecycles.StopPlan!,
            "Nothing has happened yet — this is a preview only. Restarting disconnects every connected " +
            "player. Call servyx_server_restart_apply with this exact planHash to actually restart the server.");
    }

    [McpServerTool(Name = "servyx_server_restart_apply", UseStructuredContent = true)]
    [Description(
        "BLOCKS until the restart previewed by servyx_server_restart_plan completes — worst case is the " +
        "worstCaseSeconds servyx_server_restart_plan reported, the sum of the underlying stop-escalation " +
        "ladder's stage timeouts, used as the duration budget for the container's own stop-then-start. " +
        "Progress notifications are emitted every 5 seconds while it runs. Cancelling mid-restart leaves the " +
        "server in an indeterminate state, so waiting for completion is strongly preferred over cancelling; " +
        "if the server must be forced down, servyx_server_kill_plan / servyx_server_kill_apply is the " +
        "documented recovery path. Requires the exact planHash servyx_server_restart_plan returned for this " +
        "server — a missing, stale, or mismatched hash is refused before any lifecycle call is made.")]
    public static async Task<StopApplyResponse> RestartApplyAsync(
        [Description("The server's discovery id.")] string serverId,
        [Description("The exact planHash returned by servyx_server_restart_plan for this server.")] string planHash,
        IServerQueryService query,
        ServyxServerLifecycles lifecycles,
        ServyxCoreComposition composition,
        WritableServers writable,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return new StopApplyResponse("server-not-found", "No adopted server matches this id.", null, null, null, null, null);
        }

        var unavailable = CheckLifecycleAvailability(composition, lifecycles);
        if (unavailable is not null)
        {
            return new StopApplyResponse("unavailable", unavailable.Explanation, null, null, null, null, unavailable);
        }

        var plan = lifecycles.StopPlan!;
        if (!HashMatches(detail.Summary.Id, plan, planHash))
        {
            return new StopApplyResponse("plan-hash-mismatch", PlanHashMismatchMessage("servyx_server_restart_plan"), null, null, null, null, null);
        }

        var lifecycle = await lifecycles.GetAsync(detail.Summary.Id, cancellationToken).ConfigureAwait(false);
        if (lifecycle is null)
        {
            return new StopApplyResponse("server-not-found", "The server is not (or no longer) adopted.", null, null, null, null, null);
        }

        await using var heartbeat = ProgressHeartbeat.Start(
            progress, "Restarting the server.", WorstCase(plan), cancellationToken);

        return await ToolGuard.RunAsync(
            async () =>
            {
                var outcome = await lifecycle.RestartAsync(plan, cancellationToken).ConfigureAwait(false);
                return new StopApplyResponse(
                    "restarted", "Restarted.", null, outcome.TotalDuration.TotalSeconds, null, null, null);
            },
            ex => WithRefusal(writable, detail, ex, (writeMode, remediation, message) =>
                new StopApplyResponse("refused-write-guard", message, null, null, writeMode, remediation, null)));
    }

    [McpServerTool(Name = "servyx_server_kill_plan", UseStructuredContent = true)]
    [Description(
        "Previews an unconditional kill for a server: side-effect-free, and returns a planHash that " +
        "servyx_server_kill_apply must echo back verbatim to actually run it. Nothing is killed by calling " +
        "this. Killing disconnects every connected player AND discards unflushed world state — there is no " +
        "stage after Kill for the ladder to fall back to — which is why an apply requires a plan the agent " +
        "has actually seen. This is the documented recovery path when a stop or restart must be abandoned.")]
    public static async Task<StopPlanResponse> KillPlanAsync(
        [Description("The server's discovery id.")] string serverId,
        IServerQueryService query,
        ServyxServerLifecycles lifecycles,
        ServyxCoreComposition composition,
        CancellationToken cancellationToken)
    {
        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return new StopPlanResponse("server-not-found", null, null, null, null, null);
        }

        var unavailable = CheckLifecycleAvailability(composition, lifecycles);
        if (unavailable is not null)
        {
            return new StopPlanResponse("unavailable", null, null, null, null, unavailable);
        }

        return BuildPlanResponse(
            detail.Summary.Id, KillPlan(),
            "Nothing has happened yet — this is a preview only. Killing disconnects every connected player " +
            "and discards unflushed world state; there is no stage after Kill to fall back to. Call " +
            "servyx_server_kill_apply with this exact planHash to actually kill the server.");
    }

    [McpServerTool(Name = "servyx_server_kill_apply", UseStructuredContent = true)]
    [Description(
        "BLOCKS until the kill previewed by servyx_server_kill_plan completes — the signal is sent and the " +
        "container's exit is confirmed; worst case is the worstCaseSeconds servyx_server_kill_plan reported. " +
        "Progress notifications are emitted every 5 seconds while it runs. Cancelling mid-call leaves the " +
        "server in an indeterminate state, so waiting for completion is strongly preferred over cancelling; " +
        "there is no stage past Kill, so servyx_server_kill_plan / servyx_server_kill_apply is itself the " +
        "recovery path if this call must be retried. Requires the exact planHash servyx_server_kill_plan " +
        "returned for this server — a missing, stale, or mismatched hash (including a hash minted for a " +
        "stop or restart plan) is refused before any lifecycle call is made.")]
    public static async Task<StopApplyResponse> KillApplyAsync(
        [Description("The server's discovery id.")] string serverId,
        [Description("The exact planHash returned by servyx_server_kill_plan for this server.")] string planHash,
        IServerQueryService query,
        ServyxServerLifecycles lifecycles,
        ServyxCoreComposition composition,
        WritableServers writable,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return new StopApplyResponse("server-not-found", "No adopted server matches this id.", null, null, null, null, null);
        }

        var unavailable = CheckLifecycleAvailability(composition, lifecycles);
        if (unavailable is not null)
        {
            return new StopApplyResponse("unavailable", unavailable.Explanation, null, null, null, null, unavailable);
        }

        var plan = KillPlan();
        if (!HashMatches(detail.Summary.Id, plan, planHash))
        {
            return new StopApplyResponse("plan-hash-mismatch", PlanHashMismatchMessage("servyx_server_kill_plan"), null, null, null, null, null);
        }

        var lifecycle = await lifecycles.GetAsync(detail.Summary.Id, cancellationToken).ConfigureAwait(false);
        if (lifecycle is null)
        {
            return new StopApplyResponse("server-not-found", "The server is not (or no longer) adopted.", null, null, null, null, null);
        }

        await using var heartbeat = ProgressHeartbeat.Start(
            progress, "Killing the server.", WorstCase(plan), cancellationToken);

        return await ToolGuard.RunAsync(
            async () =>
            {
                var outcome = await lifecycle.StopAsync(plan, cancellationToken).ConfigureAwait(false);
                var stage = FormatStageThatStopped(outcome.StageThatStopped);
                return new StopApplyResponse(
                    "killed", $"Killed (stage '{stage}').", stage, outcome.TotalDuration.TotalSeconds, null, null, null);
            },
            ex => WithRefusal(writable, detail, ex, (writeMode, remediation, message) =>
                new StopApplyResponse("refused-write-guard", message, null, null, writeMode, remediation, null)));
    }

    /// <summary>The synthesised, always-available kill plan <c>servyx_server_kill_plan</c>/<c>_apply</c> use — never sourced from the definition's own ladder.</summary>
    private static StopPlan KillPlan() => new([new StopStage.Kill()]);

    private static StopPlanResponse BuildPlanResponse(string serverId, StopPlan plan, string impactStatement)
    {
        var hash = StopPlanHash.Compute(serverId, plan);
        var stages = plan.Stages.Select(StopStageDto.From).ToList();
        var worstCase = plan.Stages.Sum(StageWorstCaseSeconds);
        return new StopPlanResponse("planned", hash, stages, worstCase, impactStatement, null);
    }

    private static bool HashMatches(string serverId, StopPlan plan, string? suppliedHash) =>
        // An empty/whitespace hash is refused exactly like a wrong one — never treated as "unspecified,
        // proceed anyway". planHash is a proof the caller has seen the plan, not a confirmation flag; a
        // missing proof is not a default "yes".
        !string.IsNullOrWhiteSpace(suppliedHash)
        && string.Equals(StopPlanHash.Compute(serverId, plan), suppliedHash, StringComparison.Ordinal);

    private static string PlanHashMismatchMessage(string planToolName) =>
        $"The supplied planHash does not match the current plan for this server. Call {planToolName} again " +
        "and use the hash it returns verbatim; no lifecycle call was made.";

    private static TimeSpan WorstCase(StopPlan plan) => TimeSpan.FromSeconds(plan.Stages.Sum(StageWorstCaseSeconds));

    /// <summary>
    /// A stage's contribution to a plan's worst-case duration budget. Every stage but
    /// <see cref="StopStage.Kill"/> carries a declared <c>Timeout</c>; <see cref="StopStage.Kill"/> declares
    /// none (it is "the final, unconditional stage"), so <see cref="ServerLifecycleService.DefaultKillConfirmationTimeout"/>
    /// — the same confirmation budget <see cref="ServerLifecycleService"/> itself uses — stands in for it here.
    /// </summary>
    private static double StageWorstCaseSeconds(StopStage stage) => stage switch
    {
        StopStage.Rcon rcon => rcon.Timeout.TotalSeconds,
        StopStage.ConsoleWrite console => console.Timeout.TotalSeconds,
        StopStage.Signal signal => signal.Timeout.TotalSeconds,
        StopStage.Kill => ServerLifecycleService.DefaultKillConfirmationTimeout.TotalSeconds,
        _ => throw new NotSupportedException(
            $"Unrecognized {nameof(StopStage)} case '{stage.GetType().Name}'; {nameof(ServerRuntimeTools)} must be updated to budget it."),
    };

    /// <summary>Renders the stage that actually stopped a server, e.g. <c>rcon:shutdown</c> / <c>console</c> / <c>signal:SIGTERM</c> / <c>kill</c>.</summary>
    private static string FormatStageThatStopped(StopStage stage) => stage switch
    {
        StopStage.Rcon rcon => $"rcon:{rcon.CommandId}",
        StopStage.ConsoleWrite => "console",
        StopStage.Signal signal => $"signal:{signal.SignalName}",
        StopStage.Kill => "kill",
        _ => throw new NotSupportedException(
            $"Unrecognized {nameof(StopStage)} case '{stage.GetType().Name}'; {nameof(ServerRuntimeTools)} must be updated to render it."),
    };

    /// <summary>
    /// Checks whether this process can produce an <see cref="IServerLifecycle"/> for any server at all —
    /// mirrors <see cref="ServerReadTools.StatusAsync"/>'s own two-part check (multiple/no definitions loaded,
    /// versus a single loaded definition that declares no <c>lifecycle</c> block), reusing the shared
    /// <see cref="ServyxCapabilityReport"/> fact for the first and emitting the second directly since no
    /// dedicated capability status exists for it.
    /// </summary>
    private static Unavailable? CheckLifecycleAvailability(ServyxCoreComposition composition, ServyxServerLifecycles lifecycles)
    {
        if (composition.CatalogMode != DefinitionCatalogMode.Single)
        {
            var status = composition.Capabilities.Get(ServyxCapability.StopEscalationLadder);
            return new Unavailable(
                LifecycleControlCapabilityTag,
                status.ReasonCode ?? "unknown",
                status.Explanation ?? "No further explanation was recorded.",
                status.Contributing);
        }

        if (lifecycles.StopPlan is null)
        {
            return new Unavailable(
                LifecycleControlCapabilityTag,
                UnavailableReason.DefinitionDeclaresNone,
                "The loaded game definition declares no 'lifecycle' block, so this server cannot be started, " +
                "stopped, restarted, or killed through it.",
                []);
        }

        return null;
    }

    /// <summary>
    /// Builds a caught <see cref="WritesDisabledException"/>'s <c>refused-write-guard</c> result via the
    /// shared <see cref="ToolGuard.Refuse"/> shape, deferring only the caller's own result-type construction
    /// to <paramref name="build"/> so every apply/start tool maps the refusal identically.
    /// </summary>
    private static T WithRefusal<T>(
        WritableServers writable,
        ServerDetail detail,
        WritesDisabledException ex,
        Func<string, string, string, T> build)
    {
        var mode = writable.Mode(detail.Summary.Id, detail.Summary.Name);
        var refusal = ToolGuard.Refuse(mode, detail.Summary.Name, ex);
        return build(refusal.WriteMode, refusal.Remediation, refusal.Message);
    }
}
