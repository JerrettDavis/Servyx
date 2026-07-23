namespace Servyx.Domain.Transport;

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
public sealed record CommandSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides = null,
    TimeSpan? Timeout = null);

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
