using System.Text;
using System.Text.RegularExpressions;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// Builds SSH <c>exec</c> command lines from a <see cref="Servyx.Domain.Transport.CommandSpec"/>'s argv
/// array, quoting every argument defensively so that it cannot be interpreted by the remote shell.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the single most important piece of code in this project.</b> The SSH <c>exec</c> channel
/// (<c>SSH_MSG_CHANNEL_REQUEST "exec"</c>) carries exactly one command-line string, which the remote
/// server always hands to a shell for interpretation — unlike a local <see cref="System.Diagnostics.Process"/>
/// launch, there is no argv-vector exec available over SSH. That means every individual argument in a
/// <see cref="Servyx.Domain.Transport.CommandSpec"/> must be escaped so that shell metacharacters inside it
/// (<c>;</c>, <c>`</c>, <c>$(...)</c>, quotes, newlines) are inert, rather than being concatenated into a
/// shell string with no quoting at all.
/// </para>
/// <para>
/// The technique is POSIX single-quote escaping: wrap the argument in <c>'</c>, and replace every literal
/// <c>'</c> inside it with <c>'\''</c> (close the quote, emit an escaped literal quote, reopen the quote).
/// Because nothing except a matching unescaped <c>'</c> ends a single-quoted string in POSIX shell syntax,
/// this is sufficient on its own to neutralize every other shell metacharacter — no separate case analysis
/// for <c>;</c>, backticks, <c>$(...)</c>, or embedded newlines is needed, because single quotes make all of
/// them literal.
/// </para>
/// </remarks>
public static class PosixArgv
{
    private static readonly Regex EnvironmentNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// Quotes a single argument using POSIX single-quote escaping, so that it is treated as one literal
    /// shell word regardless of its content.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="argument"/> is <see langword="null"/>.</exception>
    public static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        // Every existing single quote becomes: close-quote, escaped literal quote, reopen-quote.
        return "'" + argument.Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// Quotes a POSIX shell environment-variable assignment (<c>NAME=value</c>), prefixed to a command line
    /// to set an environment variable for that single invocation. Only <paramref name="value"/> is quoted —
    /// POSIX assignment syntax requires <paramref name="name"/> to be a bare, unquoted identifier, which is
    /// why <paramref name="name"/> is validated against a strict identifier charset rather than escaped.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is not a valid POSIX environment-variable identifier (letters, digits, and
    /// underscore only, not starting with a digit).
    /// </exception>
    public static string QuoteEnvironmentAssignment(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        if (!EnvironmentNamePattern.IsMatch(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a valid POSIX environment variable name (letters, digits, and underscore only, not starting with a digit).",
                nameof(name));
        }

        return name + "=" + QuoteArgument(value);
    }

    /// <summary>
    /// Builds a complete, safely-quoted command line for an SSH <c>exec</c> request from an executable,
    /// its arguments, an optional working directory, and optional environment overrides. Every argument —
    /// including <paramref name="executable"/> itself — is individually quoted via
    /// <see cref="QuoteArgument"/>; nothing here concatenates unescaped, caller-controlled text into the
    /// result.
    /// </summary>
    /// <param name="executable">The program to invoke. Quoted like any other argument.</param>
    /// <param name="arguments">The argv array, passed through verbatim (each entry individually quoted).</param>
    /// <param name="workingDirectory">
    /// If specified, the command is prefixed with <c>cd &lt;quoted-dir&gt; &amp;&amp; </c> so it runs with
    /// that working directory. The <c>&amp;&amp;</c> itself is shell syntax Servyx constructs, not
    /// caller-controlled text; the directory value is still quoted.
    /// </param>
    /// <param name="environmentOverrides">
    /// If specified, each entry is rendered as a quoted <c>NAME=value</c> assignment prefixed to the
    /// command, scoping the variable to this single invocation per POSIX assignment semantics.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="executable"/> or <paramref name="arguments"/> is <see langword="null"/>.</exception>
    public static string BuildCommandLine(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            sb.Append("cd ").Append(QuoteArgument(workingDirectory)).Append(" && ");
        }

        if (environmentOverrides is { Count: > 0 })
        {
            foreach (var (name, value) in environmentOverrides)
            {
                sb.Append(QuoteEnvironmentAssignment(name, value)).Append(' ');
            }
        }

        sb.Append(QuoteArgument(executable));

        foreach (var argument in arguments)
        {
            sb.Append(' ').Append(QuoteArgument(argument));
        }

        return sb.ToString();
    }
}
