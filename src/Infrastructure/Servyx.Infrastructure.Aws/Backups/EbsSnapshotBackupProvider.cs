using System.Globalization;

using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.Aws.Backups;

/// <summary>
/// An <see cref="IBackupProvider"/> whose artifacts are crash-consistent sets of AWS <em>EBS snapshots</em>
/// covering every disk attached to one EC2 instance.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A backup here is a SET of snapshots, and that is the first thing to understand.</strong> A
/// DigitalOcean droplet has one boot disk, so one droplet snapshot is one backup. An EC2 instance has a root
/// volume and may have any number of data volumes — and on a game server, the data volume is where the saves
/// are. So a "backup of this server" in this adapter means <strong>every EBS volume currently attached to the
/// instance</strong>, captured together, and nothing less. Backing up only the root would be the worse default
/// in the only direction that matters: an operator who reads "backup succeeded" and later discovers the world
/// data was never in it has lost the thing they were protecting. The opposite mistake — capturing more than
/// strictly needed — costs money, and money is recoverable.
/// </para>
/// <para>
/// <strong>What is NOT in the set, stated plainly.</strong> Instance-store (ephemeral NVMe) volumes are not EBS
/// and cannot be snapshotted at all; anything living on one is outside every backup this adapter takes.
/// Volumes attached <em>after</em> a capture are obviously not in it, and volumes detached before it are not
/// either. RAM and process state are not captured. Nothing outside the instance — an RDS database, an EFS
/// mount, an S3 bucket — is captured. <see cref="InspectAsync"/> says all of this for a specific backup, with
/// the actual volume list.
/// </para>
/// <para>
/// <strong>Consistency: AWS offers exactly one atomic multi-volume snapshot API, and it is crash-consistent,
/// not application-consistent.</strong> <c>CreateSnapshot</c> takes one volume, so snapshotting several
/// volumes with it means several calls at several instants — copies that were never a single point in time.
/// <c>CreateSnapshots</c> (plural) takes an instance and captures all its EBS volumes as one set, and that is
/// what this adapter uses. What that buys is <em>crash consistency</em>: the set is equivalent to the state
/// the disks would be in if the power cord were pulled at that instant, across all volumes at once. What it
/// does <strong>not</strong> buy is application consistency — the workload is not quiesced, its in-memory
/// buffers are not flushed, and a save file that was half-written at that instant is captured half-written.
/// A database or a game server with an unflushed write buffer can therefore need recovery on restore, exactly
/// as it would after a power cut. Getting application consistency means stopping the workload (or hooking
/// pre/post scripts through AWS Systems Manager) before the capture, which this adapter deliberately does not
/// do: stopping a running game server as a side effect of "take a backup" is not a decision a backup provider
/// gets to make. That caveat is written into <see cref="InspectAsync"/> and
/// <see cref="PlanRestoreAsync"/> rather than left in this file.
/// </para>
/// <para>
/// <strong>Foreign snapshots are never deleted, and that is structural.</strong> An AWS account contains
/// snapshots Servyx did not create — taken by hand, taken by AWS Backup or Data Lifecycle Manager, backing an
/// AMI, or left over from a machine that no longer exists. Three independent barriers stand between
/// <see cref="PruneAsync"/> and one of them, each sufficient on its own:
/// </para>
/// <list type="number">
/// <item><description>
/// <em>Partition.</em> <see cref="PruneAsync"/> splits the listing by <see cref="BackupArtifact.Ownership"/> in
/// one place and passes only the <see cref="BackupOwnership.Servyx"/> half onward. The foreign half is reduced
/// to <see cref="PruneResult.SkippedForeign"/> and then goes out of scope — it is never bound to a variable any
/// deletion code can see, under either value of <c>dryRun</c>.
/// </description></item>
/// <item><description>
/// <em>Evaluation.</em> <see cref="EbsSnapshotRetentionEvaluator.SelectForRemoval"/> throws
/// <see cref="ForeignEbsSnapshotProtectedException"/> if a foreign artifact reaches it, so retention cannot
/// even be <em>computed</em> over one. This is what makes the guarantee hold for <c>dryRun: true</c> as
/// strongly as for <c>dryRun: false</c>: a dry run's report comes from the same call, so there is no path that
/// "hypothetically" schedules a foreign snapshot for deletion.
/// </description></item>
/// <item><description>
/// <em>Deletion.</em> <see cref="DeleteServyxOwnedSetAsync"/> is the only method in this type that issues a
/// <c>DeleteSnapshot</c>, and it re-derives ownership for <em>every</em> member of the set from the live
/// snapshots' tags through <see cref="EbsSnapshotOwnership.Classify"/> — all four marks — before deleting any
/// of them. A set with one mislabelled member deletes nothing at all.
/// </description></item>
/// </list>
/// <para>
/// <strong>Creating a backup costs money and takes minutes.</strong> <c>CreateSnapshots</c> returns while every
/// snapshot is still <c>pending</c>, so <see cref="CreateAsync"/> polls until AWS reports <em>all</em> of them
/// <c>completed</c> and returns an artifact only then. Snapshots still pending when the polls are spent raise
/// <see cref="EbsSnapshotNotConfirmedException"/> naming the ids that exist and are billing — never a
/// successful <see cref="BackupArtifact"/>. And a snapshot that exists bills per GB-month for as long as it
/// exists, with no expiry: see <see cref="EbsSnapshotPricing"/>, which every figure this type produces is
/// labelled through, including the fact that incremental storage makes a naive per-GB number a ceiling rather
/// than a price.
/// </para>
/// <para>
/// <strong>Restore is a genuinely different shape here, and this type refuses rather than pretending.</strong>
/// An EBS snapshot does not restore in place: it restores by creating a <em>new volume</em>, which then has to
/// be attached, and swapping a restored root volume under a running instance means stopping the instance,
/// detaching, attaching and starting. <see cref="PlanRestoreAsync"/> is fully supported, issues only reads, and
/// spells out that exact procedure with the real snapshot ids, volume ids, devices and availability zone.
/// <see cref="RestoreAsync(string, CancellationToken)"/> always throws
/// <see cref="EbsSnapshotRestoreNotPerformedException"/>. See that member for why doing half of it would be
/// worse than refusing.
/// </para>
/// <para>
/// <strong>There is no force path.</strong> No argument here skips a guard, and no combination of arguments
/// reaches a provider call the checks below would otherwise refuse.
/// </para>
/// <para>
/// <strong>Not registered anywhere.</strong> See <see cref="EbsSnapshotBackups"/>: snapshotting and pruning are
/// mutating, billable capabilities, so this type is opt-in and unreachable from any composition root that does
/// not name it. A host with <c>Servyx:Provisioning:Enabled</c> unset never reaches it, and nothing in this
/// repository constructs one outside its tests.
/// </para>
/// </remarks>
public sealed class EbsSnapshotBackupProvider : IBackupProvider
{
    /// <summary>The default interval between reads of a pending snapshot set.</summary>
    public static readonly TimeSpan DefaultSnapshotPollInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The default number of reads made before a snapshot set is reported as not confirmed. Eighty reads
    /// fifteen seconds apart is twenty minutes, which is the order of magnitude a first snapshot of a
    /// multi-gigabyte game server disk takes; a later incremental one is usually far quicker.
    /// </summary>
    public const int DefaultSnapshotPollAttempts = 80;

