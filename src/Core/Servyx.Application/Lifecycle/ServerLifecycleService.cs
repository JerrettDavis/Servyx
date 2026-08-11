using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Application.Lifecycle;

/// <summary>
/// <see cref="IServerLifecycle"/> implementation that turns a game definition's declared readiness probes
/// and stop-escalation ladder into real, ordered, write-guarded actions against one server.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scoped to a single server.</strong> Every <see cref="IServerLifecycle"/> member takes no server
/// id — see that interface's own remarks — so one instance of this type is constructed per server, closing
/// over its container ref, RCON server name, and parsed <see cref="LifecycleDefinition"/>. The composition
/// root (a later worker) is responsible for constructing one per adopted server, not for sharing a single
/// instance across servers.
/// </para>
/// <para>
/// <strong>How stop-ladder progress is reported.</strong> <see cref="IServerLifecycle.StopAsync"/>'s
/// return shape, <see cref="StopOutcome"/>, is fixed by the interface and carries only the stage that
/// ended the container plus total duration — no live per-stage feed. Rather than invent a second
/// "progress" return shape the interface doesn't declare, this type reports live progress the same way
/// every other long-running operation in this codebase does: through <see cref="ILogger"/>, one
/// information-level log line per stage attempted. A UI that wants to show "which stage is running right
/// now" over the ladder's ~90s worst case can tail those log lines (they already flow through the same
/// structured logging pipeline every other operator-facing progress signal in Servyx uses) rather than
/// requiring this type to grow a bespoke <c>IProgress&lt;T&gt;</c> or <c>IAsyncEnumerable&lt;StopStage&gt;</c>
/// side channel that <see cref="IServerLifecycle"/> was not designed to expose.
/// </para>
/// <para>
/// <strong>The write-guard safety rule.</strong> A <see cref="WritesDisabledException"/> raised by any
/// stage — an RCON command refused because the definition declares it mutating and the server is
/// read-only, or a container Signal/Kill call refused the same way — is never caught here. It propagates
/// straight out of <see cref="StopAsync"/>, aborting the ladder immediately without attempting the next
/// stage. This is deliberate: escalating past a guard's refusal would turn "you may not stop this
/// politely" into "so kill it instead", which is the exact opposite of what the guard exists to enforce.
/// An RCON stage failing for an <em>ordinary</em> reason (unreachable channel, protocol timeout, non-zero
/// response) is handled entirely differently: it is logged and the stage's own poll-for-exit below simply
/// times out on its own, which is what causes the ladder to escalate — the normal, intended path.
/// </para>
/// </remarks>
public sealed class ServerLifecycleService : IServerLifecycle
{
    /// <summary>
    /// Default for how long to poll for the container's exit after a <see cref="StopStage.Kill"/> stage's
    /// signal is sent. <see cref="StopStage.Kill"/> carries no declared timeout (it is "the final,
    /// unconditional stage" — see its remarks), so a bounded, generous default stands in for one. SIGKILL
    /// is non-catchable, so in practice the container should exit almost immediately; ten seconds is
    /// headroom for a slow container runtime to report the resulting state, not an expectation that the
    /// process itself takes that long to die. Overridable per instance via the constructor (e.g. for
    /// tests) because, unlike every other stage's timeout, this one is not definition-declared.
    /// </summary>
    public static readonly TimeSpan DefaultKillConfirmationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The signal sent for a <see cref="StopStage.Kill"/> stage. Passed explicitly rather than omitted
    /// (which would fall back to the daemon's own default, which also happens to be SIGKILL) for two
    /// reasons: it keeps the request self-documenting in logs/audit regardless of what a given transport's
    /// default happens to be, and it textually distinguishes this stage's kill call from the
    /// <see cref="StopStage.Signal"/> stage's kill call, which also uses <see cref="ContainerLifecycleVerb.Kill"/>
    /// but with a different, catchable signal.
    /// </summary>
    private const string SigKillSignal = "SIGKILL";

    private readonly string _serverId;
    private readonly string? _serverName;
    private readonly LifecycleDefinition _definition;
    private readonly IContainerLifecycle _containerLifecycle;
    private readonly IContainerStateProbe _stateProbe;
    private readonly IRconChannelResolver _rconChannels;
    private readonly ILogStream _logStream;
    private readonly ILogger<ServerLifecycleService> _logger;
    private readonly TimeSpan _stopPollInterval;
    private readonly TimeSpan _logPollInterval;
    private readonly TimeSpan _killConfirmationTimeout;
    private readonly TimeProvider _timeProvider;

