namespace Servyx.Domain.Lifecycle;

/// <summary>A single stage in a <see cref="StopPlan"/> escalation ladder.</summary>
public abstract record StopStage
{
    private StopStage()
    {
    }

    /// <summary>
    /// Whether a failure of this stage's own action should be absorbed so the ladder escalates to the next
    /// stage, rather than aborting the whole stop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults differ by stage kind, and the reason is asymmetric. A control-channel stage talks to the
    /// workload over a side channel that is routinely unavailable exactly when an operator most wants to
    /// stop the server — the process is wedged, the port never opened, the password is stale. Letting that
    /// abort the stop would mean an unreachable control channel could wedge a shutdown permanently, so
    /// control stages default to <see langword="true"/>: their failure is a reason to escalate, which is
    /// what the ladder below them exists for. A signal stage, by contrast, fails only when the container
    /// runtime itself refused the call, which is a real fault worth surfacing rather than papering over, so
    /// it defaults to <see langword="false"/>.
    /// </para>
    /// <para>
    /// This flag never applies to a write-guard refusal. A stage refused because the server is read-only
    /// aborts the ladder regardless of this value — escalating past a guard's refusal would turn "you may
    /// not stop this politely" into "so kill it instead", which is the exact opposite of what the guard
    /// exists to enforce.
    /// </para>
    /// </remarks>
    public abstract bool ContinueOnError { get; init; }

    /// <summary>Attempt to stop by invoking an RCON command (e.g. "save" then "shutdown"), waiting up to <paramref name="Timeout"/>.</summary>
    /// <param name="CommandId">The declared command id to invoke, e.g. <c>shutdown</c>.</param>
    /// <param name="Timeout">Maximum time to wait for the stage to take effect.</param>
    /// <param name="Args">
    /// Arguments to render into the command's template, keyed by placeholder name — e.g. <c>seconds</c> and
    /// <c>message</c> for a definition whose <c>shutdown</c> template is
    /// <c>Shutdown {seconds} "{message}"</c>. Empty, never null, so a stage with no arguments still has
    /// something to hand a command-template renderer without a null check.
    /// </param>
    public sealed record Rcon(string CommandId, TimeSpan Timeout, IReadOnlyDictionary<string, string> Args) : StopStage
    {
        /// <summary>Attempt to stop by invoking an RCON command that takes no arguments.</summary>
        public Rcon(string CommandId, TimeSpan Timeout)
            : this(CommandId, Timeout, new Dictionary<string, string>(StringComparer.Ordinal))
        {
        }

        /// <inheritdoc />
        public override bool ContinueOnError { get; init; } = true;
    }

    /// <summary>Attempt to stop by writing a line to the workload's console/stdin, waiting up to <paramref name="Timeout"/>.</summary>
    public sealed record ConsoleWrite(string Text, TimeSpan Timeout) : StopStage
    {
        /// <inheritdoc />
        public override bool ContinueOnError { get; init; } = true;
    }

    /// <summary>Attempt to stop by sending an OS signal, waiting up to <paramref name="Timeout"/>.</summary>
    public sealed record Signal(string SignalName, TimeSpan Timeout) : StopStage
    {
        /// <inheritdoc />
        public override bool ContinueOnError { get; init; }
    }

    /// <summary>Forcibly terminate the workload. The final, unconditional stage.</summary>
    public sealed record Kill : StopStage
    {
        /// <inheritdoc />
        /// <remarks>Always <see langword="false"/>: there is no stage after this one to escalate to.</remarks>
        public override bool ContinueOnError { get; init; }
    }
}

/// <summary>An ordered escalation ladder: e.g. rcon → console → signal → kill. Each stage's timeout must elapse before the next is attempted.</summary>
/// <param name="Stages">The stages, in escalation order.</param>
public sealed record StopPlan(IReadOnlyList<StopStage> Stages);

/// <summary>Records which stage of a <see cref="StopPlan"/> actually stopped the server.</summary>
/// <param name="StageThatStopped">The stage that succeeded in stopping the workload.</param>
/// <param name="TotalDuration">Total wall-clock time from the first stage to the workload stopping.</param>
public sealed record StopOutcome(StopStage StageThatStopped, TimeSpan TotalDuration);
