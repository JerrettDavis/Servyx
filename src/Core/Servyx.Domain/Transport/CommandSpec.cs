namespace Servyx.Domain.Transport;

/// <summary>
/// Whether a <see cref="CommandSpec"/> is declared to change the state of the target it runs against.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the exec channel's equivalent of a definition's <c>readOnly</c> flag.</strong> A command
/// cannot be classified by verb — <c>docker exec</c> is the same API call whether it runs <c>Info</c> or
/// <c>Shutdown</c>, and <c>tar</c> archives or lists depending on one argument — so Servyx classifies by
/// <em>declared intent</em>. <c>IRconSession.InvokeAsync</c> gets that declaration from the definition's
/// command catalogue; <see cref="IExecutionTarget.ExecuteAsync"/> gets it from here, because the caller
/// building the argv is the only party that knows what the argv does.
/// </para>
/// <para>
/// <strong><see cref="Mutating"/> is deliberately the zero value, so it is what omission means.</strong> A
/// caller who does not think about intent gets the answer that is safe to be wrong about: the command is
/// refused on a read-only server rather than run against it. The opposite default would make every future
/// adapter's silence a hole, which is exactly the failure this enum exists to close.
/// </para>
/// </remarks>
public enum CommandIntent
{
    /// <summary>
    /// The command may change the target's state, so it requires <see cref="WriteMode.Enabled"/>. The
    /// default: an undeclared command is treated as mutating rather than trusted to be harmless.
    /// </summary>
    Mutating = 0,

    /// <summary>
    /// The caller declares this command only observes the target. Permitted in every
    /// <see cref="WriteMode"/>, which is what lets read-only control and readiness probes reach live state on
    /// a <see cref="WriteMode.ReadOnly"/> server.
    /// </summary>
    ReadOnly = 1,
}

/// <summary>
/// A command to execute on a target. <see cref="Executable"/> never contains arguments;
/// <see cref="Arguments"/> are passed verbatim to the target process with no shell expansion, globbing,
/// or redirection — remote transports (e.g. SSH) are responsible for quoting each argument individually
/// rather than joining them into a shell line. This is the primary defence against command injection
/// driven by game/definition authors.
/// </summary>
/// <param name="Executable">The program or entrypoint to invoke.</param>
/// <param name="Arguments">Argv array, passed through verbatim.</param>
/// <param name="WorkingDirectory">Optional working directory on the target.</param>
/// <param name="EnvironmentOverrides">Optional environment variable overrides.</param>
/// <param name="Timeout">Optional execution timeout.</param>
/// <param name="Intent">
/// Whether this command is declared to change the target. Defaults to <see cref="CommandIntent.Mutating"/>,
/// so <see cref="WriteGuardedExecutionTarget"/> refuses it on a server whose <see cref="WriteMode"/> is not
/// <see cref="WriteMode.Enabled"/> unless the caller says otherwise. See <see cref="CommandIntent"/> for why
/// the default points that way.
/// </param>
public sealed record CommandSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides = null,
    TimeSpan? Timeout = null,
    CommandIntent Intent = CommandIntent.Mutating);

/// <summary>Result of a completed, non-streaming command execution.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error.</param>
/// <param name="Duration">Wall-clock duration of the execution.</param>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration)
{
    /// <summary>True when <see cref="ExitCode"/> is zero.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Identifies which stream an <see cref="OutputChunk"/> came from.</summary>
public enum OutputStream
{
    /// <summary>Standard output.</summary>
    StdOut,

    /// <summary>Standard error.</summary>
    StdErr,
}

/// <summary>A single chunk of streamed command output.</summary>
/// <param name="Stream">Which stream this chunk came from.</param>
/// <param name="Text">The chunk's text content.</param>
/// <param name="Timestamp">When the chunk was observed.</param>
public sealed record OutputChunk(OutputStream Stream, string Text, DateTimeOffset Timestamp);
