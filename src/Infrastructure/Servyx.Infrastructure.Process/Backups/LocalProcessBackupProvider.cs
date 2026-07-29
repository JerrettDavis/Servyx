using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Servyx.Domain.Backups;
using Servyx.Domain.Connectors;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process.Backups;

/// <summary>
/// An <see cref="IBackupProvider"/> that creates, lists, inspects, restores, and prunes backups for a game
/// server installed as a plain process on the machine Servyx itself is running on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The archive is built in-process, and that is a portability decision before it is a performance
/// one.</strong> <c>SshBackupProvider</c> runs the <em>remote host's</em> <c>tar</c> because pulling a save
/// directory down over SFTP only to push it straight back up to the same machine would be absurd. Locally
/// there is no wire, so that reason evaporates and the only remaining question is whether to shell out to a
/// <c>tar</c> binary or archive with <see cref="TarWriter"/> and <see cref="GZipStream"/>. This provider
/// does the latter, for four reasons:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <em>A <c>tar</c> binary is not a safe assumption on Windows.</em> Recent Windows builds do ship
/// <c>tar.exe</c> (bsdtar), but "recent" is not "every machine an operator will run this panel on", and it
/// can be absent, shadowed on <c>PATH</c>, or a different implementation entirely. CI here runs Ubuntu and
/// development runs Windows, so a shell-out would put the one platform that can actually fail beyond the
/// reach of the tests that would catch it. A missing binary would surface as a runtime failure on the
/// platform nobody tested.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>bsdtar and GNU tar do not agree.</em> Exclude-pattern semantics, how a drive-letter path is
/// interpreted, and whether a backslash is a separator or a filename character all differ. An adapter whose
/// captured file set depends on which <c>tar</c> happens to be installed does not have one behaviour to
/// test; it has as many as there are hosts.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>The write guard reaches an in-process archive and cannot reach a subprocess.</em>
/// <see cref="WriteGuardedExecutionTarget"/> gates <c>WriteFileAsync</c> and <c>DeleteAsync</c> but
/// deliberately not <c>ExecuteAsync</c>. Building the archive here and writing it through
/// <c>WriteFileAsync</c> puts every archive byte behind the guard structurally; <c>tar --create</c> as a
/// subprocess would sail straight past it and put a real file on disk before anything checked.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>Both APIs are in the shared framework.</em> <see cref="System.Formats.Tar"/> and
/// <see cref="System.IO.Compression"/> need no package reference, so the choice costs nothing in
/// dependencies. <c>DockerBackupProvider</c> already archives this way in this repository, so this is an
/// established in-repo pattern rather than an invention.
/// </description>
/// </item>
/// </list>
/// <para>
/// The one thing given up is streaming: the archive is assembled in memory before it is written, because
/// <c>LocalExecutionTarget.WriteFileAsync</c> buffers its content stream anyway to write it atomically. A
/// tens-of-gigabytes save directory is therefore out of reach of this provider in a way it is not out of
/// reach of the SSH one. That is a real limitation, stated rather than hidden; the honest fix is a streaming
/// write on the target, which is a change to the transport, not to this file.
/// </para>
/// <para>
/// <strong>Globs are supported in both <c>include</c> and <c>exclude</c>.</strong> The SSH provider rejects
/// a wildcard include outright, because a <see cref="CommandSpec"/>'s arguments reach the host as an argv
/// array with no shell in between, so a <c>*</c> would become a literal filename and the backup would
/// quietly capture nothing. That constraint does not exist here: this provider walks the tree itself and
/// matches every candidate with <see cref="BackupGlob"/>, so <c>saves/**/*.db</c> selects what its author
/// meant. A wildcard include also prunes whole subtrees rather than filtering file by file, so an excluded
/// directory costs one skipped listing rather than one rejection per file inside it.
/// </para>
/// <para>
/// <strong>Foreign artifacts are never pruned, and that is structural.</strong> Three independent barriers
/// stand between <see cref="PruneAsync"/> and a foreign archive, each sufficient on its own:
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
/// <em>Evaluation.</em> <see cref="BackupRetentionEvaluator.SelectForRemoval"/> throws
/// <see cref="ForeignBackupProtectedException"/> if a foreign artifact reaches it, so retention cannot even
/// be <em>computed</em> over one. This is what makes the guarantee hold for <c>dryRun: true</c> as strongly
/// as for <c>dryRun: false</c>: a dry run's report is produced by the same call, so there is no path that
/// "hypothetically" schedules a foreign archive for removal.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>Deletion.</em> <see cref="DeleteServyxOwnedAsync"/> is the only method that issues a delete, and it
/// re-checks ownership <em>and</em> that the path lies inside the Servyx artifact directory — which is not
/// where any adopter looks. A foreign archive fails both checks, so even a caller that fabricated a
/// mislabelled artifact could not route a delete at one.
/// </description>
/// </item>
/// </list>
/// <para>
/// Nothing in this type moves or renames an artifact either. The only <see cref="File"/>-level rename that
/// occurs anywhere on this path is the temp-file swap inside <c>LocalExecutionTarget.WriteFileAsync</c>,
/// which targets the file being written and never an existing artifact.
/// </para>
/// <para>
/// <strong>The write guard is checked before anything is created, not after.</strong> The guard gates file
/// writes and deletes, so the archive write is covered — but creating the artifact directory is a
/// <see cref="Directory.CreateDirectory(string)"/> call, which no target abstraction mediates, exactly as
/// the local provisioner's own <c>ensure-dir</c> verb is. Without an up-front check a read-only server would
/// have a directory created for it and then fail on the write. So <see cref="CreateAsync"/>,
/// <see cref="RestoreAsync"/> and a live <see cref="PruneAsync"/> ask the guard for its posture first and
/// throw <see cref="WritesDisabledException"/> — the same exception the guard itself throws, naming the
/// server and the operation — before a directory, a temp file, or a subprocess exists. See
/// <see cref="ResolveWriteMode"/>. (This provider starts no subprocess at all, so the separate finding that
/// the guard does not gate <c>ExecuteAsync</c> has nothing to bite on here; the in-process archiving choice
/// is what makes that true.)
/// </para>
/// <para>
/// <strong>No quiesce is taken, deliberately and explicitly.</strong> The Docker and SSH providers issue a
/// definition-declared control command to flush a live server's in-memory state before archiving. This one
/// does not: it captures whatever is on disk at the moment it walks the tree. An operator taking a local
/// backup of a running server should stop it first, or accept an archive of the last state the server itself
/// wrote. The manifest carries no <c>quiescedWith</c> field precisely so that this is not ambiguous — see
/// <see cref="BackupManifest"/>.
/// </para>
/// <para>
/// <strong>Restores are planned, then applied.</strong> <see cref="PlanRestoreAsync"/> reads the manifest and
/// returns a <see cref="RestorePlan"/> naming every path a restore would overwrite; it writes nothing.
/// <see cref="RestoreAsync"/> accepts only a plan id, and the plan is single-use, time-bounded, and
/// re-validated against the archive's current size before a byte is written.
/// </para>
/// <para>
/// <strong>Not registered by <c>AddServyxLocalProcess()</c>.</strong> See
/// <see cref="LocalProcessBackupServiceCollectionExtensions.AddServyxLocalProcessBackups"/>: creating,
/// restoring, and pruning backups are mutating capabilities, so this type is opt-in and unreachable from the
/// default read-only composition root.
/// </para>
/// </remarks>
public sealed class LocalProcessBackupProvider : IBackupProvider
{
    /// <summary>Filename prefix identifying an archive as Servyx-owned.</summary>
    public const string ArchivePrefix = "servyx-";

