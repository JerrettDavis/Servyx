using System.Globalization;
using System.Net;

using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;

using Servyx.Infrastructure.Azure.Provisioning;

namespace Servyx.Infrastructure.Azure.Backups;

/// <summary>
/// An <see cref="IBackupProvider"/> whose artifacts are sets of Azure <em>managed-disk snapshots</em> covering
/// every managed disk attached to one virtual machine.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A snapshot here is a first-class ARM resource, and that changes more than it sounds like.</strong> A
/// DigitalOcean droplet snapshot is an attribute of the droplet's account; an EBS snapshot is an EC2 object
/// with tags but no resource group and no independent placement. A <c>Microsoft.Compute/snapshots</c> resource
/// has its own ARM resource id, its own region, its own tag collection and its own lifetime, and it lives in a
/// resource group somebody pays for. Three consequences run through this file: an orphaned Servyx snapshot is
/// <em>findable by the same subscription-wide tag sweep</em>
/// <see cref="AzureVirtualMachineProvisioner.ReconcileAsync"/> already performs, because it carries
/// <c>servyx.managed=true</c> exactly as a VM does; an orphaned snapshot is nonetheless a visible, billable
/// object sitting in a resource group rather than an invisible line on an invoice; and deleting one is an
/// ordinary ARM delete with an ordinary ARM async completion, not a special verb.
/// </para>
/// <para>
/// <strong>A backup is every MANAGED disk attached to the machine — OS and data — and nothing less.</strong> An
/// Azure VM has one OS disk and zero or more data disks, and on a game server the data disk is where the saves
/// are. Backing up only the OS disk would be the worse default in the only direction that matters: an operator
/// who reads "backup succeeded" and later discovers the world data was never in it has lost the thing they were
/// protecting. The opposite mistake — capturing more than strictly needed — costs money, and money is
/// recoverable. This is the same judgement the EBS adapter made, reached from the same asymmetry.
/// </para>
/// <para>
/// <strong>CONSISTENCY: AZURE OFFERS NO ATOMIC MULTI-DISK SNAPSHOT FOR A PLAIN VM, AND THIS ADAPTER DOES NOT
/// PRETEND OTHERWISE.</strong> This is the single most important difference from the EBS provider and it is
/// stated first, everywhere. AWS has <c>CreateSnapshots</c> (plural): one call, one instant, every EBS volume
/// captured as one crash-consistent set — and the EBS adapter uses it. Azure's equivalent does not exist.
/// <c>Microsoft.Compute/snapshots</c> takes exactly one <c>creationData.sourceResourceId</c>, and it is a
/// <em>disk</em>. So a multi-disk capture here is N separate ARM operations at N different instants, and the
/// resulting set is <strong>NOT a consistent point-in-time image of the machine</strong>: disk A is captured
/// before disk B, and anything the workload wrote across both in between is captured in a state the machine was
/// never actually in. On a single-disk machine the question does not arise and each snapshot is
/// crash-consistent for its own disk; on a multi-disk machine it very much does. Azure's answers to
/// cross-disk consistency are <em>different resources</em> with different lifetimes and different billing —
/// VM restore points (<c>Microsoft.Compute/restorePointCollections</c>, which do offer a
/// <c>CrashConsistent</c> mode across a machine's disks) and Azure Backup (which can be
/// application-consistent via the VM agent) — and neither is a managed-disk snapshot, which is what this
/// adapter was asked for. Adopting one of them is a design with its own orphan story, not a flag on this one.
/// Until then the caveat is written into <see cref="InspectAsync"/> and <see cref="PlanRestoreAsync"/> in
/// capitals rather than left in this file, because the person who needs to read it is restoring at 3am.
/// </para>
/// <para>
/// <strong>What is NOT in the set, stated plainly.</strong> Ephemeral OS disks and temporary/resource disks
/// (the <c>/dev/sdb</c> Azure attaches to most sizes) are not managed disks and cannot be snapshotted at all;
/// anything living on one is outside every backup this adapter takes. A machine using unmanaged (page-blob
/// VHD) disks cannot be backed up by this adapter at all and is refused rather than partially captured. Disks
/// attached <em>after</em> a capture are obviously not in it, and disks detached before it are not either. RAM
/// and process state are not captured — the workload is not quiesced and nothing is flushed, so a save file
/// mid-write is captured mid-write. Nothing outside the machine — an Azure SQL database, a file share, a
/// storage account — is captured. <see cref="InspectAsync"/> says all of this for a specific backup, with the
/// actual disk list.
/// </para>
/// <para>
/// <strong>Foreign snapshots are never deleted, and that is structural.</strong> A resource group contains
/// snapshots Servyx did not create — taken by hand in the portal, by Azure Backup, by a partner tool, or left
/// over from a disk that no longer exists. Three independent barriers stand between <see cref="PruneAsync"/>
/// and one of them, each sufficient on its own:
/// </para>
/// <list type="number">
/// <item><description>
/// <em>Partition.</em> <see cref="PruneAsync"/> splits the listing by <see cref="BackupArtifact.Ownership"/> in
/// one place and passes only the <see cref="BackupOwnership.Servyx"/> half onward. The foreign half is reduced
/// to <see cref="PruneResult.SkippedForeign"/> and then goes out of scope — it is never bound to a variable any
/// deletion code can see, under either value of <c>dryRun</c>.
/// </description></item>
/// <item><description>
/// <em>Evaluation.</em> <see cref="AzureSnapshotRetentionEvaluator.SelectForRemoval"/> throws
/// <see cref="ForeignAzureSnapshotProtectedException"/> if a foreign artifact reaches it, so retention cannot
/// even be <em>computed</em> over one. This is what makes the guarantee hold for <c>dryRun: true</c> as
/// strongly as for <c>dryRun: false</c>: a dry run's report comes from the same call, so there is no path that
/// "hypothetically" schedules a foreign snapshot for deletion.
/// </description></item>
/// <item><description>
/// <em>Deletion.</em> <see cref="DeleteServyxOwnedSetAsync"/> is the only method in this type that issues an
/// ARM <c>DELETE</c>, and it re-derives ownership for <em>every</em> member of the set from the live snapshots'
/// tags through <see cref="AzureSnapshotOwnership.Classify"/> — all four marks — before deleting any of them. A
/// set with one mislabelled member deletes nothing at all.
/// </description></item>
/// </list>
/// <para>
/// <strong>Creating a backup costs money and takes minutes, and Azure has TWO finish lines rather than
/// one.</strong> An ARM snapshot write answers before the resource is provisioned, so every write is polled to
/// a terminal ARM operation state. That is not enough: an <em>incremental</em> snapshot — which is what Servyx
/// always asks for — reports <c>provisioningState: Succeeded</c> while its data copy is still running in the
/// background, and is unusable as the source of a disk until <c>completionPercent</c> reaches 100. So
/// <see cref="CreateAsync"/> polls both, and returns an artifact only when every member has cleared both.
/// Anything short of that raises <see cref="AzureSnapshotNotConfirmedException"/> naming the snapshots that
/// exist and are billing — never a successful <see cref="BackupArtifact"/>. A snapshot that exists bills per
/// GB-month for as long as it exists, with no expiry: see <see cref="AzureSnapshotPricing"/>, which every
/// figure this type produces is labelled through.
/// </para>
/// <para>
/// <strong>Restore is a genuinely different shape here, and this type refuses rather than pretending.</strong>
/// A managed-disk snapshot does not restore in place: it restores by creating a <em>new managed disk</em> from
/// it, which then has to be attached, and swapping a restored OS disk under a machine means
/// <c>deallocating</c> the machine — a full stop, not a reboot — rewriting its storage profile, and starting it
/// again. <see cref="PlanRestoreAsync"/> is fully supported, issues only reads, and spells out that exact
/// procedure with the real snapshot names, disk names, LUNs and region.
/// <see cref="RestoreAsync(string, CancellationToken)"/> always throws
/// <see cref="AzureSnapshotRestoreNotPerformedException"/>.
/// </para>
/// <para>
/// <strong>There is no force path.</strong> No argument here skips a guard, and no combination of arguments
/// reaches a provider call the checks below would otherwise refuse.
/// </para>
/// <para>
/// <strong>Not registered anywhere.</strong> See <see cref="AzureSnapshotBackups"/>: snapshotting and pruning
/// are mutating, billable capabilities, so this type is opt-in and unreachable from any composition root that
/// does not name it. A host with <c>Servyx:Provisioning:Enabled</c> unset never reaches it, and nothing in this
/// repository constructs one outside its tests.
/// </para>
/// </remarks>
public sealed class AzureSnapshotBackupProvider : IBackupProvider
{
    /// <summary>The default interval between reads of a pending snapshot or an unfinished copy.</summary>
    public static readonly TimeSpan DefaultSnapshotPollInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The default number of reads made before a snapshot is reported as not confirmed. Eighty reads fifteen
    /// seconds apart is twenty minutes, which is the order of magnitude a first incremental snapshot of a
    /// multi-gigabyte game server disk takes to finish copying; a later one is usually far quicker.
    /// </summary>
    public const int DefaultSnapshotPollAttempts = 80;

