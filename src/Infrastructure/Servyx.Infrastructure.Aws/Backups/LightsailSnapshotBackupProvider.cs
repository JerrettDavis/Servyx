using System.Globalization;
using System.Text.Json.Nodes;

using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.Aws.Backups;

/// <summary>
/// An <see cref="IBackupProvider"/> whose artifacts are AWS <em>Lightsail instance snapshots</em>: one snapshot
/// per backup, covering the whole machine.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One artifact, not a set — and unlike the EBS adapter, that simplicity is not a compromise.</strong>
/// <c>EbsSnapshotBackupProvider</c> has to treat a backup as a <em>set</em> of snapshots because
/// <c>CreateSnapshot</c> takes one volume at a time and an EC2 instance's saves usually live on a separate data
/// volume. Lightsail's <c>CreateInstanceSnapshot</c> takes an <em>instance</em> and produces exactly one
/// <c>InstanceSnapshot</c> object, and AWS's user guide states what that object contains: "An instance snapshot
/// is a copy of the system disk and matches the original machine configuration… If you've attached block storage
/// disks to your instance, Lightsail copies those additional disks as part of your snapshot." The API agrees —
/// <c>InstanceSnapshot.fromAttachedDisks</c> is "an array of disk objects containing information about all block
/// storage disks", and <c>CreateInstancesFromSnapshot</c> takes an <c>attachedDiskMapping</c> to name the disks
/// it brings back.
/// </para>
/// <para>
/// <strong>So the answer to "does an instance snapshot cover attached block-storage disks" is yes, and this
/// adapter does not take that on trust.</strong> A Lightsail instance can have additional block storage disks
/// attached beyond the bundle's own SSD, and they are billed separately from the bundle at their own per-GB
/// rate. The snapshot covers them, but <em>which</em> disks were attached at the moment of capture is a fact only
/// the snapshot itself records, so <see cref="InspectAsync"/> reads <c>fromAttachedDisks</c> back and names every
/// disk, its device path and its size, rather than printing a blanket assurance. A disk attached <em>after</em> a
/// snapshot was taken is not in it, which is exactly the sort of thing an operator can only check against a list.
/// </para>
/// <para>
/// <strong>What is NOT covered, stated plainly.</strong> RAM and process state are not captured — a snapshot is a
/// point-in-time copy of disks, so a workload with unflushed in-memory writes is captured mid-write, and Servyx
/// does not quiesce or stop the instance to take one. Anything outside the instance is not captured: a Lightsail
/// managed database, an object-storage bucket, a load balancer's configuration, or a disk that was detached at
/// the moment of capture. And — a Lightsail-specific one that has bitten people — <strong>custom firewall rules
/// are not restored</strong> from a snapshot: AWS documents that only the default rules copy over to an instance
/// created from one, so a machine rebuilt from a backup comes back with its ports closed.
/// </para>
/// <para>
/// <strong>Consistency: crash-consistent at best, and Servyx will not claim more.</strong> AWS publishes no
/// quiesce, freeze or pre/post-script hook for a Lightsail instance snapshot, and this adapter does not stop the
/// instance before taking one — stopping a running game server as a side effect of "take a backup" is not a
/// decision a backup provider gets to make. So a snapshot is the state the disks would be in if the power had
/// been cut at that instant, and a save file that was half-written is captured half-written. That caveat is
/// written into <see cref="InspectAsync"/> and <see cref="PlanRestoreAsync"/> rather than left in this file.
/// </para>
/// <para>
/// <strong>Foreign snapshots are never deleted, and that is structural.</strong> A Lightsail account contains
/// instance snapshots Servyx did not create — taken by hand, taken by another tool, produced by Lightsail's own
/// automatic-snapshot add-on (which AWS will not let anybody tag), or left over from an instance that no longer
/// exists. Three independent barriers stand between <see cref="PruneAsync"/> and one of them, each sufficient on
/// its own:
/// </para>
/// <list type="number">
/// <item><description>
/// <em>Partition.</em> <see cref="PruneAsync"/> splits the listing by <see cref="BackupArtifact.Ownership"/> in
/// one place and passes only the <see cref="BackupOwnership.Servyx"/> half onward. The foreign half is reduced to
/// <see cref="PruneResult.SkippedForeign"/> and then goes out of scope — it is never bound to a variable any
/// deletion code can see, under either value of <c>dryRun</c>.
/// </description></item>
/// <item><description>
/// <em>Evaluation.</em> <see cref="LightsailSnapshotRetentionEvaluator.SelectForRemoval"/> throws
/// <see cref="ForeignLightsailSnapshotProtectedException"/> if a foreign artifact reaches it, so retention cannot
/// even be <em>computed</em> over one. This is what makes the guarantee hold for <c>dryRun: true</c> as strongly
/// as for <c>dryRun: false</c>: a dry run's report comes from the same call, so there is no path that
/// "hypothetically" schedules a foreign snapshot for deletion.
/// </description></item>
/// <item><description>
/// <em>Deletion.</em> <see cref="DeleteServyxOwnedAsync"/> is the only method in this type that issues a
/// <c>DeleteInstanceSnapshot</c>, and it re-derives ownership from the live snapshot object through
/// <see cref="LightsailSnapshotOwnership.Classify"/> — all four marks — instead of trusting the label it was
/// handed. A mislabelled artifact fails that re-derivation and throws.
/// </description></item>
/// </list>
/// <para>
/// <strong>Creating a snapshot costs money and takes minutes.</strong> <c>CreateInstanceSnapshot</c> answers with
/// pending <c>Operation</c> records while the copy is still queued, so <see cref="CreateAsync"/> polls the
/// snapshot itself until Lightsail reports its <c>state</c> as <c>available</c>, and returns an artifact only
/// then. A snapshot still <c>pending</c> when the polls are spent raises
/// <see cref="LightsailSnapshotNotConfirmedException"/>; an <c>error</c> state raises
/// <see cref="LightsailSnapshotFailedException"/>. Neither returns a <see cref="BackupArtifact"/>, because
/// neither is evidence that a restorable backup exists. And a snapshot that exists bills per GB-month for as long
/// as it exists, with no expiry: see <see cref="LightsailSnapshotPricing"/>, which every figure this type
/// produces is labelled through — including the fact that Lightsail's incremental billing makes a naive per-GB
/// number a ceiling rather than a price.
/// </para>
/// <para>
/// <strong>Restore is a third distinct shape, and this type refuses rather than pretending.</strong> A
/// DigitalOcean droplet restores <em>in place</em>. An EBS snapshot restores into a new <em>volume</em> that must
/// be attached. A Lightsail instance snapshot restores into a new <em>instance</em>:
/// <c>CreateInstancesFromSnapshot</c> demands a new name, a zone and a bundle, and leaves the original instance
/// running and untouched. <see cref="PlanRestoreAsync"/> is fully supported, issues only reads, and spells out
/// the whole procedure with the real names, bundle floor, zone and disks.
/// <see cref="RestoreAsync(string, CancellationToken)"/> always throws
/// <see cref="LightsailSnapshotRestoreNotPerformedException"/>. See that member for why launching a second
/// machine and calling it a restore would be worse than refusing.
/// </para>
/// <para>
/// <strong>There is no force path.</strong> No argument here skips a guard, and no combination of arguments
/// reaches a provider call the checks below would otherwise refuse. There is no acknowledging restore overload
/// either — unlike the DigitalOcean adapter, which has one because its restore <em>is</em> a single destructive
/// call that consent can authorise; here there is no single call to authorise.
/// </para>
/// <para>
/// <strong>Not registered anywhere.</strong> See <see cref="LightsailSnapshotBackups"/>: snapshotting and pruning
/// are mutating, billable capabilities, so this type is opt-in and unreachable from any composition root that
/// does not name it. A host with <c>Servyx:Provisioning:Enabled</c> unset never reaches it, and nothing in this
/// repository constructs one outside its tests.
/// </para>
/// </remarks>
public sealed class LightsailSnapshotBackupProvider : IBackupProvider
{
    /// <summary>The default interval between reads of a pending snapshot.</summary>
    public static readonly TimeSpan DefaultSnapshotPollInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The default number of reads made before a snapshot is reported as not confirmed. Eighty reads fifteen
    /// seconds apart is twenty minutes, which is the order of magnitude a first snapshot of a multi-gigabyte game
    /// server takes; a later incremental one is usually far quicker.
    /// </summary>
    public const int DefaultSnapshotPollAttempts = 80;