    /// <summary>Filename suffix of every archive this provider writes.</summary>
    public const string ArchiveSuffix = ".tar.gz";

    /// <summary>Filename suffix of the sidecar manifest written next to each archive.</summary>
    public const string ManifestSuffix = ".manifest.json";

    /// <summary>How long a <see cref="RestorePlan"/> stays applicable after it is produced.</summary>
    public static readonly TimeSpan DefaultRestorePlanTtl = TimeSpan.FromMinutes(15);

    private const string ArchiveTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    private readonly ILocalBackupContextSource _contexts;
    private readonly IReadOnlyList<IBackupAdopter> _adopters;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _restorePlanTtl;
    private readonly ConcurrentDictionary<string, PendingRestore> _plans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SandboxedPathResolver> _resolvers = new(StringComparer.Ordinal);

    /// <summary>Creates a provider over the given context source.</summary>
    /// <param name="contexts">Supplies the per-server backup context. Substituted in tests.</param>
    /// <param name="adopters">
    /// Adopters consulted by <see cref="ListAsync"/> for foreign archives. Only those whose
    /// <see cref="IBackupAdopter.Supports"/> accepts the context's deployment kind are called. This project
    /// ships none — see <see cref="ForeignLocalBackupDirectory"/>.
    /// </param>
    /// <param name="timeProvider">Clock used for archive naming and restore-plan expiry.</param>
    /// <param name="restorePlanTtl">How long a restore plan stays applicable. Defaults to <see cref="DefaultRestorePlanTtl"/>.</param>
    public LocalProcessBackupProvider(
        ILocalBackupContextSource contexts,
        IEnumerable<IBackupAdopter>? adopters = null,
        TimeProvider? timeProvider = null,
        TimeSpan? restorePlanTtl = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);

