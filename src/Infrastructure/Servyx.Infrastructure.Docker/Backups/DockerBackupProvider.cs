using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker.Backups;

/// <summary>
/// An <see cref="IBackupProvider"/> that creates, lists, inspects, restores, and prunes backups for a
/// server reached through an <see cref="IExecutionTarget"/> — in practice a Docker container plus the
/// host directory holding its compose files.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Foreign artifacts are never pruned, and that is structural.</strong> Three independent barriers
/// stand between <see cref="PruneAsync"/> and a foreign archive, each sufficient on its own:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <em>Partition.</em> <see cref="PruneAsync"/> splits the listing by <see cref="BackupArtifact.Ownership"/>
/// in one place and passes only the <see cref="BackupOwnership.Servyx"/> half onward. The foreign half is
/// counted into <see cref="PruneResult.SkippedForeign"/> and then goes out of scope — it is never bound to
/// a variable any deletion code can see, under either value of <c>dryRun</c>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>Evaluation.</em> <see cref="BackupRetentionEvaluator.SelectForRemoval"/> throws
/// <see cref="ForeignBackupProtectedException"/> if a foreign artifact reaches it, so retention cannot
/// even be <em>computed</em> over one. This is what makes the guarantee hold for <c>dryRun: true</c> as
/// strongly as for <c>dryRun: false</c>: a dry run's report is produced by the same call, so there is no
/// path that "hypothetically" schedules a foreign archive for removal.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>Deletion.</em> The single method that issues a delete re-checks ownership <em>and</em> that the
/// path lies inside <see cref="BackupStore.Directory"/> — the Servyx-owned artifact directory, which is
/// not where any adopter looks. A foreign archive fails both checks, so even a caller that fabricated a
/// mislabelled artifact could not route a delete at one.
/// </description>
/// </item>
/// </list>
/// <para>
/// <strong>Restores are planned, then applied.</strong> <see cref="PlanRestoreAsync"/> reads and returns a
/// <see cref="RestorePlan"/> naming every path a restore would overwrite; it writes nothing.
/// <see cref="RestoreAsync"/> accepts only a plan id, and the plan is single-use, time-bounded, and
/// re-validated against the archive's current size and timestamp before a byte is written.
/// </para>
/// <para>
/// <strong>Not registered by <c>AddServyxDocker()</c>.</strong> See
/// <see cref="DockerBackupServiceCollectionExtensions.AddServyxDockerBackups"/>: creating, restoring, and
/// pruning backups are mutating capabilities, so this type is opt-in and unreachable from the default
/// read-only composition root.
/// </para>
/// </remarks>
public sealed class DockerBackupProvider : IBackupProvider
{
    /// <summary>Filename prefix identifying an archive as Servyx-owned.</summary>
    public const string ArchivePrefix = "servyx-";

    /// <summary>Filename suffix of every archive this provider writes.</summary>
    public const string ArchiveSuffix = ".tar.gz";

    /// <summary>Filename suffix of the sidecar manifest written next to each archive.</summary>
    public const string ManifestSuffix = ".manifest.json";

    /// <summary>How long a <see cref="RestorePlan"/> stays applicable after it is produced.</summary>
    public static readonly TimeSpan DefaultRestorePlanTtl = TimeSpan.FromMinutes(15);

    private const int MaxTraversalDepth = 64;
    private const string ArchiveTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    private readonly IDockerBackupContextSource _contexts;
    private readonly IReadOnlyList<IBackupAdopter> _adopters;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly TimeSpan _restorePlanTtl;
    private readonly ConcurrentDictionary<string, PendingRestore> _plans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SandboxedPathResolver> _resolvers = new(StringComparer.Ordinal);