    private const decimal BytesPerGigabyte = 1_073_741_824m;

    private readonly LightsailJsonApiClient _api;
    private readonly ILightsailSnapshotContextSource _contexts;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly int _pollAttempts;
    private readonly string _region;

    /// <summary>Creates a provider over one AWS account and one region.</summary>
    /// <param name="httpClient">The HTTP client the API calls go out on. Substituted in tests; no account is required.</param>
    /// <param name="secretStore">Where the AWS key pair lives. Resolved per request and never cached.</param>
    /// <param name="identity">The URNs of the key pair. Only URNs are held.</param>
    /// <param name="region">The AWS region the instance and its snapshots live in.</param>
    /// <param name="contexts">Maps a Servyx server id to the Lightsail instance that backs it.</param>
    /// <param name="timeProvider">Clock used for snapshot naming and poll pacing.</param>
    /// <param name="snapshotPollInterval">How long to wait between reads of a pending snapshot. Defaults to <see cref="DefaultSnapshotPollInterval"/>.</param>
    /// <param name="snapshotPollAttempts">How many reads to make before reporting a snapshot unconfirmed.</param>
    /// <param name="endpoint">Overrides the regional Lightsail endpoint. For tests; production passes <see langword="null"/>.</param>
    public LightsailSnapshotBackupProvider(
        HttpClient httpClient,
        ISecretStore secretStore,
        AwsSigningIdentity identity,
        string region,
        ILightsailSnapshotContextSource contexts,
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

        // The SigV4 signer is reused exactly as the provisioning client uses it, with service = "lightsail". No
        // change to AwsRequestSigner or AwsSigV4 was needed or made: the algorithm was never EC2-specific, and
        // adding a fifth caller to it is the cheapest thing in this file.
        _api = new LightsailJsonApiClient(
            httpClient,
            new AwsRequestSigner(secretStore, identity, region, LightsailJsonApiClient.ServiceName, _timeProvider),
            region,
            endpoint);
    }

