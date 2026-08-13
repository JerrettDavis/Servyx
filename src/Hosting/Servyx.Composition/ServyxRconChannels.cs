using System.Collections.Concurrent;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon;
using Servyx.Infrastructure.Ssh.Docker;

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
/// <strong>Every session is write-guarded, against a live posture.</strong> The inner
/// <see cref="RconSession"/> is never handed out bare — it is always wrapped in
/// <see cref="WriteGuardedRconSession"/> reading the same per-server grant the server's transport resolves
/// through, so a server the operator has not granted <c>WriteMode.Enabled</c> can run <c>info</c> and
/// <c>players</c> and is refused <c>save</c>, <c>broadcast</c> and <c>shutdown</c>. A read-only server
/// therefore cannot be quiesced, which means it cannot produce a quiesced backup either — the refusal
/// surfaces as a failed backup rather than as a silently un-flushed archive. Because the posture is read per
/// command rather than captured when the session was acquired, revoking a grant is honoured on the next
/// control command even on a session that is already open and memoized.
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
/// <para>
/// <strong>A server with no static <c>Servyx:Servers:&lt;container&gt;:Rcon:*</c> entry is not necessarily
/// out of options.</strong> <see cref="RconWiringOptions"/> only ever describes channels an operator declared
/// in configuration — an adopted-through-the-UI server on a registered/configured ssh+docker host has no such
/// entry and never will, the same gap <see cref="Servyx.Infrastructure.Ssh.Docker.HostAwareLogStream"/> and
/// <see cref="Servyx.Infrastructure.Ssh.Docker.HostAwareMetricsSource"/> closed for console reads and metrics.
/// <see cref="GetSessionAsync"/> falls back to <see cref="TryDeriveAdoptedChannelAsync"/> when
/// <see cref="RconWiringOptions.Find"/> misses: it probes every currently-connectable
/// registered/configured host (via <see cref="Servyx.Infrastructure.Ssh.Docker.IHostConnectionSource"/>, the
/// same read-only <c>docker container inspect</c> probe those two types use) for a container named
/// <c>serverId</c>, and — only on a match — synthesizes a minimal <see cref="RconChannel"/> keyed off that
/// container identity, carrying the matched host's key. A server matched by no host (including a genuinely
/// unknown id, and every plain local-only server, which this type deliberately does not try to serve this
/// way — see <see cref="BuildAsync"/>) gets no channel, exactly as before.
/// </para>
/// <para>
/// <strong>A derived channel is reachable only through <c>docker-exec-tool</c>, and knows it does not really
/// need <c>direct-tcp</c>.</strong> <see cref="DockerExecToolRconReachability"/> runs <c>rcon-cli</c> inside
/// the container via <c>docker exec</c> over the matched host's own <see cref="IExecutionTarget"/> — it
/// never resolves <see cref="RconChannel.PasswordUrn"/> or connects to <see cref="RconChannel.Endpoint"/> at
/// all, since the container's own <c>rcon-cli</c> already knows its RCON password from its environment. Both
/// fields are still populated with a syntactically valid placeholder (loopback address, the definition's
/// default RCON port, a URN built the same way <see cref="RconWiringOptions.FromConfiguration"/> builds one
/// for a statically-configured channel) purely so the record's invariants hold and <c>direct-tcp</c> — which
/// IS still tried first in the composed chain, in case the adopted image happens to publish the port after
/// all — has an endpoint to probe. If it ever answers and a command is actually sent that way with no secret
/// stored at that URN, <see cref="ISecretStore"/> reports "not found" like it would for any other unresolved
/// URN; nothing here manufactures a credential.
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
    private readonly IHostConnectionSource? _hostConnections;
    private readonly IServerExecutionTargetResolver? _executionTargetResolver;
    private readonly PlayerListPlan? _players;
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
    /// <param name="hostConnections">
    /// The live registered/configured ssh+docker host set a server with no static Rcon configuration is
    /// probed against, to derive a channel for it rather than reporting none — see this type's own remarks.
    /// <see langword="null"/> (the default) disables derivation entirely: <see cref="GetSessionAsync"/> then
    /// answers <see langword="null"/> for any server <paramref name="options"/> does not name, exactly as
    /// this type always has.
    /// </param>
    /// <param name="executionTargetResolver">
    /// Resolves a derived channel's matched host key to the connected
    /// <see cref="Servyx.Domain.Transport.IExecutionTarget"/> its <c>docker-exec-tool</c> strategy runs over.
    /// Required together with <paramref name="hostConnections"/> — supplying one without the other is refused
    /// at construction, since a host that can be found but never connected to (or vice versa) is not a usable
    /// derivation path.
    /// </param>
    /// <param name="players">
    /// Which command a derived channel's session's <c>GetPlayersAsync</c> invokes and how to read its reply —
    /// the same plan the composition root's own <c>chainFactory</c> closure resolves for a statically
    /// configured channel. Defaults to <see cref="PlayerListPlan.None"/> when omitted.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A channel is configured but <paramref name="catalog"/> does not declare the definition's quiesce
    /// command, or no <paramref name="chainFactory"/> was supplied. Loud at composition time, because the
    /// alternative is discovering it during the first backup of a running server — which is precisely the
    /// moment a quiesce matters. Also thrown when exactly one of <paramref name="hostConnections"/> and
    /// <paramref name="executionTargetResolver"/> was supplied.
    /// </exception>
    public ServyxRconChannels(
        RconWiringOptions options,
        RconCommandCatalog catalog,
        IRconClient? client,
        ISecretStore? secrets,
        WritableServers writable,
        Func<RconChannel, RconReachabilityChain>? chainFactory = null,
        IRconAuditSink? audit = null,
        IHostConnectionSource? hostConnections = null,
        IServerExecutionTargetResolver? executionTargetResolver = null,
        PlayerListPlan? players = null)
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

        if (hostConnections is null != executionTargetResolver is null)
        {
            throw new ArgumentException(
                "A host-connection source and an execution-target resolver for deriving a channel on an adopted "
                + "server must be supplied together or not at all — a way to find the host with nothing to "
                + "connect through it (or vice versa) is not a usable derivation path.",
                nameof(executionTargetResolver));
        }

        _options = options;
        _catalog = catalog;
        _client = client;
        _secrets = secrets;
        _writable = writable;
        _chainFactory = chainFactory;
        _hostConnections = hostConnections;
        _executionTargetResolver = executionTargetResolver;
        _players = players;
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
        if (_client is null || _secrets is null)
        {
            return null;
        }

        // A statically configured channel (RconWiringOptions.Find) is reached through _chainFactory below; a
        // channel this type derives for an adopted server with no static config (TryDeriveAdoptedChannelAsync)
        // never touches _chainFactory at all — see BuildAsync — so it is deliberately not checked here.
        var channel = _options.Find(serverId, serverName)
            ?? await TryDeriveAdoptedChannelAsync(serverId, ct).ConfigureAwait(false);

        if (channel is null)
        {
            return null;
        }

        var lazy = _sessions.GetOrAdd(
            channel.ServerKey,
            _ => new Lazy<Task<IRconSession>>(() => BuildAsync(channel, serverId, serverName, ct)));

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

    /// <remarks>
    /// <para>
    /// <strong>The write posture is handed to the guard as a live source, never as a value.</strong> This is
    /// the second, entirely independent place a server's posture is captured — the first being
    /// <c>WriteGuardedTransport.ConnectAsync</c> on the exec path — and the two fail independently, so fixing
    /// only one would leave mutating RCON commands (<c>save</c>, <c>broadcast</c>, <c>shutdown</c>) flowing
    /// on a grant the operator already revoked. Sessions here are memoized per channel for the life of the
    /// process and are never evicted once acquisition succeeds, so a value baked in at this point would
    /// outlive any number of grant changes. Re-reading per command is what lets those caches stay exactly as
    /// they are.
    /// </para>
    /// <para>
    /// The posture is read against the caller's own <paramref name="serverId"/>/<paramref name="serverName"/>
    /// rather than <c>channel.ServerKey</c>, because the channel key is the operator's configuration spelling
    /// (a container name) while a grant is keyed on the container's durable id — see
    /// <see cref="WritableServers"/>. Resolving against the configuration key would have made every RCON
    /// grant miss.
    /// </para>
    /// </remarks>
    private async Task<IRconSession> BuildAsync(
        RconChannel channel,
        string? serverId,
        string? serverName,
        CancellationToken ct)
    {
        // A derived channel (HostKey set) never goes through the composition-supplied chainFactory: that
        // closure's own containerName/executionTarget are scoped to the single statically-declared
        // ssh+docker host (Program.cs's RCON block), which is not necessarily — and for an adopted server
        // discovered on a database-registered host, is usually NOT — the host this channel was derived
        // against. Building the chain here instead, over the host TryDeriveAdoptedChannelAsync actually
        // matched, is what makes an adopted server's own host reachable regardless of whether any host was
        // ever statically declared.
        var chain = channel.HostKey is { } hostKey
            ? await BuildDerivedChainAsync(channel, hostKey, ct).ConfigureAwait(false)
            : _chainFactory!(channel);

        var inner = await chain.AcquireAsync(channel.Endpoint, ct).ConfigureAwait(false);

        // Reads the same live grant view the transport's write guard resolves through, so the control channel
        // and the filesystem cannot disagree about whether a server is writable — at any moment, not just at
        // the moment this session happened to be acquired.
        return new WriteGuardedRconSession(
            inner,
            _catalog,
            () => _writable.Mode(serverId, serverName),
            channel.ServerKey);
    }

    /// <summary>
    /// Builds the reachability chain for a channel <see cref="TryDeriveAdoptedChannelAsync"/> derived: the
    /// same declared strategy order <see cref="RconReachabilityChainFactory.Build"/> composes for a static
    /// channel, but with <c>containerName</c>/<c>executionTarget</c> resolved against
    /// <paramref name="hostKey"/> — the host this specific channel was actually matched on — rather than the
    /// single statically-declared host the composition root's <c>chainFactory</c> closure is scoped to.
    /// </summary>
    private async Task<RconReachabilityChain> BuildDerivedChainAsync(RconChannel channel, string hostKey, CancellationToken ct)
    {
        var target = await _executionTargetResolver!.ResolveAsync(channel.ServerKey, hostKey, ct).ConfigureAwait(false);

        return RconReachabilityChainFactory.Build(
            channel, _client!, _catalog, _secrets!, channel.ServerKey, target, _players);
    }

    /// <summary>
    /// Probes every currently-connectable registered/configured host for a container matching
    /// <paramref name="serverId"/> and, on a match, synthesizes a minimal <see cref="RconChannel"/> for it —
    /// see this type's own remarks for why that channel is safe to build without any static Rcon
    /// configuration. Returns <see langword="null"/> without probing anything when this instance was
    /// constructed with no <see cref="IHostConnectionSource"/> (derivation disabled), when
    /// <paramref name="serverId"/> is empty, or when it is not a legal <see cref="SecretUrn"/> segment — the
    /// same "cannot address a secret, cannot have a channel" refusal
    /// <see cref="RconWiringOptions.FromConfiguration"/> applies to a static entry.
    /// </summary>
    private async Task<RconChannel?> TryDeriveAdoptedChannelAsync(string? serverId, CancellationToken ct)
    {
        if (_hostConnections is null || string.IsNullOrWhiteSpace(serverId) || !SecretUrn.IsValidSegment(serverId))
        {
            return null;
        }

        var hosts = await _hostConnections.GetConnectionsAsync(ct).ConfigureAwait(false);
        if (hosts.Count == 0)
        {
            return null;
        }

        var probes = await Task.WhenAll(hosts.Select(host => ProbeAsync(host, serverId, ct))).ConfigureAwait(false);
        var hostKey = probes.FirstOrDefault(probe => probe.Found).HostKey;

        if (hostKey is null)
        {
            return null;
        }

        return new RconChannel(
            serverId,
            new RconEndpoint(RconWiringOptions.DefaultHost, RconWiringOptions.DefaultPort),
            SecretUrn.Create(RconWiringOptions.SecretScope, serverId, RconWiringOptions.SecretCategory, RconWiringOptions.SecretName),
            HostKey: hostKey);
    }

    /// <summary>
    /// Read-only existence check, mirroring <c>HostAwareLogStream.ProbeAsync</c>/<c>HostAwareMetricsSource.ProbeAsync</c>
    /// exactly: a host that throws while being probed (unreachable, mid-registration, etc.) is treated as "not
    /// found there" rather than aborting the search for a good one.
    /// </summary>
    private static async Task<(string? HostKey, bool Found)> ProbeAsync(HostConnection host, string serverId, CancellationToken ct)
    {
        try
        {
            var result = await host.ExecutionTarget.ExecuteAsync(DockerCli.Inspect(serverId), ct).ConfigureAwait(false);
            return (host.HostKey, result.Succeeded);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, false);
        }
    }
}
