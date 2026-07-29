using System.Collections.Concurrent;
using System.Globalization;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Backups;

/// <summary>
/// An <see cref="IBackupProvider"/> that creates, lists, inspects, restores, and prunes backups for a game
/// server reached over SSH.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The archive is produced on the host and never crosses the wire.</strong> This is the one design
/// decision that separates this provider from <c>DockerBackupProvider</c>, which reads every file through
/// the transport and builds the tarball in-process. Over SSH that would be catastrophic: a save directory
/// with ten thousand files is ten thousand SFTP round trips, each paying the link's latency, and the whole
/// payload is then pulled down only to be pushed straight back up into the artifact directory on the same
/// machine. Instead this provider asks the host's own <c>tar</c> to write the archive directly into the
/// artifact directory, so a 40 GB world costs one command. What actually traverses the connection is: the
/// command line, <c>tar --list</c>'s entry names, one <c>sha256sum</c> line, and a few kilobytes of manifest
/// JSON.
/// </para>
/// <para>
/// Streaming the archive bytes back through the transport was never an option even if it were desirable:
/// <see cref="IExecutionTarget.ExecuteAsync"/> returns stdout as a <see cref="string"/>, so binary gzip
/// through it would be lossy. The remote-tar design turns that constraint into an advantage — the only
/// things this provider ever asks the host to say are text.
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
/// re-checks ownership <em>and</em> that the path lies inside <see cref="SshBackupContext.StoreDirectory"/>
/// — which is not where any adopter looks. A foreign archive fails both checks, so even a caller that
/// fabricated a mislabelled artifact could not route a delete at one.
/// </description>
/// </item>
/// </list>
/// <para>
/// <strong>The write guard is checked before anything runs, not after.</strong>
/// <see cref="WriteGuardedExecutionTarget"/> gates <c>WriteFileAsync</c>, <c>DeleteAsync</c> and every
/// command whose <see cref="CommandSpec.Intent"/> is not <see cref="CommandIntent.ReadOnly"/> — exec is
/// classified by declared intent, not by verb, and this provider is the reason that declaration exists on
/// the spec. Its mutating step <em>is</em> an exec: <c>tar --create</c> declares nothing, so it means
/// <see cref="CommandIntent.Mutating"/>, and a read-only server refuses it at the transport before an
/// archive can appear. Its read-only steps — <c>tar --list</c> when inspecting, the hash — declare
/// <see cref="CommandIntent.ReadOnly"/> and keep working in every mode, which is what makes inspecting a
/// backup on a read-only server possible at all. On top of that structural gate,
/// <see cref="CreateAsync"/> and <see cref="RestoreAsync"/> ask for the posture up front through the shared
/// <see cref="ExecutionTargetWriteMode"/> and throw <see cref="WritesDisabledException"/> — the same
/// exception the guard itself throws, naming the server and the operation — before a single command is
/// issued, so the refusal arrives before the quiesce rather than in the middle of the operation.
/// </para>
/// <para>
/// <strong>A configured quiesce that fails produces no archive.</strong> When the context carries a
/// <see cref="QuiesceStep"/> — which the composition root attaches only when the operator configured a
/// control channel for that server — <see cref="CreateAsync"/> issues it through the context's
/// <see cref="Servyx.Domain.Rcon.IRconSession"/> before the first command reaches the host, and converts
/// every failure route into <see cref="BackupQuiesceFailedException"/>. Because the flush happens ahead of
/// even the <c>mkdir</c>, a failed quiesce leaves no archive, no manifest and no artifact directory. A
/// context with no step is unchanged from before: the host's <c>tar</c> archives whatever the server last
/// wrote to disk, and the manifest's <c>quiescedWith</c> field records that nothing was flushed, so the two
/// kinds of archive stay distinguishable. See <see cref="QuiesceAsync"/>.
/// </para>
/// <para>
/// <strong>Restores are planned, then applied.</strong> <see cref="PlanRestoreAsync"/> reads the manifest and
/// returns a <see cref="RestorePlan"/> naming every path a restore would overwrite; it writes nothing and
/// runs nothing. <see cref="RestoreAsync"/> accepts only a plan id, and the plan is single-use,
/// time-bounded, and re-validated against the archive's current size before a byte is written.
/// </para>
/// <para>
/// <strong>Not registered by <c>AddServyxSsh()</c>.</strong> See
/// <see cref="SshBackupServiceCollectionExtensions.AddServyxSshBackups"/>: creating, restoring, and pruning
/// backups are mutating capabilities, so this type is opt-in and unreachable from the default read-only
/// composition root.
/// </para>
/// </remarks>
public sealed class SshBackupProvider : IBackupProvider
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

    /// <summary>
    /// Rooted at <c>/</c> for the same reason <c>SshProcessProvisioner</c> does it: an absolute POSIX path
    /// survives the round trip through <see cref="SftpFileChannel"/>, which re-prepends the leading slash to
    /// a <see cref="TargetPath.Value"/>. Every path this provider hands to a file operation is absolute.
    /// </summary>
    private static readonly SandboxedPathResolver HostPaths = new("/");

    private readonly ISshBackupContextSource _contexts;
    private readonly IReadOnlyList<IBackupAdopter> _adopters;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _restorePlanTtl;
    private readonly ConcurrentDictionary<string, PendingRestore> _plans = new(StringComparer.Ordinal);

    /// <summary>Creates a provider over the given context source.</summary>
    /// <param name="contexts">Supplies the per-server backup context. Substituted in tests; no live host is required.</param>
    /// <param name="adopters">
    /// Adopters consulted by <see cref="ListAsync"/> for foreign archives. Only those whose
    /// <see cref="IBackupAdopter.Supports"/> accepts the context's deployment kind are called. This project
    /// ships none — see <see cref="ForeignSshBackupDirectory"/>.
    /// </param>
    /// <param name="timeProvider">Clock used for archive naming and restore-plan expiry.</param>
    /// <param name="restorePlanTtl">How long a restore plan stays applicable. Defaults to <see cref="DefaultRestorePlanTtl"/>.</param>
    public SshBackupProvider(
        ISshBackupContextSource contexts,
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
    /// Quiesces first when the context declares a quiesce step, then runs the host's <c>tar</c> to write the
    /// archive straight into the artifact directory, then reads back its entry names and content hash and
    /// writes the sidecar manifest. The artifact directory is always excluded from the archive, so an archive
    /// can never contain a previous archive. A non-zero <c>tar</c> exit removes whatever partial archive it
    /// left and throws <see cref="SshBackupCommandFailedException"/> — a half-written tarball with a manifest
    /// beside it claiming it is complete is worse than no backup. A failed quiesce aborts before <em>any</em>
    /// command is issued at all — see <see cref="BackupQuiesceFailedException"/>.
    /// </remarks>
    /// <exception cref="WritesDisabledException">The target's write mode is not <see cref="WriteMode.Enabled"/>.</exception>
    /// <exception cref="BackupQuiesceFailedException">A declared quiesce step failed, timed out, or had no channel.</exception>
    public async Task<BackupArtifact> CreateAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);

        // Before anything: the guard would refuse the archive command on its declared Mutating intent, but only
        // once the quiesce had already run. Refuse the operation here instead, ahead of every step.
        RequireWritesEnabled(context, "create a backup");

        var members = EffectiveIncludes(context);
        var excludes = EffectiveExcludes(context);
        var root = Absolute(context.Root);
        var storeDirectory = StoreDirectoryOf(context);

        // Flush the server's in-memory state to disk before the host reads a byte of it. This sits after the
        // cheap, purely-local validation above — there is no point asking a live server to stall for a backup
        // whose include set was going to be rejected anyway — and before every command below, so a quiesce
        // that fails leaves not even the artifact directory behind.
        await QuiesceAsync(context, ct).ConfigureAwait(false);

        await RunAsync(context, "mkdir", ["-p", storeDirectory], CommandIntent.Mutating, ct).ConfigureAwait(false);

        var createdAt = _timeProvider.GetUtcNow();
        var fileName = await ReserveArchiveNameAsync(context, createdAt, ct).ConfigureAwait(false);
        var archivePath = Join(storeDirectory, fileName);

        var tarArguments = new List<string> { "--create", "--gzip", "--file", archivePath, "--directory", root };
        tarArguments.AddRange(excludes.Select(pattern => "--exclude=" + pattern));
        tarArguments.AddRange(members);

        var archived = await context.Target
            .ExecuteAsync(new CommandSpec(context.TarExecutable, tarArguments, Timeout: context.CommandTimeout), ct)
            .ConfigureAwait(false);

        if (!archived.Succeeded)
        {
            await TryDeleteAsync(context.Target, archivePath, ct).ConfigureAwait(false);
            throw new SshBackupCommandFailedException(
                $"Archiving server '{context.ServerId}' failed: '{context.TarExecutable}' exited {archived.ExitCode}. {archived.StandardError}".TrimEnd(),
                context.TarExecutable,
                archived.ExitCode,
                archived.StandardError);
        }

        var entries = await ReadArchiveEntriesAsync(context, archivePath, ct).ConfigureAwait(false);
        var sha256 = await HashAsync(context, archivePath, ct).ConfigureAwait(false);

        var stat = await context.Target.StatAsync(HostPaths.Resolve(archivePath), ct).ConfigureAwait(false);
        var sizeBytes = stat.SizeBytes ?? 0;

        var manifest = new BackupManifest(
            BackupManifest.CurrentSchemaVersion,
            context.ServerId,
            createdAt,
            fileName,
            sha256,
            sizeBytes,
            root,
            entries,
            context.Quiesce?.CommandId);

        await context.Target.WriteFileAsync(
            HostPaths.Resolve(archivePath + ManifestSuffix),
            new MemoryStream(manifest.ToUtf8Json(), writable: false),
            new FileWriteOptions(null),
            ct).ConfigureAwait(false);

        return new BackupArtifact(
            BackupArtifactId.Format(context.ServerId, archivePath),
            BackupOwnership.Servyx,
            createdAt,
            sizeBytes,
            archivePath);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The Servyx-owned half comes from <see cref="SshBackupContext.StoreDirectory"/>; the foreign half comes
    /// from whichever registered <see cref="IBackupAdopter"/>s support the context's deployment kind, matched
    /// against the declared <see cref="ForeignSshBackupDirectory"/> list. Each half is tagged with its own
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
    /// For a Servyx-owned artifact this reads the sidecar manifest over SFTP and never opens the archive at
    /// all — no command runs on the host and nothing is decompressed. For a foreign archive, which has no
    /// manifest, it runs <c>tar --list</c>, which reads entry <em>headers</em> and writes nothing to disk.
    /// Either way nothing is extracted.
    /// </remarks>
    public async Task<IReadOnlyList<string>> InspectAsync(string backupId, CancellationToken ct = default)
    {
        var (context, artifact) = await ResolveAsync(backupId, ct).ConfigureAwait(false);
        return await ReadEntryNamesAsync(context, artifact, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Read-only: this reads the archive's index and maps each entry to the absolute host path a restore
    /// would overwrite. It writes nothing, deletes nothing, and leaves the archive untouched.
    /// </remarks>
    public async Task<RestorePlan> PlanRestoreAsync(string backupId, CancellationToken ct = default)
    {
        var (context, artifact) = await ResolveAsync(backupId, ct).ConfigureAwait(false);
        var entries = await ReadEntryNamesAsync(context, artifact, ct).ConfigureAwait(false);

        var root = Absolute(context.Root);
        var affected = entries
            .Select(entry => Join(root, BackupGlob.Normalize(entry)))
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

        var extracted = await context.Target.ExecuteAsync(
            new CommandSpec(
                context.TarExecutable,
                ["--extract", "--gzip", "--file", artifact.Artifact.Location, "--directory", Absolute(context.Root)],
                Timeout: context.CommandTimeout),
            ct).ConfigureAwait(false);

        if (!extracted.Succeeded)
        {
            throw new SshBackupCommandFailedException(
                $"Restoring '{pending.Plan.BackupId}' failed: '{context.TarExecutable}' exited {extracted.ExitCode}. {extracted.StandardError}".TrimEnd(),
                context.TarExecutable,
                extracted.ExitCode,
                extracted.StandardError);
        }
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
    /// Servyx-owned and that it lives inside <see cref="SshBackupContext.StoreDirectory"/>, so a mislabelled
    /// or out-of-tree artifact throws instead of being removed.
    /// </summary>
    private static async Task DeleteServyxOwnedAsync(SshBackupContext context, ResolvedArtifact artifact, CancellationToken ct)
    {
        if (artifact.Artifact.Ownership != BackupOwnership.Servyx)
        {
            throw new ForeignBackupProtectedException(
                $"Refusing to delete '{artifact.Artifact.Location}': it is {artifact.Artifact.Ownership}, not Servyx-owned.",
                artifact.Artifact.Location);
        }

        var storeDirectory = StoreDirectoryOf(context);
        if (!artifact.Artifact.Location.StartsWith(storeDirectory + "/", StringComparison.Ordinal))
        {
            throw new ForeignBackupProtectedException(
                $"Refusing to delete '{artifact.Artifact.Location}': it is outside the Servyx artifact directory '{storeDirectory}'.",
                artifact.Artifact.Location);
        }

        await context.Target.DeleteAsync(HostPaths.Resolve(artifact.Artifact.Location), ct).ConfigureAwait(false);

        if (artifact.ManifestPath is not null)
        {
            try
            {
                await context.Target.DeleteAsync(HostPaths.Resolve(artifact.ManifestPath), ct).ConfigureAwait(false);
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
    /// Delegated to <see cref="ExecutionTargetWriteMode"/> rather than re-derived here, so this provider, the
    /// local backup provider and the local process provisioner cannot drift into three slightly different
    /// answers to the same question. A target with no guard anywhere in it answers <see langword="null"/> and
    /// is allowed through: this method's job is to surface a refusal the guard would make anyway, earlier and
    /// with a better message, not to invent a second policy. Anything that does slip past still meets the
    /// guard at the first real write <em>and</em>, since the archive step declares
    /// <see cref="CommandIntent.Mutating"/>, at the first mutating command.
    /// </remarks>
    private static void RequireWritesEnabled(SshBackupContext context, string operation) =>
        ExecutionTargetWriteMode.RequireWritesEnabled(
            context.Target,
            operation,
            context.ServerId,
            "Listing, inspecting, previewing a restore, and a dry-run prune all remain available.");

    private async Task<SshBackupContext> GetContextAsync(string serverId, CancellationToken ct)
    {
        var context = await _contexts.GetAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BackupNotFoundException($"No backup context is configured for server '{serverId}'.");

        if (BackupGlob.Normalize(context.StoreDirectory).Length == 0)
        {
            throw new InvalidOperationException(
                $"Server '{serverId}' declares no backup artifact directory. Servyx will not write archives into the " +
                "root it is backing up, because the next archive would then contain the previous one.");
        }

        if (context.Quiesce is not null && context.Control is null)
        {
            throw new BackupQuiesceFailedException(
                $"Server '{serverId}' declares a '{context.Quiesce.CommandId}' quiesce step but has no control channel to issue it on.",
                serverId,
                context.Quiesce.CommandId);
        }

        return context;
    }

    /// <summary>
    /// Runs the declared pre-archive flush, converting <em>every</em> failure route into
    /// <see cref="BackupQuiesceFailedException"/> before <see cref="CreateAsync"/> issues its first command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is deliberately no "archive anyway" path. The channel's <em>presence</em> is the operator's
    /// opt-in: a server with no control channel configured carries no step here and archives on-disk state
    /// exactly as it always has, saying so in the manifest's <c>quiescedWith</c> field. A server that does
    /// carry one and cannot complete it produces nothing — a refusal from the write guard, a rejected
    /// credential, an unreachable endpoint, a timeout and a <c>Success: false</c> reply all land here and all
    /// abort. Degrading to an un-flushed archive would produce a file that looks exactly like a good backup
    /// and is not one, and the operator would only find out at restore time.
    /// </para>
    /// <para>
    /// The caller's own cancellation is re-thrown untouched: an operator who cancelled the backup wants a
    /// cancellation, not a report that the game server failed to save.
    /// </para>
    /// </remarks>
    private async Task QuiesceAsync(SshBackupContext context, CancellationToken ct)
    {
        if (context.Quiesce is not { } step)
        {
            return;
        }

        // GetContextAsync already refused a context that declares a quiesce with no channel.
        var control = context.Control!;

        try
        {
            using var timeout = new CancellationTokenSource(step.Timeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var response = await control.InvokeAsync(step.CommandId, step.Arguments, linked.Token).ConfigureAwait(false);
            if (!response.Success)
            {
                throw new BackupQuiesceFailedException(
                    $"Quiesce command '{step.CommandId}' on server '{context.ServerId}' reported failure: {response.Text}",
                    context.ServerId,
                    step.CommandId);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new BackupQuiesceFailedException(
                $"Quiesce command '{step.CommandId}' on server '{context.ServerId}' did not complete within {step.Timeout}.",
                context.ServerId,
                step.CommandId);
        }
        catch (Exception ex) when (ex is not BackupQuiesceFailedException and not OperationCanceledException)
        {
            throw new BackupQuiesceFailedException(
                $"Quiesce command '{step.CommandId}' on server '{context.ServerId}' failed: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// The include set as <c>tar</c> members: normalized, non-empty, wildcard-free, and lexically contained
    /// in the root.
    /// </summary>
    /// <remarks>
    /// A wildcard is rejected rather than silently passed through, because <see cref="CommandSpec"/>
    /// arguments reach the host as an argv array with no shell to expand them — a <c>*</c> would become a
    /// literal filename and the backup would quietly capture nothing. A leading <c>-</c> is rejected because
    /// <c>tar</c> would read it as an option.
    /// </remarks>
    private static IReadOnlyList<string> EffectiveIncludes(SshBackupContext context)
    {
        var members = new List<string>();
        foreach (var include in context.Include)
        {
            var normalized = BackupGlob.Normalize(include);
            if (normalized.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Server '{context.ServerId}' declares an empty backup include. Name a path under '{context.Root}'.");
            }

            if (BackupGlob.HasWildcard(normalized))
            {
                throw new InvalidOperationException(
                    $"Server '{context.ServerId}' declares the wildcard backup include '{include}'. Includes are passed to " +
                    "the host's tar as literal argv members with no shell to expand them; name the directory and narrow it " +
                    "with an exclude pattern instead.");
            }

            if (normalized.StartsWith('-'))
            {
                throw new InvalidOperationException(
                    $"Server '{context.ServerId}' declares the backup include '{include}', which tar would read as an option.");
            }

            // Lexical containment, on the same resolver every file operation goes through.
            HostPaths.Resolve(Join(Absolute(context.Root), normalized));
            members.Add(normalized);
        }

        if (members.Count == 0)
        {
            throw new InvalidOperationException(
                $"Server '{context.ServerId}' declares no backup includes, so there is nothing to archive.");
        }

        return members;
    }

    /// <summary>
    /// The exclude set actually applied: the definition's own excludes, plus the Servyx artifact directory
    /// itself.
    /// </summary>
    /// <remarks>
    /// This is what makes "an archive never contains an archive" a property of the command rather than of a
    /// reviewer's attention. The artifact directory lives under the root being archived — that is the
    /// context's shape — so without this every backup would be strictly larger than the last until the disk
    /// filled. Both the bare name and its subtree are excluded, because <c>tar</c> treats them as different
    /// patterns.
    /// <para>
    /// Declared <see cref="ForeignSshBackupDirectory"/>s under the same root get the same treatment. Servyx
    /// never manages those archives, but it does know they are archives, and sweeping a cron job's tarballs
    /// into its own would double the size of every backup while adding nothing recoverable.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> EffectiveExcludes(SshBackupContext context)
    {
        var excludes = context.Exclude.Select(BackupGlob.Normalize).Where(p => p.Length > 0).ToList();

        var root = Absolute(context.Root);
        var rootPrefix = root.EndsWith('/') ? root : root + "/";

        Exclude(BackupGlob.Normalize(context.StoreDirectory));

        foreach (var foreign in context.Foreign)
        {
            var directory = Absolute(foreign.Directory);
            if (directory.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                Exclude(directory[rootPrefix.Length..]);
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
            excludes.Add(relative + "/*");
        }
    }

    private async Task<string> ReserveArchiveNameAsync(SshBackupContext context, DateTimeOffset createdAt, CancellationToken ct)
    {
        var storeDirectory = StoreDirectoryOf(context);
        var stamp = createdAt.UtcDateTime.ToString(ArchiveTimestampFormat, CultureInfo.InvariantCulture);

        for (var suffix = 1; suffix <= 1000; suffix++)
        {
            var candidate = suffix == 1
                ? $"{ArchivePrefix}{stamp}{ArchiveSuffix}"
                : $"{ArchivePrefix}{stamp}-{suffix.ToString(CultureInfo.InvariantCulture)}{ArchiveSuffix}";

            var exists = await context.Target
                .ExistsAsync(HostPaths.Resolve(Join(storeDirectory, candidate)), ct)
                .ConfigureAwait(false);

            if (!exists)
            {
                return candidate;
            }
        }

        throw new IOException(
            $"Could not find an unused backup archive name for server '{context.ServerId}' at {stamp}.");
    }

    private async Task<IReadOnlyList<ResolvedArtifact>> ListResolvedAsync(SshBackupContext context, CancellationToken ct)
    {
        var results = new List<ResolvedArtifact>();
        var storeDirectory = StoreDirectoryOf(context);

        IReadOnlyList<FileEntry> storeEntries;
        try
        {
            storeEntries = await context.Target
                .ListDirectoryAsync(HostPaths.Resolve(storeDirectory), ct)
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

            var location = Join(storeDirectory, entry.Name);

            results.Add(new ResolvedArtifact(
                new BackupArtifact(
                    BackupArtifactId.Format(context.ServerId, location),
                    BackupOwnership.Servyx,
                    ParseCreatedAt(entry.Name) ?? entry.ModifiedAt ?? DateTimeOffset.UnixEpoch,
                    entry.SizeBytes ?? 0,
                    location),
                location + ManifestSuffix));
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

                results.Add(new ResolvedArtifact(artifact, ManifestPath: null));
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
    /// as a restorable backup on an operator's say-so is exactly how a restore ends up writing a stranger's
    /// tarball over a live world.
    /// </remarks>
    private static bool IsDeclaredForeign(SshBackupContext context, string location)
    {
        foreach (var foreign in context.Foreign)
        {
            var directory = Absolute(foreign.Directory);
            var prefix = directory.EndsWith('/') ? directory : directory + "/";
            if (!location.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = location[prefix.Length..];
            if (!name.Contains('/', StringComparison.Ordinal) && BackupGlob.Matches(foreign.Pattern, name))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<(SshBackupContext Context, ResolvedArtifact Artifact)> ResolveAsync(string backupId, CancellationToken ct)
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
        SshBackupContext context,
        ResolvedArtifact artifact,
        CancellationToken ct)
    {
        if (artifact.ManifestPath is not null)
        {
            var manifest = await TryReadManifestAsync(context, artifact.ManifestPath, ct).ConfigureAwait(false);
            if (manifest is not null)
            {
                return manifest.Entries;
            }
        }

        return await ReadArchiveEntriesAsync(context, artifact.Artifact.Location, ct).ConfigureAwait(false);
    }

    private static async Task<BackupManifest?> TryReadManifestAsync(SshBackupContext context, string manifestPath, CancellationToken ct)
    {
        try
        {
            await using var stream = await context.Target
                .OpenReadAsync(HostPaths.Resolve(manifestPath), ct)
                .ConfigureAwait(false);

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            return BackupManifest.FromUtf8Json(buffer.ToArray());
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads an archive's entry names by asking the host's <c>tar</c> to list them. <c>--list</c> reads entry
    /// headers only: nothing is written to the filesystem and no entry's data is decompressed to disk.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadArchiveEntriesAsync(
        SshBackupContext context,
        string archivePath,
        CancellationToken ct)
    {
        var listed = await RunAsync(
                context,
                context.TarExecutable,
                ["--list", "--gzip", "--file", archivePath],
                CommandIntent.ReadOnly,
                ct)
            .ConfigureAwait(false);

        var names = new List<string>();
        foreach (var line in listed.StandardOutput.Split('\n'))
        {
            var trimmed = line.Trim('\r', ' ');
            if (trimmed.Length == 0 || trimmed.EndsWith('/'))
            {
                continue; // Directory members carry no content a restore would overwrite.
            }

            names.Add(BackupGlob.Normalize(trimmed));
        }

        return names;
    }

    private static async Task<string> HashAsync(SshBackupContext context, string archivePath, CancellationToken ct)
    {
        var hashed = await RunAsync(context, context.HashExecutable, [archivePath], CommandIntent.ReadOnly, ct)
            .ConfigureAwait(false);
        var token = hashed.StandardOutput.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        return token.Length == 0
            ? throw new SshBackupCommandFailedException(
                $"'{context.HashExecutable}' produced no hash for '{archivePath}'.",
                context.HashExecutable,
                hashed.ExitCode,
                hashed.StandardError)
            : token[0].ToLowerInvariant();
    }

    /// <param name="intent">
    /// What the command does to the host. Required rather than defaulted, because this helper is the one
    /// place both kinds go through and a default here would let a future caller pick up whichever answer
    /// happened to be convenient. <see cref="CommandIntent.ReadOnly"/> is what keeps inspecting an archive
    /// working on a read-only server; anything that changes the host passes
    /// <see cref="CommandIntent.Mutating"/> and is refused there by the write guard.
    /// </param>
    private static async Task<CommandResult> RunAsync(
        SshBackupContext context,
        string executable,
        IReadOnlyList<string> arguments,
        CommandIntent intent,
        CancellationToken ct)
    {
        var result = await context.Target
            .ExecuteAsync(new CommandSpec(executable, arguments, Timeout: context.CommandTimeout, Intent: intent), ct)
            .ConfigureAwait(false);

        return result.Succeeded
            ? result
            : throw new SshBackupCommandFailedException(
                $"'{executable}' exited {result.ExitCode} on server '{context.ServerId}'. {result.StandardError}".TrimEnd(),
                executable,
                result.ExitCode,
                result.StandardError);
    }

    private static async Task TryDeleteAsync(IExecutionTarget target, string path, CancellationToken ct)
    {
        try
        {
            await target.DeleteAsync(HostPaths.Resolve(path), ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // tar failed before creating anything. Nothing to clean up.
        }
    }

    private static string StoreDirectoryOf(SshBackupContext context) =>
        Join(Absolute(context.Root), BackupGlob.Normalize(context.StoreDirectory));

    /// <summary>Normalizes a host path to its absolute, forward-slash form. The root itself becomes <c>/</c>.</summary>
    private static string Absolute(string path)
    {
        var normalized = BackupGlob.Normalize(path);
        return normalized.Length == 0 ? "/" : "/" + normalized;
    }

    /// <summary>Appends <paramref name="name"/> to an absolute directory without doubling the separator.</summary>
    private static string Join(string absoluteDirectory, string name) =>
        absoluteDirectory.EndsWith('/') ? absoluteDirectory + name : absoluteDirectory + "/" + name;

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

    private sealed record ResolvedArtifact(BackupArtifact Artifact, string? ManifestPath);

    private sealed record PendingRestore(
        RestorePlan Plan,
        string ServerId,
        DateTimeOffset CreatedAt,
        long SizeBytes);
}
