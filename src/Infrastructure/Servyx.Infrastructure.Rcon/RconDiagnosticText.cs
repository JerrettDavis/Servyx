namespace Servyx.Infrastructure.Rcon;

/// <summary>
/// Bounds how much captured text (stderr, exception type names, ...) a reachability strategy may fold into
/// <see cref="Servyx.Domain.Rcon.IRconReachability.LastUnavailableReason"/>.
/// </summary>
/// <remarks>
/// A reachability probe's own commands never carry a credential — see the remarks on
/// <see cref="Servyx.Domain.Rcon.IRconReachability.LastUnavailableReason"/> — but captured process output is
/// still attacker- or environment-controlled text of unbounded length, and an unbounded reason string is its
/// own small denial-of-service against whatever eventually logs or displays
/// <see cref="RconUnreachableException"/>. Truncating here, once, keeps every call site honest
/// without each of them re-deriving the same bound.
/// </remarks>
internal static class RconDiagnosticText
{
    /// <summary>The maximum number of characters a captured diagnostic snippet may contribute.</summary>
    internal const int MaxLength = 200;

    /// <summary>
    /// Collapses <paramref name="text"/> to a single line and truncates it to <see cref="MaxLength"/>
    /// characters, appending an ellipsis when it was cut short.
    /// </summary>
    internal static string Truncate(string text, int maxLength = MaxLength)
    {
        var collapsed = text.Trim();

        if (collapsed.Length <= maxLength)
        {
            return collapsed;
        }

        return string.Concat(collapsed.AsSpan(0, maxLength), "...");
    }
}
