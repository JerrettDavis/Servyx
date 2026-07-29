using System.Globalization;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process.Provisioning;

/// <summary>
/// The <see cref="IUpdateApplier"/> half of the local process adapter: the only code in this assembly that
/// changes an install which already exists.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before widening anything in this file.</strong> An <see cref="UpdatePlan"/> produced by
/// <c>LocalProcessProvisioner.Maintenance.cs</c> can describe three kinds of difference, and they are not
/// equally recoverable: an executable change and a marker-tag change rewrite one small JSON file, whereas a
/// <em>data directory</em> change points the install at a different directory and leaves everything in the old
/// one orphaned — which is why planning reports it as <see cref="DataImpact.AtRisk"/>. This file implements the
/// <see cref="DataImpact.Preserved"/> case and nothing else. Every plan whose data impact is anything other
/// than <see cref="DataImpact.Preserved"/>, and every plan carrying a
/// <see cref="LocalProcessProvisioner.DataDirectoryAspect"/> change, is refused here with
/// <see cref="UpdateExecutionResult.Refused"/> and without a single mutating operation.
/// </para>
/// <para>
/// <strong>Refusing is not a gap to be filled in later by loosening the checks.</strong> The data-directory
/// move is absent because separating a running game server from its saves deserves its own reviewed change,
/// and until that review happens the honest state of this adapter is that it cannot do it. A future change
/// that adds it must add it as its own operation with its own tests, not by relaxing
/// <see cref="TryAcceptPlan"/> until an orphaning plan slips through.
/// </para>
/// <para>
/// <strong>Every guard below runs before any mutation.</strong> A refused plan is a statement about the
/// machine's state, not merely about this process's: no file was written, no directory was created and no
/// program was started, so nothing can have half-run.
/// </para>
/// <para>
/// <strong>The write guard now reaches the install verbs, and still cannot reach <c>ensure-dir</c>.</strong>
/// <see cref="WriteGuardedExecutionTarget"/> gates <see cref="IExecutionTarget.WriteFileAsync"/>,
/// <see cref="IExecutionTarget.DeleteAsync"/> and — since commands carry a declared
/// <see cref="CommandSpec.Intent"/> — every command not declared <see cref="CommandIntent.ReadOnly"/>. The
/// install verbs this adapter runs declare nothing, which means <see cref="CommandIntent.Mutating"/>, so
/// <c>steamcmd</c> is refused at the transport on a read-only server whether or not this file remembers to
/// check. What no transport decorator can reach is the <c>ensure-dir</c> verb: on the local adapter that is a
/// <see cref="Directory.CreateDirectory(string)"/> call in this very process, with no seam for a decorator to
/// sit at. So this file still consults the posture through the shared
/// <see cref="ExecutionTargetWriteMode"/> before the first mutation, and refuses the whole update up front
/// rather than letting it fail one step at a time. A target carrying no guard answers <see langword="null"/>
/// and is allowed through: the job here is to surface a refusal the guard would make anyway, earlier and with
/// a better message, not to invent a second policy.
/// </para>
/// <para>
/// <strong>There is no force path.</strong> No argument here skips a guard, and no combination of arguments
/// makes a refused plan execute.
/// </para>
/// </remarks>
public sealed partial class LocalProcessProvisioner : IUpdateApplier
{
    /// <summary>The stage id of the marker rewrite every update plan begins with.</summary>
    internal const string UpdateMarkerStageId = "update-marker";

    /// <summary>
    /// How many recently-planned specs <see cref="PlanUpdateAsync"/> keeps, so that
    /// <see cref="ApplyUpdateAsync"/> can execute a plan this adapter itself computed.
    /// </summary>
    private const int RememberedPlanCapacity = 16;

