using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process.Provisioning;

/// <summary>
/// The <see cref="IMaintainer"/> half of the local process adapter: update planning and drift detection.
/// Nothing in this file mutates anything.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately the same shape as <c>SshProcessProvisioner</c>'s maintenance half.</strong> The two
/// adapters share an identity model (a marker file, not a daemon or a provider registry) and an install model
/// (a closed allowlist of verbs, re-runnable against an existing install directory), so they reach the same
/// conclusions for the same reasons. Where the local shape genuinely differs — path syntax, and the fact that
/// <see cref="EnsureDirectoryInstallStep"/> spawns no process at all — the difference is noted in place rather
/// than smoothed over.
/// </para>
/// <para>
/// <strong>An update is re-running the install verbs, never a recreate.</strong> A process install has no
/// provider identity to discard and no daemon-fixed properties the way a container's image and ports are:
/// re-issuing the same <c>steamcmd +app_update ... validate</c> invocation against an existing install
/// directory updates the binaries in place, and rewriting the marker is an ordinary file write. Every change
/// this file plans is therefore <see cref="UpdateStrategy.InPlace"/>; no <see cref="PlannedChange"/> it
/// produces ever sets <see cref="PlannedChange.RequiresRecreate"/>, and this adapter advertises
/// <see cref="ProvisioningCapabilities.UpdateInPlace"/> without
/// <see cref="ProvisioningCapabilities.RecreateToUpdate"/>.
/// </para>
/// <para>
/// <strong>What "changed" means here, and the honest limit of that.</strong> The marker records only identity
/// and location — instance, job, connector, data directory, executable, and any extra tags — never which Steam
/// app id, version, or install verbs produced the install. This file compares the desired request's data
/// directory, executable, and tags against what the marker recorded, and reports
/// <see cref="UpdateStrategy.NoChangeRequired"/> whenever that recorded identity already matches — even though
/// re-running <c>steamcmd</c> might still pull a newer build of the same app. That is a genuine limitation of a
/// marker-backed shape, not an oversight; answering "is a newer build available" would mean connecting and
/// running an update check, which would not be planning any more.
/// </para>
/// </remarks>
public sealed partial class LocalProcessProvisioner : IMaintainer
{
    /// <summary>The <see cref="PlannedChange.Aspect"/> a data-directory difference is reported under.</summary>
    internal const string DataDirectoryAspect = "dataDir";

    /// <summary>The <see cref="PlannedChange.Aspect"/> an executable difference is reported under.</summary>
    internal const string ExecutableAspect = "executable";

