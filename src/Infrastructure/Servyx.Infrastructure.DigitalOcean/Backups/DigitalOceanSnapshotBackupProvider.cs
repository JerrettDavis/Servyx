using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;

using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.DigitalOcean.Backups;

/// <summary>
/// An <see cref="IBackupProvider"/> whose artifacts are DigitalOcean <em>droplet snapshots</em>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a different shape of backup from the other two adapters, and pretending otherwise would
/// be the main way to get it wrong.</strong> A Docker or SSH backup is a tar file Servyx wrote onto a disk it
/// controls: Servyx chose the path, owns the bytes, and can open the archive to see what is inside. A
/// DigitalOcean snapshot is none of those things. It is a resource in somebody's cloud account with its own
/// id, its own billing and its own lifecycle; Servyx can ask for one, list them, delete one, and restore a
/// droplet from one, and that is the whole of the vocabulary. In exchange it is the only backup that can make
/// a droplet <em>rebuild</em> recoverable, because it captures the boot disk the rebuild erases.
/// </para>
/// <para>
/// <strong>Foreign snapshots are never deleted, and that is structural.</strong> A DigitalOcean account
/// contains snapshots Servyx did not create — taken by hand, by another tool, or of a different droplet
/// entirely. Three independent barriers stand between <see cref="PruneAsync"/> and one of them, each
/// sufficient on its own:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <em>Partition.</em> <see cref="PruneAsync"/> splits the listing by <see cref="BackupArtifact.Ownership"/>
/// in one place and passes only the <see cref="BackupOwnership.Servyx"/> half onward. The foreign half is
/// counted into <see cref="PruneResult.SkippedForeign"/> and then goes out of scope — it is never bound to a
/// variable any deletion code can see, under either value of <c>dryRun</c>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>Evaluation.</em> <see cref="SnapshotRetentionEvaluator.SelectForRemoval"/> throws
/// <see cref="ForeignSnapshotProtectedException"/> if a foreign artifact reaches it, so retention cannot even
/// be <em>computed</em> over one. This is what makes the guarantee hold for <c>dryRun: true</c> as strongly
/// as for <c>dryRun: false</c>: a dry run's report comes from the same call, so there is no path that
/// "hypothetically" schedules a foreign snapshot for deletion.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>Deletion.</em> <see cref="DeleteServyxOwnedAsync"/> is the only method in this type that issues a
/// <c>DELETE</c>, and it re-derives ownership from the live snapshot object through
/// <see cref="SnapshotOwnership.Classify"/> — name, tags, resource type and droplet id, all four — instead of
/// trusting the label it was handed. A mislabelled artifact fails that re-derivation and throws.
/// </description>
/// </item>
/// </list>
/// <para>
/// <strong>Creating a snapshot costs money and takes minutes.</strong> DigitalOcean answers the snapshot POST
/// while the copy is still queued, so <see cref="CreateAsync"/> polls the action to a terminal state and
/// returns an artifact only for an observed <c>completed</c>. An action still running when the polls are
/// spent raises <see cref="SnapshotActionNotConfirmedException"/> — never a successful
/// <see cref="BackupArtifact"/>. And a snapshot that exists bills per GB-month for as long as it exists, with
/// no expiry: see <see cref="DigitalOceanSnapshotPricing"/>, which every description this type produces
/// quotes.
/// </para>
/// <para>
/// <strong>Restoring is a disk-erasing operation and is gated like one.</strong> Restoring a droplet from a
/// snapshot replaces its boot disk — the same class of operation as
/// <c>DigitalOceanDropletProvisioner.ApplyDestructiveUpdateAsync</c>, and it is gated at least as strictly.
/// <see cref="PlanRestoreAsync"/> issues reads only and says plainly what would be destroyed;
/// <see cref="RestoreAsync(string, CancellationToken)"/> — the <c>IBackupProvider</c> member — always
/// refuses, because its signature takes a plan id and nothing else and so cannot carry evidence that anybody
/// accepted the data loss. The acknowledging overload
/// <see cref="RestoreAsync(string, DataImpact?, CancellationToken)"/> is the only path to a provider call,
/// and it demands an acknowledgement naming exactly <see cref="DataImpact.Destroyed"/>, a plan this provider
/// issued, a plan that is unspent and unexpired, and a snapshot that still exists and is unchanged. See that
/// member's remarks for why the interface member refuses rather than restoring.
/// </para>
/// <para>
/// <strong>There is no force path.</strong> No argument here skips a guard, and no combination of arguments
/// reaches a provider call the checks below would otherwise refuse.
/// </para>
/// <para>
/// <strong>Not registered anywhere.</strong> See <see cref="DigitalOceanSnapshotBackups"/>: snapshotting,
/// restoring and pruning are mutating, billable capabilities, so this type is opt-in and unreachable from any
/// composition root that does not name it. A host with <c>Servyx:Provisioning:Enabled</c> unset never reaches
/// it, and nothing in this repository constructs one outside its tests.
/// </para>
/// </remarks>
public sealed class DigitalOceanSnapshotBackupProvider : IBackupProvider
{
    /// <summary>How long a <see cref="RestorePlan"/> stays applicable after it is produced.</summary>
    public static readonly TimeSpan DefaultRestorePlanTtl = TimeSpan.FromMinutes(15);