    /// <summary>The percentage <c>completionPercent</c> must reach before a snapshot's data is usable.</summary>
    public const double CopyCompletePercent = 100d;

    private const decimal BytesPerGibibyte = 1_073_741_824m;

    private readonly AzureArmApiClient _api;
    private readonly IAzureSnapshotContextSource _contexts;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly int _pollAttempts;
    private readonly string _subscriptionId;

    /// <summary>Creates a provider over one Azure subscription.</summary>
    /// <param name="httpClient">The HTTP client the API calls go out on. Substituted in tests; no account is required.</param>
    /// <param name="secretStore">Where the service principal's client secret lives. Resolved per token exchange, never cached.</param>
    /// <param name="servicePrincipal">The identity to authenticate as. Carries only the secret's URN.</param>
    /// <param name="subscriptionId">The subscription the machines and their snapshots live in.</param>
    /// <param name="contexts">Maps a Servyx server id to the virtual machine that backs it.</param>
    /// <param name="timeProvider">Clock used for set naming and poll pacing.</param>
    /// <param name="snapshotPollInterval">How long to wait between reads. Defaults to <see cref="DefaultSnapshotPollInterval"/>.</param>
    /// <param name="snapshotPollAttempts">How many reads to make before reporting a snapshot unconfirmed.</param>
    /// <param name="armBaseAddress">Overrides the ARM root. For tests; production passes <see langword="null"/>.</param>
    /// <param name="loginBaseAddress">Overrides the Entra ID root. For tests; production passes <see langword="null"/>.</param>
    public AzureSnapshotBackupProvider(
        HttpClient httpClient,
        ISecretStore secretStore,
        AzureServicePrincipal servicePrincipal,
        string subscriptionId,
        IAzureSnapshotContextSource contexts,
        TimeProvider? timeProvider = null,
        TimeSpan? snapshotPollInterval = null,
        int snapshotPollAttempts = DefaultSnapshotPollAttempts,
        Uri? armBaseAddress = null,
        Uri? loginBaseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(snapshotPollAttempts, 1);

        _contexts = contexts;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollInterval = snapshotPollInterval ?? DefaultSnapshotPollInterval;
        _pollAttempts = snapshotPollAttempts;
        _subscriptionId = subscriptionId;

        _api = new AzureArmApiClient(
            httpClient,
            secretStore,
            servicePrincipal,
            subscriptionId,
            _timeProvider,
            _pollInterval,
            _pollAttempts,
            armBaseAddress,
            loginBaseAddress);
    }

    /// <summary>The Azure subscription this provider's snapshots live in.</summary>
    public string SubscriptionId => _subscriptionId;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Every attached managed disk, or nothing.</strong> The sequence is: read the machine, enumerate
    /// the managed disks its storage profile attaches, write one snapshot resource per disk, poll each ARM
    /// operation to a terminal state, poll each snapshot's incremental copy to completion, re-derive ownership,
    /// and only then report a backup.
    /// </para>
    /// <para>
    /// <strong>The writes are sequential and they are NOT one instant.</strong> There is no ARM call that
    /// snapshots several disks together, so the set this method produces is not a consistent point-in-time
    /// image of a multi-disk machine — see the type remarks, and note that
    /// <see cref="InspectAsync"/> and <see cref="PlanRestoreAsync"/> both say so for every set they describe.
    /// Sequential rather than concurrent because a failure partway through must leave a bounded, enumerable set
    /// of billing resources for the exception to name, and because ARM throttles disk writes per subscription.
    /// </para>
    /// <para>
    /// <strong>Submission is not success, twice over.</strong> An ARM operation that never reaches a terminal
    /// state raises <see cref="AzureSnapshotNotConfirmedException"/>; a terminal <em>failure</em> raises
    /// <see cref="AzureSnapshotFailedException"/>; and a snapshot whose incremental copy never reaches 100%
    /// raises <see cref="AzureSnapshotNotConfirmedException"/> even though ARM called it <c>Succeeded</c>.
    /// None returns an artifact, and all of them name the snapshots that exist and are billing.
    /// </para>
    /// <para>
    /// <strong>The ownership marks travel in the write and are verified afterwards anyway.</strong> An ARM
    /// snapshot PUT carries its <c>tags</c> in the same request that creates the resource, so there is no
    /// window in which a billing snapshot exists untagged. The verification is still performed: a snapshot
    /// Servyx cannot re-derive ownership for would be unprunable and would bill forever, so that outcome is
    /// raised as <see cref="AzureSnapshotOwnershipNotRecordedException"/> rather than returned as a backup.
    /// </para>
    /// </remarks>
    public async Task<BackupArtifact> CreateAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var machine = await RequireLiveMachineAsync(context, ct).ConfigureAwait(false);

        RequireSnapshottableDisks(context, machine);

        var before = await ListResolvedAsync(context, machine, ct).ConfigureAwait(false);
        var isFirstOfChain = !before.Any(b => b.Artifact.Ownership == BackupOwnership.Servyx);

        var takenAt = _timeProvider.GetUtcNow();
        var setName = AzureSnapshotOwnership.FormatSetName(context.ServerId, takenAt);
        var location = machine.Location
            ?? throw new AzureSnapshotFailedException(
                $"Azure reports no location for virtual machine '{context.VirtualMachineName}' in resource group "
                + $"'{context.ResourceGroup}', and a managed-disk snapshot must be created in the same region as "
                + "its source disk. Nothing was created and nothing is billing.");

        var written = await WriteSetAsync(context, machine, setName, location, ct).ConfigureAwait(false);
        var completed = await ConfirmSetAsync(context, setName, written, ct).ConfigureAwait(false);

