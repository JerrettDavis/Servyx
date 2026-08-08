using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Servyx.Domain.Lifecycle;

namespace Servyx.Mcp;

/// <summary>
/// Computes a stable hash over a <see cref="StopPlan"/> (and the server it governs) for the D3
/// plan→apply protocol: a client previews a stop plan, gets its hash back, and must present that same
/// hash when applying — a hash that no longer matches means the plan drifted since it was previewed, and
/// the apply is refused with <c>plan-hash-mismatch</c> rather than applied against stale intent.
/// </summary>
internal static class StopPlanHash
{
    /// <summary>
    /// Computes the plan hash. Deterministic across processes and machines: every numeric component is
    /// formatted with <see cref="CultureInfo.InvariantCulture"/>, since <c>TimeSpan.TotalSeconds.ToString("0.###")</c>
    /// under a comma-decimal culture would silently change the hash — passing on a dev box, failing in CI.
    /// </summary>
    /// <param name="serverId">The server this plan governs. Included so a hash minted for one server's plan can never validate another's, even if the two plans' stages are otherwise identical.</param>
    /// <param name="plan">The plan to hash.</param>
    public static string Compute(string serverId, StopPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sb = new StringBuilder();
        sb.Append("server=").Append(serverId).Append(';');

        foreach (var stage in plan.Stages)
        {
            sb.Append(Component(stage)).Append('|');
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Renders one <see cref="StopStage"/> case to its hash component. Every case contributes a distinct
    /// tag plus every field that changes what the stage actually does, including <see cref="StopStage.ContinueOnError"/>
    /// — two plans differing only in whether a stage escalates on failure are different plans.
    /// </summary>
    private static string Component(StopStage stage) => stage switch
    {
        StopStage.Rcon rcon =>
            $"rcon:cmd={rcon.CommandId}:timeout={Seconds(rcon.Timeout)}:continue={rcon.ContinueOnError}:args={FormatArgs(rcon.Args)}",

        StopStage.ConsoleWrite console =>
            $"console-write:text={console.Text}:timeout={Seconds(console.Timeout)}:continue={console.ContinueOnError}",

        StopStage.Signal signal =>
            $"signal:name={signal.SignalName}:timeout={Seconds(signal.Timeout)}:continue={signal.ContinueOnError}",

        StopStage.Kill kill =>
            $"kill:continue={kill.ContinueOnError}",

        _ => throw new NotSupportedException(
            $"Unrecognized {nameof(StopStage)} case '{stage.GetType().Name}'; {nameof(StopPlanHash)} must be " +
            "updated to give it its own distinct hash component."),
    };

    /// <summary>Formats a <see cref="TimeSpan"/> as seconds, invariant-culture, so the hash never depends on the host's number formatting.</summary>
    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Formats a stage's argument dictionary in a stable (key-sorted, ordinal) order so hash equality does not depend on enumeration order.</summary>
    private static string FormatArgs(IReadOnlyDictionary<string, string> args) =>
        string.Join(',', args.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
}
