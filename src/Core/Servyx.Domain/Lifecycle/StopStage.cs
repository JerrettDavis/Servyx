namespace Servyx.Domain.Lifecycle;

/// <summary>A single stage in a <see cref="StopPlan"/> escalation ladder.</summary>
public abstract record StopStage
{
    private StopStage()
    {
    }

    /// <summary>Attempt to stop by invoking an RCON command (e.g. "save" then "shutdown"), waiting up to <paramref name="Timeout"/>.</summary>
    public sealed record Rcon(string CommandId, TimeSpan Timeout) : StopStage;

    /// <summary>Attempt to stop by writing a line to the workload's console/stdin, waiting up to <paramref name="Timeout"/>.</summary>
    public sealed record ConsoleWrite(string Text, TimeSpan Timeout) : StopStage;

    /// <summary>Attempt to stop by sending an OS signal, waiting up to <paramref name="Timeout"/>.</summary>
    public sealed record Signal(string SignalName, TimeSpan Timeout) : StopStage;

    /// <summary>Forcibly terminate the workload. The final, unconditional stage.</summary>
    public sealed record Kill : StopStage;
}

/// <summary>An ordered escalation ladder: e.g. rcon → console → signal → kill. Each stage's timeout must elapse before the next is attempted.</summary>
/// <param name="Stages">The stages, in escalation order.</param>
public sealed record StopPlan(IReadOnlyList<StopStage> Stages);

/// <summary>Records which stage of a <see cref="StopPlan"/> actually stopped the server.</summary>
/// <param name="StageThatStopped">The stage that succeeded in stopping the workload.</param>
/// <param name="TotalDuration">Total wall-clock time from the first stage to the workload stopping.</param>
public sealed record StopOutcome(StopStage StageThatStopped, TimeSpan TotalDuration);