    private readonly object _statusLock = new();
    private ServerStatus _status = new(ServerState.Unknown, null, null);

    /// <summary>Creates the lifecycle service for one server.</summary>
    /// <param name="serverId">The server's container ref / discovery id.</param>
    /// <param name="definition">The parsed <c>lifecycle</c> block driving this server's readiness probes and stop ladder.</param>
    /// <param name="containerLifecycle">The write-guarded container lifecycle operations (start/stop/restart/kill).</param>
    /// <param name="stateProbe">Read-only "has the container exited" probe used between stop-ladder stages.</param>
    /// <param name="rconChannels">Resolves the write-guarded RCON control session for a server.</param>
    /// <param name="logStream">The server's console output, used by log-regex readiness probes.</param>
    /// <param name="logger">Logger this instance reports per-stage stop-ladder progress and readiness diagnostics to.</param>
    /// <param name="serverName">The server's container name, if it differs from <paramref name="serverId"/>. Passed through to <paramref name="rconChannels"/>.</param>
    /// <param name="stopPollInterval">Delay between "has it exited yet" polls while waiting out a stop stage. Defaults to 500ms.</param>
    /// <param name="logPollInterval">Delay between log-tail re-polls for the log-regex readiness probe. Defaults to <see cref="PollingLogLineSource.DefaultPollInterval"/>.</param>
    /// <param name="killConfirmationTimeout">
    /// How long to poll for exit after the final <see cref="StopStage.Kill"/> stage's signal is sent, since
    /// that stage carries no definition-declared timeout of its own. Defaults to
    /// <see cref="DefaultKillConfirmationTimeout"/>.
    /// </param>
    /// <param name="timeProvider">Clock used to stamp <see cref="ServerStatus.StartedAt"/>. Defaults to <see cref="TimeProvider.System"/>.</param>
    public ServerLifecycleService(
        string serverId,
        LifecycleDefinition definition,
        IContainerLifecycle containerLifecycle,
        IContainerStateProbe stateProbe,
        IRconChannelResolver rconChannels,
        ILogStream logStream,
        ILogger<ServerLifecycleService> logger,
        string? serverName = null,
        TimeSpan? stopPollInterval = null,
        TimeSpan? logPollInterval = null,
        TimeSpan? killConfirmationTimeout = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(containerLifecycle);
        ArgumentNullException.ThrowIfNull(stateProbe);
        ArgumentNullException.ThrowIfNull(rconChannels);
        ArgumentNullException.ThrowIfNull(logStream);
        ArgumentNullException.ThrowIfNull(logger);

        _serverId = serverId;
        _definition = definition;
        _containerLifecycle = containerLifecycle;
        _stateProbe = stateProbe;
        _rconChannels = rconChannels;
        _logStream = logStream;
        _logger = logger;
        _serverName = serverName;
        _stopPollInterval = stopPollInterval ?? TimeSpan.FromMilliseconds(500);
        _logPollInterval = logPollInterval ?? PollingLogLineSource.DefaultPollInterval;
        _killConfirmationTimeout = killConfirmationTimeout ?? DefaultKillConfirmationTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<ServerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        lock (_statusLock)
        {
            return Task.FromResult(_status);
        }
    }

    /// <inheritdoc />
    /// <remarks>Polls <see cref="GetStatusAsync"/> and yields only on change; the first yielded value is always the current status.</remarks>
    public async IAsyncEnumerable<ServerStatus> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var last = await GetStatusAsync(ct).ConfigureAwait(false);
        yield return last;