    /// <summary>The default interval between reads of a snapshot or restore action.</summary>
    public static readonly TimeSpan DefaultActionPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The default number of reads made before an action is reported as not confirmed. Sixty reads ten
    /// seconds apart is ten minutes, which is the order of magnitude a snapshot of a game server's disk takes.
    /// </summary>
    public const int DefaultActionPollAttempts = 60;

    private const decimal BytesPerGigabyte = 1_073_741_824m;

    private readonly DigitalOceanApiClient _api;
    private readonly IDigitalOceanSnapshotContextSource _contexts;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _actionPollInterval;
    private readonly int _actionPollAttempts;
    private readonly TimeSpan _restorePlanTtl;
    private readonly ConcurrentDictionary<string, PendingRestore> _plans = new(StringComparer.Ordinal);

    /// <summary>Creates a provider over one DigitalOcean account.</summary>
    /// <param name="http">The HTTP client the API calls go out on. Substituted in tests; no account is required.</param>
    /// <param name="secretStore">Where the DigitalOcean personal access token lives.</param>
    /// <param name="apiTokenUrn">The URN the token is stored at. Resolved per request and never cached.</param>
    /// <param name="contexts">Maps a Servyx server id to the droplet that backs it.</param>
    /// <param name="timeProvider">Clock used for snapshot naming and restore-plan expiry.</param>
    /// <param name="actionPollInterval">How long to wait between reads of an action. Defaults to <see cref="DefaultActionPollInterval"/>.</param>
    /// <param name="actionPollAttempts">How many reads to make before reporting an action unconfirmed.</param>
    /// <param name="restorePlanTtl">How long a restore plan stays applicable. Defaults to <see cref="DefaultRestorePlanTtl"/>.</param>
    public DigitalOceanSnapshotBackupProvider(
        HttpClient http,
        ISecretStore secretStore,
        SecretUrn apiTokenUrn,
        IDigitalOceanSnapshotContextSource contexts,
        TimeProvider? timeProvider = null,
        TimeSpan? actionPollInterval = null,
        int actionPollAttempts = DefaultActionPollAttempts,
        TimeSpan? restorePlanTtl = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentOutOfRangeException.ThrowIfLessThan(actionPollAttempts, 1);

        _api = new DigitalOceanApiClient(http, secretStore, apiTokenUrn);
        _contexts = contexts;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _actionPollInterval = actionPollInterval ?? DefaultActionPollInterval;
        _actionPollAttempts = actionPollAttempts;
        _restorePlanTtl = restorePlanTtl ?? DefaultRestorePlanTtl;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Submission is not success.</strong> The sequence is: read the droplet's current snapshots,
    /// submit one snapshot action, poll that action to a terminal state, and only then look for the snapshot
    /// it produced. An action DigitalOcean never reports finished raises
    /// <see cref="SnapshotActionNotConfirmedException"/>; an errored one raises
    /// <see cref="SnapshotActionFailedException"/>. Neither returns an artifact, because neither is evidence
    /// that a backup exists.
    /// </para>
    /// <para>
    /// <strong>The ownership marks are applied after the fact and then verified.</strong> DigitalOcean's
    /// snapshot action takes a name but no tags, so the two tags are applied to the finished snapshot and the
    /// listing is re-read to confirm they are visible. A snapshot that cannot be verified as Servyx-owned
    /// raises <see cref="SnapshotOwnershipNotRecordedException"/> naming the snapshot and its monthly cost:
    /// it exists, it is billing, and — since Servyx never deletes what it cannot prove it owns — retention
    /// will never remove it. Reporting that as a successful backup would hide both facts.
    /// </para>
    /// </remarks>
    public async Task<BackupArtifact> CreateAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);

        var before = await ListResolvedAsync(context, ct).ConfigureAwait(false);
        var known = before.Select(r => r.SnapshotId).ToHashSet(StringComparer.Ordinal);

        var takenAt = _timeProvider.GetUtcNow();
        var name = SnapshotOwnership.FormatName(context.ServerId, takenAt);

        var action = await _api.SnapshotDropletAsync(context.DropletId, name, ct).ConfigureAwait(false);
        var poll = await _api
            .PollActionAsync(action.Id, _actionPollInterval, _actionPollAttempts, _timeProvider, ct)
            .ConfigureAwait(false);

