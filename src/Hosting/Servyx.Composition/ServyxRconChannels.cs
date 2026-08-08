using System.Collections.Concurrent;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon;

namespace Servyx.Composition;

/// <summary>
/// Turns a server id into the write-guarded <see cref="IRconSession"/> the rest of the host uses as its
/// control channel.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the composition-root half of RCON</strong>, and it is the piece only this layer can
/// supply: a session needs an endpoint (host configuration), a credential URN (host configuration), the
/// definition's command catalogue (a parsed definition) and a write posture (the operator's per-server
/// grant). The protocol assembly deliberately composes none of that for itself.
/// </para>
/// <para>
/// <strong>Every session is write-guarded.</strong> The inner <see cref="RconSession"/> is never handed out
/// bare — it is always wrapped in <see cref="WriteGuardedRconSession"/> with the same
/// <see cref="WriteMode"/> the operator granted the server's transport, so a server without
/// <c>Servyx:Servers:&lt;name&gt;:WriteMode = Enabled</c> can run <c>info</c> and <c>players</c> and is
/// refused <c>save</c>, <c>broadcast</c> and <c>shutdown</c>. A read-only server therefore cannot be
/// quiesced, which means it cannot produce a quiesced backup either — the refusal surfaces as a failed
/// backup rather than as a silently un-flushed archive.
/// </para>
/// <para>
/// <strong>Sessions are memoized per channel, once acquisition succeeds.</strong> Acquiring one means running
/// the composition root's <see cref="RconReachabilityChain"/> — probing <c>direct-tcp</c>, then (when a
/// remote host is configured) actually reaching the adopted Palworld container's unpublished RCON port via
/// <c>docker exec rcon-cli</c> — so, unlike the pre-reachability-chain shape, the first call per channel does
/// real I/O. <see cref="GetSessionAsync"/> caches the resulting <see cref="Task{TResult}"/> so repeated calls
/// do not re-run the chain, and evicts a failed attempt instead of caching it forever, so the next call
/// retries rather than replaying a stale failure.
/// </para>
/// </remarks>
public sealed class ServyxRconChannels
{
    /// <summary>No server has a control channel. What a read-only host composes.</summary>
    public static readonly ServyxRconChannels None = new(
        RconWiringOptions.Disabled,
        RconCommandCatalog.Empty,
        client: null,
        secrets: null,
        WritableServers.None);

    private readonly RconWiringOptions _options;
    private readonly RconCommandCatalog _catalog;
    private readonly IRconClient? _client;
    private readonly ISecretStore? _secrets;
    private readonly WritableServers _writable;
    private readonly Func<RconChannel, RconReachabilityChain>? _chainFactory;
    private readonly ConcurrentDictionary<string, Lazy<Task<IRconSession>>> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the channel set.</summary>
    /// <param name="options">The configured channels.</param>
    /// <param name="catalog">The definition's declared control commands.</param>
    /// <param name="client">The protocol client, or null when no channel is configured.</param>
    /// <param name="secrets">The secret store credentials are resolved through, or null when no channel is configured.</param>
    /// <param name="writable">The operator's per-server write grants, which gate mutating control commands.</param>
    /// <param name="chainFactory">
    /// Builds the ordered <see cref="RconReachabilityChain"/> a channel's session is acquired through, given
    /// that channel. Supplied by the composition root — see <c>Program.cs</c>'s RCON block — because it is
    /// the only layer that knows the definition's declared strategy order and, for
    /// <c>docker-exec-tool</c>, which <see cref="Servyx.Domain.Transport.IExecutionTarget"/> and container
    /// name to run it against. Required whenever <paramref name="options"/> configures at least one channel.
    /// </param>
    /// <param name="audit">
    /// Unused by this type since <paramref name="chainFactory"/> took over session construction: each
    /// reachability strategy's own <see cref="RconSession"/> (or equivalent) now carries its own audit sink,
    /// supplied by the composition root at the point it builds the chain. Kept as a constructor parameter
    /// only for call-site source compatibility with the composition root's existing <c>audit: null</c> call.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A channel is configured but <paramref name="catalog"/> does not declare the definition's quiesce
    /// command, or no <paramref name="chainFactory"/> was supplied. Loud at composition time, because the
    /// alternative is discovering it during the first backup of a running server — which is precisely the
    /// moment a quiesce matters.
    /// </exception>
    public ServyxRconChannels(
        RconWiringOptions options,
        RconCommandCatalog catalog,
        IRconClient? client,
        ISecretStore? secrets,
        WritableServers writable,
        Func<RconChannel, RconReachabilityChain>? chainFactory = null,
        IRconAuditSink? audit = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(writable);

        if (options.Any)
        {
            if (client is null || secrets is null)
            {
                throw new ArgumentException(
                    "An RCON control channel is configured, so a protocol client and a secret store are both "
                    + "required. A channel with no way to authenticate is not a degraded channel; it is one that "
                    + "would fail on first use.",
                    nameof(client));
            }

            if (!catalog.Contains(RconWiringOptions.QuiesceCommandId))
            {
                throw new ArgumentException(
                    $"An RCON control channel is configured but the definition's command catalogue declares no "
                    + $"'{RconWiringOptions.QuiesceCommandId}' command, so the backup quiesce step could never be "
                    + "issued. Refusing at startup rather than at the first backup of a running server.",
                    nameof(catalog));
            }

            if (chainFactory is null)
            {
                throw new ArgumentException(
                    "An RCON control channel is configured, so a reachability chain factory is required. A "
                    + "channel with no way to compose a reachability chain would fail on first use.",
                    nameof(chainFactory));
            }
        }

        _options = options;
        _catalog = catalog;
        _client = client;
        _secrets = secrets;
        _writable = writable;
        _chainFactory = chainFactory;
    }

