namespace Servyx.Domain.Lifecycle;

/// <summary>
/// The parsed shape of a game definition's <c>lifecycle</c> block: an ordered stop-escalation ladder and
/// the probes used to detect readiness after a start.
/// </summary>
/// <remarks>
/// This is pure data — no <see cref="ILogLineSource"/>, no <see cref="IReadinessProbeChannel"/>, no
/// transport wired in. Turning <see cref="Ready"/> into a running <see cref="IReadinessDetector"/>
/// (e.g. via <see cref="CompositeReadinessDetector"/> over a <see cref="LogRegexReadiness"/> and a
/// <see cref="ControlProbeReadiness"/>) and <see cref="Stop"/> into calls against a real control channel is
/// the composition root's job, not this parser's.
/// </remarks>
/// <param name="Ready">Readiness probes, in the order the definition declares them.</param>
/// <param name="Stop">
/// The stop-escalation ladder. <see cref="StopStage"/> is the domain's own type — this parser produces it
/// directly rather than an intermediate DTO, so there is exactly one shape for "an ordered stop ladder" in
/// the codebase.
/// </param>
/// <param name="CrashDetection">Crash-detection rules, in the order the definition declares them.</param>
/// <param name="HealthSignal">
/// The definition's <c>lifecycle.healthSignal</c> block, if it declares one — how much Servyx should trust
/// the workload's own container-level health signal (e.g. Docker <c>HEALTHCHECK</c>), and what to tell an
/// operator when that signal disagrees with what Servyx itself observes. Optional and defaults to
/// <see langword="null"/>, both because most definitions have no reason to declare it (a trustworthy health
/// signal needs no explanation) and so every pre-existing 3-argument <c>new LifecycleDefinition(...)</c>
/// call site — hand-built fixtures included — keeps compiling unchanged.
/// </param>
public sealed record LifecycleDefinition(
    IReadOnlyList<ReadinessProbeDefinition> Ready,
    StopPlan Stop,
    IReadOnlyList<CrashDetectionRule> CrashDetection,
    HealthSignalDefinition? HealthSignal = null);

/// <summary>How much Servyx should trust a workload's own container-level health signal (e.g. Docker <c>HEALTHCHECK</c>).</summary>
public enum HealthSignalTrust
{
    /// <summary>The signal is reliable; Servyx has no reason to override or explain it.</summary>
    Trust,

    /// <summary>
    /// The signal is known-unreliable for this workload; a consumer should surface
    /// <see cref="HealthSignalDefinition.Explanation"/> to an operator rather than taking the raw signal at
    /// face value.
    /// </summary>
    Ignore,
}

/// <summary>
/// One definition's <c>lifecycle.healthSignal</c> block: whether its workload's own container-level health
/// signal can be trusted, and — when it cannot — the human-readable reason to show an operator. Reused
/// verbatim wherever a discovered server's health is reported (e.g. <c>ServerQueryService</c>), the same way
/// <see cref="ReadinessProbeDefinition"/> and <see cref="CrashDetectionRule"/> are: pure data here, with no
/// opinion about how a caller renders or gates on it.
/// </summary>
/// <param name="Trust">
/// Whether the raw signal is trustworthy at face value (<see cref="HealthSignalTrust.Trust"/>) or known to
/// misreport for this workload (<see cref="HealthSignalTrust.Ignore"/>).
/// </param>
/// <param name="Explanation">
/// The text to show an operator when the workload reports unhealthy, explaining why that reading should not
/// be taken at face value. Meaningful only alongside <see cref="HealthSignalTrust.Ignore"/> — the parser
/// does not require it even then, so a definition author who omits it simply gets a null explanation rather
/// than a rejected document.
/// </param>
public sealed record HealthSignalDefinition(HealthSignalTrust Trust, string? Explanation);

/// <summary>
/// One entry of a definition's <c>lifecycle.ready</c> list: how to detect that a just-started server has
/// become ready, before any <see cref="IReadinessDetector"/> exists to run the check.
/// </summary>
public abstract record ReadinessProbeDefinition
{
    private ReadinessProbeDefinition()
    {
    }

    /// <summary>
    /// A <c>kind: log-regex</c> probe: watch console output for <paramref name="Pattern"/>. Maps onto
    /// <see cref="LogRegexReadiness"/>'s constructor arguments once an <see cref="ILogLineSource"/> exists.
    /// </summary>
    /// <param name="Pattern">The ready-pattern regex, e.g. Palworld's listening-port line.</param>
    /// <param name="Timeout">Maximum time to wait for a match.</param>
    public sealed record LogRegex(string Pattern, TimeSpan Timeout) : ReadinessProbeDefinition;

    /// <summary>
    /// A <c>kind: control-probe</c> probe: poll a control channel's command and match its response. Maps
    /// onto <see cref="ControlProbeReadiness"/>'s constructor arguments once an
    /// <see cref="IReadinessProbeChannel"/> exists for <paramref name="Channel"/>.
    /// </summary>
    /// <param name="Channel">The control channel id to probe, e.g. <c>rcon</c>.</param>
    /// <param name="Command">The declared command id to invoke on that channel, e.g. <c>info</c>.</param>
    /// <param name="Expect">Regex the command's response must match for the server to be considered ready.</param>
    /// <param name="Interval">Delay between successive probe attempts.</param>
    /// <param name="Timeout">Maximum time to wait for a matching response.</param>
    public sealed record ControlProbe(
        string Channel,
        string Command,
        string Expect,
        TimeSpan Interval,
        TimeSpan Timeout) : ReadinessProbeDefinition;
}

/// <summary>
/// One entry of a definition's <c>lifecycle.crashDetection</c> list: a console-output pattern that, when
/// matched, should be acted on (e.g. marking the server crashed rather than cleanly stopped).
/// </summary>
/// <param name="Pattern">The regex to match against console output.</param>
/// <param name="Action">The definition's declared action id, e.g. <c>mark-crashed</c>. Opaque to the parser;
/// interpreting it is the composition root's job.</param>
public sealed record CrashDetectionRule(string Pattern, string Action);
