using System.Runtime.CompilerServices;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;

namespace Servyx.Application.Lifecycle;

/// <summary>
/// Adapts an <see cref="ILogStream"/> into the <see cref="ILogLineSource"/> shape <see cref="LogRegexReadiness"/>
/// needs, by repeatedly re-polling <see cref="ILogStream.FollowAsync"/> instead of assuming it is a live,
/// never-completing stream.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why polling, not a single <c>await foreach</c>.</strong> <see cref="ILogStream.FollowAsync"/>'s
/// contract says it "replays tail backscroll, then follows new output", and most implementations honor
/// that as a genuinely live stream. But <c>SshDockerLogStream.FollowAsync</c> (the ssh+docker transport)
/// does not: per its own remarks, <c>docker logs</c> run over an SSH exec channel has no <c>--follow</c>
/// mode, so it replays the requested backlog once and completes — it is a one-shot tail, not a live
/// follow. A readiness detector built directly on <c>await foreach (var line in FollowAsync(...))</c>
/// would silently stop watching for the ready pattern the instant that one-shot enumeration ended, long
/// before the server actually finished starting. This type papers over that gap: when an underlying
/// <c>FollowAsync</c> call completes, it is simply called again after <see cref="PollInterval"/>, turning
/// a one-shot replay into an effectively-live tail. Against a transport whose <c>FollowAsync</c> truly
/// never completes (e.g. the local Docker Engine API path), the outer loop below never reaches its second
/// iteration, so this adapter behaves as a thin passthrough there.
/// </para>
/// <para>
/// <strong>Re-polling replays some already-seen lines, and that is intentional.</strong> Rather than
/// trust <see cref="ConsoleLine.Offset"/> to stay comparable across independent polls — it does not, for
/// <c>SshDockerLogStream</c>, whose offsets restart from zero on every <c>docker logs --tail</c> call —
/// this type yields every line from every poll's tail, unfiltered. That is safe because the only consumer
/// today is <see cref="LogRegexReadiness"/>, which scans lines looking for the first match and then stops:
/// re-scanning a handful of already-seen non-matching lines on each poll is wasted CPU, not a correctness
/// problem. A future consumer that treats every yielded line as a brand-new, one-time event would need a
/// different (offset- or content-de-duplicated) adapter.
/// </para>
/// </remarks>
public sealed class PollingLogLineSource : ILogLineSource
{
    /// <summary>Default delay between re-polls once an underlying <c>FollowAsync</c> call completes.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    private const int DefaultMaxBacklogLines = 200;

    private readonly ILogStream _logStream;
    private readonly int _maxBacklogLines;

    /// <summary>Creates an adapter over <paramref name="logStream"/>.</summary>
    /// <param name="logStream">The log stream to poll.</param>
    /// <param name="pollInterval">Delay between re-polls. Defaults to <see cref="DefaultPollInterval"/>.</param>
    /// <param name="maxBacklogLines">Backlog size requested on each poll. Defaults to 200.</param>
    public PollingLogLineSource(ILogStream logStream, TimeSpan? pollInterval = null, int maxBacklogLines = DefaultMaxBacklogLines)
    {
        ArgumentNullException.ThrowIfNull(logStream);

        _logStream = logStream;
        PollInterval = pollInterval ?? DefaultPollInterval;
        _maxBacklogLines = maxBacklogLines > 0 ? maxBacklogLines : DefaultMaxBacklogLines;
    }

    /// <summary>Delay between re-polls once an underlying <c>FollowAsync</c> call completes.</summary>
    public TimeSpan PollInterval { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> TailAsync(string serverId, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            await foreach (var line in _logStream.FollowAsync(serverId, new ConsoleTailOptions(_maxBacklogLines), ct).ConfigureAwait(false))
            {
                yield return line.Text;
            }

            // The underlying FollowAsync call completed -- either it was a one-shot tail replay (see type
            // remarks) or the source genuinely ran dry. Either way, wait and try again rather than treating
            // "no more lines right now" as "never any more lines".
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }
}