    /// <summary>Creates a provider over the given context source.</summary>
    /// <param name="contexts">Supplies the per-server backup context. Substituted in tests; no live daemon is required.</param>
    /// <param name="adopters">
    /// Adopters consulted by <see cref="ListAsync"/> for foreign archives. Only those whose
    /// <see cref="IBackupAdopter.Supports"/> accepts the context's deployment kind are called.
    /// </param>
    /// <param name="timeProvider">Clock used for archive naming and restore-plan expiry.</param>
    /// <param name="restorePlanTtl">How long a restore plan stays applicable. Defaults to <see cref="DefaultRestorePlanTtl"/>.</param>
    /// <param name="logger">
    /// Where a failed <see cref="DockerBackupContext.Resume"/> step is reported. Optional only so existing
    /// call sites keep compiling; a resume failure is never silent regardless, because it is also thrown
    /// (see <see cref="BackupResumeFailedException"/>) whenever there is no more important exception already
    /// in flight.
    /// </param>
    public DockerBackupProvider(
        IDockerBackupContextSource contexts,
        IEnumerable<IBackupAdopter>? adopters = null,
        TimeProvider? timeProvider = null,
        TimeSpan? restorePlanTtl = null,
        ILogger<DockerBackupProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);

        _contexts = contexts;
        _adopters = adopters?.ToList() ?? [];
        _timeProvider = timeProvider ?? TimeProvider.System;
        _restorePlanTtl = restorePlanTtl ?? DefaultRestorePlanTtl;
        _logger = logger ?? NullLogger<DockerBackupProvider>.Instance;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Quiesces first when the definition declares a quiesce step, then archives the include set minus the
    /// exclude set, then writes the sidecar manifest. A failed quiesce aborts before anything is written —
    /// see <see cref="BackupQuiesceFailedException"/>.
    /// </para>
    /// <para>
    /// <strong>The declared <see cref="DockerBackupContext.Resume"/> steps always run.</strong> The quiesce
    /// and the capture sit inside a single <c>try</c> whose <c>finally</c> issues them, so they run after a
    /// successful archive, after a capture that threw, after a quiesce that failed partway through its own
    /// list, and after cancellation. This is the whole point of the resume phase: a quiesce that turns
    /// saving off and a backup that then fails must not leave the server unable to save. See
    /// <see cref="ResumeAsync"/> for why they are not bound to <paramref name="ct"/>, and
    /// <see cref="BackupResumeFailedException"/> for how a resume failure is surfaced.
    /// </para>
    /// </remarks>
    public async Task<BackupArtifact> CreateAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);

        var captureSucceeded = false;
        try
        {
            await QuiesceAsync(context, ct).ConfigureAwait(false);
            var artifact = await CaptureAsync(context, ct).ConfigureAwait(false);
            captureSucceeded = true;
            return artifact;
        }
        finally
        {
            // Guaranteed-execution point. 'captureSucceeded' does not decide *whether* the resume runs —
            // it always runs — only whether a resume failure is allowed to throw from here. Throwing while
            // an earlier exception is unwinding would replace the reason the backup failed with a
            // downstream symptom; in that case the failure is logged and attached instead.
            await ResumeAsync(context, throwOnFailure: captureSucceeded).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The capture itself: walk the include set, build the archive, write it and its sidecar manifest.
    /// Split out of <see cref="CreateAsync"/> so the quiesce and everything it protects sit inside one
    /// <c>try</c> whose <c>finally</c> owns the resume.
    /// </summary>
    private async Task<BackupArtifact> CaptureAsync(DockerBackupContext context, CancellationToken ct)
    {
        var captured = await CollectAsync(context, ct).ConfigureAwait(false);
        var archiveBytes = await BuildArchiveAsync(captured, ct).ConfigureAwait(false);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(archiveBytes));

        var createdAt = _timeProvider.GetUtcNow();
        var fileName = await ReserveArchiveNameAsync(context, createdAt, ct).ConfigureAwait(false);
        var storeResolver = ResolverFor(context.Store.Root);
        var archiveRelative = Join(context.Store.Directory, fileName);

        await context.Store.Target.WriteFileAsync(
            storeResolver.Resolve(archiveRelative),
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
            context.Quiesce?.CommandId,
            captured.Select(c => c.EntryName).ToList());

        await context.Store.Target.WriteFileAsync(
            storeResolver.Resolve(archiveRelative + ManifestSuffix),
            new MemoryStream(manifest.ToUtf8Json(), writable: false),
            new FileWriteOptions(null),
            ct).ConfigureAwait(false);

        return new BackupArtifact(
            BackupArtifactId.Format(context.ServerId, Combine(context.Store.Root, archiveRelative)),
            BackupOwnership.Servyx,
            createdAt,
            archiveBytes.LongLength,
            Combine(context.Store.Root, archiveRelative));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The Servyx-owned half comes from <see cref="BackupStore.Directory"/>; the foreign half comes from
    /// whichever registered <see cref="IBackupAdopter"/>s support the context's deployment kind. Each half
    /// is tagged with its own <see cref="BackupOwnership"/> at the point it is discovered, never inferred
    /// later.
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
    /// For a Servyx-owned artifact this reads the sidecar manifest and never opens the archive at all. For
    /// a foreign archive, which has no manifest, it reads the tar entry <em>headers</em> with
    /// <c>copyData: false</c> — no entry's data stream is ever touched. Either way nothing is extracted and
    /// nothing is written.
    /// </remarks>
    public async Task<IReadOnlyList<string>> InspectAsync(string backupId, CancellationToken ct = default)
    {
        var (context, artifact) = await ResolveAsync(backupId, ct).ConfigureAwait(false);
        return await ReadEntryNamesAsync(context, artifact, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Read-only: this reads the archive's index and maps each entry to the path a restore would
    /// overwrite. It writes nothing, deletes nothing, and leaves the archive untouched.
    /// </remarks>
    public async Task<RestorePlan> PlanRestoreAsync(string backupId, CancellationToken ct = default)
    {
        var (context, artifact) = await ResolveAsync(backupId, ct).ConfigureAwait(false);
        var entries = await ReadEntryNamesAsync(context, artifact, ct).ConfigureAwait(false);

        var affected = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            var (source, relative) = MapEntryToSource(context, artifact, entry);
            affected.Add(Combine(source.Root, relative));
        }

        var planId = $"restore-{Guid.NewGuid():n}";
        var plan = new RestorePlan(planId, artifact.Artifact.Id, affected);

        _plans[planId] = new PendingRestore(
            plan,
            context.ServerId,
            artifact.Artifact.Location,
            _timeProvider.GetUtcNow(),
            artifact.Artifact.SizeBytes);

        return plan;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Accepts only a plan id produced by <see cref="PlanRestoreAsync"/>. The plan is consumed on the first
    /// attempt, expires after the configured TTL, and is re-checked against the archive's current size
    /// before any write — an unknown, spent, expired, or superseded plan throws
    /// <see cref="RestorePlanStaleException"/> rather than restoring something the operator never previewed.
    /// </remarks>
    public async Task RestoreAsync(string restorePlanId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restorePlanId);

        if (!_plans.TryRemove(restorePlanId, out var pending))
        {
            throw new RestorePlanStaleException(
                $"Restore plan '{restorePlanId}' is unknown or has already been applied. Preview the restore again.",
                restorePlanId);
        }

        var age = _timeProvider.GetUtcNow() - pending.CreatedAt;
        if (age > _restorePlanTtl)
        {
            throw new RestorePlanStaleException(
                $"Restore plan '{restorePlanId}' expired after {_restorePlanTtl}. Preview the restore again.",
                restorePlanId);
        }

        var context = await GetContextAsync(pending.ServerId, ct).ConfigureAwait(false);
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
    /// <c>dryRun: true</c> this issues no delete of any kind; under either flag,
    /// <see cref="PruneResult.SkippedForeign"/> reports how many foreign artifacts were seen and left alone.
    /// </remarks>
    public async Task<PruneResult> PruneAsync(string serverId, RetentionPolicy policy, bool dryRun, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await GetContextAsync(serverId, ct).ConfigureAwait(false);
        var effectivePolicy = policy is null ? context.DefaultRetention : policy;
        var all = await ListResolvedAsync(context, ct).ConfigureAwait(false);

        // Barrier 1: the partition. Only the Servyx-owned half is bound to a name anything below can see;
        // the foreign half is reduced to a count here and never reaches retention or deletion.
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
    /// Barrier 3: the only method in this type that deletes anything. It re-asserts both that the artifact
    /// is Servyx-owned and that it lives inside <see cref="BackupStore.Directory"/>, so a mislabelled or
    /// out-of-tree artifact throws instead of being removed.
    /// </summary>
    private async Task DeleteServyxOwnedAsync(DockerBackupContext context, ResolvedArtifact artifact, CancellationToken ct)
    {
        if (artifact.Artifact.Ownership != BackupOwnership.Servyx)
        {
            throw new ForeignBackupProtectedException(
                $"Refusing to delete '{artifact.Artifact.Location}': it is {artifact.Artifact.Ownership}, not Servyx-owned.",
                artifact.Artifact.Location);
        }

        var storeDirectory = Combine(context.Store.Root, context.Store.Directory);
        if (!artifact.Artifact.Location.StartsWith(storeDirectory + "/", StringComparison.Ordinal))
        {
            throw new ForeignBackupProtectedException(
                $"Refusing to delete '{artifact.Artifact.Location}': it is outside the Servyx artifact directory '{storeDirectory}'.",
                artifact.Artifact.Location);
        }

        var resolver = ResolverFor(artifact.Root);
        await artifact.Target.DeleteAsync(resolver.Resolve(artifact.RelativePath), ct).ConfigureAwait(false);

        if (artifact.ManifestRelativePath is not null)
        {
            try
            {
                await artifact.Target.DeleteAsync(resolver.Resolve(artifact.ManifestRelativePath), ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // An archive whose sidecar was already gone is still pruned; the manifest is an index, not
                // the artifact, and its absence must not leave the archive behind forever.
            }
        }
    }

    private async Task<DockerBackupContext> GetContextAsync(string serverId, CancellationToken ct)
    {
        var context = await _contexts.GetAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BackupNotFoundException($"No backup context is configured for server '{serverId}'.");

        if (context.Quiesce is not null && context.Control is null)
        {
            throw new BackupQuiesceFailedException(
                $"Server '{serverId}' declares a '{context.Quiesce.CommandId}' quiesce step but has no control channel to issue it on.",
                serverId,
                context.Quiesce.CommandId);
        }

        // Checked here, before a single command is issued, for the same reason the quiesce check is: a
        // resume that could never be delivered must be refused while the server is still writing normally,
        // not discovered in the finally block after the quiesce has already stopped it.
        if (context.Resume.Count > 0 && context.Control is null)
        {
            throw new BackupResumeFailedException(
                $"Server '{serverId}' declares a '{context.Resume[0].CommandId}' resume step but has no control channel to issue it on.",
                serverId,
                context.Resume[0].CommandId);
        }

        return context;
    }

    private async Task QuiesceAsync(DockerBackupContext context, CancellationToken ct)
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
    /// Issues every declared <see cref="DockerBackupContext.Resume"/> step, in order, after capture has
    /// finished — however it finished.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>No cancellation token is threaded in, and that is the point.</strong> Each step is bounded
    /// only by its own declared timeout. An operator who cancels a backup is asking Servyx to stop copying
    /// files; they are never asking it to leave the game server unable to write to disk. Passing the
    /// caller's token here would mean a cancelled backup skipped exactly the commands that undo the
    /// quiesce — the precise failure this phase exists to make impossible.
    /// </para>
    /// <para>
    /// <strong>One failing step does not skip the rest.</strong> A resume list is a sequence of undos, and
    /// a later one may be the one that re-enables saving. Every step is attempted, every failure is logged
    /// as an error, and the failures are then reported together.
    /// </para>
    /// </remarks>
    /// <param name="context">The context whose resume steps to issue.</param>
    /// <param name="throwOnFailure">
    /// Whether a failure may throw. False while an earlier exception is unwinding out of
    /// <see cref="CreateAsync"/>, where throwing would hide the original failure — the error log is the
    /// report in that case.
    /// </param>
    private async Task ResumeAsync(DockerBackupContext context, bool throwOnFailure)
    {
        if (context.Resume.Count == 0)
        {
            return;
        }

        // GetContextAsync already refused a context that declares a resume with no channel.
        var control = context.Control!;
        var failures = new List<Exception>();

        foreach (var step in context.Resume)
        {
            try
            {
                using var timeout = new CancellationTokenSource(step.Timeout, _timeProvider);

                var response = await control.InvokeAsync(step.CommandId, step.Arguments, timeout.Token).ConfigureAwait(false);
                if (!response.Success)
                {
                    throw new BackupResumeFailedException(
                        $"Resume command '{step.CommandId}' on server '{context.ServerId}' reported failure: {response.Text}",
                        context.ServerId,
                        step.CommandId);
                }
            }
            catch (OperationCanceledException ex)
            {
                failures.Add(new BackupResumeFailedException(
                    $"Resume command '{step.CommandId}' on server '{context.ServerId}' did not complete within {step.Timeout}.",
                    context.ServerId,
                    step.CommandId,
                    ex));
            }
            catch (BackupResumeFailedException ex)
            {
                failures.Add(ex);
            }
            catch (Exception ex)
            {
                failures.Add(new BackupResumeFailedException(
                    $"Resume command '{step.CommandId}' on server '{context.ServerId}' failed: {ex.Message}",
                    context.ServerId,
                    step.CommandId,
                    ex));
            }
        }

        if (failures.Count == 0)
        {
            return;
        }

        foreach (var failure in failures)
        {
            // Logged unconditionally, on both the throwing and the non-throwing path. When an earlier
            // failure is already unwinding, this log line is the only report the operator gets that the
            // server may still be quiesced — so it is never conditional on whether we are about to throw.
            _logger.LogError(
                failure,
                "Backup resume step failed for server {ServerId}; the server may still be quiesced and unable to write to disk.",
                context.ServerId);
        }

        if (!throwOnFailure)
        {
            return;
        }

        throw failures.Count == 1
            ? failures[0]
            : new BackupResumeFailedException(
                $"{failures.Count} resume steps failed for server '{context.ServerId}'; it may still be quiesced.",
                new AggregateException(failures));
    }

    private async Task<List<CapturedEntry>> CollectAsync(DockerBackupContext context, CancellationToken ct)
    {
        var captured = new Dictionary<string, CapturedEntry>(StringComparer.Ordinal);

        foreach (var source in context.Sources)
        {
            var excludes = EffectiveExcludes(context, source);
            var resolver = ResolverFor(source.Root);

            foreach (var pattern in source.Include.Where(p => !BackupGlob.HasWildcard(p)))
            {
                var literal = BackupGlob.Normalize(pattern);
                if (literal.Length == 0 || BackupGlob.MatchesAny(excludes, literal))
                {
                    continue;
                }

                var stat = await source.Target.StatAsync(resolver.Resolve(literal), ct).ConfigureAwait(false);
                if (!stat.Exists)
                {
                    continue;
                }

                if (stat.IsDirectory)
                {
                    if (!BackupGlob.ExcludesDirectory(excludes, literal))
                    {
                        await WalkAsync(source, resolver, [literal + "/**"], excludes, literal, 0, captured, ct).ConfigureAwait(false);
                    }

                    continue;
                }

                Add(captured, source, literal, stat.SizeBytes ?? 0, stat.ModifiedAt);
            }

            var wildcardPatterns = source.Include.Where(BackupGlob.HasWildcard).ToList();
            foreach (var root in DistinctWalkRoots(wildcardPatterns))
            {
                if (root.Length > 0 && BackupGlob.ExcludesDirectory(excludes, root))
                {
                    continue;
                }

                await WalkAsync(source, resolver, wildcardPatterns, excludes, root, 0, captured, ct).ConfigureAwait(false);
            }
        }

        return captured.Values.OrderBy(c => c.EntryName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The exclude set actually applied to a source: the definition's own excludes, plus — when the source
    /// shares a filesystem with the Servyx artifact directory — that directory itself. The definition's
    /// <c>exclude</c> already keeps the image's own backup directory out of the archive; this adds the same
    /// protection for Servyx's directory, so neither kind of archive is ever re-archived into a new one.
    /// </summary>
    private static IReadOnlyList<string> EffectiveExcludes(DockerBackupContext context, BackupSource source)
    {
        var excludes = source.Exclude.ToList();
        if (!ReferenceEquals(source.Target, context.Store.Target) ||
            !string.Equals(Normalize(source.Root), Normalize(context.Store.Root), StringComparison.Ordinal))
        {
            return excludes;
        }

        var storeDirectory = BackupGlob.Normalize(context.Store.Directory);
        if (storeDirectory.Length == 0)
        {
            return excludes;
        }

        excludes.Add(storeDirectory);
        excludes.Add(storeDirectory + "/**");
        return excludes;
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
            if (kept.Any(k => k.Length == 0 || root.StartsWith(k + "/", StringComparison.Ordinal) || string.Equals(k, root, StringComparison.Ordinal)))
            {
                continue;
            }

            kept.Add(root);
        }

        return kept;
    }

    private static async Task WalkAsync(
        BackupSource source,
        SandboxedPathResolver resolver,
        IReadOnlyList<string> includes,
        IReadOnlyList<string> excludes,
        string directory,
        int depth,
        Dictionary<string, CapturedEntry> captured,
        CancellationToken ct)
    {
        if (depth > MaxTraversalDepth)
        {
            return;
        }

        IReadOnlyList<FileEntry> entries;
        try
        {
            entries = await source.Target
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

                await WalkAsync(source, resolver, includes, excludes, child, depth + 1, captured, ct).ConfigureAwait(false);
                continue;
            }

            if (!BackupGlob.MatchesAny(includes, child) || BackupGlob.MatchesAny(excludes, child))
            {
                continue;
            }

            Add(captured, source, child, entry.SizeBytes ?? 0, entry.ModifiedAt);
        }
    }

    private static void Add(
        Dictionary<string, CapturedEntry> captured,
        BackupSource source,
        string relativePath,
        long sizeBytes,
        DateTimeOffset? modifiedAt)
    {
        var entryName = source.Id + "/" + relativePath;
        captured.TryAdd(entryName, new CapturedEntry(entryName, source, relativePath, sizeBytes, modifiedAt));
    }

    private async Task<byte[]> BuildArchiveAsync(IReadOnlyList<CapturedEntry> captured, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var fallbackTimestamp = _timeProvider.GetUtcNow();

        await using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        await using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var item in captured)
            {
                var resolver = ResolverFor(item.Source.Root);
                await using var content = await item.Source.Target
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

    private async Task<string> ReserveArchiveNameAsync(DockerBackupContext context, DateTimeOffset createdAt, CancellationToken ct)
    {
        var resolver = ResolverFor(context.Store.Root);
        var stamp = createdAt.UtcDateTime.ToString(ArchiveTimestampFormat, CultureInfo.InvariantCulture);

        for (var suffix = 1; suffix <= 1000; suffix++)
        {
            var candidate = suffix == 1
                ? $"{ArchivePrefix}{stamp}{ArchiveSuffix}"
                : $"{ArchivePrefix}{stamp}-{suffix.ToString(CultureInfo.InvariantCulture)}{ArchiveSuffix}";

            var exists = await context.Store.Target
                .ExistsAsync(resolver.Resolve(Join(context.Store.Directory, candidate)), ct)
                .ConfigureAwait(false);

            if (!exists)
            {
                return candidate;
            }
        }

        throw new IOException(
            $"Could not find an unused backup archive name for server '{context.ServerId}' at {stamp}.");
    }

    private async Task<IReadOnlyList<ResolvedArtifact>> ListResolvedAsync(DockerBackupContext context, CancellationToken ct)
    {
        var results = new List<ResolvedArtifact>();
        var storeResolver = ResolverFor(context.Store.Root);
        var storeDirectory = BackupGlob.Normalize(context.Store.Directory);

        IReadOnlyList<FileEntry> storeEntries;
        try
        {
            storeEntries = await context.Store.Target
                .ListDirectoryAsync(storeResolver.Resolve(storeDirectory.Length == 0 ? "." : storeDirectory), ct)
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

            var relative = Join(storeDirectory, entry.Name);
            var location = Combine(context.Store.Root, relative);

            results.Add(new ResolvedArtifact(
                new BackupArtifact(
                    BackupArtifactId.Format(context.ServerId, location),
                    BackupOwnership.Servyx,
                    ParseCreatedAt(entry.Name) ?? entry.ModifiedAt ?? DateTimeOffset.UnixEpoch,
                    entry.SizeBytes ?? 0,
                    location),
                context.Store.Target,
                context.Store.Root,
                relative,
                relative + ManifestSuffix,
                RestoreSourceId: null));
        }

        foreach (var adopter in _adopters.Where(a => a.Supports(context.DeploymentKind)))
        {
            var discovered = await adopter.DiscoverAsync(context.ServerId, ct).ConfigureAwait(false);
            foreach (var artifact in discovered)
            {
                if (artifact.Ownership != BackupOwnership.Foreign)
                {
                    throw new InvalidOperationException(
                        $"Adopter '{adopter.AdapterId}' returned artifact '{artifact.Id}' as {artifact.Ownership}; adopters may only report Foreign artifacts.");
                }

                var foreignSource = FindForeignSource(context, artifact.Location);
                if (foreignSource is null)
                {
                    continue;
                }

                var directory = Combine(foreignSource.Root, foreignSource.Directory);
                var relative = Join(
                    BackupGlob.Normalize(foreignSource.Directory),
                    artifact.Location[(directory.Length + 1)..]);

                results.Add(new ResolvedArtifact(
                    artifact,
                    foreignSource.Target,
                    foreignSource.Root,
                    relative,
                    ManifestRelativePath: null,
                    foreignSource.RestoreSourceId));
            }
        }

        return results;
    }

    private static ForeignBackupSource? FindForeignSource(DockerBackupContext context, string location) =>
        context.Foreign.FirstOrDefault(f =>
            location.StartsWith(Combine(f.Root, f.Directory) + "/", StringComparison.Ordinal));

    private async Task<(DockerBackupContext Context, ResolvedArtifact Artifact)> ResolveAsync(string backupId, CancellationToken ct)
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
        DockerBackupContext context,
        ResolvedArtifact artifact,
        CancellationToken ct)
    {
        if (artifact.ManifestRelativePath is not null)
        {
            var manifest = await TryReadManifestAsync(artifact, ct).ConfigureAwait(false);
            if (manifest is not null)
            {
                return manifest.Entries;
            }
        }

        return await ReadTarEntryNamesAsync(artifact, ct).ConfigureAwait(false);
    }

    private async Task<BackupManifest?> TryReadManifestAsync(ResolvedArtifact artifact, CancellationToken ct)
    {
        var resolver = ResolverFor(artifact.Root);
        try
        {
            await using var stream = await artifact.Target
                .OpenReadAsync(resolver.Resolve(artifact.ManifestRelativePath!), ct)
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
    /// Reads an archive's entry names from its tar headers with <c>copyData: false</c>, so no entry's data
    /// stream is ever read and nothing is extracted. Used for foreign archives, which have no manifest.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadTarEntryNamesAsync(ResolvedArtifact artifact, CancellationToken ct)
    {
        var resolver = ResolverFor(artifact.Root);
        await using var raw = await artifact.Target
            .OpenReadAsync(resolver.Resolve(artifact.RelativePath), ct)
            .ConfigureAwait(false);

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

    private static (BackupSource Source, string Relative) MapEntryToSource(
        DockerBackupContext context,
        ResolvedArtifact artifact,
        string entryName)
    {
        var normalized = BackupGlob.Normalize(entryName);

        if (artifact.Artifact.Ownership == BackupOwnership.Foreign)
        {
            var sourceId = artifact.RestoreSourceId
                ?? throw new NotSupportedException(
                    $"Foreign backup '{artifact.Artifact.Id}' declares no restore mapping, so Servyx cannot say where its entries belong. " +
                    "Set ForeignBackupSource.RestoreSourceId to restore from it.");

            var target = context.Sources.FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Foreign backup source names restore source '{sourceId}', which server '{context.ServerId}' does not declare.");

            return (target, normalized);
        }

        var slash = normalized.IndexOf('/');
        if (slash <= 0)
        {
            throw new InvalidOperationException(
                $"Archive entry '{entryName}' in backup '{artifact.Artifact.Id}' carries no source prefix.");
        }

        var prefix = normalized[..slash];
        var source = context.Sources.FirstOrDefault(s => string.Equals(s.Id, prefix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Archive entry '{entryName}' names source '{prefix}', which server '{context.ServerId}' does not declare.");

        return (source, normalized[(slash + 1)..]);
    }

    private async Task ApplyAsync(DockerBackupContext context, ResolvedArtifact artifact, CancellationToken ct)
    {
        var resolver = ResolverFor(artifact.Root);
        await using var raw = await artifact.Target
            .OpenReadAsync(resolver.Resolve(artifact.RelativePath), ct)
            .ConfigureAwait(false);

        await using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip, leaveOpen: true);

        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: true, ct).ConfigureAwait(false)) is not null)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.ContiguousFile))
            {
                continue;
            }

            var (source, relative) = MapEntryToSource(context, artifact, NormalizeEntryName(entry.Name));

            using var content = new MemoryStream();
            if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(content, ct).ConfigureAwait(false);
            }

            content.Position = 0;
            await source.Target.WriteFileAsync(
                ResolverFor(source.Root).Resolve(relative),
                content,
                new FileWriteOptions(null),
                ct).ConfigureAwait(false);
        }
    }

    private SandboxedPathResolver ResolverFor(string root) =>
        _resolvers.GetOrAdd(root, static r => new SandboxedPathResolver(r));

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

    private static string Normalize(string root) => root.Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Strips the leading <c>./</c> some tar writers prefix entry names with, without touching a leading
    /// dot that is part of the name itself (<c>.env</c>).
    /// </summary>
    private static string NormalizeEntryName(string name) =>
        (name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name).TrimStart('/');

    private static string Join(string directory, string name)
    {
        var normalized = BackupGlob.Normalize(directory);
        return normalized.Length == 0 ? name : normalized + "/" + name;
    }

    private static string Combine(string root, string relative)
    {
        var normalizedRoot = Normalize(root);
        var normalizedRelative = BackupGlob.Normalize(relative);
        if (normalizedRelative.Length == 0)
        {
            return normalizedRoot.Length == 0 ? "/" : normalizedRoot;
        }

        return normalizedRoot.Length == 0 ? "/" + normalizedRelative : normalizedRoot + "/" + normalizedRelative;
    }

    private sealed record CapturedEntry(
        string EntryName,
        BackupSource Source,
        string RelativePath,
        long SizeBytes,
        DateTimeOffset? ModifiedAt);

    private sealed record ResolvedArtifact(
        BackupArtifact Artifact,
        IExecutionTarget Target,
        string Root,
        string RelativePath,
        string? ManifestRelativePath,
        string? RestoreSourceId);

    private sealed record PendingRestore(
        RestorePlan Plan,
        string ServerId,
        string Location,
        DateTimeOffset CreatedAt,
        long SizeBytes);
}