        var owned = VerifyOwned(context, setName, completed, isFirstOfChain);
        return BuildSetArtifact(context, setName, owned).Artifact;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Lists this server's managed-disk snapshots, Servyx-owned and foreign alike, each labelled at the point
    /// it is classified rather than inferred later. Servyx's own snapshots are grouped into the backup sets
    /// they were written for and reported as one artifact each; a foreign snapshot is reported on its own,
    /// because Servyx has no grounds to assert that two snapshots it did not create belong together.
    /// </para>
    /// <para>
    /// <strong>One listing rather than the EBS adapter's two, and that is a real Azure simplification.</strong>
    /// The EBS adapter has to union a tag-filtered listing with a volume-filtered one, because AWS gives it no
    /// single query that returns both. Here every snapshot in the machine's resource group comes back in one
    /// paged read and classification happens afterwards, so Servyx's own snapshots — <em>including ones of a
    /// disk that has since been detached</em>, which a listing keyed on live attachments would miss and leave
    /// unprunable — and foreign snapshots of the machine's current disks are found by the same call.
    /// </para>
    /// <para>
    /// <strong>What that listing still cannot see, stated rather than glossed.</strong> A snapshot Servyx did
    /// not create, of a disk that has since been detached from the machine, is indistinguishable from any other
    /// unrelated snapshot in the group and is not reported as this server's backup. And a Servyx snapshot
    /// somehow living in a <em>different</em> resource group is invisible here — it would still be found by the
    /// subscription-wide tag sweep, because it carries <c>servyx.managed=true</c>, which is the safety net this
    /// adapter has and the EBS one does not.
    /// </para>
    /// <para>
    /// Snapshots of <em>other</em> machines are not this server's backups and are never returned — which is
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
    /// providers answer this by reading tar headers, because their artifacts are archives they can open. Azure
    /// exposes no way to enumerate a file inside a managed-disk snapshot without first creating a disk from it
    /// (or granting a SAS over it) and mounting that, so what comes back here is a description of the backup —
    /// which disks it covers, at which LUNs, when it was taken, <em>how consistent it is not</em>, what it does
    /// not cover, what it costs, and who owns it — and it says outright that the file list is not available. A
    /// plausible-looking fabricated listing would be worse than no listing, because someone would plan a
    /// restore around it.
    /// </para>
    /// <para>Read-only: <c>GET</c>s only, no mutation of any kind.</para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> InspectAsync(string backupId, CancellationToken ct = default)
    {
        var (context, backup, machine) = await ResolveAsync(backupId, ct).ConfigureAwait(false);
        return Describe(context, backup, machine);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Read-only, and blunt about what a restore is.</strong> This issues <c>GET</c>s and nothing else.
    /// The returned <see cref="RestorePlan.AffectedPaths"/> is not a file list — restoring from a managed-disk
    /// snapshot does not overwrite selected paths, and in fact does not overwrite anything by itself — so the
    /// entries state the real procedure instead: each snapshot becomes a <em>new managed disk</em>, in a named
    /// region, which must then be attached where the original was, which for the OS disk means deallocating the
    /// machine first.
    /// </para>
    /// <para>
    /// <strong>The plan leads with the consistency caveat, not with the steps.</strong> For a multi-disk set the
    /// first thing an operator has to know is that the disks were captured at different instants and were never
    /// a single point in time. Burying that under a procedure would be the most expensive omission this file
    /// could make.
    /// </para>
    /// <para>
    /// <strong>The plan is honest that this provider will not carry it out.</strong> Every entry that describes
    /// a mutating step names it as something the operator or the provisioning path does, not something
    /// <see cref="RestoreAsync(string, CancellationToken)"/> does — because that member always refuses. The plan
    /// is therefore written to be executable by hand: real snapshot names, real disk names, real LUNs, the real
    /// region, in order.
    /// </para>
    /// <para>
    /// No plan state is retained. A plan that cannot be applied has nothing to expire, and a single-use token
    /// for an operation that never runs would be theatre.
    /// </para>
    /// </remarks>
    public async Task<RestorePlan> PlanRestoreAsync(string backupId, CancellationToken ct = default)
    {
        var (context, backup, machine) = await ResolveAsync(backupId, ct).ConfigureAwait(false);

        return new RestorePlan(
            $"restore-{Guid.NewGuid():n}",
            backup.Artifact.Id,
            DescribeRestore(context, backup, machine));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>This member always refuses, for the same reason the EBS one does and with one Azure-specific
    /// aggravation.</strong> Restoring from a managed-disk snapshot creates a <em>new managed disk</em>;
    /// putting that disk back under the machine means stopping it, rewriting its storage profile and starting
    /// it again. For the OS disk, "stopping it" means <c>deallocate</c> — Azure will not swap an OS disk on a
    /// machine that is merely powered off from inside the guest, and a deallocation releases the machine's
    /// compute allocation entirely. There is no single call, there is no in-place overwrite, and there is
    /// unavoidable downtime. A method that returned successfully would be claiming something that did not
    /// happen.
    /// </para>
    /// <para>
    /// <strong>Doing half of it would be worse than refusing.</strong> The tempting middle ground is to create
    /// the disks and stop. That leaves unattached managed disks billing per GB-month at their FULL PROVISIONED
    /// size — a restored disk is an ordinary managed disk and is not billed incrementally the way its snapshot
    /// was, so this is <em>more</em> expensive than the backup it came from — next to a machine still running
    /// on its original disks, having returned success from a method called "restore".
    /// </para>
    /// <para>
    /// Swapping a restored disk under a machine is a lifecycle operation, and it belongs to the provisioning
    /// path that already gates destructive changes behind a <see cref="DataImpact"/> acknowledgement — not to a
    /// backup provider. No HTTP request of any kind is issued by this method: not an ARM call, and not even the
    /// token exchange.
    /// </para>
    /// </remarks>
    /// <exception cref="AzureSnapshotRestoreNotPerformedException">Always.</exception>
    public Task RestoreAsync(string restorePlanId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restorePlanId);

        throw new AzureSnapshotRestoreNotPerformedException(
            $"Restore plan '{restorePlanId}' was NOT carried out, and this provider never carries one out. "
            + "Restoring from an Azure managed-disk snapshot is not an in-place operation and is not a single API "
            + "call: each snapshot restores by CREATING A NEW MANAGED DISK from it (PUT Microsoft.Compute/disks "
            + "with creationData.createOption=Copy), which must then be attached — and putting a restored OS disk "
            + "back under the machine additionally requires DEALLOCATING the virtual machine (a full stop that "
            + "releases its compute allocation, not a reboot), rewriting its storage profile to reference the new "
            + "disk, and starting it again. Servyx will not report success for a sequence it did not perform, and "
            + "will not perform half of it: creating the disks and stopping there would leave unattached managed "
            + "disks billing per GB-month at their FULL PROVISIONED size — more than the incremental snapshots "
            + "they came from — beside a machine still running on its original disks. Nothing was sent to Azure, "
            + "no disk was created, no machine was stopped, and no token was exchanged. Call PlanRestoreAsync for "
            + "the exact ordered procedure, with the real snapshot names, disk names, LUNs and region — and for "
            + "the point-in-time consistency caveat that applies to any multi-disk set.",
            restorePlanId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// See the type remarks for the three barriers that make foreign snapshots unprunable. Under
    /// <c>dryRun: true</c> this issues no ARM <c>DELETE</c> of any kind; under either flag,
    /// <see cref="PruneResult.SkippedForeign"/> reports how many foreign <em>snapshots</em> were seen and left
    /// alone — snapshots and not sets, because a foreign snapshot is never grouped into a set. A snapshot that
    /// has already vanished provider-side answers <c>404</c> to the delete and is still reported as removed: it
    /// is gone, which is the outcome retention asked for, and pretending otherwise would leave the caller
    /// expecting a charge that has already stopped.
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
        var removals = AzureSnapshotRetentionEvaluator.SelectForRemoval(
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
    /// An upper bound on what this server's managed-disk snapshots cost per month, split by ownership.
    /// </summary>
    /// <remarks>
    /// A ceiling and never a price — see <see cref="AzureSnapshotPricing"/> for why Azure does not let this
    /// adapter compute a real figure, and why writing every snapshot with <c>incremental: true</c> means the
    /// real figure is normally far lower. A snapshot's charge recurs for as long as it exists, so "what am I
    /// paying for backups" is a question this adapter has to be able to answer at all; answering it with a
    /// number that overstates is tolerable only because the overstatement is stated. Read-only.
    /// </remarks>
    /// <param name="serverId">The Servyx server.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AzureSnapshotStorageCeiling> EstimateStorageCeilingAsync(
        string serverId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var all = await ListResolvedAsync(context, ct).ConfigureAwait(false);

        var servyx = all.Where(b => b.Artifact.Ownership == BackupOwnership.Servyx).ToList();
        var foreign = all.Where(b => b.Artifact.Ownership == BackupOwnership.Foreign).ToList();

        return new AzureSnapshotStorageCeiling(
            servyx.Count,
            foreign.Sum(b => b.Snapshots.Count),
            AzureSnapshotPricing.Ceiling(SumGigabytes(servyx)),
            AzureSnapshotPricing.Ceiling(SumGigabytes(foreign)),
            all.SelectMany(b => b.Snapshots).Any(s => s.DiskSizeGb is null));
    }

    // -----------------------------------------------------------------------------------------------
    // Create
    // -----------------------------------------------------------------------------------------------

    /// <summary>Writes one snapshot per attached managed disk, naming what exists if a later write fails.</summary>
    private async Task<IReadOnlyList<SubmittedSnapshot>> WriteSetAsync(
        AzureSnapshotContext context,
        MachineFacts machine,
        string setName,
        string location,
        CancellationToken ct)
    {
        var submitted = new List<SubmittedSnapshot>();

        for (var index = 0; index < machine.Disks.Count; index++)
        {
            var disk = machine.Disks[index];
            var name = AzureSnapshotOwnership.FormatMemberName(setName, index);
            var resourceId = _api.SnapshotResourceId(context.ResourceGroup, name);

            var body = new ArmSnapshotRequest
            {
                Location = location,
                Tags = (IReadOnlyDictionary<string, string>)AzureSnapshotOwnership.BuildTags(
                    context.ServerId,
                    context.ResourceGroup,
                    context.VirtualMachineName,
                    context.JobId,
                    context.ConnectorId,
                    setName,
                    disk.Name),
                Properties = new ArmSnapshotRequestProperties
                {
                    CreationData = new ArmSnapshotCreationDataRequest
                    {
                        CreateOption = "Copy",
                        SourceResourceId = disk.ManagedDiskId,
                    },

                    // Explicit, not defaulted. ARM defaults this to false, which bills the disk's stored
                    // contents on every single capture instead of only the delta - see AzureSnapshotPricing.
                    Incremental = true,
                },
            };

            try
            {
                var submission = await _api.CreateSnapshotAsync(resourceId, body, ct).ConfigureAwait(false);
                submitted.Add(new SubmittedSnapshot(name, resourceId, disk, submission));
            }
            catch (AzureApiException e)
            {
                throw new AzureSnapshotFailedException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Azure refused the snapshot of {disk.Role} '{disk.Name}' (member {index + 1} of "
                        + $"{machine.Disks.Count}) for server '{context.ServerId}', so the set is INCOMPLETE and is "
                        + $"NOT reported as a backup: a set missing a disk restores a machine that never existed. ")
                    + (submitted.Count > 0
                        ? "The snapshots written before it DO exist and ARE billing: "
                          + string.Join(", ", submitted.Select(s => s.Name)) + ". "
                        : "No snapshot had been written yet, so nothing is billing from this attempt. ")
                    + $"They carry the tag '{AzureSnapshotOwnership.SetTag}={setName}' and can be found and removed "
                    + "by it; they are Servyx-owned and will be considered by the next retention pass.",
                    submitted.Select(s => s.Name).ToList(),
                    e);
            }
        }

        return submitted;
    }

    /// <summary>
    /// Takes every submitted snapshot past both of Azure's finish lines: a terminal ARM operation state, and a
    /// completed incremental data copy.
    /// </summary>
    private async Task<IReadOnlyList<ArmSnapshot>> ConfirmSetAsync(
        AzureSnapshotContext context,
        string setName,
        IReadOnlyList<SubmittedSnapshot> submitted,
        CancellationToken ct)
    {
        var allNames = submitted.Select(s => s.Name).ToList();

        // Finish line one: ARM's own long-running operation, watched by the single poller this assembly has.
        foreach (var member in submitted)
        {
            ArmOperationPoll poll;

            try
            {
                poll = await _api.PollOperationAsync(member.Submission, ct).ConfigureAwait(false);
            }
            catch (AzureApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // The snapshot was accepted and has since been deleted by something outside Servyx. Left as the
                // raw ARM 404 this would read as "Servyx cannot talk to Azure", which is the wrong diagnosis and
                // hides the fact that the rest of the set exists and is billing.
                throw Vanished(context, setName, member.Name, allNames, e);
            }

            if (poll.Outcome == ArmOperationOutcome.Failed)
            {
                throw new AzureSnapshotFailedException(
                    $"Azure reported the snapshot of {member.Disk.Role} '{member.Disk.Name}' as "
                    + $"'{poll.StatusText}', so no backup was taken for server '{context.ServerId}'. "
                    + poll.FailureText
                    + " The other snapshots in the set may have completed; they exist and are billing: "
                    + string.Join(", ", allNames)
                    + $". They carry the tag '{AzureSnapshotOwnership.SetTag}={setName}'. Servyx does NOT report a "
                    + "partial set as a backup, and does not delete these automatically — they are Servyx-owned and "
                    + "will be considered by the next retention pass.",
                    allNames);
            }

            if (poll.Outcome == ArmOperationOutcome.StillRunning)
            {
                throw new AzureSnapshotNotConfirmedException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Azure accepted the snapshot of {member.Disk.Role} '{member.Disk.Name}' for server "
                        + $"'{context.ServerId}' and was still reporting it as '{poll.StatusText}' after "
                        + $"{poll.Polls} check(s). ")
                    + "No backup is being reported: a snapshot that was only submitted is not a backup that exists. "
                    + "The operation is most likely still running at Azure and may yet finish — which is NOT the "
                    + "same as a failure and calls for the opposite response. Do not resubmit blindly: a second set "
                    + "that completes alongside the first leaves two sets, both billing per GB-month. These "
                    + $"snapshots exist and are billing now: {string.Join(", ", allNames)}, tagged "
                    + $"'{AzureSnapshotOwnership.SetTag}={setName}'. Watch them in the portal, or list this "
                    + "server's backups, before acting further.",
                    allNames,
                    submitted: true);
            }
        }

        // Finish line two, which has no EBS analogue: an incremental snapshot reports provisioningState
        // 'Succeeded' while its data copy is still running, and is unusable as a disk source until
        // completionPercent reaches 100. Reporting a backup at the first finish line would report a backup that
        // does not yet contain the data.
        return await PollCopyCompletionAsync(context, setName, submitted, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ArmSnapshot>> PollCopyCompletionAsync(
        AzureSnapshotContext context,
        string setName,
        IReadOnlyList<SubmittedSnapshot> submitted,
        CancellationToken ct)
    {
        var allNames = submitted.Select(s => s.Name).ToList();
        var latest = new List<ArmSnapshot>();
        var polls = 0;

        for (; polls < _pollAttempts; polls++)
        {
            if (polls > 0 && _pollInterval > TimeSpan.Zero)
            {
                await Task.Delay(_pollInterval, _timeProvider, ct).ConfigureAwait(false);
            }

            latest.Clear();

            foreach (var member in submitted)
            {
                var snapshot = await _api.GetSnapshotAsync(member.ResourceId, ct).ConfigureAwait(false)
                    ?? throw Vanished(context, setName, member.Name, allNames, inner: null);

                latest.Add(snapshot);
            }

            if (latest.TrueForAll(IsCopyComplete))
            {
                return latest;
            }
        }

        var unfinished = latest
            .Where(s => !IsCopyComplete(s))
            .Select(s => string.Create(
                CultureInfo.InvariantCulture,
                $"{s.Name ?? "(unnamed)"} ({s.Properties?.CompletionPercent ?? 0d}% copied)"))
            .ToList();

        throw new AzureSnapshotNotConfirmedException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Azure reported {unfinished.Count} of server '{context.ServerId}'s {allNames.Count} snapshot(s) as "
                + $"provisioned but still copying after {polls} check(s): ")
            + string.Join(", ", unfinished)
            + ". An INCREMENTAL Azure snapshot reaches provisioningState 'Succeeded' before its background data "
            + "copy has finished, and cannot be used to create a disk until completionPercent reaches 100 — so "
            + "these are NOT yet restorable and no backup is being reported. This is not a failure: the copy is "
            + "most likely still running and may yet finish. Do not resubmit blindly; a second set that completes "
            + "alongside the first leaves two sets, both billing per GB-month. These snapshots exist and are "
            + $"billing now: {string.Join(", ", allNames)}, tagged "
            + $"'{AzureSnapshotOwnership.SetTag}={setName}'.",
            allNames,
            submitted: true);
    }

