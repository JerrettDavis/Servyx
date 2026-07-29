using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process.Tests;

/// <summary>
/// Builds <see cref="CommandSpec"/>s for the few tests that must genuinely start a process, using only an
/// interpreter the current OS ships with.
/// </summary>
/// <remarks>
/// <para>
/// No test in this assembly depends on a game server, a package manager, or any binary Servyx would install.
/// The two interpreters used here — <c>/bin/sh</c> on Unix, <c>powershell.exe</c> on Windows — are both
/// invoked in a form that passes the test's arguments through <em>as argv</em> rather than as script text:
/// <c>sh -c &lt;fixed script&gt; sh arg…</c> puts the arguments in <c>$@</c>, and <c>powershell -File
/// script.ps1 arg…</c> puts them in <c>$args</c>. The script text itself is always a constant in this file, so
/// nothing a test passes as an argument can ever become script.
/// </para>
/// <para>
/// If neither interpreter is present, <see cref="UnavailableReason"/> is non-null and the affected tests skip
/// with that reason rather than failing.
/// </para>
/// </remarks>
internal static class TestScripts
{
    /// <summary>Prints each argument on its own line.</summary>
    internal const string EchoArgumentsUnix = "printf '%s\\n' \"$@\"";

    /// <summary>Prints each argument on its own line.</summary>
    internal const string EchoArgumentsWindows = "foreach ($a in $args) { Write-Output $a }";

    private static string WindowsPowerShellPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    /// <summary>
    /// Why this machine cannot run a scripted test, or <see langword="null"/> when it can.
    /// </summary>
    internal static string? UnavailableReason
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return File.Exists(WindowsPowerShellPath)
                    ? null
                    : $"Windows PowerShell was not found at '{WindowsPowerShellPath}', so no argv-transparent interpreter is available.";
            }

            return File.Exists("/bin/sh") ? null : "/bin/sh was not found, so no argv-transparent interpreter is available.";
        }
    }

    /// <summary>
    /// Builds a command that runs <paramref name="unixScript"/> or <paramref name="windowsScript"/> with
    /// <paramref name="arguments"/> supplied as argv.
    /// </summary>
    /// <param name="scratchDirectory">
    /// Where a temporary <c>.ps1</c> may be written on Windows (PowerShell binds trailing arguments to
    /// <c>$args</c> only for <c>-File</c>, not for <c>-Command</c>, so a file is required there).
    /// </param>
    /// <param name="unixScript">The <c>sh</c> script text. A constant, never caller data.</param>
    /// <param name="windowsScript">The PowerShell script text. A constant, never caller data.</param>
    /// <param name="arguments">Arguments to pass through as argv.</param>
    internal static CommandSpec Build(string scratchDirectory, string unixScript, string windowsScript, params string[] arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(scratchDirectory, $"script-{Guid.NewGuid():N}.ps1");
            Directory.CreateDirectory(scratchDirectory);
            File.WriteAllText(scriptPath, windowsScript);

            string[] prefix = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath];
            return new CommandSpec(WindowsPowerShellPath, [.. prefix, .. arguments]);
        }

        // "sh" is $0; everything after it lands in "$@".
        string[] unixPrefix = ["-c", unixScript, "sh"];
        return new CommandSpec("/bin/sh", [.. unixPrefix, .. arguments]);
    }

    /// <summary>A command that echoes <paramref name="arguments"/> back, one per line.</summary>
    internal static CommandSpec EchoArguments(string scratchDirectory, params string[] arguments) =>
        Build(scratchDirectory, EchoArgumentsUnix, EchoArgumentsWindows, arguments);
}
