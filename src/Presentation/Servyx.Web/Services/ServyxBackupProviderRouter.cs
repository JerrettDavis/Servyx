using System.Collections.Concurrent;
using Servyx.Domain.Backups;
using Servyx.Infrastructure.Ssh.Backups;

namespace Servyx.Web.Services;

/// <summary>
/// The single <see cref="IBackupProvider"/> <c>BackupDashboardService</c> is handed when this process hosts
/// both Docker-hosted and SSH-hosted servers: it dispatches each call to the provider that owns the server
/// the call is about, and does nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a router at all.</strong> <c>BackupDashboardService</c> takes one provider. Registering two
/// <see cref="IBackupProvider"/>s and letting dependency injection pick would hand it whichever was
/// registered last — silently, and in a way that turns "Docker backups still work" into a question about
/// the order of two lines in <c>Program.cs</c>. Something has to choose deliberately; this is that
/// something, and it is the only new object in the path.
/// </para>
/// <para>
/// <strong>Why routing rather than keyed resolution or a per-server provider lookup in the UI.</strong>
/// Three of the six <see cref="IBackupProvider"/> members do not take a server id.
/// <see cref="InspectAsync"/> and <see cref="PlanRestoreAsync"/> take a backup id, and
/// <see cref="RestoreAsync"/> takes a restore-plan id, which names no server at all. A keyed lookup would
/// therefore push "which provider owns this opaque id?" out to every caller — the Backups page, the
/// scheduler, any future API — none of which knows the answer. Routing keeps that question in one place,
/// and it is the only place that <em>can</em> remember which provider issued a restore plan.
/// </para>
/// <para>
/// <strong>Docker's path is unchanged, structurally.</strong> The default provider is the fallback for
/// every call: a server id not in <see cref="SshServerIds"/>, a backup id whose encoded server is not in it,
/// a backup id that does not decode at all, and a restore-plan id this router never issued all go to the
/// default. With no SSH server configured the set is empty and every call routes to Docker — which is why
/// <c>Program.cs</c> does not compose this router at all in that case, and calls
/// <c>AddServyxBackupDashboard()</c> exactly as it did before.
/// </para>
/// <para>
/// <strong>The plan map is not a cache and never grows without bound in normal use.</strong> An entry is
/// added when an SSH restore is previewed and removed when it is applied — the same lifetime the providers'
/// own plan dictionaries have. A previewed-and-abandoned plan leaks one small entry, exactly as it does in
/// <c>DockerBackupProvider</c> and <c>SshBackupProvider</c> today; nothing here is authoritative, so an
/// entry that outlives its plan can only route a call that both providers would refuse anyway.
/// </para>
/// </remarks>
public sealed class ServyxBackupProviderRouter : IBackupProvider
{
    private readonly IBackupProvider _default;
    private readonly IBackupProvider _ssh;
    private readonly HashSet<string> _sshServerIds;
    private readonly ConcurrentDictionary<string, byte> _sshPlans = new(StringComparer.Ordinal);