    /// <summary>
    /// The one answer for a snapshot that Azure accepted and then stopped reporting, whichever poll noticed.
    /// </summary>
    /// <remarks>
    /// A snapshot can vanish between two Servyx calls — deleted in the portal, by another tool, or by a
    /// lifecycle policy. It is reported as a <em>failed capture</em> rather than as a communication error,
    /// because the set is now incomplete and the surviving members still exist and still bill; a caller told
    /// only "404 from ARM" would reach for the wrong response.
    /// </remarks>
    private static AzureSnapshotFailedException Vanished(
        AzureSnapshotContext context,
        string setName,
        string missing,
        IReadOnlyList<string> allNames,
        Exception? inner)
    {
        var message =
            $"A snapshot Azure created for server '{context.ServerId}' has vanished between the write and a read "
            + $"of it: '{missing}'. Something outside Servyx deleted it — the portal, another tool, or a lifecycle "
            + "policy. The set is therefore INCOMPLETE and is NOT reported as a backup: a set missing a disk "
            + "restores a machine that never existed. The remaining snapshots may still exist and still bill: "
            + string.Join(", ", allNames)
            + $". The set is tagged '{AzureSnapshotOwnership.SetTag}={setName}'.";

        return inner is null
            ? new AzureSnapshotFailedException(message, allNames)
            : new AzureSnapshotFailedException(message, allNames, inner);
    }

