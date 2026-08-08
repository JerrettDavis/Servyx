using Servyx.Domain.Transport;

namespace Servyx.Mcp;

/// <summary>
/// The standard shape for the <c>refused-write-guard</c> outcome every mutating tool's result union
/// carries — built by <see cref="ToolGuard.Refuse"/> so no two tools invent slightly different wording
/// for the same refusal.
/// </summary>
/// <param name="WriteMode">The write posture actually held for the target, lower-kebab (e.g. <c>read-only</c>).</param>
/// <param name="Message">The write guard's own message, crossed verbatim — see <see cref="WritesDisabledException.Message"/>.</param>
/// <param name="Remediation">
/// What an operator must do to unlock the write this call attempted: <b>both</b>
/// <c>Servyx:Provisioning:Enabled=true</c> (the gate) <b>and</b> <c>Servyx:Servers:&lt;key&gt;:WriteMode=Enabled</c>
/// (the grant) — a grant without the gate does nothing — and a statement that no I/O was performed.
/// </param>
public sealed record WriteGuardRefusal(string WriteMode, string Message, string Remediation)
{
    /// <summary>The outcome discriminant every refusal built by <see cref="ToolGuard.Refuse"/> carries.</summary>
    public string Outcome => "refused-write-guard";
}

/// <summary>
/// A shared helper every mutating tool routes through, so the mapping from
/// <see cref="WritesDisabledException"/> to a named, non-erroring MCP outcome cannot be forgotten or
/// reworded independently by each tool. Individual tools are never trusted to catch this exception
/// themselves — the same "structural, not conventional" principle <see cref="WriteGuardedExecutionTarget"/>
/// itself follows for the write check one layer down.
/// </summary>
internal static class ToolGuard
{
    /// <summary>
    /// Runs <paramref name="operation"/>. If it throws <see cref="WritesDisabledException"/> — the write
    /// guard's structural refusal, never a bug — <paramref name="onRefused"/> maps it to a normal
    /// (non-erroring) result instead of letting the exception propagate. Every other exception propagates
    /// unchanged, so the MCP SDK marks the call's <c>CallToolResult.IsError</c> — an unexpected failure is
    /// still a failure, not a refusal.
    /// </summary>
    /// <typeparam name="T">The tool's own result union type.</typeparam>
    /// <param name="operation">The mutating call to attempt.</param>
    /// <param name="onRefused">Maps a caught refusal to this tool's own <c>refused-write-guard</c> case.</param>
    internal static async Task<T> RunAsync<T>(Func<Task<T>> operation, Func<WritesDisabledException, T> onRefused)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (WritesDisabledException ex)
        {
            return onRefused(ex);
        }
    }

    /// <summary>
    /// Builds the standard <see cref="WriteGuardRefusal"/> shape for a caught <see cref="WritesDisabledException"/>.
    /// </summary>
    /// <param name="writeMode">The write posture actually held for the target that refused.</param>
    /// <param name="serverKey">The server key an operator would grant writes to, named in the remediation text.</param>
    /// <param name="ex">The caught refusal.</param>
    internal static WriteGuardRefusal Refuse(WriteMode writeMode, string serverKey, WritesDisabledException ex) =>
        new(
            WriteMode: KebabCase.From(writeMode.ToString()),
            Message: ex.Message,
            Remediation:
                "No I/O was performed — the guard refuses before the inner session, secret store, or socket "
                + $"are touched. To permit this write, set BOTH Servyx:Provisioning:Enabled=true AND "
                + $"Servyx:Servers:{serverKey}:WriteMode=Enabled; a grant without the gate does nothing.");
}