    /// <summary>The prefix every marker-tag <see cref="PlannedChange.Aspect"/> carries.</summary>
    internal const string TagAspectPrefix = "tag ";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Reads the marker, then computes. No mutating call is ever issued.</strong> The only session
    /// calls on this path are the marker's existence check and its read — the same pair
    /// <see cref="RefreshAsync"/> makes. Nothing here writes a file, deletes a file, or runs a command, and no
    /// directory is created: unlike the install operation, planning never calls
    /// <see cref="Directory.CreateDirectory(string)"/>.
    /// </para>
    /// <para>
    /// <strong>How the <see cref="DataImpact"/> is decided. Never defaulted.</strong>
    /// <see cref="DataImpact.Preserved"/> is asserted in exactly two situations, and both are claims this
    /// adapter can defend by naming the operations that would run. First, when nothing needs to change at all:
    /// no stage runs, so nothing can happen to the data. Second, when the desired data directory is the exact
    /// directory the existing install already occupies: the only verbs this adapter will carry out are
    /// <c>steamcmd +app_update ... validate</c>, which updates or adds files under the install directory and
    /// removes nothing that sits alongside them, and <c>ensure-dir</c>, which on a local target is a
    /// <see cref="Directory.CreateDirectory(string)"/> call — creation only, with no delete anywhere on the
    /// path. The moment the desired data directory differs from the recorded one, the existing install's saves
    /// stay exactly where they are, untouched on disk, but the updated install reads and writes a different
    /// directory, so nothing running afterwards is attached to them. That is <see cref="DataImpact.AtRisk"/> by
    /// definition, not <see cref="DataImpact.Preserved"/>: "the adapter deletes nothing" is not the same claim
    /// as "the workload stays attached to its data". <see cref="DataImpact.Destroyed"/> is never asserted —
    /// nothing on this path removes a directory or a file other than the marker, and the marker is not data.
    /// </para>
    /// </remarks>
    public async Task<UpdatePlan?> PlanUpdateAsync(ResourceHandle handle, ProvisioningRequest desired, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(desired);

        // Building the spec first means an install verb outside the allowlist, or a data directory that is not
        // fully qualified, is rejected before a session is opened at all — the same guarantee PlanAsync gives,
        // for the same reason.
        var desiredSpec = BuildSpec(desired);

        await using var session = await _transport
            .ConnectAsync(MachineDescriptor(handle.ProviderResourceId), ct)
            .ConfigureAwait(false);

        var liveTags = await ReadMarkerAsync(session, handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (ServyxProcessMarker.FromTags(liveTags) is null)
        {
            // Mirrors RefreshAsync: the provider no longer knows about this resource. That is not the same
            // answer as "nothing needs to change" — there is nothing to update, and inventing a
            // create-from-scratch plan would quietly turn an update preview into a provisioning one.
            return null;
        }

        return BuildUpdatePlan(handle.ProviderResourceId, liveTags!, desiredSpec);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Two different kinds of check, both genuine reads of the live machine.</strong> First, the
    /// marker's own tags are compared against what <paramref name="handle"/> recorded, catching a marker that
    /// was hand-edited, restored from a stale copy, or otherwise no longer says what Servyx wrote. Second, and
    /// separately, this method asks the filesystem whether the recorded data directory and the recorded
    /// executable inside it actually still exist. That second check is what keeps this adapter's
    /// <see cref="ProvisioningCapabilities.DetectDrift"/> claim honest: it is a read of the live resource, not
    /// a second look at the same marker.
    /// </para>
    /// <para>
    /// A marker the machine no longer has, or one that cannot be parsed, is reported as drift under the
    /// <c>"marker"</c> aspect — never as an exception, and never silently treated as a match. The same is true
    /// of a handle belonging to another provisioner: "this is not my resource" is not evidence that the
    /// resource is intact.
    /// </para>
    /// </remarks>
    public async Task<DriftResult> DetectDriftAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new DriftResult(handle, [new DriftDivergence("provisioner", Id, handle.ProvisionerId)]);
        }

        await using var session = await _transport
            .ConnectAsync(MachineDescriptor(handle.ProviderResourceId), ct)
            .ConfigureAwait(false);

        var liveTags = await ReadMarkerAsync(session, handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (liveTags is null)
        {
            return new DriftResult(handle, [new DriftDivergence("marker", "present", null)]);
        }

        var recorded = handle.Tags ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var divergences = new List<DriftDivergence>();

        foreach (var expected in recorded.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var found = liveTags.TryGetValue(expected.Key, out var value) ? value : null;
            if (!string.Equals(expected.Value, found, StringComparison.Ordinal))
            {
                divergences.Add(new DriftDivergence($"{TagAspectPrefix}{expected.Key}", expected.Value, found));
            }
        }

        // Live filesystem reads: does the recorded data directory, and the recorded executable inside it, still
        // exist. A missing marker is handled above; these two are about the payload the marker describes rather
        // than the marker itself.
        var dataDirectory = GetTag(liveTags, ServyxProcessMarker.RootPathTag);
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            if (!await ExistsQuietAsync(session, dataDirectory, ct).ConfigureAwait(false))
            {
                divergences.Add(new DriftDivergence(DataDirectoryAspect, dataDirectory, null));
            }

            // Checked independently of the directory above, exactly as the SSH adapter does: an install whose
            // directory survived but whose binary did not is a different fact from one whose directory is gone,
            // and a caller reading the divergence list should be told both when both are true.
            var executable = GetTag(liveTags, ServyxProcessMarker.ExecutableTag);
            if (!string.IsNullOrWhiteSpace(executable)
                && !await ExistsQuietAsync(session, ResolveExecutablePath(dataDirectory, executable), ct).ConfigureAwait(false))
            {
                divergences.Add(new DriftDivergence(ExecutableAspect, executable, null));
            }
        }

        return new DriftResult(handle, divergences);
    }