    /// <summary>
    /// Whether a snapshot's data has finished copying.
    /// </summary>
    /// <remarks>
    /// A snapshot that reports no <c>completionPercent</c> at all is treated as complete, and that is a
    /// deliberate, narrow concession rather than an oversight: the member is absent for a full (non-incremental)
    /// snapshot and on older api-versions, and treating "not reported" as "not finished" would make every such
    /// snapshot time out forever. Servyx always asks for an incremental snapshot at an api-version that reports
    /// it, so the concession is reachable only when the service answers differently from its contract — and
    /// <see cref="Describe"/> says outright when a snapshot's copy progress was never reported.
    /// </remarks>
    private static bool IsCopyComplete(ArmSnapshot snapshot) =>
        snapshot.Properties?.CompletionPercent is not { } percent || percent >= CopyCompletePercent;

    /// <summary>Re-derives ownership over a freshly-created set, refusing to claim one it cannot prove.</summary>
    private IReadOnlyList<ResolvedSnapshot> VerifyOwned(
        AzureSnapshotContext context,
        string setName,
        IReadOnlyList<ArmSnapshot> snapshots,
        bool isFirstOfChain)
    {
        var resolved = snapshots.Select(s => Resolve(context, s)).ToList();
        var unowned = resolved.Where(r => r.Artifact.Ownership != BackupOwnership.Servyx).ToList();

        if (unowned.Count > 0)
        {
            throw new AzureSnapshotOwnershipNotRecordedException(
                $"Snapshots of virtual machine '{context.VirtualMachineName}' WERE taken and exist in resource "
                + $"group '{context.ResourceGroup}', but Servyx could not verify {unowned.Count} of them as its "
                + $"own, so this is not a managed backup of server '{context.ServerId}'. Servyx never deletes a "
                + "snapshot it cannot prove it owns, so retention will NEVER remove these: they will bill until "
                + "somebody deletes them by hand. Unverifiable: "
                + string.Join("; ", unowned.Select(u => $"{u.Name} (tags: {RenderTags(u.Tags)})"))
                + $". The whole set carries '{AzureSnapshotOwnership.SetTag}={setName}' and is: "
                + string.Join(", ", resolved.Select(r => r.Name))
                + ". " + AzureSnapshotPricing.DescribeMonthlyCeiling(SumGigabytes(resolved), isFirstOfChain),
                resolved.Select(r => r.Name).ToList());
        }

        return resolved;
    }

    // -----------------------------------------------------------------------------------------------
    // Prune
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Barrier 3: the only method in this type that issues an ARM <c>DELETE</c>.
    /// </summary>
    /// <remarks>
    /// It re-derives ownership for every member of the set from the live snapshots' tags, through
    /// <see cref="AzureSnapshotOwnership.Classify"/>, rather than trusting the label it was handed — and it does
    /// so for <em>all</em> members before deleting <em>any</em>, so a set with one mislabelled member deletes
    /// nothing rather than deleting the ones that happened to check out first. A mislabelled or out-of-scope
    /// artifact throws <see cref="ForeignAzureSnapshotProtectedException"/>, so even a caller that fabricated an
    /// artifact carrying <see cref="BackupOwnership.Servyx"/> could not route a delete at somebody else's
    /// snapshot.
    /// </remarks>
    private async Task DeleteServyxOwnedSetAsync(
        AzureSnapshotContext context,
        ResolvedBackup backup,
        CancellationToken ct)
    {
        if (backup.Artifact.Ownership != BackupOwnership.Servyx)
        {
            throw new ForeignAzureSnapshotProtectedException(
                $"Refusing to delete backup '{backup.Artifact.Id}': it is {backup.Artifact.Ownership}, not "
                + "Servyx-owned. Deleting an Azure snapshot cannot be undone.",
                backup.Artifact.Location);
        }

        foreach (var snapshot in backup.Snapshots)
        {
            var rederived = AzureSnapshotOwnership.Classify(
                snapshot.Tags,
                context.ServerId,
                context.ResourceGroup,
                context.VirtualMachineName);

            if (rederived != BackupOwnership.Servyx)
            {
                throw new ForeignAzureSnapshotProtectedException(
                    $"Refusing to delete backup '{backup.Artifact.Id}': snapshot '{snapshot.Name}' was presented as "
                    + "Servyx-owned, but its live tags do not carry Servyx's four marks for server "
                    + $"'{context.ServerId}' on virtual machine "
                    + $"'{AzureSnapshotOwnership.FormatSourceVirtualMachine(context.ResourceGroup, context.VirtualMachineName)}' "
                    + $"(tags: {RenderTags(snapshot.Tags)}). No snapshot in this set was deleted. Deleting an Azure "
                    + "snapshot cannot be undone.",
                    snapshot.Artifact.Location);
            }
        }

        foreach (var snapshot in backup.Snapshots)
        {
            await _api.DeleteSnapshotAsync(snapshot.ResourceId, ct).ConfigureAwait(false);
        }
    }

    // -----------------------------------------------------------------------------------------------
    // Reading
    // -----------------------------------------------------------------------------------------------

    private async Task<AzureSnapshotContext> GetContextAsync(string serverId, CancellationToken ct)
    {
        var context = await _contexts.GetAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new AzureSnapshotNotFoundException(
                $"No Azure snapshot context is configured for server '{serverId}', so Servyx does not know which "
                + "virtual machine backs it.");

        if (!AzureSnapshotOwnership.IsSupportedServerId(context.ServerId))
        {
            throw new ArgumentException(
                $"Server id '{context.ServerId}' cannot be carried in an Azure snapshot resource name or tag, so a "
                + "snapshot taken for it could never be recognised as Servyx's afterwards — it would bill forever "
                + $"and never be pruned. Ids may be at most {AzureSnapshotOwnership.MaxServerIdLength} characters "
                + "of letters, digits, '-', '_' and '.'.",
                nameof(serverId));
        }

        if (string.IsNullOrWhiteSpace(context.ResourceGroup) || string.IsNullOrWhiteSpace(context.VirtualMachineName))
        {
            throw new ArgumentException(
                $"Server '{context.ServerId}' maps to no Azure resource group and virtual machine name, so Servyx "
                + "cannot tell which machine's disks to snapshot — or, far worse, whose snapshots retention would "
                + "be entitled to delete.",
                nameof(serverId));
        }

        return context;
    }

    private string VirtualMachineId(AzureSnapshotContext context) =>
        _api.ResourceId(context.ResourceGroup, "Microsoft.Compute", "virtualMachines", context.VirtualMachineName);

    private async Task<MachineFacts> ReadMachineAsync(AzureSnapshotContext context, CancellationToken ct) =>
        MachineFacts.From(await _api
            .GetVirtualMachineDisksAsync(VirtualMachineId(context), ct)
            .ConfigureAwait(false));

    private async Task<MachineFacts> RequireLiveMachineAsync(AzureSnapshotContext context, CancellationToken ct)
    {
        var machine = await ReadMachineAsync(context, ct).ConfigureAwait(false);

        if (!machine.Exists)
        {
            throw new AzureSnapshotNotFoundException(
                $"Azure no longer reports virtual machine '{context.VirtualMachineName}' in resource group "
                + $"'{context.ResourceGroup}' for server '{context.ServerId}', so there is nothing to snapshot. "
                + "Note that snapshots already taken of it are NOT affected: a managed-disk snapshot is an "
                + "independent ARM resource, so it survives the machine's deletion, still exists, and still bills.");
        }

        return machine;
    }

    private static void RequireSnapshottableDisks(AzureSnapshotContext context, MachineFacts machine)
    {
        if (machine.UnmanagedAttachments.Count > 0)
        {
            throw new AzureSnapshotFailedException(
                $"Virtual machine '{context.VirtualMachineName}' (server '{context.ServerId}') has "
                + $"{machine.UnmanagedAttachments.Count} attachment(s) that are not managed disks: "
                + string.Join(", ", machine.UnmanagedAttachments)
                + ". Microsoft.Compute/snapshots can only be created from a MANAGED disk, so this machine cannot be "
                + "backed up by this provider — and Servyx will not capture the managed disks alone and call that a "
                + "backup of the machine. Nothing was created and nothing is billing.");
        }

        if (machine.Disks.Count == 0)
        {
            throw new AzureSnapshotFailedException(
                $"Virtual machine '{context.VirtualMachineName}' (server '{context.ServerId}') has no managed disks "
                + "attached, so there is nothing for a managed-disk snapshot to capture and no backup was taken. If "
                + "the workload's data is on an ephemeral OS disk or the temporary resource disk, note that NEITHER "
                + "can be snapshotted by any Azure API — that data is not backed up by this provider and never will "
                + "be.");
        }

        if (machine.Disks.Count > 100)
        {
            throw new AzureSnapshotFailedException(
                $"Virtual machine '{context.VirtualMachineName}' has "
                + machine.Disks.Count.ToString(CultureInfo.InvariantCulture)
                + " managed disks, which is more than the two-digit member index a Servyx snapshot set name can "
                + "carry. No snapshot was written: a set whose members could not be named unambiguously could not "
                + "be reassembled from a listing, and a backup Servyx cannot recognise afterwards bills forever.");
        }
    }