    /// <summary>Creates a router.</summary>
    /// <param name="defaultProvider">The provider every unrecognised call goes to. In this host, Docker's.</param>
    /// <param name="sshProvider">The provider owning the servers named by <paramref name="sshServerIds"/>.</param>
    /// <param name="sshServerIds">The ids of the SSH-hosted servers.</param>
    public ServyxBackupProviderRouter(
        IBackupProvider defaultProvider,
        IBackupProvider sshProvider,
        IEnumerable<string> sshServerIds)
    {
        ArgumentNullException.ThrowIfNull(defaultProvider);
        ArgumentNullException.ThrowIfNull(sshProvider);
        ArgumentNullException.ThrowIfNull(sshServerIds);

        _default = defaultProvider;
        _ssh = sshProvider;
        _sshServerIds = new HashSet<string>(sshServerIds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The server ids routed to the SSH provider. Every other id goes to the default.</summary>
    public IReadOnlyCollection<string> SshServerIds => _sshServerIds;

    /// <summary>
    /// Builds a router over the providers a container holds, selecting the SSH one <em>by type</em> rather
    /// than by registration order — the same reason <c>AddServyxSshProvisioning</c> selects its transport by
    /// <c>TransportId</c>: adding another provider to the composition root must not be able to silently
    /// re-point either half.
    /// </summary>
    /// <param name="providers">Every registered <see cref="IBackupProvider"/>. Must contain exactly one SSH provider and exactly one other.</param>
    /// <param name="sshServerIds">The ids of the SSH-hosted servers.</param>
    /// <exception cref="InvalidOperationException">The container does not hold exactly one provider of each kind.</exception>
    public static ServyxBackupProviderRouter FromRegistered(
        IEnumerable<IBackupProvider> providers,
        IEnumerable<string> sshServerIds)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var all = providers.ToList();
        var ssh = all.OfType<SshBackupProvider>().ToList();
        var others = all.Where(p => p is not SshBackupProvider).ToList();

        if (ssh.Count != 1 || others.Count != 1)
        {
            throw new InvalidOperationException(
                $"Routing backups needs exactly one {nameof(SshBackupProvider)} and exactly one other "
                + $"{nameof(IBackupProvider)}; this container holds {ssh.Count} and {others.Count}. Composing a "
                + "router over an ambiguous set would decide which machine a restore overwrites by registration "
                + "order.");
        }

        return new ServyxBackupProviderRouter(others[0], ssh[0], sshServerIds);
    }

    /// <inheritdoc />
    public Task<BackupArtifact> CreateAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        return ForServer(serverId).CreateAsync(serverId, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupArtifact>> ListAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        return ForServer(serverId).ListAsync(serverId, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> InspectAsync(string backupId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        return ForBackup(backupId).InspectAsync(backupId, ct);
    }

    /// <inheritdoc />
    public async Task<RestorePlan> PlanRestoreAsync(string backupId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);

        var provider = ForBackup(backupId);
        var plan = await provider.PlanRestoreAsync(backupId, ct).ConfigureAwait(false);

        // A restore-plan id names no server, so this is the only moment at which the owning provider is
        // knowable. Recorded for SSH only; everything unrecorded falls through to the default, which is
        // exactly what a Docker-only host does today.
        if (ReferenceEquals(provider, _ssh))
        {
            _sshPlans[plan.Id] = 0;
        }

        return plan;
    }

    /// <inheritdoc />
    public Task RestoreAsync(string restorePlanId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restorePlanId);

        // Consumed here as well as in the provider: a plan is single-use, so leaving the routing entry
        // behind would keep a spent id pointing at a provider that has already forgotten it.
        return _sshPlans.TryRemove(restorePlanId, out _)
            ? _ssh.RestoreAsync(restorePlanId, ct)
            : _default.RestoreAsync(restorePlanId, ct);
    }

    /// <inheritdoc />
    public Task<PruneResult> PruneAsync(string serverId, RetentionPolicy policy, bool dryRun, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(policy);

        return ForServer(serverId).PruneAsync(serverId, policy, dryRun, ct);
    }

    private IBackupProvider ForServer(string serverId) =>
        _sshServerIds.Contains(serverId) ? _ssh : _default;

    /// <summary>
    /// Routes an opaque backup id by the server it encodes. The decoder is
    /// <see cref="BackupArtifactId"/>'s own, so the Web layer does not restate an adapter convention it
    /// would then be free to disagree with; an id that does not decode routes to the default, where an
    /// unknown id already fails as "not found" rather than being trusted.
    /// </summary>
    private IBackupProvider ForBackup(string backupId) =>
        BackupArtifactId.TryGetServerId(backupId, out var serverId) && _sshServerIds.Contains(serverId)
            ? _ssh
            : _default;
}