    /// <summary>The configured channels.</summary>
    public RconWiringOptions Options => _options;

    /// <summary>The definition's declared control commands.</summary>
    public RconCommandCatalog Catalog => _catalog;

    /// <summary>
    /// Returns the write-guarded control session for a server, or <see langword="null"/> when the operator
    /// configured no RCON channel for it.
    /// </summary>
    /// <remarks>
    /// Async because acquiring a session now means running <see cref="RconReachabilityChain.AcquireAsync"/>
    /// — direct TCP is tried first, then (when a remote host is configured) <c>docker exec rcon-cli</c>,
    /// which really does reach the adopted Palworld container's unpublished RCON port. See
    /// <c>Program.cs</c>'s RCON block for how the chain is composed.
    /// </remarks>
    /// <param name="serverId">The server's discovery id.</param>
    /// <param name="serverName">The server's container name, if known.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="RconUnreachableException">
    /// No strategy in the chain could reach the endpoint. The message names every strategy tried and why.
    /// </exception>
    public async Task<IRconSession?> GetSessionAsync(string? serverId, string? serverName = null, CancellationToken ct = default)
    {
        if (_options.Find(serverId, serverName) is not { } channel || _client is null || _secrets is null || _chainFactory is null)
        {
            return null;
        }

        var lazy = _sessions.GetOrAdd(channel.ServerKey, _ => new Lazy<Task<IRconSession>>(() => BuildAsync(channel, ct)));

        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            // Improves on ServyxBackupContextSource.SessionAsync's Lazy<Task<T>> memoization, which caches a
            // faulted task forever once the factory has run once: a Lazy<T> only re-invokes its factory when
            // the factory delegate itself throws synchronously, and here the factory returns a Task rather
            // than throwing, so the faulted Task would otherwise be replayed to every future caller even
            // after e.g. the container starts or the network heals. Evicting exactly the failed entry — never
            // a fresher, successful one a concurrent caller may have already installed, hence the atomic
            // key+value removal — means a failed acquisition is retried on the next call instead of
            // permanently poisoning the channel.
            ((ICollection<KeyValuePair<string, Lazy<Task<IRconSession>>>>)_sessions).Remove(
                new KeyValuePair<string, Lazy<Task<IRconSession>>>(channel.ServerKey, lazy));
            throw;
        }
    }

    private async Task<IRconSession> BuildAsync(RconChannel channel, CancellationToken ct)
    {
        var chain = _chainFactory!(channel);
        var inner = await chain.AcquireAsync(channel.Endpoint, ct).ConfigureAwait(false);

        // Derived from the same Servyx:Servers:<name>:WriteMode configuration the transport's write grants
        // are, so the control channel and the filesystem cannot disagree about whether a server is writable.
        var mode = _writable.IsWritable(channel.ServerKey) ? WriteMode.Enabled : WriteMode.ReadOnly;

        return new WriteGuardedRconSession(inner, _catalog, mode, channel.ServerKey);
    }
}