    private async Task<IReadOnlyList<ResolvedBackup>> ListResolvedAsync(
        AzureSnapshotContext context,
        CancellationToken ct)
    {
        // A deleted machine is not an error on a read path. Its snapshots outlive it - they are independent ARM
        // resources - so retention must be able to reach them, and the resource-group listing below does not
        // depend on the machine existing at all.
        var machine = await ReadMachineAsync(context, ct).ConfigureAwait(false);
        return await ListResolvedAsync(context, machine, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ResolvedBackup>> ListResolvedAsync(
        AzureSnapshotContext context,
        MachineFacts machine,
        CancellationToken ct)
    {
        var snapshots = await _api.ListSnapshotsAsync(context.ResourceGroup, ct).ConfigureAwait(false);

        // ARM resource ids are compared case-insensitively by the service itself, so a case-sensitive match here
        // would silently classify a foreign snapshot of this machine's own disk as unrelated and hide it from
        // SkippedForeign - the one number whose whole job is to be honest about what Servyx left alone.
        var attachedDiskIds = new HashSet<string>(
            machine.Disks.Select(d => d.ManagedDiskId),
            StringComparer.OrdinalIgnoreCase);

        var resolved = new List<ResolvedSnapshot>();

        foreach (var snapshot in snapshots)
        {
            if (snapshot.Name is not { Length: > 0 })
            {
                continue;
            }

            var candidate = Resolve(context, snapshot);

            if (candidate.Artifact.Ownership == BackupOwnership.Servyx
                || (candidate.SourceDiskId is { Length: > 0 } source && attachedDiskIds.Contains(source)))
            {
                resolved.Add(candidate);
            }
        }

        var sets = resolved
            .Where(r => r.Artifact.Ownership == BackupOwnership.Servyx)
            .GroupBy(r => r.SetName!, StringComparer.Ordinal)
            .Select(g => BuildSetArtifact(context, g.Key, g.OrderBy(r => r.Name, StringComparer.Ordinal).ToList()))
            .OrderBy(b => b.Artifact.CreatedAt)
            .ThenBy(b => b.Artifact.Id, StringComparer.Ordinal)
            .ToList();

        // "First of the chain" is a property of the whole listing, not of a set on its own: the oldest capture
        // Servyx holds of these disks is the one whose snapshots stored the disks' used blocks, and every later
        // one stored only what changed since. It is what decides whether the cost ceiling is close to the truth
        // or far above it, so it is computed here rather than guessed where a figure is rendered.
        var backups = new List<ResolvedBackup>(
            sets.Select((set, index) => set with { IsFirstOfChain = index == 0 }));

        backups.AddRange(resolved
            .Where(r => r.Artifact.Ownership == BackupOwnership.Foreign)
            .Select(foreign => new ResolvedBackup(foreign.Artifact, foreign.Name, [foreign])));

        return backups
            .OrderBy(b => b.Artifact.CreatedAt)
            .ThenBy(b => b.Artifact.Id, StringComparer.Ordinal)
            .ToList();
    }

    private ResolvedSnapshot Resolve(AzureSnapshotContext context, ArmSnapshot snapshot)
    {
        var name = snapshot.Name ?? string.Empty;
        var tags = ServyxAzureTags.FromArmTags(snapshot.Tags);

        var ownership = AzureSnapshotOwnership.Classify(
            tags,
            context.ServerId,
            context.ResourceGroup,
            context.VirtualMachineName);

        var setName = ownership == BackupOwnership.Servyx
            ? AzureSnapshotOwnership.ReadSetName(tags)
            : null;

        var artifact = new BackupArtifact(
            AzureSnapshotBackupId.FormatSnapshot(context.ServerId, name),
            ownership,
            snapshot.Properties?.TimeCreated ?? DateTimeOffset.UnixEpoch,
            ToBytes(snapshot.Properties?.DiskSizeGb),
            AzureSnapshotBackupId.LocationOfSnapshot(_subscriptionId, context.ResourceGroup, name));

        return new ResolvedSnapshot(
            artifact,
            name,
            snapshot.Id ?? _api.SnapshotResourceId(context.ResourceGroup, name),
            snapshot.Properties?.CreationData?.SourceResourceId,
            snapshot.Properties?.ProvisioningState,
            snapshot.Properties?.Incremental,
            snapshot.Properties?.CompletionPercent,
            snapshot.Properties?.DiskSizeGb,
            tags,
            setName);
    }

    private ResolvedBackup BuildSetArtifact(
        AzureSnapshotContext context,
        string setName,
        IReadOnlyList<ResolvedSnapshot> members)
    {
        // The set's timestamp comes from its name rather than from a member's timeCreated, and here that is
        // load-bearing rather than tidy: the members of an Azure set were created by SEPARATE ARM operations at
        // genuinely different instants, so a member's own clock would make retention's buckets depend on which
        // disk happened to be snapshotted first.
        var createdAt = AzureSnapshotOwnership.TryParseSetName(setName, out _, out var named)
            ? named
            : members.Select(m => m.Artifact.CreatedAt).DefaultIfEmpty(DateTimeOffset.UnixEpoch).Min();

        var artifact = new BackupArtifact(
            AzureSnapshotBackupId.FormatSet(context.ServerId, setName),
            BackupOwnership.Servyx,
            createdAt,
            members.Sum(m => m.Artifact.SizeBytes),
            AzureSnapshotBackupId.LocationOfSet(_subscriptionId, context.ResourceGroup, setName));

        return new ResolvedBackup(artifact, setName, members);
    }

    private async Task<(AzureSnapshotContext Context, ResolvedBackup Backup, MachineFacts Machine)>
        ResolveAsync(string backupId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);

        if (!AzureSnapshotBackupId.TryGetServerId(backupId, out var serverId))
        {
            throw new AzureSnapshotNotFoundException(
                $"Backup id '{backupId}' is not in a form this provider issued.",
                backupId);
        }

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var machine = await ReadMachineAsync(context, ct).ConfigureAwait(false);
        var resolved = await ListResolvedAsync(context, machine, ct).ConfigureAwait(false);

        var backup = resolved.FirstOrDefault(b =>
            string.Equals(b.Artifact.Id, backupId, StringComparison.Ordinal))
            ?? throw new AzureSnapshotNotFoundException(
                $"Backup '{backupId}' does not exist: Azure no longer reports it for server '{serverId}'. It may "
                + "have been deleted in the portal, by another tool, or by a prune — or, for a Servyx backup set, "
                + "one of its snapshots may have been removed, which un-makes the set.",
                backupId);

        return (context, backup, machine);
    }

    // -----------------------------------------------------------------------------------------------
    // Description
    // -----------------------------------------------------------------------------------------------

    private IReadOnlyList<string> Describe(
        AzureSnapshotContext context,
        ResolvedBackup backup,
        MachineFacts machine)
    {
        var isServyx = backup.Artifact.Ownership == BackupOwnership.Servyx;
        var diskNames = machine.Disks.ToDictionary(d => d.ManagedDiskId, d => d, StringComparer.OrdinalIgnoreCase);

        var lines = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"Azure managed-disk backup '{backup.Key}' of virtual machine '{context.VirtualMachineName}' in "
                + $"resource group '{context.ResourceGroup}' (subscription {_subscriptionId}), taken "
                + $"{Format(backup.Artifact.CreatedAt)}. It is {backup.Snapshots.Count} snapshot(s)."),

            $"Ownership: {backup.Artifact.Ownership}."
                + (isServyx
                    ? " Created by Servyx and subject to this server's retention policy."
                    : " Servyx did not create this snapshot and will never delete it — it is listed and "
                      + "inspectable, and retention cannot reach it."),
        };