        switch (poll.Outcome)
        {
            case DropletActionOutcome.Completed:
                break;

            case DropletActionOutcome.Errored:
                throw new SnapshotActionFailedException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"DigitalOcean reported snapshot action {poll.ActionId} on droplet {context.DropletId} as "
                        + $"errored, so no backup was taken for server '{context.ServerId}'. ")
                    + DescribeProviderMessage(poll),
                    poll.ActionId);

            default:
                throw new SnapshotActionNotConfirmedException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"DigitalOcean accepted snapshot action {poll.ActionId} on droplet {context.DropletId} and "
                        + $"was still reporting it as '{poll.Status ?? "(no status)"}' after {poll.Polls} check(s). ")
                    + "No backup is being reported for server '" + context.ServerId + "': a snapshot that was only "
                    + "submitted is not a snapshot that exists. The copy is most likely still running at "
                    + "DigitalOcean and may yet finish — which is NOT the same as a failure and calls for the "
                    + "opposite response. Do not resubmit blindly: a second snapshot that completes alongside the "
                    + "first leaves two copies, both billing per GB-month. Watch the action at DigitalOcean, or "
                    + "list the droplet's snapshots, before acting further.",
                    poll.ActionId,
                    submitted: true);
        }

        var after = await ListResolvedAsync(context, ct).ConfigureAwait(false);
        var created = after
            .Where(r => !known.Contains(r.SnapshotId) && string.Equals(r.Name, name, StringComparison.Ordinal))
            .OrderByDescending(r => r.Artifact.CreatedAt)
            .ThenBy(r => r.SnapshotId, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new SnapshotActionFailedException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"DigitalOcean reported snapshot action {poll.ActionId} on droplet {context.DropletId} as "
                    + $"completed, but no new snapshot named '{name}' appeared in the account afterwards. Servyx "
                    + $"will not report a backup it cannot see. Check the droplet's snapshots at DigitalOcean before "
                    + $"retrying — if one did appear under a different name it is billing and Servyx does not own it."),
                poll.ActionId);

        return await MarkAsServyxOwnedAsync(context, created, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Lists every snapshot DigitalOcean holds <em>of this server's droplet</em>, Servyx-owned and foreign
    /// alike, each labelled at the point it is classified rather than inferred later. Snapshots of other
    /// droplets in the same account are not this server's backups and are not returned — which is also what
    /// keeps one server's retention from ever seeing another server's snapshots.
    /// </remarks>
    public async Task<IReadOnlyList<BackupArtifact>> ListAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var resolved = await ListResolvedAsync(context, ct).ConfigureAwait(false);
        return resolved.Select(r => r.Artifact).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>A snapshot has no readable index, and this does not invent one.</strong> The Docker and SSH
    /// providers answer this by reading tar headers, because their artifacts are archives they can open.
    /// DigitalOcean exposes no way to enumerate or extract a single file from a snapshot without first
    /// restoring it onto a droplet, so what comes back here is a description of the artifact — what it
    /// covers, when it was taken, what it costs, and who owns it — and it says outright that the file list is
    /// not available. A plausible-looking fabricated listing would be worse than no listing, because someone
    /// would plan a restore around it.
    /// </para>
    /// <para>Read-only: one <c>GET</c>, no mutation of any kind.</para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> InspectAsync(string backupId, CancellationToken ct = default)
    {
        var (context, snapshot) = await ResolveAsync(backupId, ct).ConfigureAwait(false);
        return Describe(context, snapshot);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Read-only, and blunt about what a restore would do.</strong> This issues one <c>GET</c> and
    /// nothing else. The returned <see cref="RestorePlan.AffectedPaths"/> is not a file list — a snapshot
    /// restore does not overwrite selected paths, it replaces the droplet's entire boot disk — so the entries
    /// state that consequence in words, including that everything written since the snapshot was taken is
    /// destroyed, that the operation is the same class as a rebuild, and that it cannot be undone.
    /// </para>
    /// <para>
    /// The plan is recorded as single-use and time-bounded. It records the snapshot's size and creation time
    /// so that <see cref="RestoreAsync(string, DataImpact?, CancellationToken)"/> can refuse a plan whose
    /// snapshot has changed or vanished since it was previewed.
    /// </para>
    /// </remarks>
    public async Task<RestorePlan> PlanRestoreAsync(string backupId, CancellationToken ct = default)
    {
        var (context, snapshot) = await ResolveAsync(backupId, ct).ConfigureAwait(false);

        if (!TryReadSnapshotImageId(snapshot.SnapshotId, out _))
        {
            throw new NotSupportedException(
                $"Snapshot '{snapshot.SnapshotId}' does not have a numeric DigitalOcean image id, so a droplet "
                + "cannot be restored from it: the restore action names the snapshot by image id. Nothing was sent "
                + "to DigitalOcean.");
        }

        var affected = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"DESTRUCTIVE: the ENTIRE boot disk of droplet {context.DropletId} is replaced by the contents of "
                + $"snapshot {snapshot.SnapshotId} ('{snapshot.Name}')."),
            string.Create(
                CultureInfo.InvariantCulture,
                $"Everything written to droplet {context.DropletId} since {Format(snapshot.Artifact.CreatedAt)} is "
                + $"destroyed — the installed game, its configuration, and every save file made since then. None of "
                + $"it can be recovered from the droplet afterwards, because none of it is still there."),
            "This is the same class of operation as a droplet rebuild: DataImpact.Destroyed. It is not undoable, "
            + "and Servyx takes no automatic snapshot of the current disk before running it.",
            string.Create(
                CultureInfo.InvariantCulture,
                $"The droplet keeps its id ({context.DropletId}) and its address; nothing else about it survives."),
            "The snapshot itself is NOT consumed or deleted by a restore, and continues to bill.",
            $"Backup ownership: {snapshot.Artifact.Ownership}.",
            DigitalOceanSnapshotPricing.DescribeMonthlyCost(snapshot.SizeGigabytes),
            "To carry this out, call the acknowledging RestoreAsync overload with DataImpact.Destroyed. The "
            + "IBackupProvider.RestoreAsync(planId) member refuses every time, by design.",
        };

        var planId = $"restore-{Guid.NewGuid():n}";
        var plan = new RestorePlan(planId, snapshot.Artifact.Id, affected);

        _plans[planId] = new PendingRestore(
            plan,
            context.ServerId,
            snapshot.SnapshotId,
            _timeProvider.GetUtcNow(),
            snapshot.SizeGigabytes,
            snapshot.Artifact.CreatedAt);

        return plan;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>This member always refuses, and that is the design.</strong> Restoring a droplet from a
    /// snapshot erases the droplet's boot disk. The droplet provisioner's rebuild path will not do that
    /// without an acknowledgement naming <see cref="DataImpact.Destroyed"/> exactly, supplied separately from
    /// the plan; this operation is in the same class and is not gated more weakly. But
    /// <see cref="IBackupProvider.RestoreAsync"/> takes a plan id and nothing else, so there is no argument
    /// on this signature that could carry that acknowledgement — and an adapter that inferred consent from
    /// "you called the method" would be treating the absence of evidence as evidence.
    /// </para>
    /// <para>
    /// So this throws <see cref="SnapshotRestoreNotAcknowledgedException"/> without issuing any HTTP request
    /// and without consuming the plan, and points at
    /// <see cref="RestoreAsync(string, DataImpact?, CancellationToken)"/>, which is the only path to a
    /// provider call. The previewed plan stays usable, so nobody is forced to preview twice.
    /// </para>
    /// </remarks>
    /// <exception cref="SnapshotRestoreNotAcknowledgedException">Always.</exception>
    public Task RestoreAsync(string restorePlanId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restorePlanId);

        throw new SnapshotRestoreNotAcknowledgedException(
            $"Restore plan '{restorePlanId}' was NOT carried out. Restoring a droplet from a DigitalOcean snapshot "
            + "replaces the droplet's entire boot disk: everything written since the snapshot was taken is "
            + "destroyed and cannot be recovered. That is DataImpact.Destroyed, the same class of operation as a "
            + "droplet rebuild, and Servyx runs it only when someone has separately accepted exactly that. "
            + "IBackupProvider.RestoreAsync(planId) cannot carry an acknowledgement, so it never restores. Call "
            + "DigitalOceanSnapshotBackupProvider.RestoreAsync(planId, DataImpact.Destroyed) instead. Nothing was "
            + "sent to DigitalOcean and no disk was erased; this plan has not been consumed and can still be used.",
            restorePlanId);
    }

    /// <summary>
    /// Carries out a previewed restore, replacing the droplet's boot disk with the snapshot's contents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only path in this type that reaches a disk-erasing provider call, and it is gated at least as
    /// strictly as <c>DigitalOceanDropletProvisioner.ApplyDestructiveUpdateAsync</c>. Reaching the submission
    /// needs all of: an acknowledgement naming exactly <see cref="DataImpact.Destroyed"/>; a plan id this
    /// provider issued; a plan that has not already been used; a plan that has not expired; a snapshot that
    /// still exists at DigitalOcean; and a snapshot whose size and creation time still match what was
    /// previewed. Every one of those checks runs before any HTTP request is made, except the two that
    /// <em>are</em> a read, so a refusal issues no mutating request of any kind.
    /// </para>
    /// <para>
    /// The acknowledgement is checked in both directions and is an exact match:
    /// <see cref="DataImpact.AtRisk"/> does not authorise it, <see cref="DataImpact.Preserved"/> authorises
    /// nothing at all, and no acknowledgement authorises nothing at all. This restates the rule
    /// <c>Servyx.Application</c>'s <c>DataImpactAcknowledgement</c> enforces, for the same reason the rebuild
    /// path restates it: this assembly references only <c>Servyx.Domain</c>.
    /// </para>
    /// <para>
    /// <strong>Submission is not success here either.</strong> Only an observed <c>completed</c> returns. An
    /// errored action raises <see cref="SnapshotActionFailedException"/>; one still running when the polls are
    /// spent raises <see cref="SnapshotActionNotConfirmedException"/>, whose message says plainly not to
    /// resubmit — a second restore overwrites the disk again, including anything the first has already put
    /// back.
    /// </para>
    /// </remarks>
    /// <param name="restorePlanId">A plan id from <see cref="PlanRestoreAsync"/>.</param>
    /// <param name="acknowledgedDataImpact">Must be exactly <see cref="DataImpact.Destroyed"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SnapshotRestoreNotAcknowledgedException">The acknowledgement is missing or is not <see cref="DataImpact.Destroyed"/>.</exception>
    /// <exception cref="SnapshotRestorePlanStaleException">The plan is unknown, spent, expired, or its snapshot has changed or vanished.</exception>
    public async Task RestoreAsync(
        string restorePlanId,
        DataImpact? acknowledgedDataImpact,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restorePlanId);

        // Guard 1 - the acknowledgement, checked first and without touching the network or the plan store. A
        // refusal here leaves the previewed plan intact so the operator can retry with the acknowledgement.
        if (acknowledgedDataImpact != DataImpact.Destroyed)
        {
            throw new SnapshotRestoreNotAcknowledgedException(
                $"Restore plan '{restorePlanId}' was NOT carried out: the acknowledgement supplied was "
                + $"{(acknowledgedDataImpact is null ? "none" : acknowledgedDataImpact.Value.ToString())}. Restoring "
                + "a droplet from a snapshot replaces its entire boot disk, so it runs only when someone has "
                + $"separately accepted exactly {DataImpact.Destroyed} — an acknowledgement of "
                + $"{DataImpact.AtRisk}, or none at all, is not an approval of data loss. Nothing was sent to "
                + "DigitalOcean and no disk was erased; this plan has not been consumed.",
                restorePlanId);
        }

        // Guard 2 - the plan must be one this provider issued and must not already have been used. Consumed
        // here, so a plan cannot be applied twice even if the second attempt races the first.
        if (!_plans.TryRemove(restorePlanId, out var pending))
        {
            throw new SnapshotRestorePlanStaleException(
                $"Restore plan '{restorePlanId}' is unknown or has already been applied. Nothing was sent to "
                + "DigitalOcean. Preview the restore again.",
                restorePlanId);
        }

        // Guard 3 - the plan must still be fresh. A plan previewed and walked away from describes a droplet
        // that may no longer be the droplet that exists now, and this operation is not undoable.
        var age = _timeProvider.GetUtcNow() - pending.CreatedAt;
        if (age > _restorePlanTtl)
        {
            throw new SnapshotRestorePlanStaleException(
                $"Restore plan '{restorePlanId}' expired after {_restorePlanTtl}. Nothing was sent to DigitalOcean. "
                + "Preview the restore again.",
                restorePlanId);
        }

        var context = await GetContextAsync(pending.ServerId, ct).ConfigureAwait(false);
        var resolved = await ListResolvedAsync(context, ct).ConfigureAwait(false);

        // Guard 4 - the snapshot must still be there. A snapshot can be deleted at DigitalOcean between the
        // preview and the apply, and restoring "the closest thing still present" is not a thing this adapter
        // will do.
        var snapshot = resolved.FirstOrDefault(r =>
            string.Equals(r.SnapshotId, pending.SnapshotId, StringComparison.Ordinal))
            ?? throw new SnapshotRestorePlanStaleException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Snapshot {pending.SnapshotId} no longer exists at DigitalOcean, so restore plan "
                    + $"'{restorePlanId}' cannot be applied: droplet {context.DropletId} was NOT touched and its "
                    + $"disk was NOT erased. The snapshot was deleted between the preview and this call — by the "
                    + $"console, by another tool, or by a prune. List the droplet's snapshots and preview again."),
                restorePlanId);

        // Guard 5 - and it must be the same snapshot, not a different one that reused the id or was replaced.
        if (snapshot.SizeGigabytes != pending.SizeGigabytes || snapshot.Artifact.CreatedAt != pending.SnapshotCreatedAt)
        {
            throw new SnapshotRestorePlanStaleException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Snapshot {pending.SnapshotId} has changed since restore plan '{restorePlanId}' was previewed, "
                    + $"so it was NOT applied and no disk was erased. Preview the restore again."),
                restorePlanId);
        }

        if (!TryReadSnapshotImageId(snapshot.SnapshotId, out var imageId))
        {
            throw new SnapshotRestorePlanStaleException(
                $"Snapshot '{snapshot.SnapshotId}' does not have a numeric DigitalOcean image id, so the restore "
                + $"action cannot name it. Nothing was sent to DigitalOcean.",
                restorePlanId);
        }

        // The first and only mutating request on this path. Everything above ran without issuing one, so a
        // refusal reaching this line is impossible and a refusal before it sent nothing.
        var action = await _api
            .RestoreDropletFromSnapshotAsync(context.DropletId, imageId, ct)
            .ConfigureAwait(false);

        var poll = await _api
            .PollActionAsync(action.Id, _actionPollInterval, _actionPollAttempts, _timeProvider, ct)
            .ConfigureAwait(false);

        switch (poll.Outcome)
        {
            case DropletActionOutcome.Completed:
                return;

            case DropletActionOutcome.Errored:
                throw new SnapshotActionFailedException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"DigitalOcean reported restore action {poll.ActionId} on droplet {context.DropletId} as "
                        + $"errored. ")
                    + DescribeProviderMessage(poll)
                    + " The action was accepted before it errored, so the droplet's disk may have been partly or "
                    + "wholly overwritten already — treat the machine's contents as lost until you have read the "
                    + "droplet and confirmed otherwise.",
                    poll.ActionId);

            default:
                throw new SnapshotActionNotConfirmedException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"DigitalOcean accepted restore action {poll.ActionId} on droplet {context.DropletId} and "
                        + $"was still reporting it as '{poll.Status ?? "(no status)"}' after {poll.Polls} check(s). ")
                    + "The restore was NOT confirmed and was NOT reported as failed — a restore takes minutes and "
                    + "this one is most likely still running at DigitalOcean and may yet complete. That is a "
                    + "different situation from a failure and calls for the opposite response: do NOT resubmit, "
                    + "because a second restore overwrites the disk again, including anything the first one has "
                    + "already put back. Watch the action at DigitalOcean before acting further.",
                    poll.ActionId,
                    submitted: true);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// See the type remarks for the three barriers that make foreign snapshots unprunable. Under
    /// <c>dryRun: true</c> this issues no <c>DELETE</c> of any kind; under either flag,
    /// <see cref="PruneResult.SkippedForeign"/> reports how many foreign snapshots were seen and left alone.
    /// A snapshot that has already vanished provider-side answers 404 to the delete and is still reported as
    /// removed — it is gone, which is the outcome retention asked for, and pretending otherwise would leave
    /// the caller expecting a charge that has already stopped.
    /// </remarks>
    public async Task<PruneResult> PruneAsync(
        string serverId,
        RetentionPolicy policy,
        bool dryRun,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var effectivePolicy = policy ?? context.DefaultRetention;
        var all = await ListResolvedAsync(context, ct).ConfigureAwait(false);

        // Barrier 1: the partition. Only the Servyx-owned half is bound to a name anything below can see; the
        // foreign half is reduced to a count here and never reaches retention or deletion.
        var skippedForeign = all.Count(r => r.Artifact.Ownership == BackupOwnership.Foreign);
        var ownedByServyx = all
            .Where(r => r.Artifact.Ownership == BackupOwnership.Servyx)
            .ToList();

        // Barrier 2: evaluation. SelectForRemoval throws on anything not Servyx-owned, so a dry run and a live
        // run compute their answer from the identical, ownership-asserting call.
        var removals = SnapshotRetentionEvaluator.SelectForRemoval(
            ownedByServyx.Select(r => r.Artifact).ToList(),
            effectivePolicy);

        var removalIds = removals.Select(a => a.Id).ToList();
        if (dryRun)
        {
            return new PruneResult(removalIds, skippedForeign);
        }

        foreach (var removal in removals)
        {
            var resolved = ownedByServyx.First(r => string.Equals(r.Artifact.Id, removal.Id, StringComparison.Ordinal));
            await DeleteServyxOwnedAsync(context, resolved, ct).ConfigureAwait(false);
        }

        return new PruneResult(removalIds, skippedForeign);
    }

    /// <summary>
    /// The total monthly list price of every snapshot this server has at DigitalOcean, split by ownership.
    /// </summary>
    /// <remarks>
    /// A snapshot's charge recurs for as long as it exists, so "what am I paying for backups" is a question
    /// this adapter has to be able to answer — the other two providers never needed one, because their
    /// artifacts sit on a disk already being paid for. Read-only: one <c>GET</c>.
    /// </remarks>
    /// <param name="serverId">The Servyx server.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SnapshotStorageCost> EstimateStorageCostAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var all = await ListResolvedAsync(context, ct).ConfigureAwait(false);

        var servyxGb = SumGigabytes(all.Where(r => r.Artifact.Ownership == BackupOwnership.Servyx));
        var foreignGb = SumGigabytes(all.Where(r => r.Artifact.Ownership == BackupOwnership.Foreign));

        return new SnapshotStorageCost(
            all.Count(r => r.Artifact.Ownership == BackupOwnership.Servyx),
            all.Count(r => r.Artifact.Ownership == BackupOwnership.Foreign),
            DigitalOceanSnapshotPricing.For(servyxGb),
            DigitalOceanSnapshotPricing.For(foreignGb),
            all.Any(r => r.SizeGigabytes is null));
    }

    /// <summary>
    /// Barrier 3: the only method in this type that issues a <c>DELETE</c>.
    /// </summary>
    /// <remarks>
    /// It re-derives ownership from the live snapshot object — resource type, droplet id, name and both tags,
    /// through <see cref="SnapshotOwnership.Classify"/> — rather than trusting the label it was handed, and
    /// additionally re-checks that the snapshot belongs to this context's droplet. A mislabelled or
    /// out-of-scope artifact throws <see cref="ForeignSnapshotProtectedException"/> instead of being deleted,
    /// so even a caller that fabricated an artifact with <see cref="BackupOwnership.Servyx"/> on it could not
    /// route a delete at somebody else's snapshot.
    /// </remarks>
    private async Task DeleteServyxOwnedAsync(
        DigitalOceanSnapshotContext context,
        ResolvedSnapshot snapshot,
        CancellationToken ct)
    {
        if (snapshot.Artifact.Ownership != BackupOwnership.Servyx)
        {
            throw new ForeignSnapshotProtectedException(
                $"Refusing to delete snapshot '{snapshot.SnapshotId}': it is {snapshot.Artifact.Ownership}, not "
                + "Servyx-owned. Deleting a DigitalOcean snapshot cannot be undone.",
                snapshot.Artifact.Location);
        }

        var rederived = SnapshotOwnership.Classify(
            snapshot.ResourceType,
            snapshot.ResourceId,
            snapshot.Name,
            snapshot.Tags,
            context.ServerId,
            context.DropletId);

        if (rederived != BackupOwnership.Servyx)
        {
            throw new ForeignSnapshotProtectedException(
                $"Refusing to delete snapshot '{snapshot.SnapshotId}': it was presented as Servyx-owned, but the "
                + $"live snapshot object does not carry Servyx's marks for server '{context.ServerId}' (name "
                + $"'{snapshot.Name}', resource_type '{snapshot.ResourceType}', resource_id "
                + $"'{snapshot.ResourceId}', tags [{string.Join(", ", snapshot.Tags)}]). Deleting a DigitalOcean "
                + "snapshot cannot be undone.",
                snapshot.Artifact.Location);
        }

        await _api.DeleteSnapshotAsync(snapshot.SnapshotId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies both ownership tags to a freshly-created snapshot and verifies that DigitalOcean reports them.
    /// </summary>
    private async Task<BackupArtifact> MarkAsServyxOwnedAsync(
        DigitalOceanSnapshotContext context,
        ResolvedSnapshot created,
        CancellationToken ct)
    {
        var managedTag = SnapshotOwnership.ManagedTag;
        var instanceTag = SnapshotOwnership.InstanceTag(context.ServerId);

        try
        {
            await _api.EnsureTagExistsAsync(managedTag, ct).ConfigureAwait(false);
            await _api.TagSnapshotAsync(managedTag, created.SnapshotId, ct).ConfigureAwait(false);
            await _api.EnsureTagExistsAsync(instanceTag, ct).ConfigureAwait(false);
            await _api.TagSnapshotAsync(instanceTag, created.SnapshotId, ct).ConfigureAwait(false);
        }
        catch (DigitalOceanApiException ex)
        {
            throw new SnapshotOwnershipNotRecordedException(
                UnownedSnapshotMessage(context, created) + " DigitalOcean's reason: " + ex.Message,
                created.SnapshotId,
                ex);
        }

        var verified = (await ListResolvedAsync(context, ct).ConfigureAwait(false))
            .FirstOrDefault(r => string.Equals(r.SnapshotId, created.SnapshotId, StringComparison.Ordinal));

        if (verified is null || verified.Artifact.Ownership != BackupOwnership.Servyx)
        {
            throw new SnapshotOwnershipNotRecordedException(
                UnownedSnapshotMessage(context, verified ?? created),
                created.SnapshotId);
        }

        return verified.Artifact;
    }

    private static string UnownedSnapshotMessage(DigitalOceanSnapshotContext context, ResolvedSnapshot snapshot) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Snapshot {snapshot.SnapshotId} of droplet {context.DropletId} WAS taken and exists at DigitalOcean, "
            + $"but Servyx could not mark it as its own, so it is not a managed backup of server "
            + $"'{context.ServerId}'. ")
        + "Servyx never deletes a snapshot it cannot prove it owns, so retention will NEVER remove this one: it "
        + "will bill until somebody deletes it by hand or applies the tags '"
        + SnapshotOwnership.ManagedTag + "' and '" + SnapshotOwnership.InstanceTag(context.ServerId) + "' to it. "
        + DigitalOceanSnapshotPricing.DescribeMonthlyCost(snapshot.SizeGigabytes);

    private async Task<DigitalOceanSnapshotContext> GetContextAsync(string serverId, CancellationToken ct)
    {
        var context = await _contexts.GetAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new SnapshotNotFoundException(
                $"No DigitalOcean snapshot context is configured for server '{serverId}', so Servyx does not know "
                + "which droplet backs it.");

        if (!SnapshotOwnership.IsSupportedServerId(context.ServerId))
        {
            throw new ArgumentException(
                $"Server id '{context.ServerId}' cannot be carried in a DigitalOcean snapshot name or tag, so a "
                + "snapshot taken for it could never be recognised as Servyx's afterwards — it would bill forever "
                + "and never be pruned. Ids may contain only letters, digits, '-' and '_'.",
                nameof(serverId));
        }

        if (context.DropletId <= 0)
        {
            throw new ArgumentException(
                $"Server '{context.ServerId}' maps to droplet id {context.DropletId}, which is not a DigitalOcean "
                + "droplet id.",
                nameof(serverId));
        }

        return context;
    }

    /// <summary>
    /// Lists this droplet's snapshots and labels each one at the point it is discovered.
    /// </summary>
    /// <remarks>
    /// Ownership is decided here, once, by <see cref="SnapshotOwnership.Classify"/>, and never re-inferred
    /// from an artifact's shape further downstream. Snapshots of other droplets are filtered out entirely:
    /// they are not this server's backups, and never letting them into the list is what keeps one server's
    /// retention from ever seeing another server's snapshots.
    /// </remarks>
    private async Task<IReadOnlyList<ResolvedSnapshot>> ListResolvedAsync(
        DigitalOceanSnapshotContext context,
        CancellationToken ct)
    {
        var snapshots = await _api.ListDropletSnapshotsAsync(ct).ConfigureAwait(false);
        var results = new List<ResolvedSnapshot>();

        foreach (var snapshot in snapshots)
        {
            if (snapshot.Id is not { Length: > 0 } snapshotId)
            {
                continue;
            }

            if (!long.TryParse(snapshot.ResourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var owner)
                || owner != context.DropletId
                || !string.Equals(
                    snapshot.ResourceType,
                    DigitalOceanApiClient.DropletSnapshotResourceType,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var tags = snapshot.Tags ?? [];
            var ownership = SnapshotOwnership.Classify(
                snapshot.ResourceType,
                snapshot.ResourceId,
                snapshot.Name,
                tags,
                context.ServerId,
                context.DropletId);

            var createdAt = snapshot.CreatedAt
                ?? (SnapshotOwnership.TryParseName(snapshot.Name, out _, out var named) ? named : DateTimeOffset.UnixEpoch);

            results.Add(new ResolvedSnapshot(
                new BackupArtifact(
                    SnapshotBackupId.Format(context.ServerId, snapshotId),
                    ownership,
                    createdAt,
                    ToBytes(snapshot.SizeGigabytes),
                    SnapshotBackupId.LocationOf(snapshotId)),
                snapshotId,
                snapshot.Name ?? string.Empty,
                snapshot.ResourceType,
                snapshot.ResourceId,
                tags,
                snapshot.SizeGigabytes,
                snapshot.MinDiskSize));
        }

        return results;
    }

    private async Task<(DigitalOceanSnapshotContext Context, ResolvedSnapshot Snapshot)> ResolveAsync(
        string backupId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);

        if (!SnapshotBackupId.TryGetServerId(backupId, out var serverId))
        {
            throw new SnapshotNotFoundException(
                $"Backup id '{backupId}' is not in a form this provider issued.",
                backupId);
        }

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var resolved = await ListResolvedAsync(context, ct).ConfigureAwait(false);

        return (context, resolved.FirstOrDefault(r => string.Equals(r.Artifact.Id, backupId, StringComparison.Ordinal))
            ?? throw new SnapshotNotFoundException(
                $"Backup '{backupId}' does not exist: DigitalOcean no longer reports a snapshot with that id for "
                + $"server '{serverId}'. It may have been deleted in the console, by another tool, or by a prune.",
                backupId));
    }

    private static IReadOnlyList<string> Describe(DigitalOceanSnapshotContext context, ResolvedSnapshot snapshot) =>
    [
        string.Create(
            CultureInfo.InvariantCulture,
            $"DigitalOcean snapshot {snapshot.SnapshotId} ('{snapshot.Name}') of droplet {context.DropletId}, "
            + $"taken {Format(snapshot.Artifact.CreatedAt)}."),
        $"Ownership: {snapshot.Artifact.Ownership}."
            + (snapshot.Artifact.Ownership == BackupOwnership.Foreign
                ? " Servyx did not create this snapshot and will never delete it — it is listed, inspectable and "
                  + "restorable, and retention cannot reach it."
                : " Created by Servyx and subject to this server's retention policy."),
        "Contents: the droplet's entire boot disk as it was at that instant."
            + (snapshot.MinDiskSize is { } minDisk
                ? string.Create(CultureInfo.InvariantCulture, $" It needs a droplet disk of at least {minDisk} GB to restore onto.")
                : string.Empty),
        "File list: NOT AVAILABLE. DigitalOcean exposes no way to enumerate or extract individual files from a "
        + "snapshot without first restoring it onto a droplet, so Servyx does not claim to know what is inside. "
        + "This is a real difference from an archive-based backup, not an omission.",
        DigitalOceanSnapshotPricing.DescribeMonthlyCost(snapshot.SizeGigabytes),
        "Lifetime: a snapshot never expires on its own. It exists, and bills, until something deletes it — "
        + (snapshot.Artifact.Ownership == BackupOwnership.Servyx
            ? "for this one, that means Servyx's retention policy or a human."
            : "and for this one, only a human, because Servyx never prunes what it did not create."),
        "Restoring from it replaces the droplet's whole boot disk (DataImpact.Destroyed). Preview with "
        + "PlanRestoreAsync before doing anything.",
    ];

    private static string DescribeProviderMessage(DropletActionPoll poll) =>
        poll.Message is { Length: > 0 } message
            ? string.Create(CultureInfo.InvariantCulture, $"DigitalOcean's message: {message}")
            : "DigitalOcean supplied no explanation with the action.";

    private static bool TryReadSnapshotImageId(string snapshotId, out long imageId) =>
        long.TryParse(snapshotId, NumberStyles.None, CultureInfo.InvariantCulture, out imageId) && imageId > 0;

    private static decimal? SumGigabytes(IEnumerable<ResolvedSnapshot> snapshots)
    {
        decimal total = 0m;
        var any = false;

        foreach (var snapshot in snapshots)
        {
            any = true;
            if (snapshot.SizeGigabytes is not { } gigabytes)
            {
                continue;
            }

            total += gigabytes;
        }

        return any ? total : 0m;
    }

    private static long ToBytes(decimal? gigabytes) =>
        gigabytes is { } value && value >= 0m ? (long)decimal.Round(value * BytesPerGigabyte) : 0L;

    private static string Format(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>One snapshot, with both the artifact Servyx reports and the provider fields it was derived from.</summary>
    private sealed record ResolvedSnapshot(
        BackupArtifact Artifact,
        string SnapshotId,
        string Name,
        string? ResourceType,
        string? ResourceId,
        IReadOnlyList<string> Tags,
        decimal? SizeGigabytes,
        int? MinDiskSize);

    private sealed record PendingRestore(
        RestorePlan Plan,
        string ServerId,
        string SnapshotId,
        DateTimeOffset CreatedAt,
        decimal? SizeGigabytes,
        DateTimeOffset SnapshotCreatedAt);
}

/// <summary>
/// What a server's DigitalOcean snapshots cost per month, split by whether Servyx owns them.
/// </summary>
/// <param name="ServyxOwnedCount">How many snapshots Servyx created and manages under retention.</param>
/// <param name="ForeignCount">How many snapshots Servyx did not create and will never delete.</param>
/// <param name="ServyxOwnedMonthly">The monthly list price of the Servyx-owned snapshots.</param>
/// <param name="ForeignMonthly">
/// The monthly list price of the foreign ones. Reported separately and never summed silently into the first
/// figure: it is a real charge on the account, but it is not a charge Servyx's retention will ever reduce.
/// </param>
/// <param name="AnySizeUnknown">
/// Whether DigitalOcean had not yet reported a size for at least one snapshot, so the figures are a lower
/// bound rather than a total.
/// </param>
public sealed record SnapshotStorageCost(
    int ServyxOwnedCount,
    int ForeignCount,
    CostEstimate ServyxOwnedMonthly,
    CostEstimate ForeignMonthly,
    bool AnySizeUnknown);
