using System.Collections.Concurrent;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Backups;

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
/// </remarks>
public sealed class ServyxSshBackupContextSource : ISshBackupContextSource, IAsyncDisposable
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
    private readonly ITransport _transport;
    private readonly ServyxRconChannels _rcon;
    private readonly ConcurrentDictionary<string, Lazy<Task<IExecutionTarget>>> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a context source.</summary>
    /// <param name="options">The SSH-hosted servers the operator configured.</param>
    /// <param name="transport">
    /// The (write-guarded) SSH transport sessions are opened through. Supplied explicitly rather than
    /// resolved from dependency injection because this process also registers a Docker
    /// <see cref="ITransport"/>, and a single-service injection would resolve to whichever was registered
    /// last.
    /// </param>
    /// <param name="rcon">
    /// The configured RCON control channels. Defaults to <see cref="ServyxRconChannels.None"/>, which yields
    /// no control channel, therefore no quiesce step, therefore an archive of on-disk state that says so in
    /// its manifest.
    /// </param>
    public ServyxSshBackupContextSource(
        SshBackupWiringOptions options,
        ITransport transport,
        ServyxRconChannels? rcon = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);

        _options = options;
        _transport = transport;
        _rcon = rcon ?? ServyxRconChannels.None;
    }

    /// <inheritdoc />
    public async Task<SshBackupContext> GetAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var server = _options.Find(serverId)
            ?? throw new InvalidOperationException(
                $"'{serverId}' is not configured as an SSH-hosted server, so there is nothing to back up. Add "
                + $"'{SshBackupWiringOptions.SectionKey}:{serverId}:{SshBackupWiringOptions.SshKey}:Enabled', ':Host' "
                + "and ':Root' if it should be.");

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

    private Task<IExecutionTarget> SessionAsync(SshBackupServer server, CancellationToken ct)
    {
        var lazy = _sessions.GetOrAdd(
            server.ServerKey,
            _ => new Lazy<Task<IExecutionTarget>>(() => _transport.ConnectAsync(
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

            try
            {
                await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A session that failed to open, or that the host already closed, must not stop the
                // remaining sessions from being released during shutdown.
            }
        }

        _sessions.Clear();
    }
}