        foreach (var snapshot in backup.Snapshots)
        {
            var attachment = snapshot.SourceDiskId is { Length: > 0 } id && diskNames.TryGetValue(id, out var disk)
                ? disk.Role
                : "a disk not currently attached to this machine";

            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  {snapshot.Name}: from {attachment} ({snapshot.SourceDiskName ?? "source disk unknown"}), source "
                + $"disk size {(snapshot.DiskSizeGb is { } gb ? gb + " GB" : "not reported")}, state "
                + $"{snapshot.ProvisioningState ?? "not reported"}, "
                + $"{(snapshot.Incremental is true ? "incremental" : snapshot.Incremental is false ? "FULL (not incremental — Servyx did not write this one)" : "incremental flag not reported")}, "
                + $"copy {(snapshot.CompletionPercent is { } percent ? percent + "%" : "progress not reported")}."));
        }

        lines.Add(ConsistencyLine(backup, isServyx));

        if (isServyx)
        {
            var covered = backup.Snapshots.Count;
            lines.Add(machine.Exists
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Coverage: this set holds {covered} snapshot(s); the machine currently has "
                    + $"{machine.Disks.Count} managed disk(s) attached. ")
                  + (covered == machine.Disks.Count
                      ? "Every disk attached now is represented, though a disk attached AFTER the capture cannot be."
                      : "THE COUNTS DIFFER: a disk has been attached or detached since the capture, or the capture "
                        + "was incomplete. Do not assume this set restores the machine as it is configured today.")
                : "Coverage: the machine no longer exists, so Servyx cannot say which disks it had when this set "
                  + "was taken. The snapshots survive it and still bill.");
        }

        lines.Add(
            "NOT covered by this backup: ephemeral OS disks and the temporary/resource disk, neither of which is a "
            + "managed disk and neither of which any Azure API can snapshot; RAM and process state; anything on a "
            + "disk not attached to this machine at the moment of capture; and anything outside the machine "
            + "entirely (Azure SQL, file shares, storage accounts).");

        lines.Add(
            "File list: NOT AVAILABLE. Azure exposes no way to enumerate or extract an individual file from a "
            + "managed-disk snapshot without first creating a disk from it (or granting a SAS over it) and mounting "
            + "that, so Servyx does not claim to know what is inside. This is a real difference from an "
            + "archive-based backup, not an omission.");

        lines.Add(AzureSnapshotPricing.DescribeMonthlyCeiling(SumGigabytes(backup.Snapshots), backup.IsFirstOfChain));

        lines.Add(
            "Lifetime: an Azure snapshot is an independent ARM resource and never expires on its own. It exists, "
            + "and bills, until something deletes it — "
            + (isServyx
                ? "for this one, that means Servyx's retention policy or a human."
                : "and for this one, only a human, because Servyx never prunes what it did not create.")
            + " It also survives deletion of the virtual machine and of the disk it came from. Note that deleting "
            + "one snapshot of an incremental chain frees only the blocks no surviving snapshot still references, "
            + "so a prune can reduce the bill by less than the ceiling above suggests.");

        lines.Add(
            "Restoring from it does NOT overwrite anything in place: each snapshot restores by creating a NEW "
            + "managed disk, which must then be attached. Preview with PlanRestoreAsync, which sets out the full "
            + "procedure; this provider's RestoreAsync always refuses, by design.");

        return lines;
    }

    private IReadOnlyList<string> DescribeRestore(
        AzureSnapshotContext context,
        ResolvedBackup backup,
        MachineFacts machine)
    {
        var diskNames = machine.Disks.ToDictionary(d => d.ManagedDiskId, d => d, StringComparer.OrdinalIgnoreCase);
        var region = machine.Location is { Length: > 0 } location
            ? "region " + location
            : "the SAME region as the source disks (Azure no longer reports the machine, so Servyx cannot name it "
              + "— read it off a snapshot before creating anything; a disk cannot be attached across regions)";

        var lines = new List<string>
        {
            // The consistency caveat leads, because for a multi-disk set it is the fact that changes how a
            // restore is planned - not a footnote to the procedure.
            ConsistencyLine(backup, backup.Artifact.Ownership == BackupOwnership.Servyx),

            "NOT AN OVERWRITE, AND NOT ONE CALL. Restoring from an Azure managed-disk snapshot does not replace a "
            + "disk in place the way a DigitalOcean droplet restore does. Each snapshot restores by creating a NEW "
            + "managed disk (PUT Microsoft.Compute/disks with creationData.createOption=Copy and sourceResourceId "
            + "set to the snapshot), which then has to be attached. Nothing about virtual machine "
            + $"'{context.VirtualMachineName}' changes until that attach happens.",

            "THIS PROVIDER WILL NOT CARRY IT OUT. RestoreAsync always refuses. The steps below are the real "
            + "procedure, for an operator or for the provisioning path — not a description of something Servyx is "
            + "about to do. Nothing has been sent to Azure by previewing this plan beyond the reads that built it.",

            string.Create(
                CultureInfo.InvariantCulture,
                $"Source: backup '{backup.Key}' of virtual machine '{context.VirtualMachineName}' in resource group "
                + $"'{context.ResourceGroup}', taken {Format(backup.Artifact.CreatedAt)}, comprising "
                + $"{backup.Snapshots.Count} snapshot(s)."),
        };

        var step = 1;
        foreach (var snapshot in backup.Snapshots)
        {
            var disk = snapshot.SourceDiskId is { Length: > 0 } id && diskNames.TryGetValue(id, out var found)
                ? found
                : null;

            var attachment = disk is null
                ? ", which is not currently attached to this machine, so you must decide where it belongs."
                : $", which is currently attached as the {disk.Role}.";

            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Step {step++}: create a managed disk from snapshot '{snapshot.Name}' in {region} — a disk cannot "
                + $"be attached across regions. It restores the contents of "
                + $"{snapshot.SourceDiskName ?? "(source disk unknown)"}{attachment}"));
        }

        lines.Add(
            "Step " + step++ + ": the new disks are NOT the machine's disks yet. They are unattached, and from the "
            + "moment they exist they bill per GB-month at their FULL PROVISIONED size — a restored disk is an "
            + "ordinary managed disk and is not billed incrementally the way its snapshot was, so this step is "
            + "MORE expensive per GB than the backup it came from.");

        lines.Add(
            "Step " + step++ + ": to put a restored OS disk back under the machine you must DEALLOCATE the virtual "
            + "machine (Azure will not swap an OS disk on a machine that is merely powered off from inside the "
            + "guest, and a deallocation releases its compute allocation), rewrite its storageProfile.osDisk to "
            + "reference the new disk, and start it again. That is downtime, and it is unavoidable. A data disk can "
            + "be detached and re-attached without deallocating, but the workload must release it first.");

        lines.Add(
            "DATA IMPACT of completing this procedure: " + DataImpact.Destroyed + " for whatever is on the disks you "
            + "detach. The detached disks are not deleted by the detach itself and can be re-attached, so the loss "
            + "is recoverable up until you delete them — but everything written since "
            + Format(backup.Artifact.CreatedAt) + " is absent from the restored disks.");

        lines.Add(
            "The snapshots are NOT consumed or deleted by a restore. They continue to exist and to bill. "
            + AzureSnapshotPricing.DescribeMonthlyCeiling(SumGigabytes(backup.Snapshots), backup.IsFirstOfChain));

        lines.Add($"Backup ownership: {backup.Artifact.Ownership}.");

        return lines;
    }

    /// <summary>
    /// The one sentence about consistency, written once and used by both descriptions so they can never drift.
    /// </summary>
    private static string ConsistencyLine(ResolvedBackup backup, bool isServyx)
    {
        if (!isServyx)
        {
            return "Consistency: UNKNOWN. Servyx did not take this snapshot and cannot say whether it was taken "
                + "alongside snapshots of the machine's other disks, or whether the workload was quiesced first. It "
                + "is reported as a single snapshot because that is all Servyx can honestly assert about it.";
        }

        return backup.Snapshots.Count > 1
            ? "Consistency: NOT A CONSISTENT POINT IN TIME, AND THIS IS NOT A DEFECT IN SERVYX. Azure offers no "
              + "atomic multi-disk snapshot for a plain virtual machine — Microsoft.Compute/snapshots takes exactly "
              + "one source DISK — so the "
              + backup.Snapshots.Count.ToString(CultureInfo.InvariantCulture)
              + " snapshots in this set were written by SEPARATE ARM operations at DIFFERENT instants. Each one is "
              + "crash-consistent for its own disk, but anything the workload wrote across two disks in between is "
              + "captured in a state the machine was never actually in. (AWS's CreateSnapshots does offer one "
              + "atomic call across an instance's volumes, and Servyx's EBS provider uses it; Azure's equivalents "
              + "are VM restore points and Azure Backup, which are different resources with different lifetimes "
              + "and are not managed-disk snapshots.) It is also NOT application-consistent: the workload was not "
              + "stopped and its buffers were not flushed, so a save file mid-write is captured mid-write. Plan a "
              + "restore as you would a recovery from a power cut during a disk swap, not from a clean shutdown."
            : "Consistency: CRASH-CONSISTENT for this machine's single disk. The set has one member, so the "
              + "cross-disk ordering problem that affects a multi-disk Azure capture does not arise here. It is NOT "
              + "application-consistent: the workload was not stopped and its buffers were not flushed, so a save "
              + "file that was mid-write is captured mid-write and may need recovery on restore, exactly as after a "
              + "power cut.";
    }

    // -----------------------------------------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------------------------------------

    private static string RenderTags(IReadOnlyDictionary<string, string> tags) =>
        tags.Count == 0
            ? "none"
            : string.Join(", ", tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}"));

    private static decimal? SumGigabytes(IEnumerable<ResolvedSnapshot> snapshots)
    {
        decimal total = 0m;

        foreach (var snapshot in snapshots)
        {
            if (snapshot.DiskSizeGb is { } gb)
            {
                total += gb;
            }
        }

        return total;
    }

    private static decimal? SumGigabytes(IEnumerable<ResolvedBackup> backups) =>
        SumGigabytes(backups.SelectMany(b => b.Snapshots));

    /// <summary>
    /// Azure's <c>diskSizeGB</c> as bytes.
    /// </summary>
    /// <remarks>
    /// Converted as gibibytes despite the field's name, because Azure's managed disk sizes are binary — a "128
    /// GB" P10 is 128 GiB — and reporting a decimal-GB figure would understate every artifact's size by seven
    /// per cent for no reason.
    /// </remarks>
    private static long ToBytes(int? gigabytes) =>
        gigabytes is { } value && value >= 0 ? (long)decimal.Round(value * BytesPerGibibyte) : 0L;

    private static string Format(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>One managed disk attached to the machine, in the order a capture visits them.</summary>
    /// <param name="Role">Human-facing description, e.g. <c>OS disk</c> or <c>data disk at LUN 0</c>.</param>
    /// <param name="Name">The disk's ARM name.</param>
    /// <param name="ManagedDiskId">The disk's ARM resource id — a snapshot's <c>sourceResourceId</c>.</param>
    /// <param name="SizeGb">Its provisioned size, if ARM reported one on the attachment.</param>
    private sealed record MachineDisk(string Role, string Name, string ManagedDiskId, int? SizeGb);

    /// <summary>What a description and a capture need from the live machine.</summary>
    /// <remarks>
    /// Legitimately empty when the machine has been deleted — its snapshots outlive it — so
    /// <see cref="Exists"/> is checked rather than inferred, and every consumer says "not known" rather than
    /// inventing a region or a LUN. Getting the region wrong on a restore is not cosmetic: a disk cannot be
    /// attached across regions, so a disk created in the wrong one is an unattachable, billing mistake.
    /// </remarks>
    private sealed record MachineFacts(
        bool Exists,
        string? Location,
        IReadOnlyList<MachineDisk> Disks,
        IReadOnlyList<string> UnmanagedAttachments)
    {
        internal static MachineFacts From(ArmVirtualMachineDisks? machine)
        {
            if (machine is null)
            {
                return new MachineFacts(false, null, [], []);
            }

            var disks = new List<MachineDisk>();
            var unmanaged = new List<string>();
            var profile = machine.Properties?.StorageProfile;

            if (profile?.OsDisk is { } osDisk)
            {
                var name = osDisk.Name ?? "(unnamed OS disk)";
                if (osDisk.ManagedDisk?.Id is { Length: > 0 } id)
                {
                    disks.Add(new MachineDisk("OS disk", name, id, osDisk.DiskSizeGb));
                }
                else
                {
                    unmanaged.Add("OS disk '" + name + "'");
                }
            }

            // Ordered by LUN so a set's member indices are stable across captures: a data disk's position in
            // ARM's array is not guaranteed, and an unstable order would make one capture's member 01 a
            // different disk from the next capture's.
            foreach (var data in (profile?.DataDisks ?? []).OrderBy(d => d.Lun ?? int.MaxValue)
                         .ThenBy(d => d.Name, StringComparer.Ordinal))
            {
                var name = data.Name ?? "(unnamed data disk)";
                var role = data.Lun is { } lun
                    ? "data disk at LUN " + lun.ToString(CultureInfo.InvariantCulture)
                    : "data disk (LUN not reported)";

                if (data.ManagedDisk?.Id is { Length: > 0 } id)
                {
                    disks.Add(new MachineDisk(role, name, id, data.DiskSizeGb));
                }
                else
                {
                    unmanaged.Add(role + " '" + name + "'");
                }
            }

            return new MachineFacts(true, machine.Location, disks, unmanaged);
        }
    }

    /// <summary>One snapshot write ARM accepted, before anything is known about its outcome.</summary>
    private sealed record SubmittedSnapshot(
        string Name,
        string ResourceId,
        MachineDisk Disk,
        ArmOperationSubmission Submission);

    /// <summary>One snapshot, with both the artifact Servyx reports and the ARM fields it came from.</summary>
    private sealed record ResolvedSnapshot(
        BackupArtifact Artifact,
        string Name,
        string ResourceId,
        string? SourceDiskId,
        string? ProvisioningState,
        bool? Incremental,
        double? CompletionPercent,
        int? DiskSizeGb,
        IReadOnlyDictionary<string, string> Tags,
        string? SetName)
    {
        /// <summary>The ARM name of the disk this snapshot came from, read off the source id.</summary>
        internal string? SourceDiskName =>
            SourceDiskId is { Length: > 0 } id && id.LastIndexOf('/') is var slash && slash >= 0 && slash + 1 < id.Length
                ? id[(slash + 1)..]
                : null;
    }

    /// <summary>One backup: a Servyx set of snapshots, or a single foreign snapshot standing alone.</summary>
    private sealed record ResolvedBackup(
        BackupArtifact Artifact,
        string Key,
        IReadOnlyList<ResolvedSnapshot> Snapshots)
    {
        /// <summary>
        /// Whether this is the only capture Servyx holds of these disks, which is what decides whether the cost
        /// ceiling is close to the truth or wildly above it.
        /// </summary>
        internal bool IsFirstOfChain { get; init; }
    }
}