    private const decimal BytesPerGibibyte = 1_073_741_824m;

    private readonly Ec2QueryApiClient _api;
    private readonly IEbsSnapshotContextSource _contexts;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly int _pollAttempts;
    private readonly string _region;

    /// <summary>Creates a provider over one AWS account and one region.</summary>
    /// <param name="httpClient">The HTTP client the API calls go out on. Substituted in tests; no account is required.</param>
    /// <param name="secretStore">Where the AWS key pair lives. Resolved per request and never cached.</param>
    /// <param name="identity">The URNs of the key pair. Only URNs are held.</param>
    /// <param name="region">The AWS region the instance and its snapshots live in.</param>
    /// <param name="contexts">Maps a Servyx server id to the EC2 instance that backs it.</param>
    /// <param name="timeProvider">Clock used for set naming and poll pacing.</param>
    /// <param name="snapshotPollInterval">How long to wait between reads of a pending set. Defaults to <see cref="DefaultSnapshotPollInterval"/>.</param>
    /// <param name="snapshotPollAttempts">How many reads to make before reporting a set unconfirmed.</param>
    /// <param name="endpoint">Overrides the regional EC2 endpoint. For tests; production passes <see langword="null"/>.</param>
    public EbsSnapshotBackupProvider(
        HttpClient httpClient,
        ISecretStore secretStore,
        AwsSigningIdentity identity,
        string region,
        IEbsSnapshotContextSource contexts,
        TimeProvider? timeProvider = null,
        TimeSpan? snapshotPollInterval = null,
        int snapshotPollAttempts = DefaultSnapshotPollAttempts,
        Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentOutOfRangeException.ThrowIfLessThan(snapshotPollAttempts, 1);

        _contexts = contexts;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollInterval = snapshotPollInterval ?? DefaultSnapshotPollInterval;
        _pollAttempts = snapshotPollAttempts;
        _region = region;

        _api = new Ec2QueryApiClient(
            httpClient,
            new AwsRequestSigner(secretStore, identity, region, Ec2QueryApiClient.ServiceName, _timeProvider),
            region,
            endpoint);
    }

    /// <summary>The AWS region this provider's snapshots live in.</summary>
    public string Region => _region;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Every attached EBS volume, in one call, or nothing.</strong> The sequence is: read the instance,
    /// enumerate the EBS volumes its block device mapping attaches, submit one <c>CreateSnapshots</c> naming
    /// the instance, poll every snapshot it produced to a terminal state, and only then report a backup. If AWS
    /// returns fewer snapshots than the instance has EBS volumes, that is a partial capture and it is refused
    /// with <see cref="EbsSnapshotFailedException"/> naming what does exist — a set missing the data volume is
    /// not a backup of the server, and reporting it as one is the specific data-loss trap this adapter exists
    /// to avoid.
    /// </para>
    /// <para>
    /// <strong>Submission is not success.</strong> <c>CreateSnapshots</c> answers while every snapshot is
    /// <c>pending</c>. Snapshots AWS never reports <c>completed</c> raise
    /// <see cref="EbsSnapshotNotConfirmedException"/>; an <c>error</c> state raises
    /// <see cref="EbsSnapshotFailedException"/>. Neither returns an artifact, because neither is evidence that
    /// a restorable backup exists. Both name the snapshot ids, because in both cases snapshots exist and are
    /// billing.
    /// </para>
    /// <para>
    /// <strong>The ownership marks travel in the create call and are verified afterwards anyway.</strong>
    /// <c>CreateSnapshots</c> takes a <c>TagSpecification</c>, so there is no window in which a billing
    /// snapshot exists untagged — a real improvement over the DigitalOcean adapter, which has to tag after the
    /// fact. The verification is still performed: a snapshot Servyx cannot re-derive ownership for would be
    /// unprunable and would bill forever, so that outcome is raised as
    /// <see cref="EbsSnapshotOwnershipNotRecordedException"/> rather than returned as a backup.
    /// </para>
    /// </remarks>
    public async Task<BackupArtifact> CreateAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var instance = await RequireLiveInstanceAsync(context, ct).ConfigureAwait(false);

        var volumeIds = AttachedVolumeIds(instance);
        if (volumeIds.Count == 0)
        {
            throw new EbsSnapshotFailedException(
                $"EC2 instance {context.Ec2InstanceId} (server '{context.ServerId}') has no EBS volumes attached, so "
                + "there is nothing for an EBS snapshot to capture and no backup was taken. If the workload's data "
                + "is on an instance-store (ephemeral NVMe) volume, note that instance store CANNOT be snapshotted "
                + "by any AWS API — that data is not backed up by this provider and never will be.");
        }

        var before = await ListResolvedAsync(context, instance, ct).ConfigureAwait(false);
        var isFirstOfChain = !before.Any(b => b.Artifact.Ownership == BackupOwnership.Servyx);

        var takenAt = _timeProvider.GetUtcNow();
        var setName = EbsSnapshotOwnership.FormatSetName(context.ServerId, takenAt);
        var tags = EbsSnapshotOwnership.BuildTags(
            context.ServerId,
            context.Ec2InstanceId,
            context.JobId,
            context.ConnectorId,
            setName);

        var submitted = await _api
            .CreateSnapshotsAsync(CreateSnapshotsParameters(context.Ec2InstanceId, setName, tags), ct)
            .ConfigureAwait(false);

        var submittedIds = submitted.Select(s => s.SnapshotId).ToList();

        if (submittedIds.Count == 0)
        {
            throw new EbsSnapshotFailedException(
                $"AWS accepted the CreateSnapshots call for instance {context.Ec2InstanceId} but reported no "
                + "snapshots, so Servyx has no ids to poll, to record, or to clean up. Snapshots may nonetheless "
                + $"exist and be billing; they would carry the tag '{EbsSnapshotOwnership.SetTag}={setName}'. "
                + "Reconcile by tag before retrying.",
                submittedIds);
        }

