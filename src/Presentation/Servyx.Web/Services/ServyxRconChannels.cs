using System.Collections.Concurrent;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon;

namespace Servyx.Web.Services;

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
/// <strong>Sessions are cheap and cached only for identity.</strong> <see cref="RconSession"/> holds no
/// socket: <see cref="SourceRconClient"/> connects, authenticates, sends and closes per command. Caching
/// here avoids re-deriving the guard on every call, not a connection.
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
    private readonly IRconAuditSink? _audit;
    private readonly ConcurrentDictionary<string, IRconSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the channel set.</summary>
    /// <param name="options">The configured channels.</param>
    /// <param name="catalog">The definition's declared control commands.</param>
    /// <param name="client">The protocol client, or null when no channel is configured.</param>
    /// <param name="secrets">The secret store credentials are resolved through, or null when no channel is configured.</param>
    /// <param name="writable">The operator's per-server write grants, which gate mutating control commands.</param>
    /// <param name="audit">The sink raw, catalogue-bypassing commands are recorded to.</param>
    /// <exception cref="ArgumentException">
    /// A channel is configured but <paramref name="catalog"/> does not declare the definition's quiesce
    /// command. Loud at composition time, because the alternative is discovering it during the first backup
    /// of a running server — which is precisely the moment a quiesce matters.
    /// </exception>
    public ServyxRconChannels(
        RconWiringOptions options,
        RconCommandCatalog catalog,
        IRconClient? client,
        ISecretStore? secrets,
        WritableServers writable,
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
        }

        _options = options;
        _catalog = catalog;
        _client = client;
        _secrets = secrets;
        _writable = writable;
        _audit = audit;
    }

    /// <summary>The configured channels.</summary>
    public RconWiringOptions Options => _options;

    /// <summary>The definition's declared control commands.</summary>
    public RconCommandCatalog Catalog => _catalog;

    /// <summary>
    /// Returns the write-guarded control session for a server, or <see langword="null"/> when the operator
    /// configured no RCON channel for it.
    /// </summary>
    /// <param name="serverId">The server's discovery id.</param>
    /// <param name="serverName">The server's container name, if known.</param>
    public IRconSession? TryGetSession(string? serverId, string? serverName = null)
    {
        if (_options.Find(serverId, serverName) is not { } channel || _client is null || _secrets is null)
        {
            return null;
        }

        return _sessions.GetOrAdd(channel.ServerKey, _ => Build(channel));
    }

    private IRconSession Build(RconChannel channel)
    {
        var inner = new RconSession(
            _client!,
            channel.Endpoint,
            _catalog,
            _secrets!,
            channel.PasswordUrn,
            _audit);

        // Derived from the same Servyx:Servers:<name>:WriteMode configuration the transport's write grants
        // are, so the control channel and the filesystem cannot disagree about whether a server is writable.
        var mode = _writable.IsWritable(channel.ServerKey) ? WriteMode.Enabled : WriteMode.ReadOnly;

        return new WriteGuardedRconSession(inner, _catalog, mode, channel.ServerKey);
    }
}