        _contexts = contexts;
        _adopters = adopters?.ToList() ?? [];
        _timeProvider = timeProvider ?? TimeProvider.System;
        _restorePlanTtl = restorePlanTtl ?? DefaultRestorePlanTtl;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Walks the include set minus the exclude set, builds a gzipped tar in memory, hashes it, and writes the
    /// archive and its sidecar manifest through the target — so both writes pass the write guard. The
    /// artifact directory is always excluded from the walk, so an archive can never contain a previous
    /// archive, and so are any declared foreign archive directories that sit under the same root.
    /// </remarks>
    /// <exception cref="WritesDisabledException">The target's write mode is not <see cref="WriteMode.Enabled"/>.</exception>
    public async Task<BackupArtifact> CreateAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);

        // Before anything: creating the artifact directory is a Directory.CreateDirectory the guard cannot
        // see, so a read-only server has to be refused here rather than at the first guarded write.
        RequireWritesEnabled(context, "create a backup");

        // Purely local validation, ahead of the first byte read: an include set that was going to be
        // rejected should be rejected before the walk, not after it.
        var includes = EffectiveIncludes(context);
        var excludes = EffectiveExcludes(context);

        var captured = await CollectAsync(context, includes, excludes, ct).ConfigureAwait(false);
        var archiveBytes = await BuildArchiveAsync(context, captured, ct).ConfigureAwait(false);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(archiveBytes));

        var createdAt = _timeProvider.GetUtcNow();
        var fileName = await ReserveArchiveNameAsync(context, createdAt, ct).ConfigureAwait(false);

        var storeRelative = StoreRelativeOf(context);
        var archiveRelative = storeRelative + "/" + fileName;
        var resolver = ResolverFor(context.Root);

        // The first mutation of any kind on this path.
        EnsureDirectory(AbsolutePath(context, storeRelative));

        await context.Target.WriteFileAsync(
            resolver.Resolve(archiveRelative),
            new MemoryStream(archiveBytes, writable: false),
            new FileWriteOptions(null),
            ct).ConfigureAwait(false);

        var manifest = new BackupManifest(
            BackupManifest.CurrentSchemaVersion,
            context.ServerId,
            createdAt,
            fileName,
            sha256,
            archiveBytes.LongLength,
            AbsolutePath(context, string.Empty),
            captured.Select(c => c.EntryName).ToList());

        await context.Target.WriteFileAsync(
            resolver.Resolve(archiveRelative + ManifestSuffix),
            new MemoryStream(manifest.ToUtf8Json(), writable: false),
            new FileWriteOptions(null),
            ct).ConfigureAwait(false);

        var location = AbsolutePath(context, archiveRelative);
        return new BackupArtifact(
            BackupArtifactId.Format(context.ServerId, location),
            BackupOwnership.Servyx,
            createdAt,
            archiveBytes.LongLength,
            location);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The Servyx-owned half comes from the context's artifact directory; the foreign half comes from
    /// whichever registered <see cref="IBackupAdopter"/>s support the context's deployment kind, matched
    /// against the declared <see cref="ForeignLocalBackupDirectory"/> list. Each half is tagged with its own
    /// <see cref="BackupOwnership"/> at the point it is discovered, never inferred later.
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
    /// For a Servyx-owned artifact this reads the sidecar manifest and never opens the archive at all —
    /// nothing is decompressed. For an archive with no manifest (a foreign one, or one whose sidecar was
    /// removed outside Servyx) it reads the tar entry <em>headers</em> with <c>copyData: false</c>, so no
    /// entry's data stream is ever touched. Either way nothing is extracted and nothing is written.
    /// </remarks>
    public async Task<IReadOnlyList<string>> InspectAsync(string backupId, CancellationToken ct = default)
    {
        var (context, artifact) = await ResolveAsync(backupId, ct).ConfigureAwait(false);
        return await ReadEntryNamesAsync(context, artifact, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Read-only: this reads the archive's index and maps each entry to the absolute path a restore would
    /// overwrite. It writes nothing, deletes nothing, creates no directory, and leaves the archive untouched.
    /// </remarks>
    public async Task<RestorePlan> PlanRestoreAsync(string backupId, CancellationToken ct = default)
    {
        var (context, artifact) = await ResolveAsync(backupId, ct).ConfigureAwait(false);
        var entries = await ReadEntryNamesAsync(context, artifact, ct).ConfigureAwait(false);

        var affected = entries
            .Select(entry => AbsolutePath(context, BackupGlob.Normalize(entry)))
            .ToList();

        var planId = $"restore-{Guid.NewGuid():n}";
        var plan = new RestorePlan(planId, artifact.Artifact.Id, affected);

        _plans[planId] = new PendingRestore(
            plan,
            context.ServerId,
            _timeProvider.GetUtcNow(),
            artifact.Artifact.SizeBytes);

        return plan;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Accepts only a plan id produced by <see cref="PlanRestoreAsync"/>. The plan is consumed on the first
    /// attempt, expires after the configured TTL, and is re-checked against the archive's current size before
    /// any write — an unknown, spent, expired, or superseded plan throws
    /// <see cref="RestorePlanStaleException"/> rather than restoring something the operator never previewed.
    /// <para>
    /// The write-mode check happens <em>before</em> the plan is consumed, so refusing a read-only server does
    /// not also burn the operator's plan: they enable writes and apply the same plan, rather than having to
    /// preview again because the refusal spent it.
    /// </para>
    /// <para>
    /// Every entry name out of the archive is re-resolved through the sandbox before it is written, so an
    /// archive carrying <c>../</c> in a header cannot write outside the server's own data directory.
    /// </para>
    /// </remarks>
    /// <exception cref="WritesDisabledException">The target's write mode is not <see cref="WriteMode.Enabled"/>.</exception>
    public async Task RestoreAsync(string restorePlanId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restorePlanId);

        if (!_plans.TryGetValue(restorePlanId, out var pending))
        {
            throw new RestorePlanStaleException(
                $"Restore plan '{restorePlanId}' is unknown or has already been applied. Preview the restore again.",
                restorePlanId);
        }

        var context = await GetContextAsync(pending.ServerId, ct).ConfigureAwait(false);
        RequireWritesEnabled(context, "restore a backup");

        // Only now is the plan spent: a refusal above leaves it applicable once writes are enabled.
        if (!_plans.TryRemove(restorePlanId, out pending))
        {
            throw new RestorePlanStaleException(
                $"Restore plan '{restorePlanId}' was applied concurrently. Preview the restore again.",
                restorePlanId);
        }

        var age = _timeProvider.GetUtcNow() - pending.CreatedAt;
        if (age > _restorePlanTtl)
        {
            throw new RestorePlanStaleException(
                $"Restore plan '{restorePlanId}' expired after {_restorePlanTtl}. Preview the restore again.",
                restorePlanId);
        }

        var resolved = await ListResolvedAsync(context, ct).ConfigureAwait(false);
        var artifact = resolved.FirstOrDefault(r => string.Equals(r.Artifact.Id, pending.Plan.BackupId, StringComparison.Ordinal))
            ?? throw new RestorePlanStaleException(
                $"The backup '{pending.Plan.BackupId}' this plan was computed from no longer exists.",
                restorePlanId);

        if (artifact.Artifact.SizeBytes != pending.SizeBytes)
        {
            throw new RestorePlanStaleException(
                $"The backup '{pending.Plan.BackupId}' changed after this plan was computed. Preview the restore again.",
                restorePlanId);
        }

        await ApplyAsync(context, artifact, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// See the type remarks for the three barriers that make foreign artifacts unprunable. Under
    /// <c>dryRun: true</c> this issues no delete of any kind and needs no write permission; under either
    /// flag, <see cref="PruneResult.SkippedForeign"/> reports how many foreign artifacts were seen and left
    /// alone.
    /// </remarks>
    public async Task<PruneResult> PruneAsync(string serverId, RetentionPolicy policy, bool dryRun, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        if (!dryRun)
        {
            RequireWritesEnabled(context, "prune backups");
        }

        var effectivePolicy = policy ?? context.DefaultRetention;
        var all = await ListResolvedAsync(context, ct).ConfigureAwait(false);

        // Barrier 1: the partition. Only the Servyx-owned half is bound to a name anything below can see; the
        // foreign half is reduced to a count here and never reaches retention or deletion.
        var skippedForeign = all.Count(r => r.Artifact.Ownership == BackupOwnership.Foreign);
        var ownedByServyx = all
            .Where(r => r.Artifact.Ownership == BackupOwnership.Servyx)
            .ToList();

        // Barrier 2: evaluation. SelectForRemoval throws on anything not Servyx-owned, so a dry run and a
        // live run compute their answer from the identical, ownership-asserting call.
        var removals = BackupRetentionEvaluator.SelectForRemoval(
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
    /// Barrier 3: the only method in this type that deletes anything. It re-asserts both that the artifact is
    /// Servyx-owned and that it lives inside the Servyx artifact directory, so a mislabelled or out-of-tree
    /// artifact throws instead of being removed.
    /// </summary>
    private async Task DeleteServyxOwnedAsync(LocalBackupContext context, ResolvedArtifact artifact, CancellationToken ct)
    {
        if (artifact.Artifact.Ownership != BackupOwnership.Servyx)
        {
            throw new ForeignBackupProtectedException(
                $"Refusing to delete '{artifact.Artifact.Location}': it is {artifact.Artifact.Ownership}, not Servyx-owned.",
                artifact.Artifact.Location);
        }

        var storeDirectory = AbsolutePath(context, StoreRelativeOf(context));
        if (!artifact.Artifact.Location.StartsWith(storeDirectory + System.IO.Path.DirectorySeparatorChar, PathComparison))
        {
            throw new ForeignBackupProtectedException(
                $"Refusing to delete '{artifact.Artifact.Location}': it is outside the Servyx artifact directory '{storeDirectory}'.",
                artifact.Artifact.Location);
        }

        if (artifact.RelativePath is null)
        {
            // Only a foreign artifact has no root-relative path, and the ownership check above already
            // refused those. Reaching here means the two disagreed, which is a bug, not a delete.
            throw new ForeignBackupProtectedException(
                $"Refusing to delete '{artifact.Artifact.Location}': it is not addressable inside the server's data directory.",
                artifact.Artifact.Location);
        }

        var resolver = ResolverFor(context.Root);
        await context.Target.DeleteAsync(resolver.Resolve(artifact.RelativePath), ct).ConfigureAwait(false);

        if (artifact.ManifestRelativePath is not null)
        {
            try
            {
                await context.Target.DeleteAsync(resolver.Resolve(artifact.ManifestRelativePath), ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // An archive whose sidecar was already gone is still pruned; the manifest is an index, not the
                // artifact, and its absence must not leave the archive behind forever.
            }
        }
    }

    /// <summary>
    /// Refuses the operation when the target carries a write guard that is not
    /// <see cref="WriteMode.Enabled"/>.
    /// </summary>
    /// <remarks>
    /// A target with no guard anywhere in it answers <see langword="null"/> and is allowed through: this
    /// method's job is to surface a refusal the guard would make anyway, earlier and with a better message,
    /// not to invent a second policy. Anything that does slip past still meets the guard at the first real
    /// write.
    /// </remarks>
    private static void RequireWritesEnabled(LocalBackupContext context, string operation)
    {
        var mode = ResolveWriteMode(context.Target);
        if (mode is null or WriteMode.Enabled)
        {
            return;
        }

        throw new WritesDisabledException(
            $"Refusing to {operation} for server '{context.ServerId}': the server's write mode is {mode}. " +
            $"Writes require {nameof(WriteMode)}.{nameof(WriteMode.Enabled)}, set per server and never globally. " +
            "Listing, inspecting, previewing a restore, and a dry-run prune all remain available.");
    }

    /// <summary>
    /// The write posture a target carries, looking through a composite to whichever half would perform the
    /// write, or <see langword="null"/> when no guard is present.
    /// </summary>
    private static WriteMode? ResolveWriteMode(IExecutionTarget target) => target switch
    {
        WriteGuardedExecutionTarget guarded => guarded.Mode,
        ICompositeExecutionTarget composite => Composite(composite),
        _ => null,
    };

    private static WriteMode? Composite(ICompositeExecutionTarget composite)
    {
        var file = composite.FileTarget is null ? null : ResolveWriteMode(composite.FileTarget);
        var exec = composite.ExecTarget is null ? null : ResolveWriteMode(composite.ExecTarget);

        // Either half being read-only is enough to refuse: this provider needs the file half to write an
        // archive, and a caller that guarded only the exec half still meant "this server does not mutate".
        return (file, exec) switch
        {
            (null, null) => null,
            (not null, null) => file,
            (null, not null) => exec,
            _ => (WriteMode)Math.Min((int)file!.Value, (int)exec!.Value),
        };
    }

    private async Task<LocalBackupContext> GetContextAsync(string serverId, CancellationToken ct)
    {
        var context = await _contexts.GetAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BackupNotFoundException($"No backup context is configured for server '{serverId}'.");

        if (StoreRelativeOf(context).Length == 0)
        {
            throw new InvalidOperationException(
                $"Server '{serverId}' declares no backup artifact directory. Servyx will not write archives into the " +
                "root it is backing up, because the next archive would then contain the previous one.");
        }

        return context;
    }

    /// <summary>The include set, normalized. Wildcards are kept, because this provider expands them itself.</summary>
    private static IReadOnlyList<string> EffectiveIncludes(LocalBackupContext context)
    {
        var includes = new List<string>();
        foreach (var include in context.Include)
        {
            includes.Add(NormalizeRelative(include));
        }

        if (includes.Count == 0)
        {
            throw new InvalidOperationException(
                $"Server '{context.ServerId}' declares no backup includes, so there is nothing to archive.");
        }

        return includes;
    }

    /// <summary>
    /// The exclude set actually applied: the definition's own excludes, plus the Servyx artifact directory
    /// itself, plus any declared foreign archive directory that sits under the same root.
    /// </summary>
    /// <remarks>
    /// This is what makes "an archive never contains an archive" a property of the traversal rather than of a
    /// reviewer's attention. The artifact directory lives under the root being archived — that is the
    /// context's shape — so without this every backup would be strictly larger than the last until the disk
    /// filled. Foreign directories get the same treatment: Servyx never manages those archives, but it does
    /// know they are archives, and sweeping someone else's tarballs into its own would double the size of
    /// every backup while adding nothing recoverable.
    /// </remarks>
    private IReadOnlyList<string> EffectiveExcludes(LocalBackupContext context)
    {
        var excludes = context.Exclude.Select(BackupGlob.Normalize).Where(p => p.Length > 0).ToList();

        Exclude(StoreRelativeOf(context));

        foreach (var foreign in context.Foreign)
        {
            if (TryMakeRootRelative(context, foreign.Directory, out var relative))
            {
                Exclude(relative);
            }
        }

        return excludes;

        void Exclude(string relative)
        {
            if (relative.Length == 0)
            {
                return;
            }

            excludes.Add(relative);
            excludes.Add(relative + "/**");
        }
    }

    /// <summary>
    /// Walks the include set and returns every file that will become an archive entry, ordered by entry name
    /// so two backups of the same tree produce the same entry order.
    /// </summary>
    private async Task<IReadOnlyList<CapturedEntry>> CollectAsync(
        LocalBackupContext context,
        IReadOnlyList<string> includes,
        IReadOnlyList<string> excludes,
        CancellationToken ct)
    {
        var captured = new Dictionary<string, CapturedEntry>(StringComparer.Ordinal);
        var resolver = ResolverFor(context.Root);

        foreach (var literal in includes.Where(i => !BackupGlob.HasWildcard(i)))
        {
            if (literal.Length > 0 && BackupGlob.MatchesAny(excludes, literal))
            {
                continue;
            }

            var stat = await context.Target
                .StatAsync(resolver.Resolve(literal.Length == 0 ? "." : literal), ct)
                .ConfigureAwait(false);

            if (!stat.Exists)
            {
                continue;
            }

            if (stat.IsDirectory)
            {
                if (literal.Length == 0 || !BackupGlob.ExcludesDirectory(excludes, literal))
                {
                    var pattern = literal.Length == 0 ? "**" : literal + "/**";
                    await WalkAsync(context, resolver, [pattern], excludes, literal, 0, captured, ct).ConfigureAwait(false);
                }

                continue;
            }

            Add(captured, literal, stat.SizeBytes ?? 0, stat.ModifiedAt);
        }

        var wildcards = includes.Where(BackupGlob.HasWildcard).ToList();
        foreach (var root in DistinctWalkRoots(wildcards))
        {
            if (root.Length > 0 && BackupGlob.ExcludesDirectory(excludes, root))
            {
                continue;
            }

            await WalkAsync(context, resolver, wildcards, excludes, root, 0, captured, ct).ConfigureAwait(false);
        }

        return captured.Values.OrderBy(c => c.EntryName, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> DistinctWalkRoots(IEnumerable<string> patterns)
    {
        var roots = patterns
            .Select(BackupGlob.StaticPrefix)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(r => r.Length)
            .ToList();

        var kept = new List<string>();
        foreach (var root in roots)
        {
            if (kept.Any(k => k.Length == 0
                || string.Equals(k, root, StringComparison.Ordinal)
                || root.StartsWith(k + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            kept.Add(root);
        }

        return kept;
    }

    private static async Task WalkAsync(
        LocalBackupContext context,
        SandboxedPathResolver resolver,
        IReadOnlyList<string> includes,
        IReadOnlyList<string> excludes,
        string directory,
        int depth,
        Dictionary<string, CapturedEntry> captured,
        CancellationToken ct)
    {
        if (depth > context.MaxTraversalDepth)
        {
            return;
        }

        IReadOnlyList<FileEntry> entries;
        try
        {
            entries = await context.Target
                .ListDirectoryAsync(resolver.Resolve(directory.Length == 0 ? "." : directory), ct)
                .ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            var child = directory.Length == 0 ? entry.Name : directory + "/" + entry.Name;

            if (entry.IsDirectory)
            {
                if (BackupGlob.ExcludesDirectory(excludes, child))
                {
                    continue;
                }

                await WalkAsync(context, resolver, includes, excludes, child, depth + 1, captured, ct).ConfigureAwait(false);
                continue;
            }

            if (!BackupGlob.MatchesAny(includes, child) || BackupGlob.MatchesAny(excludes, child))
            {
                continue;
            }

            Add(captured, child, entry.SizeBytes ?? 0, entry.ModifiedAt);
        }
    }

    private static void Add(
        Dictionary<string, CapturedEntry> captured,
        string relativePath,
        long sizeBytes,
        DateTimeOffset? modifiedAt) =>
        captured.TryAdd(relativePath, new CapturedEntry(relativePath, relativePath, sizeBytes, modifiedAt));

    private async Task<byte[]> BuildArchiveAsync(
        LocalBackupContext context,
        IReadOnlyList<CapturedEntry> captured,
        CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var resolver = ResolverFor(context.Root);
        var fallbackTimestamp = _timeProvider.GetUtcNow();

        await using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        await using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var item in captured)
            {
                await using var content = await context.Target
                    .OpenReadAsync(resolver.Resolve(item.RelativePath), ct)
                    .ConfigureAwait(false);

                var entry = new PaxTarEntry(TarEntryType.RegularFile, item.EntryName)
                {
                    DataStream = content,
                    ModificationTime = item.ModifiedAt ?? fallbackTimestamp,
                };

                await writer.WriteEntryAsync(entry, ct).ConfigureAwait(false);
            }
        }

        return buffer.ToArray();
    }

    private async Task<string> ReserveArchiveNameAsync(LocalBackupContext context, DateTimeOffset createdAt, CancellationToken ct)
    {
        var resolver = ResolverFor(context.Root);
        var storeRelative = StoreRelativeOf(context);
        var stamp = createdAt.UtcDateTime.ToString(ArchiveTimestampFormat, CultureInfo.InvariantCulture);

        for (var suffix = 1; suffix <= 1000; suffix++)
        {
            var candidate = suffix == 1
                ? $"{ArchivePrefix}{stamp}{ArchiveSuffix}"
                : $"{ArchivePrefix}{stamp}-{suffix.ToString(CultureInfo.InvariantCulture)}{ArchiveSuffix}";

            var exists = await context.Target
                .ExistsAsync(resolver.Resolve(storeRelative + "/" + candidate), ct)
                .ConfigureAwait(false);

            if (!exists)
            {
                return candidate;
            }
        }

        throw new IOException(
            $"Could not find an unused backup archive name for server '{context.ServerId}' at {stamp}.");
    }

    private async Task<IReadOnlyList<ResolvedArtifact>> ListResolvedAsync(LocalBackupContext context, CancellationToken ct)
    {
        var results = new List<ResolvedArtifact>();
        var resolver = ResolverFor(context.Root);
        var storeRelative = StoreRelativeOf(context);

        IReadOnlyList<FileEntry> storeEntries;
        try
        {
            storeEntries = await context.Target
                .ListDirectoryAsync(resolver.Resolve(storeRelative), ct)
                .ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException)
        {
            storeEntries = [];
        }

        foreach (var entry in storeEntries)
        {
            if (entry.IsDirectory ||
                !entry.Name.StartsWith(ArchivePrefix, StringComparison.Ordinal) ||
                !entry.Name.EndsWith(ArchiveSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = storeRelative + "/" + entry.Name;
            var location = AbsolutePath(context, relative);

            results.Add(new ResolvedArtifact(
                new BackupArtifact(
                    BackupArtifactId.Format(context.ServerId, location),
                    BackupOwnership.Servyx,
                    ParseCreatedAt(entry.Name) ?? entry.ModifiedAt ?? DateTimeOffset.UnixEpoch,
                    entry.SizeBytes ?? 0,
                    location),
                relative,
                relative + ManifestSuffix));
        }

        foreach (var adopter in _adopters.Where(a => a.Supports(context.DeploymentKind)))
        {
            var discovered = await adopter.DiscoverAsync(context.ServerId, ct).ConfigureAwait(false);
            foreach (var artifact in discovered)
            {
                if (artifact.Ownership != BackupOwnership.Foreign)
                {
                    throw new InvalidOperationException(
                        $"Adopter '{adopter.AdapterId}' returned artifact '{artifact.Id}' as {artifact.Ownership}; " +
                        "adopters may only report Foreign artifacts.");
                }

                if (!IsDeclaredForeign(context, artifact.Location))
                {
                    continue;
                }

                results.Add(new ResolvedArtifact(artifact, RelativePath: null, ManifestRelativePath: null));
            }
        }

        return results;
    }

    /// <summary>
    /// Whether <paramref name="location"/> sits in one of the foreign directories the composition root
    /// declared, and matches that directory's archive pattern.
    /// </summary>
    /// <remarks>
    /// An adopter naming a file nobody declared is ignored rather than trusted: surfacing an arbitrary path
    /// as a restorable backup on an adopter's say-so is exactly how a restore ends up writing a stranger's
    /// tarball over a live world — and here that world is on the panel's own machine.
    /// </remarks>
    private static bool IsDeclaredForeign(LocalBackupContext context, string location)
    {
        foreach (var foreign in context.Foreign)
        {
            var directory = System.IO.Path.GetFullPath(foreign.Directory);
            var prefix = directory.EndsWith(System.IO.Path.DirectorySeparatorChar)
                ? directory
                : directory + System.IO.Path.DirectorySeparatorChar;

            if (!location.StartsWith(prefix, PathComparison))
            {
                continue;
            }

            var name = location[prefix.Length..];
            if (name.Length > 0 &&
                !name.Contains(System.IO.Path.DirectorySeparatorChar) &&
                !name.Contains('/') &&
                BackupGlob.Matches(foreign.Pattern, name))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<(LocalBackupContext Context, ResolvedArtifact Artifact)> ResolveAsync(string backupId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);

        if (!BackupArtifactId.TryGetServerId(backupId, out var serverId))
        {
            throw new BackupNotFoundException($"Backup id '{backupId}' is not in a form this provider issued.", backupId);
        }

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var resolved = await ListResolvedAsync(context, ct).ConfigureAwait(false);
        var artifact = resolved.FirstOrDefault(r => string.Equals(r.Artifact.Id, backupId, StringComparison.Ordinal))
            ?? throw new BackupNotFoundException($"Backup '{backupId}' does not exist for server '{serverId}'.", backupId);

        return (context, artifact);
    }

    private async Task<IReadOnlyList<string>> ReadEntryNamesAsync(
        LocalBackupContext context,
        ResolvedArtifact artifact,
        CancellationToken ct)
    {
        if (artifact.ManifestRelativePath is not null)
        {
            var manifest = await TryReadManifestAsync(context, artifact.ManifestRelativePath, ct).ConfigureAwait(false);
            if (manifest is not null)
            {
                return manifest.Entries;
            }
        }

        await using var raw = await OpenArtifactAsync(context, artifact, ct).ConfigureAwait(false);
        await using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip, leaveOpen: true);

        var names = new List<string>();
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false)) is not null)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.ContiguousFile)
            {
                names.Add(NormalizeEntryName(entry.Name));
            }
        }

        return names;
    }

    private async Task<BackupManifest?> TryReadManifestAsync(LocalBackupContext context, string manifestRelativePath, CancellationToken ct)
    {
        try
        {
            await using var stream = await context.Target
                .OpenReadAsync(ResolverFor(context.Root).Resolve(manifestRelativePath), ct)
                .ConfigureAwait(false);

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            return BackupManifest.FromUtf8Json(buffer.ToArray());
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens an artifact's bytes for reading. A Servyx-owned artifact goes through the context's target, so
    /// the sandbox applies; a foreign one lives outside the root by definition and is opened by absolute
    /// path, read-only, which is the only thing Servyx ever does to a foreign archive.
    /// </summary>
    private async Task<Stream> OpenArtifactAsync(LocalBackupContext context, ResolvedArtifact artifact, CancellationToken ct)
    {
        if (artifact.RelativePath is not null)
        {
            return await context.Target
                .OpenReadAsync(ResolverFor(context.Root).Resolve(artifact.RelativePath), ct)
                .ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return new FileStream(
            artifact.Artifact.Location,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous,
            });
    }

    private async Task ApplyAsync(LocalBackupContext context, ResolvedArtifact artifact, CancellationToken ct)
    {
        var resolver = ResolverFor(context.Root);

        await using var raw = await OpenArtifactAsync(context, artifact, ct).ConfigureAwait(false);
        await using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip, leaveOpen: true);

        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: true, ct).ConfigureAwait(false)) is not null)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.ContiguousFile))
            {
                continue;
            }

            var relative = NormalizeEntryName(entry.Name);
            if (relative.Length == 0)
            {
                continue;
            }

            // Re-resolved through the sandbox: an entry name carrying "../" is refused here, not written.
            var path = resolver.Resolve(relative);

            using var content = new MemoryStream();
            if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(content, ct).ConfigureAwait(false);
            }

            content.Position = 0;

            var absolute = AbsolutePath(context, path.Value);
            EnsureDirectory(System.IO.Path.GetDirectoryName(absolute) ?? AbsolutePath(context, string.Empty));

            await context.Target
                .WriteFileAsync(path, content, new FileWriteOptions(null), ct)
                .ConfigureAwait(false);
        }
    }

    private SandboxedPathResolver ResolverFor(string root) =>
        _resolvers.GetOrAdd(root, static r => new SandboxedPathResolver(r));

    /// <summary>
    /// The absolute, OS-native path a root-relative value names. Containment is asserted by the sandbox
    /// resolver first, so this never composes a path outside the root.
    /// </summary>
    private string AbsolutePath(LocalBackupContext context, string relative)
    {
        var resolved = ResolverFor(context.Root).Resolve(relative.Length == 0 ? "." : relative);
        var root = System.IO.Path.GetFullPath(context.Root);

        return resolved.Value.Length == 0
            ? root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            : System.IO.Path.GetFullPath(
                System.IO.Path.Combine(root, resolved.Value.Replace('/', System.IO.Path.DirectorySeparatorChar)));
    }

    private static string StoreRelativeOf(LocalBackupContext context) => NormalizeRelative(context.StoreDirectory);

    /// <summary>
    /// Normalizes a root-relative path, mapping the several spellings of "the root itself" — <c>""</c>,
    /// <c>"."</c>, <c>"./"</c>, <c>"/"</c> — onto the empty string the walker uses for it.
    /// </summary>
    private static string NormalizeRelative(string value)
    {
        var normalized = BackupGlob.Normalize(value);
        return normalized == "." ? string.Empty : normalized;
    }

    /// <summary>
    /// Expresses an absolute directory as a root-relative path, when it lies under the context's root.
    /// </summary>
    private bool TryMakeRootRelative(LocalBackupContext context, string absoluteDirectory, out string relative)
    {
        relative = string.Empty;

        var root = AbsolutePath(context, string.Empty);
        var candidate = System.IO.Path.GetFullPath(absoluteDirectory)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        var prefix = root + System.IO.Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, PathComparison))
        {
            return false;
        }

        relative = BackupGlob.Normalize(candidate[prefix.Length..]);
        return relative.Length > 0;
    }

    /// <summary>
    /// The one mutation on these paths that no target abstraction mediates, and therefore the reason
    /// <see cref="RequireWritesEnabled"/> runs up front. <see cref="IExecutionTarget"/> has no
    /// create-directory member — the local provisioner's own <c>ensure-dir</c> verb is likewise a bare
    /// <see cref="Directory.CreateDirectory(string)"/>. Creation only: nothing here removes or replaces
    /// anything, and the path has already been through the sandbox resolver.
    /// </summary>
    private static void EnsureDirectory(string absoluteDirectory) => Directory.CreateDirectory(absoluteDirectory);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Strips the leading <c>./</c> some tar writers prefix entry names with, without touching a leading dot
    /// that is part of the name itself (<c>.env</c>).
    /// </summary>
    private static string NormalizeEntryName(string name) =>
        (name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name).TrimStart('/');

    private static DateTimeOffset? ParseCreatedAt(string fileName)
    {
        var stem = fileName[ArchivePrefix.Length..^ArchiveSuffix.Length];
        var dash = stem.IndexOf('-');
        if (dash > 0)
        {
            stem = stem[..dash];
        }

        return DateTimeOffset.TryParseExact(
            stem,
            ArchiveTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private sealed record CapturedEntry(
        string EntryName,
        string RelativePath,
        long SizeBytes,
        DateTimeOffset? ModifiedAt);

    /// <summary>
    /// An artifact plus the paths it is reachable through. <paramref name="RelativePath"/> is null for a
    /// foreign artifact, which by construction lives outside the context's root and is therefore never
    /// addressable through the sandboxed target — which is also why no delete can ever be routed at one.
    /// </summary>
    private sealed record ResolvedArtifact(
        BackupArtifact Artifact,
        string? RelativePath,
        string? ManifestRelativePath);

    private sealed record PendingRestore(
        RestorePlan Plan,
        string ServerId,
        DateTimeOffset CreatedAt,
        long SizeBytes);
}