        if (submittedIds.Count != volumeIds.Count)
        {
            throw new EbsSnapshotFailedException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"CreateSnapshots covered {submittedIds.Count} of instance {context.Ec2InstanceId}'s "
                    + $"{volumeIds.Count} attached EBS volume(s), so the set is INCOMPLETE and is NOT reported as a "
                    + $"backup of server '{context.ServerId}': restoring from a set that is missing a volume "
                    + $"reconstructs a machine that never existed. ")
                + "The snapshots that were created DO exist and ARE billing: "
                + string.Join(", ", submittedIds)
                + $". They carry the tag '{EbsSnapshotOwnership.SetTag}={setName}' and can be found and removed by "
                + "it. Attached volumes were: " + string.Join(", ", volumeIds) + ".",
                submittedIds);
        }

        var completed = await PollToCompletionAsync(context, setName, submittedIds, ct).ConfigureAwait(false);

        var owned = VerifyOwned(context, setName, completed, isFirstOfChain);
        return BuildSetArtifact(context, setName, owned).Artifact;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Lists this server's EBS snapshots, Servyx-owned and foreign alike, each labelled at the point it is
    /// classified rather than inferred later. Servyx's own snapshots are grouped into the backup sets they were
    /// created in and reported as one artifact each; a foreign snapshot is reported on its own, because Servyx
    /// has no grounds to assert that two snapshots it did not create belong together.
    /// </para>
    /// <para>
    /// <strong>Two listings, unioned, and both are needed.</strong> One filters on
    /// <see cref="EbsSnapshotOwnership.SourceInstanceTag"/> and finds every snapshot Servyx took of this
    /// instance, including snapshots of a volume that has since been detached — which a listing keyed on the
    /// live attachment set would miss, leaving them unprunable and billing forever. The other filters on the
    /// volumes currently attached and finds snapshots Servyx did <em>not</em> take of those volumes, which the
    /// tag listing structurally cannot see. Reporting <c>SkippedForeign: 0</c> for an account full of
    /// hand-taken snapshots because Servyx only looked at its own tags would be technically true and
    /// substantively a lie.
    /// </para>
    /// <para>
    /// Snapshots of <em>other</em> instances are not this server's backups and are never returned — which is
    /// also what keeps one server's retention from ever seeing another server's snapshots.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<BackupArtifact>> ListAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var resolved = await ListResolvedAsync(context, ct).ConfigureAwait(false);

        return resolved.Select(b => b.Artifact).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>A snapshot has no readable index, and this does not invent one.</strong> The Docker and SSH
    /// providers answer this by reading tar headers, because their artifacts are archives they can open. AWS
    /// exposes no way to enumerate a file inside an EBS snapshot without first creating a volume from it and
    /// mounting that volume, so what comes back here is a description of the backup — which volumes it covers,
    /// at which devices, when it was taken, how consistent it is, what it does not cover, what it costs, and
    /// who owns it — and it says outright that the file list is not available. A plausible-looking fabricated
    /// listing would be worse than no listing, because someone would plan a restore around it.
    /// </para>
    /// <para>Read-only: <c>GET</c>s only, no mutation of any kind.</para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> InspectAsync(string backupId, CancellationToken ct = default)
    {
        var (context, backup, instance) = await ResolveAsync(backupId, ct).ConfigureAwait(false);
        return Describe(context, backup, instance);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Read-only, and blunt about what a restore is.</strong> This issues <c>GET</c>s and nothing else.
    /// The returned <see cref="RestorePlan.AffectedPaths"/> is not a file list — restoring from an EBS snapshot
    /// does not overwrite selected paths, and in fact does not overwrite anything by itself — so the entries
    /// state the real procedure instead: each snapshot becomes a <em>new</em> volume, in a named availability
    /// zone, which must then be attached at the device the original was at, which for the root volume means
    /// stopping the instance first.
    /// </para>
    /// <para>
    /// <strong>The plan is honest that this provider will not carry it out.</strong> Every entry that describes
    /// a mutating step names it as something the operator or the provisioning path does, not something
    /// <see cref="RestoreAsync(string, CancellationToken)"/> does — because that member always refuses. The
    /// plan is therefore written to be executable by hand: real snapshot ids, real volume ids, real device
    /// names, the real availability zone, in order.
    /// </para>
    /// <para>
    /// No plan state is retained. A plan that cannot be applied has nothing to expire, and a single-use token
    /// for an operation that never runs would be theatre.
    /// </para>
    /// </remarks>
    public async Task<RestorePlan> PlanRestoreAsync(string backupId, CancellationToken ct = default)
    {
        var (context, backup, instance) = await ResolveAsync(backupId, ct).ConfigureAwait(false);

        return new RestorePlan(
            $"restore-{Guid.NewGuid():n}",
            backup.Artifact.Id,
            DescribeRestore(context, backup, instance));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>This member always refuses, and the reason is different from — and stronger than — the
    /// DigitalOcean adapter's.</strong> There, restore is one API call and the refusal is about consent: the
    /// signature cannot carry an acknowledgement of data loss. Here the refusal is about the operation itself.
    /// Restoring from an EBS snapshot creates a <em>new volume</em>; putting that volume back under the running
    /// instance means stopping the instance, detaching the current volume, attaching the new one at the same
    /// device, and starting again. There is no single call, there is no in-place overwrite, and there is
    /// unavoidable downtime. A method that returned successfully would be claiming something that did not
    /// happen.
    /// </para>
    /// <para>
    /// <strong>Doing half of it would be worse than refusing.</strong> The tempting middle ground is to create
    /// the volumes and stop. That leaves unattached volumes billing per GB-month, next to an instance still
    /// running on its original disks, having returned success from a method called "restore" — and the caller
    /// would reasonably believe the server was restored. The honest answer is to refuse, and to make
    /// <see cref="PlanRestoreAsync"/> good enough that the refusal is not obstructive: it names every snapshot,
    /// the volume and device each came from, and the ordered procedure.
    /// </para>
    /// <para>
    /// Swapping a restored volume under an instance is a lifecycle operation, and it belongs to the
    /// provisioning path that already gates destructive changes behind a <see cref="DataImpact"/>
    /// acknowledgement — not to a backup provider. No HTTP request of any kind is issued by this method.
    /// </para>
    /// </remarks>
    /// <exception cref="EbsSnapshotRestoreNotPerformedException">Always.</exception>
    public Task RestoreAsync(string restorePlanId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restorePlanId);

        throw new EbsSnapshotRestoreNotPerformedException(
            $"Restore plan '{restorePlanId}' was NOT carried out, and this provider never carries one out. "
            + "Restoring from an EBS snapshot is not an in-place operation and is not a single API call: each "
            + "snapshot restores by CREATING A NEW VOLUME (CreateVolume), which must then be attached "
            + "(AttachVolume) — and putting a restored ROOT volume back under the instance additionally requires "
            + "stopping the instance, detaching the current root, attaching the new volume at the same device, and "
            + "starting the instance again. Servyx will not report success for a sequence it did not perform, and "
            + "will not perform half of it: creating the volumes and stopping there would leave unattached volumes "
            + "billing per GB-month beside an instance still running on its original disks. Nothing was sent to "
            + "AWS, no volume was created, and no disk was touched. Call PlanRestoreAsync for the exact ordered "
            + "procedure, with the real snapshot ids, volume ids, devices and availability zone.",
            restorePlanId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// See the type remarks for the three barriers that make foreign snapshots unprunable. Under
    /// <c>dryRun: true</c> this issues no <c>DeleteSnapshot</c> of any kind; under either flag,
    /// <see cref="PruneResult.SkippedForeign"/> reports how many foreign <em>snapshots</em> were seen and left
    /// alone — snapshots and not sets, because a foreign snapshot is never grouped into a set. A snapshot that
    /// has already vanished provider-side answers <c>InvalidSnapshot.NotFound</c> to the delete and is still
    /// reported as removed: it is gone, which is the outcome retention asked for, and pretending otherwise
    /// would leave the caller expecting a charge that has already stopped.
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
        var skippedForeign = all
            .Where(b => b.Artifact.Ownership == BackupOwnership.Foreign)
            .Sum(b => b.Snapshots.Count);

        var ownedByServyx = all
            .Where(b => b.Artifact.Ownership == BackupOwnership.Servyx)
            .ToList();

        // Barrier 2: evaluation. SelectForRemoval throws on anything not Servyx-owned, so a dry run and a live
        // run compute their answer from the identical, ownership-asserting call.
        var removals = EbsSnapshotRetentionEvaluator.SelectForRemoval(
            ownedByServyx.Select(b => b.Artifact).ToList(),
            effectivePolicy);

        var removalIds = removals.Select(a => a.Id).ToList();
        if (dryRun)
        {
            return new PruneResult(removalIds, skippedForeign);
        }

        foreach (var removal in removals)
        {
            var resolved = ownedByServyx.First(b => string.Equals(b.Artifact.Id, removal.Id, StringComparison.Ordinal));
            await DeleteServyxOwnedSetAsync(context, resolved, ct).ConfigureAwait(false);
        }

        return new PruneResult(removalIds, skippedForeign);
    }

    /// <summary>
    /// An upper bound on what this server's EBS snapshots cost per month, split by ownership.
    /// </summary>
    /// <remarks>
    /// A ceiling and never a price — see <see cref="EbsSnapshotPricing"/> for why AWS does not let this adapter
    /// compute a real figure, and why the incremental billing model means the real figure is normally far
    /// lower. A snapshot's charge recurs for as long as it exists, so "what am I paying for backups" is a
    /// question this adapter has to be able to answer at all; answering it with a number that overstates is
    /// tolerable only because the overstatement is stated. Read-only.
    /// </remarks>
    /// <param name="serverId">The Servyx server.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<EbsSnapshotStorageCeiling> EstimateStorageCeilingAsync(
        string serverId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var all = await ListResolvedAsync(context, ct).ConfigureAwait(false);

        var servyx = all.Where(b => b.Artifact.Ownership == BackupOwnership.Servyx).ToList();
        var foreign = all.Where(b => b.Artifact.Ownership == BackupOwnership.Foreign).ToList();

        return new EbsSnapshotStorageCeiling(
            servyx.Count,
            foreign.Sum(b => b.Snapshots.Count),
            EbsSnapshotPricing.Ceiling(SumGibibytes(servyx)),
            EbsSnapshotPricing.Ceiling(SumGibibytes(foreign)),
            all.SelectMany(b => b.Snapshots).Any(s => s.VolumeSizeGib is null));
    }

    /// <summary>
    /// Barrier 3: the only method in this type that issues a <c>DeleteSnapshot</c>.
    /// </summary>
    /// <remarks>
    /// It re-derives ownership for every member of the set from the live snapshots' tags, through
    /// <see cref="EbsSnapshotOwnership.Classify"/>, rather than trusting the label it was handed — and it does
    /// so for <em>all</em> members before deleting <em>any</em>, so a set with one mislabelled member deletes
    /// nothing rather than deleting the ones that happened to check out first. A mislabelled or out-of-scope
    /// artifact throws <see cref="ForeignEbsSnapshotProtectedException"/>, so even a caller that fabricated an
    /// artifact carrying <see cref="BackupOwnership.Servyx"/> could not route a delete at somebody else's
    /// snapshot.
    /// </remarks>
    private async Task DeleteServyxOwnedSetAsync(
        EbsSnapshotContext context,
        ResolvedBackup backup,
        CancellationToken ct)
    {
        if (backup.Artifact.Ownership != BackupOwnership.Servyx)
        {
            throw new ForeignEbsSnapshotProtectedException(
                $"Refusing to delete backup '{backup.Artifact.Id}': it is {backup.Artifact.Ownership}, not "
                + "Servyx-owned. Deleting an EBS snapshot cannot be undone.",
                backup.Artifact.Location);
        }

        foreach (var snapshot in backup.Snapshots)
        {
            var rederived = EbsSnapshotOwnership.Classify(snapshot.Tags, context.ServerId, context.Ec2InstanceId);

            if (rederived != BackupOwnership.Servyx)
            {
                throw new ForeignEbsSnapshotProtectedException(
                    $"Refusing to delete backup '{backup.Artifact.Id}': snapshot '{snapshot.SnapshotId}' was "
                    + $"presented as Servyx-owned, but its live tags do not carry Servyx's four marks for server "
                    + $"'{context.ServerId}' on instance '{context.Ec2InstanceId}' (tags: "
                    + $"{RenderTags(snapshot.Tags)}). No snapshot in this set was deleted. Deleting an EBS snapshot "
                    + "cannot be undone.",
                    snapshot.Artifact.Location);
            }
        }

        foreach (var snapshot in backup.Snapshots)
        {
            await _api.DeleteSnapshotAsync(snapshot.SnapshotId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Polls a submitted set until AWS reports every snapshot in it terminal.</summary>
    private async Task<IReadOnlyList<Ec2Snapshot>> PollToCompletionAsync(
        EbsSnapshotContext context,
        string setName,
        IReadOnlyList<string> snapshotIds,
        CancellationToken ct)
    {
        IReadOnlyList<Ec2Snapshot> latest = [];
        var polls = 0;

        for (; polls < _pollAttempts; polls++)
        {
            if (polls > 0 && _pollInterval > TimeSpan.Zero)
            {
                await Task.Delay(_pollInterval, _timeProvider, ct).ConfigureAwait(false);
            }

            latest = await ReadSubmittedAsync(context, setName, snapshotIds, ct).ConfigureAwait(false);

            if (latest.FirstOrDefault(s => s.IsErrored) is { } errored)
            {
                throw new EbsSnapshotFailedException(
                    $"AWS reported snapshot {errored.SnapshotId} of instance {context.Ec2InstanceId} as "
                    + $"'{Ec2Snapshot.ErrorState}', so no backup was taken for server '{context.ServerId}'. "
                    + (errored.StateMessage is { Length: > 0 } reason
                        ? "AWS's reason: " + reason + " "
                        : "AWS supplied no explanation. ")
                    + "The other snapshots in the set may have completed; they exist and are billing: "
                    + string.Join(", ", snapshotIds)
                    + $". They carry the tag '{EbsSnapshotOwnership.SetTag}={setName}'. Servyx does NOT report a "
                    + "partial set as a backup, and does not delete these automatically — they are Servyx-owned and "
                    + "will be considered by the next retention pass.",
                    snapshotIds);
            }

            if (latest.Count == snapshotIds.Count && latest.All(s => s.IsCompleted))
            {
                return latest;
            }
        }

        var pending = latest
            .Where(s => !s.IsCompleted)
            .Select(s => string.Create(
                CultureInfo.InvariantCulture,
                $"{s.SnapshotId} ({s.State ?? "no state"}{(s.Progress is { Length: > 0 } p ? ", " + p : string.Empty)})"))
            .ToList();

        throw new EbsSnapshotNotConfirmedException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"AWS accepted a snapshot of instance {context.Ec2InstanceId} and was still reporting "
                + $"{pending.Count} of its {snapshotIds.Count} snapshot(s) as unfinished after {polls} check(s): ")
            + string.Join(", ", pending)
            + $". No backup is being reported for server '{context.ServerId}': snapshots that were only submitted "
            + "are not a backup that exists. The copies are most likely still running at AWS and may yet finish — "
            + "which is NOT the same as a failure and calls for the opposite response. Do not resubmit blindly: a "
            + "second set that completes alongside the first leaves two sets, both billing per GB-month. These "
            + $"snapshots exist and are billing now: {string.Join(", ", snapshotIds)}, tagged "
            + $"'{EbsSnapshotOwnership.SetTag}={setName}'. Watch them in the EC2 console, or list this server's "
            + "backups, before acting further.",
            snapshotIds,
            submitted: true);
    }

    /// <summary>
    /// Reads back the submitted snapshots, treating one that AWS no longer knows as the honest failure it is.
    /// </summary>
    private async Task<IReadOnlyList<Ec2Snapshot>> ReadSubmittedAsync(
        EbsSnapshotContext context,
        string setName,
        IReadOnlyList<string> snapshotIds,
        CancellationToken ct)
    {
        IReadOnlyList<Ec2Snapshot> latest;

        try
        {
            latest = await _api.DescribeSnapshotsByIdsAsync(snapshotIds, ct).ConfigureAwait(false);
        }
        catch (AwsApiException e) when (string.Equals(e.ErrorCode, "InvalidSnapshot.NotFound", StringComparison.Ordinal))
        {
            throw VanishedDuringCreate(context, setName, snapshotIds, [], e);
        }

        if (latest.Count < snapshotIds.Count)
        {
            throw VanishedDuringCreate(
                context,
                setName,
                snapshotIds,
                latest.Select(s => s.SnapshotId).ToList(),
                inner: null);
        }

        return latest;
    }

    private static EbsSnapshotFailedException VanishedDuringCreate(
        EbsSnapshotContext context,
        string setName,
        IReadOnlyList<string> snapshotIds,
        IReadOnlyList<string> stillPresent,
        Exception? inner)
    {
        var missing = snapshotIds.Except(stillPresent, StringComparer.Ordinal).ToList();

        var message =
            $"A snapshot AWS created for server '{context.ServerId}' has vanished from instance "
            + $"{context.Ec2InstanceId}'s set between the create and a poll of it"
            + (missing.Count > 0 ? ": " + string.Join(", ", missing) : string.Empty)
            + ". Something outside Servyx deleted it — the console, another tool, or a lifecycle policy. The set is "
            + "therefore INCOMPLETE and is NOT reported as a backup: a set missing a volume restores a machine that "
            + "never existed. "
            + (stillPresent.Count > 0
                ? "The remaining snapshots DO exist and ARE billing: " + string.Join(", ", stillPresent) + ". "
                : "Servyx can no longer see any of the set's snapshots; check the account before assuming none "
                  + "exist. ")
            + $"The set is tagged '{EbsSnapshotOwnership.SetTag}={setName}'.";

        return inner is null
            ? new EbsSnapshotFailedException(message, snapshotIds)
            : new EbsSnapshotFailedException(message, inner);
    }

    /// <summary>Re-derives ownership over a freshly-created set, refusing to claim one it cannot prove.</summary>
    private IReadOnlyList<ResolvedSnapshot> VerifyOwned(
        EbsSnapshotContext context,
        string setName,
        IReadOnlyList<Ec2Snapshot> snapshots,
        bool isFirstOfChain)
    {
        var resolved = snapshots.Select(s => Resolve(context, s)).ToList();
        var unowned = resolved.Where(r => r.Artifact.Ownership != BackupOwnership.Servyx).ToList();

        if (unowned.Count > 0)
        {
            throw new EbsSnapshotOwnershipNotRecordedException(
                $"Snapshots of instance {context.Ec2InstanceId} WERE taken and exist at AWS, but Servyx could not "
                + $"verify {unowned.Count} of them as its own, so this is not a managed backup of server "
                + $"'{context.ServerId}'. Servyx never deletes a snapshot it cannot prove it owns, so retention "
                + "will NEVER remove these: they will bill until somebody deletes them by hand. Unverifiable: "
                + string.Join(
                    "; ",
                    unowned.Select(u => $"{u.SnapshotId} (tags: {RenderTags(u.Tags)})"))
                + $". The whole set carries '{EbsSnapshotOwnership.SetTag}={setName}' and is: "
                + string.Join(", ", resolved.Select(r => r.SnapshotId))
                + ". " + EbsSnapshotPricing.DescribeMonthlyCeiling(SumGibibytes(resolved), isFirstOfChain),
                resolved.Select(r => r.SnapshotId).ToList());
        }

        return resolved;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> CreateSnapshotsParameters(
        string ec2InstanceId,
        string setName,
        IReadOnlyDictionary<string, string> tags)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("InstanceSpecification.InstanceId", ec2InstanceId),

            // Explicit, not defaulted. This one flag is the difference between "every disk" and "every disk
            // except the one the operating system is on", and leaving it to a service default would make the
            // scope of a Servyx backup depend on an AWS decision nobody here would notice changing.
            new("InstanceSpecification.ExcludeBootVolume", "false"),

            new("Description", setName),
            new("TagSpecification.1.ResourceType", EbsSnapshotOwnership.TagResourceType),
        };

        var index = 1;
        foreach (var tag in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var prefix = "TagSpecification.1.Tag." + index.ToString(CultureInfo.InvariantCulture) + ".";
            parameters.Add(new KeyValuePair<string, string>(prefix + "Key", tag.Key));
            parameters.Add(new KeyValuePair<string, string>(prefix + "Value", tag.Value));
            index++;
        }

        return parameters;
    }

    private async Task<EbsSnapshotContext> GetContextAsync(string serverId, CancellationToken ct)
    {
        var context = await _contexts.GetAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new EbsSnapshotNotFoundException(
                $"No EBS snapshot context is configured for server '{serverId}', so Servyx does not know which EC2 "
                + "instance backs it.");

        if (!EbsSnapshotOwnership.IsSupportedServerId(context.ServerId))
        {
            throw new ArgumentException(
                $"Server id '{context.ServerId}' cannot be carried in an EBS snapshot set name or tag, so a snapshot "
                + "taken for it could never be recognised as Servyx's afterwards — it would bill forever and never "
                + "be pruned. Ids may contain only letters, digits, '-', '_' and '.'.",
                nameof(serverId));
        }

        if (string.IsNullOrWhiteSpace(context.Ec2InstanceId))
        {
            throw new ArgumentException(
                $"Server '{context.ServerId}' maps to no EC2 instance id, so Servyx cannot tell which machine's "
                + "volumes to snapshot — or, far worse, whose snapshots retention would be entitled to delete.",
                nameof(serverId));
        }

        return context;
    }

    private async Task<Ec2Instance> RequireLiveInstanceAsync(EbsSnapshotContext context, CancellationToken ct)
    {
        var instance = await _api.DescribeInstanceAsync(context.Ec2InstanceId, ct).ConfigureAwait(false);

        if (instance is null || instance.IsGone)
        {
            throw new EbsSnapshotNotFoundException(
                $"EC2 no longer reports instance {context.Ec2InstanceId} for server '{context.ServerId}'"
                + (instance is null ? " at all" : $"; it is '{instance.State}'")
                + ", so there is nothing to snapshot. Note that snapshots already taken of it are NOT affected: "
                + "they survive the instance's termination, still exist, and still bill.");
        }

        return instance;
    }

    private static List<string> AttachedVolumeIds(Ec2Instance? instance) =>
        instance is null
            ? []
            : instance.BlockDevices
                .Select(d => d.VolumeId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

    private async Task<IReadOnlyList<ResolvedBackup>> ListResolvedAsync(
        EbsSnapshotContext context,
        CancellationToken ct)
    {
        // A terminated instance is not an error on a read path. Its snapshots outlive it, still exist and still
        // bill, so retention must be able to reach them — see the tag listing below, which does not depend on
        // the machine existing at all.
        var instance = await _api.DescribeInstanceAsync(context.Ec2InstanceId, ct).ConfigureAwait(false);
        return await ListResolvedAsync(context, instance, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ResolvedBackup>> ListResolvedAsync(
        EbsSnapshotContext context,
        Ec2Instance? instance,
        CancellationToken ct)
    {
        var byTag = await _api
            .DescribeSnapshotsByTagAsync(EbsSnapshotOwnership.SourceInstanceTag, context.Ec2InstanceId, ct)
            .ConfigureAwait(false);

        List<string> attachedVolumeIds = instance is null || instance.IsGone ? [] : AttachedVolumeIds(instance);
        IReadOnlyList<Ec2Snapshot> byVolume = attachedVolumeIds.Count == 0
            ? []
            : await _api.DescribeSnapshotsByVolumeAsync(attachedVolumeIds, ct).ConfigureAwait(false);

        var union = new Dictionary<string, Ec2Snapshot>(StringComparer.Ordinal);
        foreach (var snapshot in byTag.Concat(byVolume))
        {
            union[snapshot.SnapshotId] = snapshot;
        }

        var resolved = union.Values.Select(s => Resolve(context, s)).ToList();

        var sets = resolved
            .Where(r => r.Artifact.Ownership == BackupOwnership.Servyx)
            .GroupBy(r => r.SetName!, StringComparer.Ordinal)
            .Select(g => BuildSetArtifact(context, g.Key, g.OrderBy(r => r.SnapshotId, StringComparer.Ordinal).ToList()))
            .OrderBy(b => b.Artifact.CreatedAt)
            .ThenBy(b => b.Artifact.Id, StringComparer.Ordinal)
            .ToList();

        // "First of the chain" is a property of the whole listing, not of a set on its own: the oldest capture
        // Servyx holds of these volumes is the one whose snapshots stored the volumes' in-use blocks, and every
        // later one stored only what changed since. It is what decides whether the cost ceiling is close to the
        // truth or far above it, so it is computed here rather than guessed at the point a figure is rendered.
        var backups = new List<ResolvedBackup>(
            sets.Select((set, index) => set with { IsFirstOfChain = index == 0 }));

        backups.AddRange(resolved
            .Where(r => r.Artifact.Ownership == BackupOwnership.Foreign)
            .Select(foreign => new ResolvedBackup(foreign.Artifact, foreign.SnapshotId, [foreign])));

        return backups
            .OrderBy(b => b.Artifact.CreatedAt)
            .ThenBy(b => b.Artifact.Id, StringComparer.Ordinal)
            .ToList();
    }

    private ResolvedSnapshot Resolve(EbsSnapshotContext context, Ec2Snapshot snapshot)
    {
        var ownership = EbsSnapshotOwnership.Classify(snapshot.Tags, context.ServerId, context.Ec2InstanceId);
        var setName = ownership == BackupOwnership.Servyx
            ? EbsSnapshotOwnership.ReadSetName(snapshot.Tags)
            : null;

        var artifact = new BackupArtifact(
            EbsSnapshotBackupId.Format(context.ServerId, snapshot.SnapshotId),
            ownership,
            snapshot.StartTime ?? DateTimeOffset.UnixEpoch,
            ToBytes(snapshot.VolumeSizeGib),
            EbsSnapshotBackupId.LocationOfSnapshot(_region, snapshot.SnapshotId));

        return new ResolvedSnapshot(
            artifact,
            snapshot.SnapshotId,
            snapshot.VolumeId,
            snapshot.State,
            snapshot.Description,
            snapshot.VolumeSizeGib,
            snapshot.Tags,
            setName);
    }

    private ResolvedBackup BuildSetArtifact(
        EbsSnapshotContext context,
        string setName,
        IReadOnlyList<ResolvedSnapshot> members)
    {
        // The set's timestamp comes from its name rather than from a member's startTime: every member of a set
        // was created by one call, and reading the instant back off the name Servyx wrote makes the artifact's
        // CreatedAt - which is what retention buckets on - independent of AWS's per-snapshot clock.
        var createdAt = EbsSnapshotOwnership.TryParseSetName(setName, out _, out var named)
            ? named
            : members.Select(m => m.Artifact.CreatedAt).DefaultIfEmpty(DateTimeOffset.UnixEpoch).Min();

        var artifact = new BackupArtifact(
            EbsSnapshotBackupId.Format(context.ServerId, setName),
            BackupOwnership.Servyx,
            createdAt,
            members.Sum(m => m.Artifact.SizeBytes),
            EbsSnapshotBackupId.LocationOfSet(_region, setName));

        return new ResolvedBackup(artifact, setName, members);
    }

    private async Task<(EbsSnapshotContext Context, ResolvedBackup Backup, InstanceFacts Instance)>
        ResolveAsync(string backupId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);

        if (!EbsSnapshotBackupId.TryGetServerId(backupId, out var serverId))
        {
            throw new EbsSnapshotNotFoundException(
                $"Backup id '{backupId}' is not in a form this provider issued.",
                backupId);
        }

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var instance = await _api.DescribeInstanceAsync(context.Ec2InstanceId, ct).ConfigureAwait(false);
        var resolved = await ListResolvedAsync(context, instance, ct).ConfigureAwait(false);

        var backup = resolved.FirstOrDefault(b =>
            string.Equals(b.Artifact.Id, backupId, StringComparison.Ordinal))
            ?? throw new EbsSnapshotNotFoundException(
                $"Backup '{backupId}' does not exist: AWS no longer reports it for server '{serverId}'. It may have "
                + "been deleted in the console, by another tool, or by a prune — or, for a Servyx backup set, one "
                + "of its snapshots may have been removed, which un-makes the set.",
                backupId);

        return (context, backup, InstanceFacts.From(instance));
    }

    /// <summary>
    /// The two things a description needs from the live instance: where each volume is attached, and which
    /// availability zone a restored volume would have to be created in.
    /// </summary>
    /// <remarks>
    /// Both are legitimately absent when the instance has been terminated — its snapshots outlive it — so both
    /// are nullable and every consumer says "not known" rather than inventing a device name or a zone. Getting
    /// the zone wrong on a restore is not a cosmetic error: a volume cannot be attached across availability
    /// zones, so a volume created in the wrong one is an unattachable, billing mistake.
    /// </remarks>
    private sealed record InstanceFacts(
        string? AvailabilityZone,
        IReadOnlyDictionary<string, string> Devices)
    {
        internal static InstanceFacts From(Ec2Instance? instance)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var device in instance?.BlockDevices ?? [])
            {
                if (device.DeviceName is { Length: > 0 } name)
                {
                    map[device.VolumeId] = name;
                }
            }

            return new InstanceFacts(instance is null || instance.IsGone ? null : instance.AvailabilityZone, map);
        }
    }

    private IReadOnlyList<string> Describe(
        EbsSnapshotContext context,
        ResolvedBackup backup,
        InstanceFacts instance)
    {
        var devices = instance.Devices;

        var isServyx = backup.Artifact.Ownership == BackupOwnership.Servyx;

        var lines = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"AWS EBS backup '{backup.Key}' of EC2 instance {context.Ec2InstanceId} in {_region}, taken "
                + $"{Format(backup.Artifact.CreatedAt)}. It is {backup.Snapshots.Count} EBS snapshot(s)."),

            $"Ownership: {backup.Artifact.Ownership}."
                + (isServyx
                    ? " Created by Servyx and subject to this server's retention policy."
                    : " Servyx did not create this snapshot and will never delete it — it is listed and "
                      + "inspectable, and retention cannot reach it."),
        };

        foreach (var snapshot in backup.Snapshots)
        {
            var volume = snapshot.VolumeId ?? "(volume unknown)";
            var device = snapshot.VolumeId is { } id && devices.TryGetValue(id, out var name)
                ? name
                : "not currently attached to this instance";

            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  {snapshot.SnapshotId}: from volume {volume} ({device}), source volume size "
                + $"{(snapshot.VolumeSizeGib is { } gib ? gib + " GiB" : "not reported")}, state "
                + $"{snapshot.State ?? "not reported"}."));
        }

        lines.Add(
            isServyx
                ? "Consistency: CRASH-CONSISTENT across all of the instance's EBS volumes at once. AWS's "
                  + "CreateSnapshots captured them as one set, which is equivalent to the state the disks would be "
                  + "in if the power cord had been pulled at that instant. It is NOT application-consistent: the "
                  + "workload was not stopped and its in-memory buffers were not flushed, so a save file that was "
                  + "mid-write is captured mid-write and may need recovery on restore, exactly as after a power cut."
                : "Consistency: UNKNOWN. Servyx did not take this snapshot and cannot say whether it was taken "
                  + "alongside snapshots of the instance's other volumes, or whether the workload was quiesced "
                  + "first. It is reported as a single snapshot because that is all Servyx can honestly assert "
                  + "about it.");

        lines.Add(
            "NOT covered by this backup: instance-store (ephemeral NVMe) volumes, which no AWS API can snapshot; "
            + "RAM and process state; anything on a volume not attached to this instance at the moment of "
            + "capture; and anything outside the instance entirely (RDS, EFS, S3).");

        lines.Add(
            "File list: NOT AVAILABLE. AWS exposes no way to enumerate or extract an individual file from an EBS "
            + "snapshot without first creating a volume from it and mounting that volume, so Servyx does not claim "
            + "to know what is inside. This is a real difference from an archive-based backup, not an omission.");

        lines.Add(EbsSnapshotPricing.DescribeMonthlyCeiling(SumGibibytes(backup.Snapshots), backup.IsFirstOfChain));

        lines.Add(
            "Lifetime: an EBS snapshot never expires on its own. It exists, and bills, until something deletes it — "
            + (isServyx
                ? "for this one, that means Servyx's retention policy or a human."
                : "and for this one, only a human, because Servyx never prunes what it did not create.")
            + " Note that deleting one snapshot of an incremental chain frees only the blocks no surviving snapshot "
            + "still references, so a prune can reduce the bill by less than the ceiling above suggests.");

        lines.Add(
            "Restoring from it does NOT overwrite anything in place: each snapshot restores by creating a NEW EBS "
            + "volume, which must then be attached. Preview with PlanRestoreAsync, which sets out the full "
            + "procedure; this provider's RestoreAsync always refuses, by design.");

        return lines;
    }

    private IReadOnlyList<string> DescribeRestore(
        EbsSnapshotContext context,
        ResolvedBackup backup,
        InstanceFacts instance)
    {
        var devices = instance.Devices;
        var zone = instance.AvailabilityZone is { Length: > 0 } az
            ? "availability zone " + az
            : "the SAME availability zone as the instance (EC2 no longer reports the instance, so Servyx cannot "
              + "name it — look it up before creating anything; a volume cannot be attached across zones)";

        var lines = new List<string>
        {
            "NOT AN OVERWRITE, AND NOT ONE CALL. Restoring from an EBS snapshot does not replace a disk in place "
            + "the way a DigitalOcean droplet restore does. Each snapshot restores by creating a NEW EBS volume "
            + "(CreateVolume), which then has to be attached (AttachVolume). Nothing about "
            + $"instance {context.Ec2InstanceId} changes until that attach happens.",

            "THIS PROVIDER WILL NOT CARRY IT OUT. RestoreAsync always refuses. The steps below are the real "
            + "procedure, for an operator or for the provisioning path — not a description of something Servyx is "
            + "about to do. Nothing has been sent to AWS by previewing this plan.",

            string.Create(
                CultureInfo.InvariantCulture,
                $"Source: backup '{backup.Key}' of instance {context.Ec2InstanceId} in {_region}, taken "
                + $"{Format(backup.Artifact.CreatedAt)}, comprising {backup.Snapshots.Count} snapshot(s)."),
        };

        var step = 1;
        foreach (var snapshot in backup.Snapshots)
        {
            var device = snapshot.VolumeId is { } id && devices.TryGetValue(id, out var name) ? name : null;
            var attachment = device is null
                ? ", which is not currently attached to this instance, so you must decide which device it belongs at."
                : $", which is currently attached at {device}.";

            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Step {step++}: CreateVolume from {snapshot.SnapshotId} in {zone} — a volume cannot be attached "
                + $"across availability zones. It restores the contents of volume "
                + $"{snapshot.VolumeId ?? "(unknown)"}{attachment}"));
        }

        lines.Add(
            "Step " + step++ + ": the new volumes are NOT the instance's volumes yet. They are unattached, and from "
            + "the moment they exist they bill per GB-month at the full provisioned size — a restored volume is a "
            + "normal EBS volume and is not billed incrementally the way its snapshot was.");

        lines.Add(
            "Step " + step++ + ": to put a restored ROOT volume back under the instance you must STOP the instance, "
            + "DetachVolume the current root, AttachVolume the restored one at the same device name, and start the "
            + "instance again. That is downtime, and it is unavoidable: AWS will not detach a root volume from a "
            + "running instance. A non-root data volume can be swapped without stopping, but the workload must "
            + "release it first.");

        lines.Add(
            "DATA IMPACT of completing this procedure: " + DataImpact.Destroyed + " for whatever is on the volumes "
            + "you detach. The detached volumes are not deleted by the detach itself and can be re-attached, so the "
            + "loss is recoverable up until you delete them — but everything written since "
            + Format(backup.Artifact.CreatedAt) + " is absent from the restored volumes.");

        lines.Add(
            "The snapshots are NOT consumed or deleted by a restore. They continue to exist and to bill. "
            + EbsSnapshotPricing.DescribeMonthlyCeiling(SumGibibytes(backup.Snapshots), backup.IsFirstOfChain));

        lines.Add(
            backup.Artifact.Ownership == BackupOwnership.Servyx
                ? "Consistency of the source: CRASH-CONSISTENT across all the instance's EBS volumes at once, not "
                  + "application-consistent. Expect the restored filesystems to replay a journal, and expect an "
                  + "application that was mid-write to need its own recovery — plan the restore as you would a "
                  + "recovery from a power cut, not from a clean shutdown."
                : "Consistency of the source: UNKNOWN. Servyx did not take this snapshot and cannot say what state "
                  + "the volume was in, or whether matching snapshots of the instance's other volumes exist.");

        lines.Add($"Backup ownership: {backup.Artifact.Ownership}.");

        return lines;
    }

    private static string RenderTags(IReadOnlyDictionary<string, string> tags) =>
        tags.Count == 0
            ? "none"
            : string.Join(", ", tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}"));

    private static decimal? SumGibibytes(IEnumerable<ResolvedSnapshot> snapshots)
    {
        decimal total = 0m;

        foreach (var snapshot in snapshots)
        {
            if (snapshot.VolumeSizeGib is { } gib)
            {
                total += gib;
            }
        }

        return total;
    }

    private static decimal? SumGibibytes(IEnumerable<ResolvedBackup> backups) =>
        SumGibibytes(backups.SelectMany(b => b.Snapshots));

    private static long ToBytes(int? gibibytes) =>
        gibibytes is { } value && value >= 0 ? (long)decimal.Round(value * BytesPerGibibyte) : 0L;

    private static string Format(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>One EBS snapshot, with both the artifact Servyx reports and the provider fields it came from.</summary>
    private sealed record ResolvedSnapshot(
        BackupArtifact Artifact,
        string SnapshotId,
        string? VolumeId,
        string? State,
        string? Description,
        int? VolumeSizeGib,
        IReadOnlyDictionary<string, string> Tags,
        string? SetName);

    /// <summary>One backup: a Servyx set of snapshots, or a single foreign snapshot standing alone.</summary>
    private sealed record ResolvedBackup(
        BackupArtifact Artifact,
        string Key,
        IReadOnlyList<ResolvedSnapshot> Snapshots)
    {
        /// <summary>
        /// Whether this is the only capture Servyx holds of these volumes, which is what decides whether the
        /// cost ceiling is close to the truth or wildly above it.
        /// </summary>
        internal bool IsFirstOfChain { get; init; }
    }
}