        using var timer = new PeriodicTimer(_stopPollInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var current = await GetStatusAsync(ct).ConfigureAwait(false);
            if (current == last)
            {
                continue;
            }

            last = current;
            yield return current;
        }
    }

    /// <inheritdoc />
    public async Task<StartOutcome> StartAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        SetStatus(new ServerStatus(ServerState.Starting, null, null));

        await _containerLifecycle.InvokeAsync(
            new ContainerLifecycleRequest(ContainerLifecycleVerb.Start, _serverId),
            ct).ConfigureAwait(false);

        var readiness = await WaitForReadinessAsync(ct).ConfigureAwait(false);

        SetStatus(readiness.Ready
            ? new ServerStatus(ServerState.Running, _timeProvider.GetUtcNow(), TimeSpan.Zero)
            : new ServerStatus(ServerState.Unknown, null, null));

        return readiness with { TimeToReady = stopwatch.Elapsed };
    }

    /// <inheritdoc />
    public async Task<StopOutcome> StopAsync(StopPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Stages.Count == 0)
        {
            throw new ArgumentException("A stop plan must declare at least one stage.", nameof(plan));
        }

        var stopwatch = Stopwatch.StartNew();
        SetStatus(new ServerStatus(ServerState.Stopping, _status.StartedAt, null));

        foreach (var stage in plan.Stages)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("Stop ladder for '{ServerId}': attempting stage {Stage}", _serverId, stage);

            // A WritesDisabledException raised anywhere inside ExecuteStageAsync is intentionally not
            // caught here (or anywhere in this method) -- see the type-level remarks. It propagates out of
            // StopAsync unmodified, aborting the ladder before any later stage runs.
            var escalate = await ExecuteStageAsync(stage, ct).ConfigureAwait(false);
            if (!escalate)
            {
                _logger.LogInformation("Stop ladder for '{ServerId}': stage {Stage} stopped the container", _serverId, stage);
                SetStatus(new ServerStatus(ServerState.Stopped, null, null));
                return new StopOutcome(stage, stopwatch.Elapsed);
            }
        }

        // The ladder is exhausted. Its final stage is, by contract, StopStage.Kill -- "the final,
        // unconditional stage" -- so reaching here without a reported exit is unexpected, but is still
        // reported honestly (Unknown, not assumed Stopped) rather than silently claiming success.
        var finalStage = plan.Stages[^1];
        SetStatus(new ServerStatus(ServerState.Unknown, null, null));
        return new StopOutcome(finalStage, stopwatch.Elapsed);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A container-level restart is one primitive (<see cref="ContainerLifecycleVerb.Restart"/>) — it
    /// never walks <paramref name="plan"/>'s escalation ladder, since Docker itself performs
    /// stop-then-start as a single operation. <see cref="StopOutcome.StageThatStopped"/> therefore has no
    /// natural value for a restart; <paramref name="plan"/>'s own final stage (by convention always
    /// <see cref="StopStage.Kill"/>) is reported as a documented placeholder purely so this method can
    /// satisfy <see cref="IServerLifecycle"/>'s fixed <c>Task&lt;StopOutcome&gt;</c> signature without
    /// inventing a second outcome shape the interface does not declare. Callers should treat
    /// <see cref="StopOutcome.TotalDuration"/> as the meaningful field here, and read post-restart
    /// readiness via <see cref="GetStatusAsync"/>/<see cref="WatchAsync"/> rather than from this return
    /// value.
    /// </remarks>
    public async Task<StopOutcome> RestartAsync(StopPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Stages.Count == 0)
        {
            throw new ArgumentException("A stop plan must declare at least one stage.", nameof(plan));
        }

        var stopwatch = Stopwatch.StartNew();
        SetStatus(new ServerStatus(ServerState.Stopping, _status.StartedAt, null));

        await _containerLifecycle.InvokeAsync(
            new ContainerLifecycleRequest(ContainerLifecycleVerb.Restart, _serverId),
            ct).ConfigureAwait(false);

        SetStatus(new ServerStatus(ServerState.Starting, null, null));
        var readiness = await WaitForReadinessAsync(ct).ConfigureAwait(false);

        SetStatus(readiness.Ready
            ? new ServerStatus(ServerState.Running, _timeProvider.GetUtcNow(), TimeSpan.Zero)
            : new ServerStatus(ServerState.Unknown, null, null));

        return new StopOutcome(plan.Stages[^1], stopwatch.Elapsed);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Always thrown. Config editing exists now via <see cref="Servyx.Domain.Configuration.IPlanExecutor.PreviewAsync"/>,
    /// <see cref="Servyx.Domain.Configuration.IPlanExecutor.ApplyAsync"/>,
    /// <see cref="Servyx.Domain.Configuration.IPlanExecutor.RevertAsync"/>, and
    /// <c>ChangePlanPanel</c> for preview, apply, and revert flows. However, <see cref="RecreateAsync"/>
    /// is not yet wired into the apply flow — no wiring exists to let an approved <c>ConfigChangePlan</c>
    /// carrying a <c>RecreateRequired</c> consequence invoke this method.
    /// </exception>
    public Task RecreateAsync(string approvedChangePlanId, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "RecreateAsync is not supported yet: it is not wired into the config change plan apply flow. "
            + "Although config editing exists via IPlanExecutor and ChangePlanPanel, no mechanism currently "
            + "invokes RecreateAsync when an approved plan carries a RecreateRequired consequence.");

    private async Task<StartOutcome> WaitForReadinessAsync(CancellationToken ct)
    {
        if (_definition.Ready.Count == 0)
        {
            return new StartOutcome(
                true,
                TimeSpan.Zero,
                new ReadinessSignal(true, "none", "no readiness probes are declared; container start is treated as ready"));
        }

        var stopwatch = Stopwatch.StartNew();
        var detector = BuildReadinessDetector();

        // Each wrapped sub-detector overrides this context's Timeout with its own definition-declared
        // value (see FixedTimeoutReadinessDetector) -- this value is only a formality for the case of a
        // single, unwrapped detector's own bookkeeping.
        var overallTimeout = _definition.Ready.Max(GetDeclaredTimeout);
        var signal = await detector.WaitForReadyAsync(new ReadinessContext(_serverId, overallTimeout), ct).ConfigureAwait(false);

        return new StartOutcome(signal.Ready, stopwatch.Elapsed, signal);
    }

    private IReadinessDetector BuildReadinessDetector()
    {
        var detectors = _definition.Ready.Select(BuildDetector).ToList();
        return detectors.Count == 1 ? detectors[0] : new CompositeReadinessDetector(detectors);
    }

    private IReadinessDetector BuildDetector(ReadinessProbeDefinition probe) => probe switch
    {
        ReadinessProbeDefinition.LogRegex logRegex => new FixedTimeoutReadinessDetector(
            new LogRegexReadiness(new PollingLogLineSource(_logStream, _logPollInterval), logRegex.Pattern),
            logRegex.Timeout),

        ReadinessProbeDefinition.ControlProbe controlProbe => new FixedTimeoutReadinessDetector(
            new ControlProbeReadiness(
                new RconReadinessProbeChannel(_rconChannels, controlProbe.Command, _serverName),
                controlProbe.Expect,
                controlProbe.Interval),
            controlProbe.Timeout),

        _ => throw new NotSupportedException($"Unsupported readiness probe type '{probe.GetType().Name}'."),
    };

    /// <summary>
    /// <see cref="ReadinessProbeDefinition"/> declares no common <c>Timeout</c> member on its base type
    /// (each concrete case declares its own), so this extracts it per case for callers that need it
    /// without caring which kind of probe it is.
    /// </summary>
    private static TimeSpan GetDeclaredTimeout(ReadinessProbeDefinition probe) => probe switch
    {
        ReadinessProbeDefinition.LogRegex logRegex => logRegex.Timeout,
        ReadinessProbeDefinition.ControlProbe controlProbe => controlProbe.Timeout,
        _ => throw new NotSupportedException($"Unsupported readiness probe type '{probe.GetType().Name}'."),
    };

    /// <summary>
    /// Executes one stop-ladder stage's action and then polls for the container's exit up to that stage's
    /// timeout. Returns <see langword="true"/> when the ladder should escalate to the next stage,
    /// <see langword="false"/> when this stage stopped the container.
    /// </summary>
    private async Task<bool> ExecuteStageAsync(StopStage stage, CancellationToken ct)
    {
        switch (stage)
        {
            case StopStage.Rcon rcon:
                await InvokeRconStageAsync(rcon, ct).ConfigureAwait(false);
                return await WaitForExitOrTimeoutAsync(rcon.Timeout, ct).ConfigureAwait(false);

            case StopStage.ConsoleWrite consoleWrite:
                await AttemptAsync(
                    consoleWrite,
                    () => _logStream.WriteAsync(_serverId, consoleWrite.Text, ct),
                    ct).ConfigureAwait(false);
                return await WaitForExitOrTimeoutAsync(consoleWrite.Timeout, ct).ConfigureAwait(false);

            case StopStage.Signal signal:
                await AttemptAsync(
                    signal,
                    async () => await _containerLifecycle.InvokeAsync(
                        new ContainerLifecycleRequest(ContainerLifecycleVerb.Kill, _serverId, Signal: signal.SignalName),
                        ct).ConfigureAwait(false),
                    ct).ConfigureAwait(false);
                return await WaitForExitOrTimeoutAsync(signal.Timeout, ct).ConfigureAwait(false);

            case StopStage.Kill:
                await _containerLifecycle.InvokeAsync(
                    new ContainerLifecycleRequest(ContainerLifecycleVerb.Kill, _serverId, Signal: SigKillSignal),
                    ct).ConfigureAwait(false);
                return await WaitForExitOrTimeoutAsync(_killConfirmationTimeout, ct).ConfigureAwait(false);

            default:
                throw new NotSupportedException($"Unsupported stop stage type '{stage.GetType().Name}'.");
        }
    }

    /// <summary>
    /// Invokes an RCON stop stage. A missing channel is treated exactly like a failed invocation, since
    /// "the control channel is not there" and "the control channel did not answer" are the same situation
    /// from the ladder's point of view.
    /// </summary>
    private Task InvokeRconStageAsync(StopStage.Rcon rcon, CancellationToken ct) =>
        AttemptAsync(
            rcon,
            async () =>
            {
                var session = await _rconChannels.GetSessionAsync(_serverId, _serverName, ct).ConfigureAwait(false);
                if (session is null)
                {
                    throw new InvalidOperationException(
                        $"No rcon channel is configured for '{_serverId}', so stop stage '{rcon.CommandId}' cannot run.");
                }

                await session.InvokeAsync(rcon.CommandId, rcon.Args, ct).ConfigureAwait(false);
            },
            ct);

    /// <summary>
    /// Runs one stop stage's action, applying <see cref="StopStage.ContinueOnError"/>: an ordinary failure
    /// is logged and absorbed when the stage declares it may be, so
    /// <see cref="WaitForExitOrTimeoutAsync"/> escalates on its own timeout — the ladder's normal, intended
    /// path — and is rethrown otherwise, aborting the whole stop.
    /// </summary>
    /// <remarks>
    /// <see cref="WritesDisabledException"/> and caller cancellation are never absorbed, whatever the flag
    /// says. A guard refusal must abort the ladder rather than escalate past it (see the type-level
    /// remarks), and swallowing the caller's own cancellation would make a cancelled stop keep walking.
    /// </remarks>
    private async Task AttemptAsync(StopStage stage, Func<Task> action, CancellationToken ct)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (WritesDisabledException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (stage.ContinueOnError)
        {
            // Ordinary failure: unreachable channel, protocol timeout, non-zero response, a runtime that
            // refused the signal. Logged, not rethrown.
            _logger.LogWarning(ex, "Stop ladder for '{ServerId}': stage {Stage} failed; escalating", _serverId, stage);
        }
    }

    /// <summary>
    /// Polls <see cref="IContainerStateProbe"/> until the container exits or <paramref name="stageTimeout"/>
    /// elapses. Returns <see langword="false"/> (do not escalate) as soon as an exit is observed,
    /// <see langword="true"/> (escalate) if the timeout elapses first. The caller's own <paramref name="ct"/>
    /// cancellation is never swallowed -- only this method's private per-stage timeout is.
    /// </summary>
    private async Task<bool> WaitForExitOrTimeoutAsync(TimeSpan stageTimeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(stageTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            while (true)
            {
                var snapshot = await _stateProbe.GetStateAsync(_serverId, linkedCts.Token).ConfigureAwait(false);
                if (snapshot.Exited)
                {
                    return false;
                }

                await Task.Delay(_stopPollInterval, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the per-stage timeout elapsed; the caller's own token was not cancelled.
            return true;
        }
    }

    private void SetStatus(ServerStatus status)
    {
        lock (_statusLock)
        {
            _status = status;
        }
    }

    /// <summary>
    /// Wraps a sub-detector so it always waits out its own definition-declared timeout, independent of
    /// whatever <see cref="ReadinessContext.Timeout"/> a racing <see cref="CompositeReadinessDetector"/>
    /// happens to pass down.
    /// </summary>
    /// <remarks>
    /// Needed because <see cref="LifecycleDefinition.Ready"/> lets each probe declare its own timeout
    /// (Palworld's log-regex probe waits up to 10 minutes, its control-probe fallback up to 12), while
    /// <see cref="CompositeReadinessDetector"/> races every sub-detector against one shared context.
    /// Without this wrapper, composing probes with different declared timeouts would force every probe to
    /// share whichever single timeout the composite's caller happened to pick.
    /// </remarks>
    private sealed class FixedTimeoutReadinessDetector : IReadinessDetector
    {
        private readonly IReadinessDetector _inner;
        private readonly TimeSpan _timeout;

        public FixedTimeoutReadinessDetector(IReadinessDetector inner, TimeSpan timeout)
        {
            _inner = inner;
            _timeout = timeout;
        }

        public Task<ReadinessSignal> WaitForReadyAsync(ReadinessContext context, CancellationToken ct = default) =>
            _inner.WaitForReadyAsync(context with { Timeout = _timeout }, ct);
    }
}