/// <summary>
/// An upper bound on what a server's managed-disk snapshots cost per month, split by whether Servyx owns them.
/// </summary>
/// <param name="ServyxOwnedSetCount">How many backup sets Servyx created and manages under retention.</param>
/// <param name="ForeignSnapshotCount">How many individual snapshots Servyx did not create and will never delete.</param>
/// <param name="ServyxOwnedMonthlyCeiling">The maximum monthly list price of the Servyx-owned sets.</param>
/// <param name="ForeignMonthlyCeiling">
/// The maximum monthly list price of the foreign snapshots. Reported separately and never summed silently into
/// the first figure: it is a real charge on the subscription, but it is not a charge Servyx's retention will
/// ever reduce.
/// </param>
/// <param name="AnySizeUnknown">
/// Whether Azure reported no source disk size for at least one snapshot, so even the ceiling is incomplete.
/// </param>
/// <remarks>
/// Every figure here is a <strong>ceiling</strong> derived from source disk sizes, not a price — see
/// <see cref="AzureSnapshotPricing"/>. Servyx writes every snapshot as incremental, so the real charge for a
/// server with several captures of the same disks is normally a small fraction of the number below.
/// </remarks>
public sealed record AzureSnapshotStorageCeiling(
    int ServyxOwnedSetCount,
    int ForeignSnapshotCount,
    CostEstimate ServyxOwnedMonthlyCeiling,
    CostEstimate ForeignMonthlyCeiling,
    bool AnySizeUnknown);