/// <summary>
/// An upper bound on what a server's EBS snapshots cost per month, split by whether Servyx owns them.
/// </summary>
/// <param name="ServyxOwnedSetCount">How many backup sets Servyx created and manages under retention.</param>
/// <param name="ForeignSnapshotCount">How many individual snapshots Servyx did not create and will never delete.</param>
/// <param name="ServyxOwnedMonthlyCeiling">The maximum monthly list price of the Servyx-owned sets.</param>
/// <param name="ForeignMonthlyCeiling">
/// The maximum monthly list price of the foreign snapshots. Reported separately and never summed silently into
/// the first figure: it is a real charge on the account, but it is not a charge Servyx's retention will ever
/// reduce.
/// </param>
/// <param name="AnySizeUnknown">
/// Whether AWS reported no source volume size for at least one snapshot, so even the ceiling is incomplete.
/// </param>
/// <remarks>
/// Every figure here is a <strong>ceiling</strong> derived from source volume sizes, not a price — see
/// <see cref="EbsSnapshotPricing"/>. EBS snapshots are billed incrementally, so the real charge for a server
/// with several captures of the same volumes is normally a small fraction of the number below.
/// </remarks>
public sealed record EbsSnapshotStorageCeiling(
    int ServyxOwnedSetCount,
    int ForeignSnapshotCount,
    CostEstimate ServyxOwnedMonthlyCeiling,
    CostEstimate ForeignMonthlyCeiling,
    bool AnySizeUnknown);