    /// <summary>
    /// The whole of update planning: pure comparison between the marker's recorded tags and the desired spec.
    /// Touches only <see cref="_timeProvider"/> (for the plan's expiry) and issues no session call of its own —
    /// every call on the update path is the marker read its caller already made.
    /// </summary>
    private UpdatePlan BuildUpdatePlan(string markerPath, IReadOnlyDictionary<string, string> liveTags, LocalProcessSpec desiredSpec)
    {
        var changes = new List<PlannedChange>();

        var liveDataDirectory = GetTag(liveTags, ServyxProcessMarker.RootPathTag);
        if (!string.Equals(liveDataDirectory, desiredSpec.DataDirectory, StringComparison.Ordinal))
        {
            changes.Add(new PlannedChange(DataDirectoryAspect, liveDataDirectory, desiredSpec.DataDirectory, RequiresRecreate: false));
        }

        var liveExecutable = GetTag(liveTags, ServyxProcessMarker.ExecutableTag);
        if (!string.Equals(liveExecutable, desiredSpec.Executable, StringComparison.Ordinal))
        {
            changes.Add(new PlannedChange(ExecutableAspect, liveExecutable, desiredSpec.Executable, RequiresRecreate: false));
        }

        // Identity and extra tags. dataDir and executable already have their own aspect names above, and are
        // also ordinary marker tags, so re-reporting them here would describe one difference twice.
        foreach (var desiredTag in desiredSpec.Marker.ToTags(desiredSpec.AdditionalTags).OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (string.Equals(desiredTag.Key, ServyxProcessMarker.RootPathTag, StringComparison.Ordinal)
                || string.Equals(desiredTag.Key, ServyxProcessMarker.ExecutableTag, StringComparison.Ordinal))
            {
                continue;
            }

            var found = liveTags.TryGetValue(desiredTag.Key, out var value) ? value : null;
            if (!string.Equals(desiredTag.Value, found, StringComparison.Ordinal))
            {
                changes.Add(new PlannedChange($"{TagAspectPrefix}{desiredTag.Key}", found, desiredTag.Value, RequiresRecreate: false));
            }
        }

        var strategy = changes.Count == 0 ? UpdateStrategy.NoChangeRequired : UpdateStrategy.InPlace;
        var dataImpact = AssertDataImpact(strategy, liveDataDirectory, desiredSpec.DataDirectory);
        var stages = strategy == UpdateStrategy.NoChangeRequired
            ? (IReadOnlyList<ProvisioningStage>)[]
            : BuildUpdateStages(markerPath, desiredSpec, dataImpact, liveDataDirectory);

        var planHash = ComputeUpdatePlanHash(markerPath, liveTags, desiredSpec, strategy, dataImpact);

        var plan = new UpdatePlan(
            planId: $"{Id}:update:{desiredSpec.Marker.InstanceId}:{planHash[..12]}",
            planHash: planHash,
            provisionerId: Id,
            strategy: strategy,
            dataImpact: dataImpact,
            changes: changes,
            stages: stages,
            expiresAt: _timeProvider.GetUtcNow().AddMinutes(15));

        RememberPlannedSpec(planHash, desiredSpec);
        return plan;
    }

    /// <summary>
    /// The deliberate data-impact assertion for an update, made from the recorded and desired data directories
    /// rather than from a default. Every branch is reachable and every branch is a claim this adapter can
    /// defend — see the remarks on <see cref="PlanUpdateAsync"/>.
    /// </summary>
    private static DataImpact AssertDataImpact(UpdateStrategy strategy, string? liveDataDirectory, string desiredDataDirectory)
    {
        if (strategy == UpdateStrategy.NoChangeRequired)
        {
            // Nothing would run, so nothing can happen to the data.
            return DataImpact.Preserved;
        }

        if (!string.Equals(liveDataDirectory, desiredDataDirectory, StringComparison.Ordinal))
        {
            // The update would point the install at a different directory. The old directory's contents are not
            // deleted, but nothing the updated install runs will reference them any more — orphaned, not
            // destroyed, which is exactly what AtRisk means.
            return DataImpact.AtRisk;
        }

        // The data directory is unchanged: steamcmd updates or adds files under it, ensure-dir only creates,
        // and neither removes the save data sitting alongside them.
        return DataImpact.Preserved;
    }