    private readonly Lock _plannedSpecsLock = new();
    private readonly Dictionary<string, LocalProcessSpec> _plannedSpecs = new(StringComparer.Ordinal);
    private readonly Queue<string> _plannedSpecOrder = new();

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Executes an approved in-place update that preserves the install's data, and nothing else. The sequence
    /// is: check every guard that can be checked without touching the machine, open the marker session and
    /// re-read the live marker, refuse if the server's write mode forbids writing, and only then mutate —
    /// rewriting the marker, then re-running the install verbs against the existing data directory.
    /// </para>
    /// <para>
    /// <strong>The plan hash is checked twice, and the second check is the one that matters here.</strong>
    /// <paramref name="approvedPlanHash"/> is compared against <paramref name="revalidatedPlan"/> at the top,
    /// which catches a caller that handed in a stale approval. That comparison alone would still trust the
    /// caller's claim that the plan object <em>is</em> a revalidation, so this method additionally requires the
    /// hash to name a plan this very adapter computed from the live marker — and, immediately before the first
    /// mutating step, recomputes the plan from the marker it has just re-read and requires the hash to still
    /// match. A plan whose inputs moved between preview and apply therefore cannot execute, and the recompute
    /// is a pure comparison over an already-read marker, so it issues no extra call.
    /// </para>
    /// <para>
    /// <strong>The marker rewrite changes what the plan said would change, and nothing else.</strong> The new
    /// tag set is the live one with the desired identity, extra tags, data directory and executable laid over
    /// it — so a tag some other tool added, and the original <c>servyx.created-at</c>, survive the update. An
    /// update that reset the creation timestamp would quietly relabel an install as newly provisioned.
    /// </para>
    /// </remarks>
    public async Task<UpdateExecutionResult> ApplyUpdateAsync(
        ResourceHandle handle,
        UpdatePlan revalidatedPlan,
        string approvedPlanHash,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(revalidatedPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedPlanHash);

        // Guard 1 - the approval must be for this exact plan. A caller above already compared these two;
        // reaching here with a mismatch means that step was skipped, and the answer is still "nothing runs"
        // rather than "the caller is trusted".
        if (!string.Equals(revalidatedPlan.PlanHash, approvedPlanHash, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                "This update was not applied because the plan handed to the local process adapter is not the plan "
                + $"that was approved: the approval names '{approvedPlanHash}' and the plan hashes to "
                + $"'{revalidatedPlan.PlanHash}'. Nothing on this machine was touched. Preview again and confirm the "
                + "plan you are then shown.");
        }

        // Guard 2 - the plan and the resource must both belong to this provisioner. Executing another adapter's
        // plan against a marker path would rewrite whichever install happened to sit there.
        if (!string.Equals(revalidatedPlan.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: the plan belongs to provisioner '{revalidatedPlan.ProvisionerId}', "
                + $"not to '{Id}'. Nothing on this machine was touched.");
        }

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: the resource belongs to provisioner '{handle.ProvisionerId}', not to "
                + $"'{Id}'. Nothing on this machine was touched.");
        }

        // Guard 3 - the plan must describe an in-place update that preserves data, and must not carry a change
        // this file does not implement.
        if (!TryAcceptPlan(revalidatedPlan, out var refusal))
        {
            return new UpdateExecutionResult.Refused(refusal);
        }

        // Guard 4 - the plan must be one this adapter computed from the live marker. A hash it never produced
        // cannot be turned back into the install verbs the plan's own stages promise to run, and executing the
        // marker rewrite while silently skipping those stages would report a half-applied update as an applied
        // one.
        if (!TryRecallPlannedSpec(revalidatedPlan.PlanHash, out var desiredSpec))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: plan '{revalidatedPlan.PlanId}' was not computed by this adapter from "
                + "the live install, so the install verbs its stages describe are not available to run. Nothing on "
                + "this machine was touched. Recompute the plan against the live install and confirm the plan you "
                + "are then shown.");
        }

        await using var markerSession = await _transport
            .ConnectAsync(MachineDescriptor(handle.ProviderResourceId), ct)
            .ConfigureAwait(false);

        // Guard 5 - the install must still be there. Reading the marker is also what the revalidation below
        // compares against, so this read is not an extra call.
        var liveTags = await ReadMarkerAsync(markerSession, handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (ServyxProcessMarker.FromTags(liveTags) is null)
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: '{handle.ProviderResourceId}' is not a Servyx-managed marker file any "
                + "more, so there is no install to update. Nothing on this machine was touched.");
        }

        // Guard 6 - the write posture. The structural guard now refuses the marker write and the install
        // commands, but not the in-process ensure-dir verb, and it would refuse them one at a time rather
        // than refusing the update. It is consulted here, before anything runs. See the type remarks.
        if (ExecutionTargetWriteMode.Resolve(markerSession) is { } mode && mode != WriteMode.Enabled)
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: the server's write mode is {mode}. "
                + $"Writes require {nameof(WriteMode)}.{nameof(WriteMode.Enabled)}, set per server and never globally. "
                + "Nothing on this machine was touched — no install verb ran, no directory was created, and the "
                + "marker file is exactly as it was. Previewing the update and detecting drift both remain "
                + "available.");
        }

        // Guard 7 - revalidation, immediately before the first mutating step. The plan is recomputed from the
        // marker just read and must still hash to the approved value; anything that moved between preview and
        // now shows up here rather than being applied blind.
        var recomputed = BuildUpdatePlan(handle.ProviderResourceId, liveTags!, desiredSpec);
        if (!string.Equals(recomputed.PlanHash, approvedPlanHash, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                "This update was not applied: recomputing the plan against the install as it is right now yields "
                + $"'{recomputed.PlanHash}', not the approved '{approvedPlanHash}'. The install changed between the "
                + "preview and now. Nothing on this machine was touched. Preview again and confirm the plan you are "
                + "then shown.");
        }

        return await ExecutePreservingUpdateAsync(handle, markerSession, liveTags!, desiredSpec, recomputed, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The mutating half, reached only once every guard in <see cref="ApplyUpdateAsync"/> has passed.
    /// </summary>
    /// <remarks>
    /// The marker is rewritten before the install verbs run, matching both the plan's own stage order — which
    /// is the order the operator approved — and the create path's marker-first discipline. The data directory
    /// is unchanged by construction on this path, so re-creating it is a no-op on any install that still
    /// exists, and creation-only either way.
    /// </remarks>
    private async Task<UpdateExecutionResult> ExecutePreservingUpdateAsync(
        ResourceHandle handle,
        IExecutionTarget markerSession,
        IReadOnlyDictionary<string, string> liveTags,
        LocalProcessSpec desiredSpec,
        UpdatePlan plan,
        CancellationToken ct)
    {
        var tags = BuildUpdatedTags(liveTags, desiredSpec);

        Directory.CreateDirectory(desiredSpec.DataDirectory);

        await using (var content = new MemoryStream(ServyxProcessMarker.Serialize(tags), writable: false))
        {
            await markerSession
                .WriteFileAsync(ToMachinePath(handle.ProviderResourceId), content, new FileWriteOptions(null), ct)
                .ConfigureAwait(false);
        }

        await using (var installSession = await _transport
            .ConnectAsync(BuildTargetDescriptor(desiredSpec.DataDirectory), ct)
            .ConfigureAwait(false))
        {
            for (var i = 0; i < desiredSpec.InstallSteps.Count; i++)
            {
                try
                {
                    await RunInstallStepAsync(installSession, desiredSpec, desiredSpec.InstallSteps[i], i, ct)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    // The verb failed. The marker already records the updated install, which is deliberate and
                    // is the same trade the create path makes: whatever state the machine is in stays
                    // discoverable by a sweep. Reported as Failed rather than thrown, because a step exiting
                    // non-zero is an outcome the provider produced, not a defect in this adapter.
                    return new UpdateExecutionResult.Failed(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"The marker for '{handle.ProviderResourceId}' was rewritten, but the update then failed "
                            + $"while re-running the install verbs against '{desiredSpec.DataDirectory}'. The data "
                            + $"directory was not deleted and nothing in it was removed. {ex.Message}"));
                }
            }
        }

        var resource = await RefreshAsync(handle, ct).ConfigureAwait(false);
        if (resource is null)
        {
            return new UpdateExecutionResult.Failed(
                $"The update of '{handle.ProviderResourceId}' ran to completion, but the marker could not be read "
                + "back afterwards, so Servyx cannot describe the install that now exists. Reconcile before acting "
                + "on it.");
        }

        return new UpdateExecutionResult.Completed(
            resource,
            $"Install '{handle.ProviderResourceId}' was updated in place. The marker was rewritten and "
            + $"{desiredSpec.InstallSteps.Count} install verb(s) were re-run against '{desiredSpec.DataDirectory}'. "
            + $"The data directory is the one the install already occupied, so every file in it is where it was; "
            + $"the plan's stated data impact was {plan.DataImpact}.");
    }

    /// <summary>
    /// The tag set an update writes: the live marker's tags with the desired identity, extra tags, data
    /// directory and executable laid over them.
    /// </summary>
    /// <remarks>
    /// Starting from the live tags rather than rebuilding from scratch is what keeps this method from doing
    /// more than the plan describes: a tag the plan never mentioned — including
    /// <see cref="ServyxProcessMarker.CreatedAtTag"/>, which records when the install was <em>created</em> —
    /// is carried across untouched. The mandatory Servyx identity tags are applied last, by
    /// <see cref="ServyxProcessMarker.ToTags"/>, so nothing in the live file can shadow one.
    /// </remarks>
    private IReadOnlyDictionary<string, string> BuildUpdatedTags(
        IReadOnlyDictionary<string, string> liveTags,
        LocalProcessSpec desiredSpec)
    {
        var overlay = new Dictionary<string, string>(liveTags, StringComparer.Ordinal);

        foreach (var extra in desiredSpec.AdditionalTags)
        {
            overlay[extra.Key] = extra.Value;
        }

        overlay[ServyxProcessMarker.RootPathTag] = desiredSpec.DataDirectory;
        overlay[ServyxProcessMarker.ProvisionerIdTag] = Id;
        overlay[ServyxProcessMarker.ExecutableTag] = desiredSpec.Executable;

        if (!overlay.ContainsKey(ServyxProcessMarker.CreatedAtTag))
        {
            overlay[ServyxProcessMarker.CreatedAtTag] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        }

        return desiredSpec.Marker.ToTags(overlay);
    }

    /// <summary>
    /// Decides whether this file will execute <paramref name="plan"/> at all, or explains why it will not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately strict. The plan must describe an in-place update, must state its data impact as
    /// <see cref="DataImpact.Preserved"/>, and must carry only changes this file implements — the executable
    /// and marker tags. A plan that also moves the data directory is refused outright rather than partly
    /// executed: applying the parts it understands and skipping the rest would report a half-applied update as
    /// an applied one.
    /// </para>
    /// <para>
    /// The strategy and data-impact checks are partly redundant with the per-change check, and they are kept
    /// anyway: they are the two properties the person approving the plan actually read, so they are the two
    /// this file re-reads before acting. In particular no plan whose <see cref="DataImpact"/> is anything other
    /// than <see cref="DataImpact.Preserved"/> can reach a mutation from here, whatever its changes claim.
    /// </para>
    /// </remarks>
    private static bool TryAcceptPlan(UpdatePlan plan, out string refusal)
    {
        if (plan.Strategy != UpdateStrategy.InPlace)
        {
            refusal =
                $"This update was not applied: the plan's strategy is {plan.Strategy}, and the local process adapter "
                + "executes only an in-place update. Nothing on this machine was touched.";
            return false;
        }

        if (plan.DataImpact != DataImpact.Preserved)
        {
            refusal =
                $"This update was not applied: the plan states its impact on persistent data as {plan.DataImpact}, "
                + "and the local process adapter executes only updates that preserve it. Moving an install to a "
                + "different data directory leaves its saves behind, attached to nothing, and is deliberately not "
                + "implemented. Nothing on this machine was touched.";
            return false;
        }

        var unsupported = plan.Changes
            .Where(c => !string.Equals(c.Aspect, ExecutableAspect, StringComparison.Ordinal)
                && !c.Aspect.StartsWith(TagAspectPrefix, StringComparison.Ordinal))
            .ToList();

        if (unsupported.Count > 0)
        {
            refusal =
                "This update was not applied: the local process adapter re-runs the install verbs and rewrites the "
                + "marker, and this plan describes "
                + string.Join("; ", unsupported.Select(c => c.Description))
                + ". Applying the part it understands and skipping the rest would report a half-applied update as an "
                + "applied one, so nothing on this machine was touched.";
            return false;
        }

        refusal = string.Empty;
        return true;
    }

    /// <summary>
    /// Remembers the desired spec a plan was computed from, keyed by that plan's hash, so
    /// <see cref="ApplyUpdateAsync"/> can run the install verbs the plan's stages promise.
    /// </summary>
    /// <remarks>
    /// The plan hash covers the desired spec in full, so an entry can only ever be recalled by a caller holding
    /// the very plan it was stored for. The store is bounded and oldest-first, so a long-lived provisioner
    /// cannot accumulate specs indefinitely; falling out of it makes an apply <em>refuse</em>, never
    /// mis-execute.
    /// </remarks>
    private void RememberPlannedSpec(string planHash, LocalProcessSpec spec)
    {
        lock (_plannedSpecsLock)
        {
            if (!_plannedSpecs.TryAdd(planHash, spec))
            {
                return;
            }

            _plannedSpecOrder.Enqueue(planHash);
            while (_plannedSpecOrder.Count > RememberedPlanCapacity)
            {
                _plannedSpecs.Remove(_plannedSpecOrder.Dequeue());
            }
        }
    }

    private bool TryRecallPlannedSpec(string planHash, out LocalProcessSpec spec)
    {
        lock (_plannedSpecsLock)
        {
            return _plannedSpecs.TryGetValue(planHash, out spec!);
        }
    }

    /// <summary>
    /// Carries out one install verb. Shared by the create operation and the update path so the closed set of
    /// verbs — and the refusal to invent behaviour for a verb that has none — exists in exactly one place.
    /// </summary>
    /// <exception cref="InvalidOperationException">The step failed, or names a verb with no execution behaviour.</exception>
    internal static async Task RunInstallStepAsync(
        IExecutionTarget session,
        LocalProcessSpec spec,
        LocalInstallStep step,
        int index,
        CancellationToken ct)
    {
        switch (step)
        {
            case SteamCmdInstallStep steamCmd:
            {
                var command = steamCmd.ToCommand(spec);
                var result = await session.ExecuteAsync(command, ct).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Install stage '{step.StageId(index)}' ('{command.Executable}') exited with code "
                            + $"{result.ExitCode}: {result.StandardError}"));
                }

                return;
            }

            case EnsureDirectoryInstallStep ensureDirectory:
                // Realised without spawning anything — see the remarks on LocalInstallStep. The path was
                // validated as fully qualified when the spec was built, i.e. at plan time.
                Directory.CreateDirectory(ensureDirectory.Path);
                return;

            default:
                // Unreachable: LocalInstallStep's constructor is private protected, so the hierarchy is closed
                // to LocalProcessSpec.cs. Present so that adding a verb without teaching this switch about it
                // fails loudly rather than silently skipping the step.
                throw new InvalidOperationException(
                    $"Install verb '{step.Verb}' has no execution behaviour in the '{Id}' provisioner.");
        }
    }
}
