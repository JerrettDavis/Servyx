using System.Text.RegularExpressions;

namespace Servyx.Domain.Lifecycle;

/// <summary>
/// Readiness detector based on matching a regex against console output.
/// </summary>
/// <remarks>
/// <para>
/// Game definitions supply the pattern, and definitions are untrusted input, so the pattern is compiled
/// with <see cref="RegexOptions.NonBacktracking"/> whenever possible: that engine matches in time linear
/// in the input length, so a malicious or accidental catastrophic-backtracking pattern cannot become a
/// ReDoS vector against the panel host, regardless of what log output it is run against.
/// </para>
/// <para>
/// <see cref="RegexOptions.NonBacktracking"/> does not support backreferences or lookaround assertions.
/// When the supplied pattern uses either (construction throws <see cref="NotSupportedException"/>), this
/// type falls back to <see cref="RegexOptions.Compiled"/> with an explicit per-match timeout. That
/// fallback is a real, accepted tradeoff, not a safe equivalent: a backtracking engine can still be driven
/// into exponential-time behaviour by an adversarial log line, and the match timeout only bounds the
/// damage to a single match attempt (it throws <see cref="RegexMatchTimeoutException"/>, which is treated
/// as "this line didn't match" so the scan continues) rather than eliminating the algorithmic risk. Prefer
/// patterns that avoid backreferences/lookarounds so the linear-time engine can be used.
/// </para>
/// </remarks>
public sealed class LogRegexReadiness : IReadinessDetector
{
    /// <summary>Number of most-recent log lines retained for diagnostics on timeout.</summary>
    public const int RecentLinesCapacity = 50;

    private static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromSeconds(1);

    private readonly ILogLineSource _logSource;
    private readonly Regex _pattern;
    private readonly string _detectorId;

    /// <summary>Creates a detector that watches <paramref name="logSource"/> for <paramref name="pattern"/>.</summary>
    /// <param name="logSource">Where to read log lines from.</param>
    /// <param name="pattern">
    /// The ready-pattern, e.g. Palworld's
    /// <c>Running Palworld dedicated server on \[?[0-9A-Fa-f:.]+\]?:(?&lt;port&gt;\d+)</c>. Named capture
    /// groups are exposed on the resulting <see cref="ReadinessSignal.CapturedGroups"/>.
    /// </param>
    /// <param name="detectorId">Identifier recorded on produced signals. Defaults to <c>"log-regex"</c>.</param>
    /// <param name="matchTimeout">
    /// Per-match evaluation timeout, applied to whichever engine ends up being used (see remarks on this
    /// type for the <see cref="RegexOptions.NonBacktracking"/>/<see cref="RegexOptions.Compiled"/> choice).
    /// Defaults to one second.
    /// </param>
    public LogRegexReadiness(ILogLineSource logSource, string pattern, string detectorId = "log-regex", TimeSpan? matchTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(logSource);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(detectorId);

        _logSource = logSource;
        _detectorId = detectorId;
        _pattern = CompilePattern(pattern, matchTimeout ?? DefaultMatchTimeout);
    }

    private static Regex CompilePattern(string pattern, TimeSpan matchTimeout)
    {
        try
        {
            return new Regex(pattern, RegexOptions.NonBacktracking, matchTimeout);
        }
        catch (NotSupportedException)
        {
            // Pattern uses a construct the linear-time engine can't run (backreference/lookaround).
            // See the tradeoff documented on this type.
            return new Regex(pattern, RegexOptions.Compiled, matchTimeout);
        }
    }

    /// <inheritdoc />
    public async Task<ReadinessSignal> WaitForReadyAsync(ReadinessContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var timeoutCts = new CancellationTokenSource(context.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var recentLines = new Queue<string>(RecentLinesCapacity + 1);

        try
        {
            await foreach (var line in _logSource.TailAsync(context.ServerId, linkedCts.Token).ConfigureAwait(false))
            {
                recentLines.Enqueue(line);
                while (recentLines.Count > RecentLinesCapacity)
                {
                    recentLines.Dequeue();
                }

                Match match;
                try
                {
                    match = _pattern.Match(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    // A single adversarial line failed to evaluate within the per-match timeout.
                    // Treat as "no match on this line" and keep scanning subsequent lines.
                    continue;
                }

                if (match.Success)
                {
                    return new ReadinessSignal(
                        Ready: true,
                        DetectorId: _detectorId,
                        Detail: $"matched log line: \"{line}\"",
                        CapturedGroups: ExtractNamedGroups(match));
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the per-attempt timeout elapsed; the caller's own token was not cancelled.
            return TimedOutSignal(context, recentLines);
        }

        // The log stream ended (source exhausted) without a match, and the caller wasn't cancelled.
        return TimedOutSignal(context, recentLines);
    }

    private ReadinessSignal TimedOutSignal(ReadinessContext context, Queue<string> recentLines)
        => new(
            Ready: false,
            DetectorId: _detectorId,
            Detail: $"no match for the ready pattern within {context.Timeout}",
            RecentLogLines: recentLines.ToArray());

    private static IReadOnlyDictionary<string, string>? ExtractNamedGroups(Match match)
    {
        Dictionary<string, string>? named = null;
        foreach (Group group in match.Groups)
        {
            // Unnamed groups are auto-named with their numeric index by the regex engine; only
            // explicitly named groups (e.g. (?<port>...)) have a non-numeric name.
            if (!group.Success || int.TryParse(group.Name, out _))
            {
                continue;
            }

            named ??= [];
            named[group.Name] = group.Value;
        }

        return named;
    }
}