    /// <summary>The AWS region this provider's snapshots live in.</summary>
    public string Region => _region;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Submission is not success.</strong> The sequence is: read the instance, list the account's
    /// snapshots of it, submit one <c>CreateInstanceSnapshot</c>, poll <em>the snapshot</em> until Lightsail
    /// reports it <c>available</c>, and only then report a backup. Lightsail answers the create with pending
    /// <c>Operation</c> records — a response that says the request was accepted, not that anything has happened —
    /// so an operation Lightsail itself reports as <c>Failed</c> raises
    /// <see cref="LightsailSnapshotFailedException"/> immediately, an <c>error</c> snapshot state raises the
    /// same, and a snapshot never observed reaching <c>available</c> raises
    /// <see cref="LightsailSnapshotNotConfirmedException"/>. None of the three returns an artifact.
    /// </para>
    /// <para>
    /// <strong>The poll watches the snapshot's <c>state</c> and nothing else, because that is all there is.</strong>
    /// AWS documents <c>InstanceSnapshot.progress</c> as "populated only for disk snapshots, and null for
    /// instance snapshots", so there is no percentage to report and this adapter invents none.
    /// </para>
    /// <para>
    /// <strong>The ownership marks travel in the create call and are verified afterwards anyway.</strong>
    /// <c>CreateInstanceSnapshot</c> takes a <c>tags</c> array, so there is no window in which a billing snapshot
    /// exists untagged — the same improvement the EC2 adapter has over the DigitalOcean one, which must tag after
    /// the fact. The verification is still performed: a snapshot Servyx cannot re-derive ownership for would be
    /// unprunable and would bill forever, so that outcome is raised as
    /// <see cref="LightsailSnapshotOwnershipNotRecordedException"/> rather than returned as a backup.
    /// </para>
    /// </remarks>
    public async Task<BackupArtifact> CreateAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);

        var instance = await _api.GetInstanceAsync(context.InstanceName, ct).ConfigureAwait(false)
            ?? throw new LightsailSnapshotNotFoundException(
                $"Lightsail no longer reports an instance named '{context.InstanceName}' for server "
                + $"'{context.ServerId}', so there is nothing to snapshot. Note that snapshots already taken of it "
                + "are NOT affected: a manual instance snapshot survives its instance's deletion, still exists, and "
                + "still bills.");

        var before = await ListResolvedAsync(context, ct).ConfigureAwait(false);
        var isFirstOfChain = !before.Any(s => s.Artifact.Ownership == BackupOwnership.Servyx);

        var takenAt = _timeProvider.GetUtcNow();
        var snapshotName = LightsailSnapshotOwnership.FormatSnapshotName(context.ServerId, takenAt);

        // A Lightsail snapshot's identity is the name the caller chose, and names are unique per region. Two
        // captures inside the same second would collide, and Lightsail would refuse the second - so the refusal
        // happens here, before the call, naming the snapshot that already exists rather than surfacing a generic
        // InvalidInputException.
        if (before.Any(s => string.Equals(s.SnapshotName, snapshotName, StringComparison.Ordinal)))
        {
            throw new LightsailSnapshotFailedException(
                $"A snapshot named '{snapshotName}' already exists in this Lightsail region, so no second one was "
                + $"requested for server '{context.ServerId}'. Lightsail resource names are unique per region and "
                + "this adapter names a snapshot after the second it was taken in, so this means two captures were "
                + "asked for within the same second. The existing snapshot is untouched; retry in a moment.",
                snapshotName);
        }

        var tags = LightsailSnapshotOwnership.BuildTags(context.ServerId, context.JobId, context.ConnectorId);

        var operations = await _api
            .CreateInstanceSnapshotAsync(CreateInstanceSnapshotBody(context.InstanceName, snapshotName, tags), ct)
            .ConfigureAwait(false);

        if (operations.FirstOrDefault(o => o.IsFailure) is { } failed)
        {
            throw new LightsailSnapshotFailedException(
                $"Lightsail reported the CreateInstanceSnapshot operation for instance '{context.InstanceName}' as "
                + $"Failed, so no backup was taken for server '{context.ServerId}'. Lightsail's words: "
                + failed.FailureText,
                snapshotName);
        }

        var completed = await PollToAvailableAsync(context, snapshotName, ct).ConfigureAwait(false);
        var resolved = Resolve(context, completed);

        if (resolved.Artifact.Ownership != BackupOwnership.Servyx)
        {
            throw new LightsailSnapshotOwnershipNotRecordedException(
                $"Snapshot '{snapshotName}' of instance '{context.InstanceName}' WAS taken and exists at Lightsail, "
                + $"but Servyx could not verify it as its own, so it is not a managed backup of server "
                + $"'{context.ServerId}'. Servyx never deletes a snapshot it cannot prove it owns, so retention will "
                + "NEVER remove this one: it will bill until somebody deletes it by hand. The live snapshot reports "
                + $"fromInstanceName '{completed.FromInstanceName ?? "(none)"}' and tags "
                + $"{RenderTags(completed.Tags)}. "
                + LightsailSnapshotPricing.DescribeMonthlyCeiling(SourceGigabytes(completed), isFirstOfChain),
                snapshotName);
        }

        return resolved.Artifact;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Lists every instance snapshot Lightsail holds <em>of this server's instance</em>, Servyx-owned and foreign
    /// alike, each labelled at the point it is classified rather than inferred later. Snapshots of other
    /// instances in the same account are not this server's backups and are not returned — which is also what
    /// keeps one server's retention from ever seeing another server's snapshots.
    /// </para>
    /// <para>
    /// <strong>One listing does the whole job, which the EBS adapter needs two calls for.</strong> There, a
    /// snapshot records only the volume it came from, so Servyx has to union a tag-filtered listing with a
    /// volume-filtered one to see both its own snapshots and the foreign snapshots of the same disks. Lightsail
    /// records <c>fromInstanceName</c> on every instance snapshot, so a single unfiltered <c>GetInstanceSnapshots</c>
    /// narrowed on that field returns exactly this instance's snapshots whoever created them. The price is that
    /// the narrowing happens in this process rather than at the service: <c>GetInstanceSnapshots</c> accepts no
    /// filter parameter of any kind, so every snapshot in the region crosses the wire.
    /// </para>
    /// <para>
    /// A snapshot whose <c>fromInstanceName</c> Lightsail does not report is not returned. It cannot be shown to
    /// be this instance's, and a backup listing is not the place to guess.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<BackupArtifact>> ListAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var resolved = await ListResolvedAsync(context, ct).ConfigureAwait(false);

        return resolved.Select(s => s.Artifact).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>A snapshot has no readable index, and this does not invent one.</strong> The Docker and SSH
    /// providers answer this by reading tar headers, because their artifacts are archives they can open.
    /// Lightsail exposes no way to enumerate or extract an individual file from an instance snapshot without
    /// first creating an instance (or a disk) from it, so what comes back here is a description of the backup —
    /// which disks it covers, at which device paths, when it was taken, how consistent it is, what it does not
    /// cover, what it costs, and who owns it — and it says outright that the file list is not available. A
    /// plausible-looking fabricated listing would be worse than no listing, because someone would plan a restore
    /// around it.
    /// </para>
    /// <para>Read-only: reads only, no mutation of any kind.</para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> InspectAsync(string backupId, CancellationToken ct = default)
    {
        var (context, snapshot) = await ResolveAsync(backupId, ct).ConfigureAwait(false);
        return Describe(context, snapshot);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Read-only, and blunt about what a Lightsail restore actually is.</strong> This issues reads and
    /// nothing else. The returned <see cref="RestorePlan.AffectedPaths"/> is not a file list — restoring from an
    /// instance snapshot does not overwrite selected paths, and does not overwrite <em>anything</em>: it creates
    /// a second instance. So the entries state that consequence first, name the new-instance parameters
    /// Lightsail would demand, list the disks that would come back, and then set out the steps Servyx will not
    /// take — the ones that would turn a second machine into a restored server.
    /// </para>
    /// <para>
    /// <strong>The plan states the data impact honestly in both directions.</strong> The
    /// <c>CreateInstancesFromSnapshot</c> call itself is <see cref="DataImpact.Preserved"/> with respect to the
    /// existing instance: nothing on it is touched. The destruction, if any, is in what an operator does
    /// afterwards to the old machine, and Servyx neither does it nor pretends the plan authorises it.
    /// </para>
    /// <para>
    /// No plan state is retained. A plan that cannot be applied has nothing to expire, and a single-use token for
    /// an operation that never runs would be theatre — the same reasoning as
    /// <c>EbsSnapshotBackupProvider.PlanRestoreAsync</c>, and the opposite of the DigitalOcean adapter, whose
    /// plans are spent because they authorise a real destructive call.
    /// </para>
    /// </remarks>
    public async Task<RestorePlan> PlanRestoreAsync(string backupId, CancellationToken ct = default)
    {
        var (context, snapshot) = await ResolveAsync(backupId, ct).ConfigureAwait(false);

        return new RestorePlan(
            $"restore-{Guid.NewGuid():n}",
            snapshot.Artifact.Id,
            DescribeRestore(context, snapshot));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>This member always refuses, and the reason is specific to Lightsail's restore shape.</strong>
    /// <c>CreateInstancesFromSnapshot</c> produces a <em>new instance</em>: a new name, its own public address,
    /// its own bill, and — per AWS's documentation — only the default firewall rules, because custom rules do not
    /// copy. The existing instance keeps running on its unrecovered data. A method that returned successfully
    /// having launched that second machine would have restored nothing while doubling the account's compute
    /// charge, and the caller would reasonably believe their server was back.
    /// </para>
    /// <para>
    /// Making it a real restore means moving the static IP, re-creating firewall rules, re-pointing Servyx's own
    /// record of which instance backs this server, and disposing of the old machine. Those are lifecycle and
    /// state-keeping operations belonging to the provisioning path — which already gates destructive changes
    /// behind a <see cref="DataImpact"/> acknowledgement — not to a backup provider. No HTTP request of any kind
    /// is issued by this method.
    /// </para>
    /// <para>
    /// <see cref="PlanRestoreAsync"/> is fully supported and names every one of those steps with the real values,
    /// so the refusal is not obstructive.
    /// </para>
    /// </remarks>
    /// <exception cref="LightsailSnapshotRestoreNotPerformedException">Always.</exception>
    public Task RestoreAsync(string restorePlanId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restorePlanId);

        throw new LightsailSnapshotRestoreNotPerformedException(
            $"Restore plan '{restorePlanId}' was NOT carried out, and this provider never carries one out. "
            + "Restoring from a Lightsail instance snapshot does not overwrite the existing instance: "
            + "CreateInstancesFromSnapshot requires a NEW instance name, an availability zone and a bundle, and "
            + "produces a SECOND, separately-billing instance while the original keeps running untouched. Servyx "
            + "will not report success for a restore that restored nothing, and will not perform half of the real "
            + "procedure: launching the new machine and stopping there would leave the account paying for two "
            + "instances while the server everyone is actually using still runs the unrecovered data. The rest of "
            + "the procedure — moving the static IP, re-creating the custom firewall rules that do NOT copy from a "
            + "snapshot, re-pointing Servyx at the new instance, and disposing of the old one — is lifecycle work "
            + "outside a backup provider. Nothing was sent to AWS, no instance was created, and no data was "
            + "touched. Call PlanRestoreAsync for the exact ordered procedure with the real snapshot name, bundle "
            + "floor, availability zone and attached disks.",
            restorePlanId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// See the type remarks for the three barriers that make foreign snapshots unprunable. Under
    /// <c>dryRun: true</c> this issues no <c>DeleteInstanceSnapshot</c> of any kind; under either flag,
    /// <see cref="PruneResult.SkippedForeign"/> reports how many foreign snapshots were seen and left alone. A
    /// snapshot that has already vanished provider-side answers <c>NotFoundException</c> to the delete and is
    /// still reported as removed — it is gone, which is the outcome retention asked for, and pretending otherwise
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
        var skippedForeign = all.Count(s => s.Artifact.Ownership == BackupOwnership.Foreign);
        var ownedByServyx = all
            .Where(s => s.Artifact.Ownership == BackupOwnership.Servyx)
            .ToList();

        // Barrier 2: evaluation. SelectForRemoval throws on anything not Servyx-owned, so a dry run and a live
        // run compute their answer from the identical, ownership-asserting call.
        var removals = LightsailSnapshotRetentionEvaluator.SelectForRemoval(
            ownedByServyx.Select(s => s.Artifact).ToList(),
            effectivePolicy);

        var removalIds = removals.Select(a => a.Id).ToList();
        if (dryRun)
        {
            return new PruneResult(removalIds, skippedForeign);
        }

        foreach (var removal in removals)
        {
            var resolved = ownedByServyx.First(s => string.Equals(s.Artifact.Id, removal.Id, StringComparison.Ordinal));
            await DeleteServyxOwnedAsync(context, resolved, ct).ConfigureAwait(false);
        }

        return new PruneResult(removalIds, skippedForeign);
    }

    /// <summary>
    /// An upper bound on what this server's Lightsail snapshots cost per month, split by ownership.
    /// </summary>
    /// <remarks>
    /// A ceiling and never a price — see <see cref="LightsailSnapshotPricing"/> for why Lightsail does not let
    /// this adapter compute a real figure, and why the incremental billing model AWS documents means the real
    /// figure is normally far lower. A snapshot's charge recurs for as long as it exists, so "what am I paying
    /// for backups" is a question this adapter has to be able to answer at all; answering it with a number that
    /// overstates is tolerable only because the overstatement is stated. Read-only.
    /// </remarks>
    /// <param name="serverId">The Servyx server.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<LightsailSnapshotStorageCeiling> EstimateStorageCeilingAsync(
        string serverId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var all = await ListResolvedAsync(context, ct).ConfigureAwait(false);

        var servyx = all.Where(s => s.Artifact.Ownership == BackupOwnership.Servyx).ToList();
        var foreign = all.Where(s => s.Artifact.Ownership == BackupOwnership.Foreign).ToList();

        return new LightsailSnapshotStorageCeiling(
            servyx.Count,
            foreign.Count,
            LightsailSnapshotPricing.Ceiling(SumGigabytes(servyx)),
            LightsailSnapshotPricing.Ceiling(SumGigabytes(foreign)),
            all.Any(s => s.SourceGigabytes is null));
    }

    /// <summary>
    /// Barrier 3: the only method in this type that issues a <c>DeleteInstanceSnapshot</c>.
    /// </summary>
    /// <remarks>
    /// It re-derives ownership from the live snapshot object — <c>fromInstanceName</c>, the snapshot's name and
    /// both ownership tags, through <see cref="LightsailSnapshotOwnership.Classify"/> — rather than trusting the
    /// label it was handed. A mislabelled or out-of-scope artifact throws
    /// <see cref="ForeignLightsailSnapshotProtectedException"/> instead of being deleted, so even a caller that
    /// fabricated an artifact carrying <see cref="BackupOwnership.Servyx"/> could not route a delete at somebody
    /// else's snapshot.
    /// </remarks>
    private async Task DeleteServyxOwnedAsync(
        LightsailSnapshotContext context,
        ResolvedSnapshot snapshot,
        CancellationToken ct)
    {
        if (snapshot.Artifact.Ownership != BackupOwnership.Servyx)
        {
            throw new ForeignLightsailSnapshotProtectedException(
                $"Refusing to delete snapshot '{snapshot.SnapshotName}': it is {snapshot.Artifact.Ownership}, not "
                + "Servyx-owned. Deleting a Lightsail instance snapshot cannot be undone.",
                snapshot.Artifact.Location);
        }

        var rederived = LightsailSnapshotOwnership.Classify(
            snapshot.FromInstanceName,
            snapshot.SnapshotName,
            snapshot.Tags,
            context.ServerId,
            context.InstanceName);

        if (rederived != BackupOwnership.Servyx)
        {
            throw new ForeignLightsailSnapshotProtectedException(
                $"Refusing to delete snapshot '{snapshot.SnapshotName}': it was presented as Servyx-owned, but the "
                + $"live snapshot object does not carry Servyx's four marks for server '{context.ServerId}' on "
                + $"instance '{context.InstanceName}' (fromInstanceName "
                + $"'{snapshot.FromInstanceName ?? "(none)"}', tags {RenderTags(snapshot.Tags)}). Deleting a "
                + "Lightsail instance snapshot cannot be undone.",
                snapshot.Artifact.Location);
        }

        await _api.DeleteInstanceSnapshotAsync(snapshot.SnapshotName, ct).ConfigureAwait(false);
    }

    /// <summary>Polls a submitted snapshot until Lightsail reports it <c>available</c>.</summary>
    private async Task<LightsailInstanceSnapshot> PollToAvailableAsync(
        LightsailSnapshotContext context,
        string snapshotName,
        CancellationToken ct)
    {
        LightsailInstanceSnapshot? latest = null;
        var observed = false;
        var polls = 0;

        for (; polls < _pollAttempts; polls++)
        {
            if (polls > 0 && _pollInterval > TimeSpan.Zero)
            {
                await Task.Delay(_pollInterval, _timeProvider, ct).ConfigureAwait(false);
            }

            latest = await _api.GetInstanceSnapshotAsync(snapshotName, ct).ConfigureAwait(false);

            if (latest is null)
            {
                continue;
            }

            observed = true;

            if (latest.IsErrored)
            {
                throw new LightsailSnapshotFailedException(
                    $"Lightsail reported snapshot '{snapshotName}' of instance '{context.InstanceName}' as "
                    + $"'{LightsailInstanceSnapshot.ErrorState}', so no backup was taken for server "
                    + $"'{context.ServerId}'. The failed snapshot object still exists in the account until "
                    + "something deletes it; Servyx does not delete it automatically, because it carries Servyx's "
                    + "ownership marks and will be considered by the next retention pass.",
                    snapshotName);
            }

            if (latest.IsAvailable)
            {
                return latest;
            }
        }

        throw new LightsailSnapshotNotConfirmedException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Lightsail accepted a snapshot of instance '{context.InstanceName}' named '{snapshotName}' and ")
            + (observed
                ? $"was still reporting it as '{latest?.State ?? "(no state)"}' after {polls} check(s). It EXISTS "
                  + "and IS billing now."
                : $"Servyx never saw a snapshot of that name appear at all in {polls} check(s). Servyx cannot tell "
                  + "whether it is still materialising, or whether nothing was created; do not assume the second.")
            + $" No backup is being reported for server '{context.ServerId}': a snapshot that was only submitted is "
            + "not a snapshot that exists. The copy is most likely still running at AWS and may yet finish — which "
            + "is NOT the same as a failure and calls for the opposite response. Do not resubmit blindly: a second "
            + "snapshot that completes alongside the first leaves two, both billing per GB-month. Look for "
            + $"'{snapshotName}' in the Lightsail console, or list this server's backups, before acting further.",
            snapshotName,
            submitted: true,
            observed: observed);
    }

    /// <summary>The <c>CreateInstanceSnapshot</c> request body, with every ownership tag applied inline.</summary>
    /// <remarks>
    /// Tags are sorted for the reason <c>AwsLightsailRequests</c> sorts its own: a request whose parameter order
    /// varied run to run would sign two different payloads for one logical change.
    /// </remarks>
    private static JsonObject CreateInstanceSnapshotBody(
        string instanceName,
        string snapshotName,
        IReadOnlyDictionary<string, string> tags)
    {
        var tagsArray = new JsonArray();

        foreach (var tag in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            tagsArray.Add(new JsonObject { ["key"] = tag.Key, ["value"] = tag.Value });
        }

        return new JsonObject
        {
            ["instanceName"] = instanceName,
            ["instanceSnapshotName"] = snapshotName,
            ["tags"] = tagsArray,
        };
    }

    private async Task<LightsailSnapshotContext> GetContextAsync(string serverId, CancellationToken ct)
    {
        var context = await _contexts.GetAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new LightsailSnapshotNotFoundException(
                $"No Lightsail snapshot context is configured for server '{serverId}', so Servyx does not know "
                + "which instance backs it.");

        if (!LightsailSnapshotOwnership.IsSupportedServerId(context.ServerId))
        {
            throw new ArgumentException(
                $"Server id '{context.ServerId}' cannot be carried in a Lightsail instance-snapshot name, so a "
                + "snapshot taken for it could never be recognised as Servyx's afterwards — it would bill forever "
                + "and never be pruned. Lightsail resource names match \\w[\\w\\-]*\\w, so ids may contain only "
                + "letters, digits, '-' and '_'.",
                nameof(serverId));
        }

        if (string.IsNullOrWhiteSpace(context.InstanceName))
        {
            throw new ArgumentException(
                $"Server '{context.ServerId}' maps to no Lightsail instance name, so Servyx cannot tell which "
                + "machine to snapshot — or, far worse, whose snapshots retention would be entitled to delete.",
                nameof(serverId));
        }

        return context;
    }

    /// <summary>
    /// Lists this instance's snapshots and labels each one at the point it is discovered.
    /// </summary>
    /// <remarks>
    /// Ownership is decided here, once, by <see cref="LightsailSnapshotOwnership.Classify"/>, and never
    /// re-inferred from an artifact's shape further downstream. Snapshots of other instances are filtered out
    /// entirely: they are not this server's backups, and never letting them into the list is what keeps one
    /// server's retention from ever seeing another server's snapshots.
    /// </remarks>
    private async Task<IReadOnlyList<ResolvedSnapshot>> ListResolvedAsync(
        LightsailSnapshotContext context,
        CancellationToken ct)
    {
        var snapshots = await _api.GetInstanceSnapshotsAsync(ct).ConfigureAwait(false);

        var resolved = snapshots
            .Where(s => string.Equals(s.FromInstanceName, context.InstanceName, StringComparison.Ordinal))
            .Select(s => Resolve(context, s))
            .OrderBy(s => s.Artifact.CreatedAt)
            .ThenBy(s => s.Artifact.Id, StringComparer.Ordinal)
            .ToList();

        // "First of the chain" is a property of the whole listing, not of a snapshot on its own: the oldest
        // capture Servyx holds of this instance is the one that stored its in-use blocks, and every later one
        // stored only what changed since. It is what decides whether the cost ceiling is close to the truth or far
        // above it, so it is computed here rather than guessed at the point a figure is rendered.
        var oldestServyxOwned = resolved.FindIndex(s => s.Artifact.Ownership == BackupOwnership.Servyx);
        if (oldestServyxOwned >= 0)
        {
            resolved[oldestServyxOwned] = resolved[oldestServyxOwned] with { IsFirstOfChain = true };
        }

        return resolved;
    }

    private ResolvedSnapshot Resolve(LightsailSnapshotContext context, LightsailInstanceSnapshot snapshot)
    {
        var ownership = LightsailSnapshotOwnership.Classify(
            snapshot.FromInstanceName,
            snapshot.Name,
            snapshot.Tags,
            context.ServerId,
            context.InstanceName);

        // For a Servyx snapshot the instant comes from the name Servyx wrote, not from Lightsail's clock: it is
        // what retention buckets on, and reading it back off the name makes the bucketing independent of the
        // provider's own timestamp. A foreign snapshot has no such name, so its provider timestamp is used - and
        // a foreign snapshot's instant never reaches retention anyway.
        var createdAt = ownership == BackupOwnership.Servyx
            && LightsailSnapshotOwnership.TryParseSnapshotName(snapshot.Name, out _, out var named)
                ? named
                : snapshot.CreatedAt ?? DateTimeOffset.UnixEpoch;

        var artifact = new BackupArtifact(
            LightsailSnapshotBackupId.Format(context.ServerId, snapshot.Name),
            ownership,
            createdAt,
            ToBytes(snapshot.TotalSourceGigabytes),
            LightsailSnapshotBackupId.LocationOf(_region, snapshot.Name));

        return new ResolvedSnapshot(artifact, snapshot);
    }

    private async Task<(LightsailSnapshotContext Context, ResolvedSnapshot Snapshot)> ResolveAsync(
        string backupId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);

        if (!LightsailSnapshotBackupId.TryGetServerId(backupId, out var serverId))
        {
            throw new LightsailSnapshotNotFoundException(
                $"Backup id '{backupId}' is not in a form this provider issued.",
                backupId);
        }

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var resolved = await ListResolvedAsync(context, ct).ConfigureAwait(false);

        return (context, resolved.FirstOrDefault(s =>
            string.Equals(s.Artifact.Id, backupId, StringComparison.Ordinal))
            ?? throw new LightsailSnapshotNotFoundException(
                $"Backup '{backupId}' does not exist: Lightsail no longer reports an instance snapshot with that "
                + $"name for server '{serverId}'. It may have been deleted in the console, by another tool, or by a "
                + "prune — or the instance it names may have been replaced, in which case the snapshot is still "
                + "there but is no longer this server's.",
                backupId));
    }

    private IReadOnlyList<string> Describe(LightsailSnapshotContext context, ResolvedSnapshot snapshot)
    {
        var source = snapshot.Source;
        var isServyx = snapshot.Artifact.Ownership == BackupOwnership.Servyx;

        var lines = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"AWS Lightsail instance snapshot '{snapshot.SnapshotName}' of instance "
                + $"'{context.InstanceName}' in {_region}, taken {Format(snapshot.Artifact.CreatedAt)}."),

            $"Ownership: {snapshot.Artifact.Ownership}."
                + (isServyx
                    ? " Created by Servyx and subject to this server's retention policy."
                    : " Servyx did not create this snapshot and will never delete it — it is listed and "
                      + "inspectable, and retention cannot reach it."
                      + (source.IsFromAutoSnapshot
                          ? " It came from Lightsail's own automatic-snapshot add-on, which AWS does not allow "
                            + "anybody to tag; Lightsail rotates those itself, keeping the latest seven."
                          : string.Empty)),

            "Covered: the instance's ENTIRE system disk as it was at that instant"
                + (source.FromAttachedDisks.Count > 0
                    ? $", PLUS {source.FromAttachedDisks.Count} attached block storage disk(s), which Lightsail "
                      + "copies as part of an instance snapshot."
                    : ". Lightsail reports no attached block storage disks on this snapshot, so the instance had "
                      + "none attached when it was taken — anything the workload wrote to a disk attached later is "
                      + "not in this backup."),
        };

        foreach (var disk in source.FromAttachedDisks)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  disk '{disk.Name}' at {disk.Path ?? "(path not reported)"}, "
                + $"{(disk.SizeInGb is { } gb ? gb + " GB" : "size not reported")}"
                + $"{(disk.IsSystemDisk ? ", the instance's system disk." : ".")}"));
        }

        lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"Source machine: blueprint '{source.FromBlueprintId ?? "not reported"}', bundle "
            + $"'{source.FromBundleId ?? "not reported"}'. A restore cannot use a SMALLER bundle than this one."));

        lines.Add(
            "Consistency: CRASH-CONSISTENT at best. The snapshot is a point-in-time copy of the disks; Servyx does "
            + "not stop the instance or quiesce the workload first, and Lightsail publishes no hook that would. A "
            + "save file that was mid-write is captured mid-write and may need recovery on restore, exactly as "
            + "after a power cut. Servyx does not claim application consistency and cannot obtain it here.");

        lines.Add(
            "NOT covered by this backup: RAM and process state; anything on a block storage disk that was not "
            + "attached at the moment of capture; anything outside the instance entirely (a Lightsail managed "
            + "database, a bucket, a load balancer); and — a Lightsail-specific one — the instance's CUSTOM "
            + "FIREWALL RULES, which AWS documents as not copying to an instance created from a snapshot.");

        lines.Add(
            "File list: NOT AVAILABLE. Lightsail exposes no way to enumerate or extract an individual file from an "
            + "instance snapshot without first creating a resource from it, so Servyx does not claim to know what "
            + "is inside. This is a real difference from an archive-based backup, not an omission.");

        lines.Add(LightsailSnapshotPricing.DescribeMonthlyCeiling(snapshot.SourceGigabytes, snapshot.IsFirstOfChain));

        lines.Add(
            "Lifetime: a manual Lightsail snapshot never expires on its own, and survives the deletion of the "
            + "instance it came from. It exists, and bills, until something deletes it — "
            + (isServyx
                ? "for this one, that means Servyx's retention policy or a human."
                : "and for this one, only a human or Lightsail's own rotation, because Servyx never prunes what it "
                  + "did not create."));

        lines.Add(
            "Restoring from it creates a NEW instance and overwrites nothing. Preview with PlanRestoreAsync, which "
            + "sets out the full procedure; this provider's RestoreAsync always refuses, by design.");

        return lines;
    }

    private IReadOnlyList<string> DescribeRestore(LightsailSnapshotContext context, ResolvedSnapshot snapshot)
    {
        var source = snapshot.Source;
        var zone = source.AvailabilityZone is { Length: > 0 } az
            ? "availability zone " + az
            : "an availability zone you choose (Lightsail did not report one on this snapshot)";

        var lines = new List<string>
        {
            "NOT AN OVERWRITE, AND NOT A RESTORE OF THIS SERVER. Restoring from a Lightsail instance snapshot does "
            + "not replace a disk in place the way a DigitalOcean droplet restore does, and does not produce a "
            + "volume to attach the way an EBS snapshot does. CreateInstancesFromSnapshot creates a NEW, SEPARATE, "
            + $"SEPARATELY-BILLING instance. Instance '{context.InstanceName}' is not touched, not stopped, and "
            + "keeps running on its current data throughout.",

            "THIS PROVIDER WILL NOT CARRY IT OUT. RestoreAsync always refuses. The steps below are the real "
            + "procedure, for an operator or for the provisioning path — not a description of something Servyx is "
            + "about to do. Nothing has been sent to AWS by previewing this plan.",

            string.Create(
                CultureInfo.InvariantCulture,
                $"Source: snapshot '{snapshot.SnapshotName}' of instance '{context.InstanceName}' in {_region}, "
                + $"taken {Format(snapshot.Artifact.CreatedAt)}."),

            string.Create(
                CultureInfo.InvariantCulture,
                $"Step 1: CreateInstancesFromSnapshot with instanceSnapshotName '{snapshot.SnapshotName}', a NEW "
                + $"instanceName of your choosing, {zone}, and a bundleId of at least "
                + $"'{source.FromBundleId ?? "the original bundle"}'. ")
            + "AWS does not allow a snapshot to be restored onto a smaller bundle than the instance it came from.",
        };

        if (source.FromAttachedDisks.Count > 0)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Step 2: the snapshot carries {source.FromAttachedDisks.Count} attached block storage disk(s), "
                + $"which come back with the new instance. ")
                + "CreateInstancesFromSnapshot's attachedDiskMapping is what names them and says which path each "
                + "is mounted at; Servyx does not choose names for disks it is not creating. The disks were:");

            foreach (var disk in source.FromAttachedDisks)
            {
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  '{disk.Name}' at {disk.Path ?? "(path not reported)"}, "
                    + $"{(disk.SizeInGb is { } gb ? gb + " GB" : "size not reported")}."));
            }
        }
        else
        {
            lines.Add(
                "Step 2: Lightsail reports no attached block storage disks on this snapshot, so there is no "
                + "attachedDiskMapping to supply — only the system disk comes back.");
        }

        lines.Add(
            "Step 3: the new instance is NOT this server yet. It has a different name and a different public "
            + "address, and — AWS documents this explicitly — only the DEFAULT firewall rules: every custom rule "
            + "the original instance had must be re-created by hand, or the workload's port will be closed.");

        lines.Add(
            "Step 4: from this point BOTH instances exist and BOTH bill. Making the new one the server means "
            + "moving any static IP, re-pointing DNS, telling Servyx that this server now runs on the new "
            + "instance, and only then deleting or stopping the old machine. Servyx does none of that from here.");

        lines.Add(
            "DATA IMPACT of step 1 alone: " + DataImpact.Preserved + ". Creating an instance from a snapshot "
            + $"destroys nothing — everything on '{context.InstanceName}' is exactly where it was. The destructive "
            + "part is step 4, when the old instance is deleted, and that is " + DataImpact.Destroyed
            + " for everything written to it since " + Format(snapshot.Artifact.CreatedAt)
            + ". Servyx will not perform it and this plan does not authorise it.");

        lines.Add(
            "The snapshot itself is NOT consumed or deleted by a restore. It continues to exist and to bill. "
            + LightsailSnapshotPricing.DescribeMonthlyCeiling(snapshot.SourceGigabytes, snapshot.IsFirstOfChain));

        lines.Add(
            snapshot.Artifact.Ownership == BackupOwnership.Servyx
                ? "Consistency of the source: CRASH-CONSISTENT at best, not application-consistent. Expect the "
                  + "restored filesystem to replay a journal, and expect an application that was mid-write to need "
                  + "its own recovery — plan the restore as you would a recovery from a power cut, not from a "
                  + "clean shutdown."
                : "Consistency of the source: UNKNOWN. Servyx did not take this snapshot and cannot say what state "
                  + "the instance was in when it was taken, or whether the workload had been stopped first.");

        lines.Add($"Backup ownership: {snapshot.Artifact.Ownership}.");

        return lines;
    }

    private static string RenderTags(IReadOnlyDictionary<string, string> tags) =>
        tags.Count == 0
            ? "none"
            : string.Join(", ", tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}"));

    private static decimal? SourceGigabytes(LightsailInstanceSnapshot snapshot) => snapshot.TotalSourceGigabytes;

    private static decimal? SumGigabytes(IEnumerable<ResolvedSnapshot> snapshots)
    {
        decimal total = 0m;

        foreach (var snapshot in snapshots)
        {
            total += snapshot.SourceGigabytes ?? 0m;
        }

        return total;
    }

    private static long ToBytes(int? gigabytes) =>
        gigabytes is { } value && value >= 0 ? (long)decimal.Round(value * BytesPerGigabyte) : 0L;

    private static string Format(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>One instance snapshot, with both the artifact Servyx reports and the provider object it came from.</summary>
    private sealed record ResolvedSnapshot(BackupArtifact Artifact, LightsailInstanceSnapshot Source)
    {
        internal string SnapshotName => Source.Name;

        internal string? FromInstanceName => Source.FromInstanceName;

        internal IReadOnlyDictionary<string, string> Tags => Source.Tags;

        internal decimal? SourceGigabytes => Source.TotalSourceGigabytes;

        /// <summary>
        /// Whether this is the only capture Servyx holds of this instance, which is what decides whether the cost
        /// ceiling is close to the truth or far above it.
        /// </summary>
        internal bool IsFirstOfChain { get; init; }
    }
}