    private static IReadOnlyList<ProvisioningStage> BuildUpdateStages(
        string markerPath,
        LocalProcessSpec desiredSpec,
        DataImpact dataImpact,
        string? liveDataDirectory)
    {
        var dataClause = string.Equals(liveDataDirectory, desiredSpec.DataDirectory, StringComparison.Ordinal)
            ? "the data directory is unchanged, so re-running the install verbs below updates the install in place without touching its saved data"
            : $"the data directory is changing from '{liveDataDirectory ?? "(unknown)"}' to '{desiredSpec.DataDirectory}', so whatever is at the old path is left behind, orphaned rather than deleted";

        var stages = new List<ProvisioningStage>
        {
            new(
                UpdateMarkerStageId,
                Id,
                $"Rewrite the Servyx marker file '{markerPath}' to record instance '{desiredSpec.Marker.InstanceId}', " +
                $"data directory '{desiredSpec.DataDirectory}', and executable '{desiredSpec.Executable}'. " +
                $"Data impact of this plan is {dataImpact}: {dataClause}."),
        };

        for (var i = 0; i < desiredSpec.InstallSteps.Count; i++)
        {
            stages.Add(new ProvisioningStage(desiredSpec.InstallSteps[i].StageId(i), Id, desiredSpec.InstallSteps[i].Describe(desiredSpec)));
        }

        return stages;
    }

    private string ComputeUpdatePlanHash(
        string markerPath,
        IReadOnlyDictionary<string, string> liveTags,
        LocalProcessSpec desiredSpec,
        UpdateStrategy strategy,
        DataImpact dataImpact)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append(":update\n");
        builder.Append(markerPath).Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"{strategy}/{dataImpact}\n");

        foreach (var tag in liveTags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"live-tag {tag.Key}={tag.Value}\n");
        }

        builder.Append(desiredSpec.DataDirectory).Append('\n');
        builder.Append(desiredSpec.Executable).Append('\n');
        builder.Append(desiredSpec.SteamCmdPath).Append('\n');

        for (var i = 0; i < desiredSpec.InstallSteps.Count; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"step {i} {desiredSpec.InstallSteps[i].HashInput(desiredSpec)}\n");
        }

        foreach (var entry in desiredSpec.Environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"env {entry.Key}={entry.Value}\n");
        }

        foreach (var tag in desiredSpec.Marker.ToTags(desiredSpec.AdditionalTags).OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"desired-tag {tag.Key}={tag.Value}\n");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// Resolves <paramref name="executable"/> against <paramref name="dataDirectory"/>: a fully-qualified path
    /// stays as it is, and everything else is taken relative to the data directory.
    /// </summary>
    /// <remarks>
    /// A definition's <c>executable</c> is conventionally written <c>./PalServer.sh</c>. The leading
    /// <c>./</c> is stripped in both spellings because the value comes from a definition authored for either
    /// platform, and <see cref="Path.Combine(string, string)"/> would otherwise keep it as a literal path
    /// segment.
    /// </remarks>
    private static string ResolveExecutablePath(string dataDirectory, string executable)
    {
        if (Path.IsPathFullyQualified(executable))
        {
            return executable;
        }

        var relative = executable.StartsWith("./", StringComparison.Ordinal) || executable.StartsWith(".\\", StringComparison.Ordinal)
            ? executable[2..]
            : executable;

        return Path.Combine(Path.TrimEndingDirectorySeparator(dataDirectory), relative);
    }

    /// <summary>
    /// Whether <paramref name="absolutePath"/> exists on this machine, treating a path that cannot be resolved
    /// into the session's sandbox as absent rather than throwing. A drift check must answer, not fail.
    /// </summary>
    private static async Task<bool> ExistsQuietAsync(IExecutionTarget session, string absolutePath, CancellationToken ct)
    {
        TargetPath path;
        try
        {
            path = ToMachinePath(absolutePath);
        }
        catch (Exception ex) when (ex is PathEscapesSandboxException or ArgumentException)
        {
            return false;
        }

        return await session.ExistsAsync(path, ct).ConfigureAwait(false);
    }

    private static string? GetTag(IReadOnlyDictionary<string, string> tags, string key) =>
        tags.TryGetValue(key, out var value) ? value : null;
}
