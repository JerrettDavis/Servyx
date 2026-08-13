using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Servyx.Application.Servers;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Backups;
using Servyx.Infrastructure.Ssh.Docker;
using GameDefinition = Servyx.Domain.Definitions.Model.GameDefinition;

namespace Servyx.Composition;

/// <summary>
/// Turns a server id into the <see cref="SshBackupContext"/> <c>SshBackupProvider</c> needs: a connected
/// host, the root its backup paths are relative to, what to capture, and where Servyx writes its own
/// archives.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the piece only the composition root can supply</strong> — see
/// <c>AddServyxSshBackups</c>'s remarks. Turning a server id into a machine, a data root and a capture set
/// is host knowledge, and this source refuses to invent any of it: everything comes from
/// <see cref="SshBackupWiringOptions"/>, and a server the operator did not configure is a hard failure
/// rather than a guess. It mirrors <see cref="ServyxBackupContextSource"/>, the Docker equivalent, member
/// for member.
/// </para>
/// <para>
/// <strong>Sessions are cached and owned here.</strong> The provider never disposes an
/// <see cref="IExecutionTarget"/> it is handed, per <see cref="ISshBackupContextSource"/>'s contract, so one
/// session per server is created on first use and disposed when this service is. An SSH connection is far
/// more expensive to open than a Docker one, which makes the caching matter more here, not less.
/// </para>
/// <para>
/// <strong>Quiesce is attached exactly when a control channel exists, and never otherwise.</strong> Same
/// convention as Docker's: the presence of an RCON channel for this server (see
/// <see cref="RconWiringOptions"/>) is the operator's opt-in, and it fills both
/// <see cref="SshBackupContext.Control"/> and <see cref="SshBackupContext.Quiesce"/> or neither. A context
/// naming a quiesce step with no channel to issue it on is refused outright by
/// <c>SshBackupProvider.CreateAsync</c>, and a configured quiesce that fails produces no archive at all —
/// there is deliberately no "archive anyway" path, because an un-flushed archive is indistinguishable from
/// a good one until the day someone restores it.
/// </para>
/// <para>
/// <strong>A declared foreign directory names a location; it does not by itself surface anything.</strong>
/// <see cref="SshBackupContext.Foreign"/> is populated from <see cref="SshBackupServer.ForeignDirectory"/>
/// when the operator names one (e.g. the host's own cron writing archives into <c>backups/</c> under
/// <see cref="SshBackupServer.Root"/>), but <c>SshBackupProvider.ListResolvedAsync</c> only ever surfaces
/// what a registered <c>IBackupAdopter</c> discovers <em>inside</em> a declared directory, and
/// <c>AddServyxSshBackups()</c> registers none — a generic SSH host ships no convention Servyx could adopt
/// on its own say-so. A host that knows its own layout registers its own adopter; the directory declared
/// here is what that adopter would be told to look inside.
/// </para>
/// <para>
/// <strong>A server the operator never statically configured is not automatically refused any more.</strong>
/// When <see cref="SshBackupWiringOptions.Find"/> misses, <see cref="GetAsync"/> falls back to
/// <see cref="FromAdoptedAsync"/>: a server with a non-null <c>ServerSummary.HostKey</c> — one discovered on
/// a registered/configured ssh+docker host through the UI, with zero <c>Servyx:Servers:&lt;name&gt;:Ssh:*</c>
/// ever declared — gets a context built from what is already known about it, the same "derive one instead of
/// requiring static config" shape <c>ServyxRconChannels.TryDeriveAdoptedChannelAsync</c> established for RCON.
/// The execution target is resolved through <see cref="IServerExecutionTargetResolver"/> (the same shared,
/// already-connected host session <see cref="HostAwareLogStream"/>/<see cref="HostAwareMetricsSource"/> use),
/// never through <see cref="_transport"/> — that field stays scoped to the static path's own
/// <c>SshTransport</c>+<c>WriteGuardedTransport</c>, which knows nothing about a host it was never told about
/// and would otherwise need a second, redundant SSH connection to the same machine
/// <see cref="IHostConnectionSource"/> already holds open. Because the resolved target is not itself
/// write-guarded (unlike a session opened through <see cref="_transport"/>), it is wrapped in
/// <see cref="WriteGuardedExecutionTarget"/> over the SAME container-id-keyed, database-backed grant
/// <see cref="ServyxRconChannels"/>'s derived RCON sessions already read — see
/// <see cref="ContainerGrantWriteModeResolver"/> — so an adopted server's writes obey the exact same operator
/// grant everything else about it does, never an ungated execution target.
/// </para>
/// </remarks>
public sealed class ServyxSshBackupContextSource : ISshBackupContextSource, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// The <see cref="ForeignSshBackupDirectory.AdapterId"/> a directory declared through
    /// <see cref="SshBackupServer.ForeignDirectory"/> is tagged with. A fixed, generic id rather than one
    /// per host: this composition root registers no <see cref="IBackupAdopter"/> of its own (see this
    /// class's remarks), so nothing today matches against it — it exists so a future, host-specific adopter
    /// has a stable id to filter on.
    /// </summary>
    public const string ForeignAdapterId = "ssh-configured";

    private readonly SshBackupWiringOptions _options;
    private readonly ITransport? _transport;
    private readonly ServyxRconChannels _rcon;
    private readonly IServerQueryService? _query;
    private readonly IServerExecutionTargetResolver? _executionTargetResolver;
    private readonly WritableServers? _writable;
    private readonly GameDefinition? _definition;
    private readonly ConcurrentDictionary<string, Lazy<Task<IExecutionTarget>>> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a context source.</summary>
    /// <param name="options">The SSH-hosted servers the operator configured.</param>
    /// <param name="transport">
    /// The (write-guarded) SSH transport <em>statically</em> configured sessions are opened through — see
    /// this type's remarks for why a server adopted through the UI never reaches this parameter at all.
    /// Supplied explicitly rather than resolved from dependency injection because this process also registers
    /// a Docker <see cref="ITransport"/>, and a single-service injection would resolve to whichever was
    /// registered last. <see langword="null"/> when this process has no statically-configured SSH-hosted
    /// server at all (<see cref="SshBackupWiringOptions.Any"/> is <see langword="false"/>) — safe because
    /// <see cref="SshBackupWiringOptions.Find"/> then never matches anything for <see cref="SessionAsync"/> to
    /// be reached through.
    /// </param>
    /// <param name="rcon">
    /// The configured RCON control channels. Defaults to <see cref="ServyxRconChannels.None"/>, which yields
    /// no control channel, therefore no quiesce step, therefore an archive of on-disk state that says so in
    /// its manifest.
    /// </param>
    /// <param name="query">
    /// Resolves a server id to its host identity and mount information for the adopted-server fallback (see
    /// this type's remarks). <see langword="null"/> disables that fallback entirely: a server not found in
    /// <paramref name="options"/> is then refused exactly as it always was.
    /// </param>
    /// <param name="executionTargetResolver">
    /// Resolves an adopted server's registered host to its already-connected execution target. Required
    /// alongside <paramref name="query"/> and <paramref name="writable"/> for the adopted-server fallback.
    /// </param>
    /// <param name="writable">
    /// The live, database-backed write grant view an adopted server's resolved execution target is guarded
    /// by — see <see cref="ContainerGrantWriteModeResolver"/>. Required alongside <paramref name="query"/>
    /// and <paramref name="executionTargetResolver"/> for the adopted-server fallback.
    /// </param>
    /// <param name="definition">
    /// The single loaded game definition an adopted server's capture set (<c>backup.include</c>/
    /// <c>backup.exclude</c>) is read from. <see langword="null"/> (no definition loaded, or more than one)
    /// means an adopted server has no capture set and <see cref="FromAdoptedAsync"/> refuses it loudly.
    /// </param>
    public ServyxSshBackupContextSource(
        SshBackupWiringOptions options,
        ITransport? transport,
        ServyxRconChannels? rcon = null,
        IServerQueryService? query = null,
        IServerExecutionTargetResolver? executionTargetResolver = null,
        WritableServers? writable = null,
        GameDefinition? definition = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _transport = transport;
        _rcon = rcon ?? ServyxRconChannels.None;
        _query = query;
        _executionTargetResolver = executionTargetResolver;
        _writable = writable;
        _definition = definition;
    }

    /// <inheritdoc />
    public async Task<SshBackupContext> GetAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var server = _options.Find(serverId);
        if (server is null)
        {
            return await FromAdoptedAsync(serverId, ct).ConfigureAwait(false);
        }

        var target = await SessionAsync(server, ct).ConfigureAwait(false);

        // Null unless the operator configured an RCON channel for this server. The pair below is all-or-
        // nothing on purpose: a context carrying a quiesce step with no channel is refused by the provider,
        // and a context carrying a channel with no step would open a control session it never used.
        var control = await _rcon.GetSessionAsync(server.ServerKey, ct: ct).ConfigureAwait(false);

        return new SshBackupContext(
            ServerId: server.ServerKey,
            DeploymentKind: server.DeploymentKind,
            Target: target,
            Root: server.Root,
            Include: server.Include,
            Exclude: server.Exclude,
            StoreDirectory: server.StoreDirectory,

            // Populated only when the operator named a directory — see SshBackupWiringOptions.SshBackupServer's
            // ForeignDirectory remarks. Still inert on its own: SshBackupProvider only ever surfaces what a
            // registered IBackupAdopter discovers inside a declared directory, and AddServyxSshBackups()
            // registers none for the reason its own remarks give (a generic SSH host ships no convention to
            // discover). Declaring the directory here is what a host-specific adopter would need to exist at
            // all; it does not by itself make anything appear as Foreign.
            Foreign: server.ForeignDirectory is null
                ? []
                : [new ForeignSshBackupDirectory(ForeignAdapterId, server.ForeignDirectory, server.ForeignPattern)],
            DefaultRetention: server.DefaultRetention,
            Quiesce: control is null
                ? null
                : new QuiesceStep(RconWiringOptions.QuiesceCommandId, null, RconWiringOptions.QuiesceTimeout),
            Control: control);
    }

    /// <summary>
    /// Builds a context for a server <see cref="SshBackupWiringOptions.Find"/> did not match, by resolving
    /// what an SSH+docker adoption already knows about it — its registered host and its container's host-side
    /// bind mount — rather than requiring the operator to hand-declare a second time what discovery already
    /// found once. See this type's remarks for why the target comes from
    /// <see cref="IServerExecutionTargetResolver"/> rather than <see cref="_transport"/>, and why it is
    /// wrapped in a write guard here rather than trusted to already carry one.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="serverId"/> is not a server this process can resolve at all (unknown to
    /// <c>IServerQueryService</c>, or resolvable but with no <c>HostKey</c> — a local/non-SSH server, which is
    /// Docker's job, not this type's); the adopted-server fallback was not wired at all (any of
    /// <c>query</c>/<c>executionTargetResolver</c>/<c>writable</c> is <see langword="null"/>); the matched
    /// host reports no host-side mount path for the container's data volume; or no game definition is loaded
    /// to source a capture set from.
    /// </exception>
    private async Task<SshBackupContext> FromAdoptedAsync(string serverId, CancellationToken ct)
    {
        if (_query is null || _executionTargetResolver is null || _writable is null)
        {
            throw NotConfigured(serverId);
        }

        var detail = await _query.GetServerDetailAsync(serverId, ct).ConfigureAwait(false);
        if (detail?.Summary.HostKey is not { } hostKey)
        {
            // Either genuinely unknown, or a local/non-SSH server — HostKey is null for exactly that case
            // (see ServerSummary's remarks) and Docker's own ServyxBackupContextSource is what owns it, not
            // this type. Refusing with the same message a hand-configured miss gets keeps both refusals
            // indistinguishable to a caller that only cares "is this server backed up over SSH or not".
            throw NotConfigured(serverId);
        }

        var root = Normalize(detail.MountHostPath
            ?? throw new InvalidOperationException(
                $"Cannot back up '{serverId}': it is adopted on registered host '{hostKey}', but no host-side "
                + "mount path is known for its data volume, so there is no directory on that host to archive. "
                + "This container must declare a bind-mounted (not a named-volume) data directory before it "
                + "can be backed up over SSH."));

        var backup = _definition?.Backup;
        var include = DataRelative(serverId, "Include", backup?.Include ?? []);
        if (include.Count == 0)
        {
            throw new InvalidOperationException(
                $"No backup capture set is configured for '{serverId}': no game definition is loaded, so "
                + "there is no 'backup.include' to capture. Load a game definition before backing up this "
                + "server.");
        }

        var exclude = DataRelative(serverId, "Exclude", backup?.Exclude ?? []);
        var containerName = detail.Summary.Name;

        var inner = await _executionTargetResolver.ResolveAsync(serverId, hostKey, ct).ConfigureAwait(false);

        // Not write-guarded by construction — see this type's remarks — so it is wrapped here, over the same
        // container-id-keyed database grant ServyxRconChannels' own derived (adopted) sessions read.
        var target = new WriteGuardedExecutionTarget(
            inner,
            new ContainerGrantWriteModeResolver(_writable, serverId),
            new TargetDescriptor(SshBackupWiringOptions.TransportId, hostKey, null, null, new Dictionary<string, string>(StringComparer.Ordinal)),
            containerName);

        // Null unless the operator configured an RCON channel for this server (or ServyxRconChannels derived
        // one — see its own remarks). Same all-or-nothing pairing as the static path above.
        var control = await _rcon.GetSessionAsync(serverId, containerName, ct).ConfigureAwait(false);

        return new SshBackupContext(
            ServerId: serverId,
            DeploymentKind: SshBackupWiringOptions.DefaultDeploymentKind,
            Target: target,
            Root: root,
            Include: include,
            Exclude: exclude,
            StoreDirectory: SshBackupWiringOptions.DefaultStoreDirectory,

            // No adopter ships for a generic SSH host regardless of how the context was built — see this
            // class's own remarks on ForeignAdapterId — and an adopted server has no ForeignDirectory an
            // operator could have named either, so there is nothing honest to declare here.
            Foreign: [],
            DefaultRetention: BackupWiringOptions.FallbackRetention,
            Quiesce: control is null
                ? null
                : new QuiesceStep(RconWiringOptions.QuiesceCommandId, null, RconWiringOptions.QuiesceTimeout),
            Control: control);
    }

    private static InvalidOperationException NotConfigured(string serverId) => new(
        $"'{serverId}' is not configured as an SSH-hosted server, so there is nothing to back up. Add "
        + $"'{SshBackupWiringOptions.SectionKey}:{serverId}:{SshBackupWiringOptions.SshKey}:Enabled', ':Host' "
        + "and ':Root', or adopt it on a registered ssh+docker host so its capture set can be derived instead.");

    /// <summary>
    /// Matches a leading <c>${DATA_DIR}/</c> or <c>${COMPOSE_DIR}/</c> root variable, the same way
    /// <c>Servyx.Composition.ServyxBackupContextSource.SplitRoot</c> does — see that type's remarks for why
    /// both are recognised (a definition that will not parse against either variable must fail loudly, never
    /// silently drop the entry) even though only the former is ever captured here.
    /// </summary>
    private static readonly Regex RootVariablePrefix =
        new(@"^\$\{(DATA_DIR|COMPOSE_DIR)\}/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Reduces a definition-declared <c>backup.include</c>/<c>backup.exclude</c> glob list to its
    /// <c>${DATA_DIR}</c>-relative entries — the only half an adopted server's host-side bind mount can serve,
    /// since there is no configured host-side compose directory for a server nobody declared in
    /// <c>Servyx:Backups:ComposeDirectory</c> terms. A <c>${COMPOSE_DIR}</c>-relative entry is therefore
    /// dropped here, quietly and deliberately — the same "declared but not yet operative" gap
    /// <c>ServyxBackupContextSource</c>'s own remarks describe for an unconfigured compose directory, not a
    /// new one. An entry naming neither root variable is never dropped silently: it fails loudly, because that
    /// is a definition authoring error this deployment cannot safely guess past.
    /// </summary>
    private static IReadOnlyList<string> DataRelative(string serverId, string blockName, IReadOnlyList<string> raw)
    {
        var result = new List<string>();
        foreach (var entry in raw)
        {
            var match = RootVariablePrefix.Match(entry);
            if (!match.Success)
            {
                throw new InvalidOperationException(
                    $"'{serverId}''s game definition declares a 'backup.{blockName.ToLowerInvariant()}' entry "
                    + $"rooted at a variable this deployment does not model — only '${{DATA_DIR}}' and "
                    + $"'${{COMPOSE_DIR}}' are understood: '{entry}'. Refusing to silently drop it from the "
                    + "backup capture set.");
            }

            if (string.Equals(match.Groups[1].Value, "DATA_DIR", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(entry[match.Length..]);
            }
        }

        return result;
    }

    private static string Normalize(string root)
    {
        var normalized = root.Trim().Replace('\\', '/').TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    /// <summary>
    /// <see cref="IWriteModeResolver"/> over <see cref="WritableServers"/>'s container-id-keyed grant rather
    /// than the <see cref="TargetDescriptor"/>-keyed shape every other resolver in this process reads —
    /// exactly the same substitution <c>ServyxRconChannels.BuildAsync</c> makes for a derived RCON session
    /// (<c>() => _writable.Mode(serverId, serverName)</c>), because an adopted server's grant is a database
    /// row keyed on its container id, not on any <see cref="TargetDescriptor"/> a static SSH endpoint would
    /// carry. The descriptor <see cref="Resolve"/> receives is therefore ignored entirely; it exists only
    /// because <see cref="WriteGuardedExecutionTarget"/>'s resolver-backed constructor requires one to re-ask
    /// on every gated call.
    /// </summary>
    private sealed class ContainerGrantWriteModeResolver : IWriteModeResolver
    {
        private readonly WritableServers _writable;
        private readonly string _serverId;

        public ContainerGrantWriteModeResolver(WritableServers writable, string serverId)
        {
            _writable = writable;
            _serverId = serverId;
        }

        public WriteMode Resolve(TargetDescriptor target) => _writable.Mode(_serverId);
    }

    private Task<IExecutionTarget> SessionAsync(SshBackupServer server, CancellationToken ct)
    {
        // Reachable only through a server SshBackupWiringOptions.Find matched, which never happens when
        // _options is SshBackupWiringOptions.None — the only state in which this process would have been
        // constructed with a null transport. See the constructor's own remarks.
        var transport = _transport
            ?? throw new InvalidOperationException(
                $"'{server.ServerKey}' is statically configured as an SSH-hosted server, but this process was "
                + "constructed with no SSH transport to reach it through. This indicates a composition defect, "
                + "not a runtime condition an operator can hit.");

        var lazy = _sessions.GetOrAdd(
            server.ServerKey,
            _ => new Lazy<Task<IExecutionTarget>>(() => transport.ConnectAsync(
                new TargetDescriptor(
                    SshBackupWiringOptions.TransportId,

                    // The same string SshBackupWiringOptions.WriteGrants scopes this server's grant to, so a
                    // server the operator enabled writes on is the host this session is allowed to write to.
                    server.Endpoint,
                    server.CredentialUrn?.Value,
                    DockerContext: null,
                    new Dictionary<string, string>(StringComparer.Ordinal)),
                ct)));

        return lazy.Value;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _sessions.Values)
        {
            if (!lazy.IsValueCreated)
            {
                continue;
            }

            // Only a connect that has already finished can be drained. Awaiting one still in flight would
            // block disposal on however long the host takes to answer — and, if that connect is itself
            // stuck, forever. Disposal must always terminate; a session belonging to an unfinished connect
            // is released when its own transport is, which is the same guarantee it had before this type
            // existed.
            if (!lazy.Value.IsCompletedSuccessfully)
            {
                continue;
            }

            try
            {
                await lazy.Value.Result.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A session that failed to open, or that the host already closed, must not stop the
                // remaining sessions from being released during shutdown.
            }
        }

        _sessions.Clear();
    }

    /// <summary>
    /// Releases the same sessions <see cref="DisposeAsync"/> does, synchronously.
    /// </summary>
    /// <remarks>
    /// Implemented alongside <see cref="IAsyncDisposable"/> rather than instead of it because
    /// <c>ServiceProvider.Dispose()</c> — which every synchronously-disposed host and test harness calls —
    /// <em>throws</em> for a resolved singleton that implements only <see cref="IAsyncDisposable"/>. This
    /// service is registered in the composition root and resolved by hosts this project does not own, so it
    /// has to be disposable both ways. There is no sync-over-async hazard worth avoiding here: every task
    /// being awaited has already completed or is a session teardown, and disposal runs with no
    /// synchronization context.
    /// </remarks>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