/// <summary>
/// An upper bound on what a server's Lightsail instance snapshots cost per month, split by whether Servyx owns
/// them.
/// </summary>
/// <param name="ServyxOwnedCount">How many snapshots Servyx created and manages under retention.</param>
/// <param name="ForeignCount">How many snapshots Servyx did not create and will never delete.</param>
/// <param name="ServyxOwnedMonthlyCeiling">The maximum monthly list price of the Servyx-owned snapshots.</param>
/// <param name="ForeignMonthlyCeiling">
/// The maximum monthly list price of the foreign ones. Reported separately and never summed silently into the
/// first figure: it is a real charge on the account, but it is not a charge Servyx's retention will ever reduce.
/// </param>
/// <param name="AnySizeUnknown">
/// Whether Lightsail reported no source disk size for at least one snapshot, so even the ceiling is incomplete.
/// </param>
/// <remarks>
/// Every figure here is a <strong>ceiling</strong> derived from source disk sizes, not a price — see
/// <see cref="LightsailSnapshotPricing"/>. Lightsail bills snapshots incrementally and AWS says so explicitly, so
/// the real charge for a server with several captures of the same instance is normally a small fraction of the
/// number below.
/// </remarks>
public sealed record LightsailSnapshotStorageCeiling(
    int ServyxOwnedCount,
    int ForeignCount,
    CostEstimate ServyxOwnedMonthlyCeiling,
    CostEstimate ForeignMonthlyCeiling,
    bool AnySizeUnknown);
