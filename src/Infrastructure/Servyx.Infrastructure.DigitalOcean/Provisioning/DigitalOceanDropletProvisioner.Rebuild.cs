using System.Globalization;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Provisioning;

/// <summary>
/// The <see cref="IDestructiveUpdateApplier"/> half of the droplet adapter: the only code in this assembly
/// that deletes a customer's data on purpose.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before widening anything in this file.</strong> A DigitalOcean <em>rebuild</em> reimages
/// a droplet's boot disk. The installed game, its configuration and every save file are replaced by a fresh
/// copy of the image, and none of it can be recovered from the droplet afterwards, because none of it is
/// still there. The droplet keeps its id and its address; nothing else about it survives. There is no gentler
/// operation: DigitalOcean exposes nothing else that changes a droplet's image.
/// </para>
/// <para>
/// <strong>This exists only because the approvals above it are complete.</strong> It is not a relaxation of
/// <c>DigitalOceanDropletProvisioner.Resize.cs</c>, which still refuses every rebuild it is handed and always
/// will — the resize entry point cannot reach any line of this file. Reaching a provider call from here needs
/// all of: a plan whose hash still matches the approval, a plan this provisioner produced, a droplet id, a
/// plan whose <see cref="DataImpact"/> is <see cref="DataImpact.Destroyed"/>, an acknowledgement naming
/// exactly <see cref="DataImpact.Destroyed"/>, and a plan whose one and only change is the image. Any of them
/// missing is <see cref="UpdateExecutionResult.Refused"/>, and a refusal issues no HTTP request of any kind.
/// </para>
/// <para>
/// <strong>Nothing here can resize, and nothing in the resize path can rebuild.</strong> The two operations
/// are separate interface members with separate implementations, and each builds a request body whose action
/// <c>type</c> is a property with no setter — <c>rebuild</c> here, <c>resize</c> there. There is no argument,
/// at any call site, that converts one into the other; see <see cref="RebuildDropletActionRequest"/> and
/// <see cref="ResizeDropletActionRequest"/>.
/// </para>
/// <para>
/// <strong>A region change remains unexecutable.</strong> Its plan describes a region difference, never a
/// lone image change, so <see cref="TryReadRebuildTarget"/> refuses it — as it refuses a rebuild bundled with
/// a resize or a retag, because executing the part this file understands would report a half-applied update
/// as an applied one.
/// </para>
/// <para>
/// <strong>Submission is not success, and a timeout is not a failure.</strong> DigitalOcean answers the
/// rebuild POST while the rebuild is still queued, and a rebuild takes minutes. Only an observed
/// <see cref="DropletActionOutcome.Completed"/> becomes <see cref="UpdateExecutionResult.Completed"/>; an
/// action still running when the polls are spent is <see cref="UpdateExecutionResult.TimedOut"/>, which is
/// deliberately a different type from <see cref="UpdateExecutionResult.Failed"/>. The two demand opposite
/// responses from an operator here even more sharply than they do for a resize: "still reimaging" means wait,
/// and "the reimage failed" means the machine may need attention — while resubmitting a running rebuild
/// erases the disk a second time, including whatever the first one had already restored onto it.
/// </para>
/// <para>
/// <strong>There is no force path.</strong> No argument here skips a guard, and no combination of arguments
/// reaches a provider call that the checks below would otherwise refuse.
/// </para>
/// </remarks>
public sealed partial class DigitalOceanDropletProvisioner : IDestructiveUpdateApplier
{
    /// <summary>
    /// The <see cref="PlannedChange.Aspect"/> that update planning gives a droplet image difference. The one
    /// aspect this file can execute, matched exactly rather than by prefix.
    /// </summary>
    private const string ImageAspect = "image";

    /// <summary>
    /// The <see cref="PlannedChange.Aspect"/> of a region difference, named here only so the refusal for one
    /// can say what is actually wrong rather than counting changes at the operator.
    /// </summary>
    private const string RegionAspect = "region";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Executes an approved rebuild and nothing else. The sequence is: check every guard (no HTTP), submit
    /// one rebuild action, poll that action to a conclusion, and — only once DigitalOcean has been observed
    /// reporting it <c>completed</c> — re-read the droplet so the caller is handed the machine that now
    /// exists rather than the one that was asked for.
    /// </para>
    /// <para>
    /// A provider refusal of the submission surfaces as <see cref="UpdateExecutionResult.Failed"/> carrying
    /// DigitalOcean's own error text, and — because the submission is the first and only mutating request on
    /// this path — such a failure means the disk was never touched.
    /// </para>
    /// </remarks>
    public async Task<UpdateExecutionResult> ApplyDestructiveUpdateAsync(
        ResourceHandle handle,
        UpdatePlan revalidatedPlan,
        string approvedPlanHash,
        DataImpact? acknowledgedDataImpact,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(revalidatedPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedPlanHash);

        // Guard 1 - the approval must be for this exact plan, checked immediately before the submission with
        // no provider call in between. A stale plan is refused and never executed: the droplet the approval
        // described is not necessarily the droplet that exists now, and this operation is not undoable.
        if (!string.Equals(revalidatedPlan.PlanHash, approvedPlanHash, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                "This rebuild was not carried out because the plan handed to the DigitalOcean adapter is not the "
                + $"plan that was approved: the approval names '{approvedPlanHash}' and the plan hashes to "
                + $"'{revalidatedPlan.PlanHash}'. Nothing was sent to DigitalOcean and no disk was erased. Preview "
                + "again and confirm the plan you are then shown.");
        }

        // Guard 2 - the plan and the resource must both belong to this provisioner. Executing another
        // adapter's plan against a droplet id would reimage whichever droplet happened to share that number.
        if (!string.Equals(revalidatedPlan.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This rebuild was not carried out: the plan belongs to provisioner "
                + $"'{revalidatedPlan.ProvisionerId}', not to '{Id}'. Nothing was sent to DigitalOcean.");
        }

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This rebuild was not carried out: the resource belongs to provisioner "
                + $"'{handle.ProvisionerId}', not to '{Id}'. Nothing was sent to DigitalOcean.");
        }

        if (!TryReadDropletId(handle.ProviderResourceId, out var dropletId))
        {
            return new UpdateExecutionResult.Refused(
                $"This rebuild was not carried out: '{handle.ProviderResourceId}' is not a DigitalOcean droplet id, "
                + "so there is no droplet to rebuild. Nothing was sent to DigitalOcean.");
        }

        // Guard 3 - the second, independent approval. This is the same rule Servyx.Application's
        // DataImpactAcknowledgement enforces, restated here because that token type lives in Application and
        // this assembly references only Servyx.Domain - the same reason the Docker adapter's recreate path
        // restates it. It is checked in both directions and it is an exact match: an acknowledgement of
        // AtRisk does not authorise a Destroyed plan, an acknowledgement of Preserved authorises nothing at
        // all, and no acknowledgement authorises nothing at all.
        if (revalidatedPlan.DataImpact != DataImpact.Destroyed)
        {
            return new UpdateExecutionResult.Refused(
                $"This rebuild was not carried out: the plan states its impact on persistent data as "
                + $"{revalidatedPlan.DataImpact}, and a rebuild erases the droplet's boot disk, so only a plan "
                + $"that states {DataImpact.Destroyed} can be executed here. A plan claiming anything milder than "
                + "what would actually happen is a reason to stop, not to proceed. Nothing was sent to "
                + "DigitalOcean.");
        }

        if (acknowledgedDataImpact != DataImpact.Destroyed)
        {
            return new UpdateExecutionResult.Refused(
                $"This rebuild was not carried out: the plan states its impact on persistent data as "
                + $"{revalidatedPlan.DataImpact} and the acknowledgement supplied was "
                + $"{(acknowledgedDataImpact is null ? "none" : acknowledgedDataImpact.Value.ToString())}. "
                + "Rebuilding this droplet deletes everything stored on it, so it runs only when someone has "
                + $"separately accepted exactly that — an acknowledgement of {DataImpact.AtRisk}, or none at all, "
                + "is not an approval of data loss. Nothing was sent to DigitalOcean and no disk was erased.");
        }

        // Guard 4 - the plan must describe a lone rebuild, and nothing else at all.
        if (!TryReadRebuildTarget(revalidatedPlan, out var targetImage, out var refusal))
        {
            return new UpdateExecutionResult.Refused(refusal);
        }

        DropletActionResource action;
        try
        {
            // The first and only mutating request on this path. Everything above ran without touching the
            // network, so a refusal reaching this line is impossible and a refusal before it sent nothing.
            action = await _api.RebuildDropletAsync(dropletId, targetImage, ct).ConfigureAwait(false);
        }
        catch (DigitalOceanApiException ex)
        {
            // Translated, not swallowed, and carrying DigitalOcean's own words - the caller needs to be able
            // to read the provider's reason, not this adapter's paraphrase of it.
            return new UpdateExecutionResult.Failed(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"DigitalOcean refused the rebuild of droplet {dropletId} from image '{targetImage}'. The "
                    + $"droplet was not changed and its disk was not erased. {ex.Message}"));
        }

        var poll = await _api
            .PollActionAsync(action.Id, _actionPollInterval, _actionPollAttempts, _timeProvider, ct)
            .ConfigureAwait(false);

        return poll.Outcome switch
        {
            DropletActionOutcome.Completed => await CompletedRebuildAsync(handle, dropletId, targetImage, poll, ct)
                .ConfigureAwait(false),
            DropletActionOutcome.Errored => new UpdateExecutionResult.Failed(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"DigitalOcean reported rebuild action {poll.ActionId} on droplet {dropletId} as errored. ")
                + (poll.Message is { Length: > 0 } message
                    ? string.Create(CultureInfo.InvariantCulture, $"DigitalOcean's message: {message} ")
                    : "DigitalOcean supplied no explanation with the action. ")
                + "The rebuild did not complete. The action was accepted before it errored, so the droplet's disk "
                + "may have been partly or wholly overwritten already - treat the machine's contents as lost until "
                + "you have read the droplet and confirmed otherwise."),
            _ => new UpdateExecutionResult.TimedOut(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"DigitalOcean accepted rebuild action {poll.ActionId} on droplet {dropletId} and was still "
                    + $"reporting it as '{poll.Status ?? "(no status)"}' after {poll.Polls} check(s). ")
                + "The rebuild was NOT confirmed and was NOT reported as failed - a rebuild takes minutes and this "
                + "one is most likely still running at DigitalOcean and may yet complete. That is a different "
                + "situation from a failure and calls for the opposite response: do NOT resubmit, because a second "
                + "rebuild erases the disk again, including anything the first one has already put back. Watch the "
                + "action at DigitalOcean, or re-read the droplet, before acting further."),
        };
    }

    /// <summary>
    /// Re-reads the droplet after DigitalOcean has been observed reporting the rebuild complete, so the
    /// caller receives the machine as it now is.
    /// </summary>
    /// <remarks>
    /// A droplet that cannot be re-read at this point is reported as a failure rather than as a success,
    /// because a success carries the resource and there is none to carry. The message says plainly that the
    /// rebuild itself did complete and that the disk is therefore already gone, so nobody reads a missing
    /// resource as evidence that the destruction did not happen.
    /// </remarks>
    private async Task<UpdateExecutionResult> CompletedRebuildAsync(
        ResourceHandle handle,
        long dropletId,
        string targetImage,
        DropletActionPoll poll,
        CancellationToken ct)
    {
        ProvisionedResource? resource;
        try
        {
            resource = await RefreshAsync(handle, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DigitalOceanApiException)
        {
            return new UpdateExecutionResult.Failed(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"DigitalOcean reported rebuild action {poll.ActionId} on droplet {dropletId} as completed, so "
                    + $"the droplet HAS been reimaged from '{targetImage}' and its previous contents are already "
                    + $"gone - but it could not be read back afterwards, so Servyx cannot describe the machine that "
                    + $"now exists. {ex.Message}"));
        }

        if (resource is null)
        {
            return new UpdateExecutionResult.Failed(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"DigitalOcean reported rebuild action {poll.ActionId} on droplet {dropletId} as completed, so "
                    + $"the droplet HAS been reimaged from '{targetImage}' and its previous contents are already "
                    + $"gone - but DigitalOcean no longer describes that droplet as a Servyx-managed one. ")
                + "Servyx cannot describe the machine that now exists. Reconcile before acting on it.");
        }

        return new UpdateExecutionResult.Completed(
            resource,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Droplet {dropletId} was rebuilt from image '{targetImage}'. DigitalOcean reported action "
                + $"{poll.ActionId} as completed after {poll.Polls} check(s). ")
            + "The boot disk was erased and replaced: everything the machine held before this ran - the installed "
            + "game, its configuration, and every save file - is gone and cannot be recovered from the droplet. The "
            + "droplet kept its id and its address.");
    }

    /// <summary>
    /// Reads the image a plan asks for, or explains why this file will not execute the plan at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately strict, and strict in the same shape as the resize path: the plan must carry
    /// <em>exactly one</em> change and that change must be the <c>image</c> aspect. A rebuild bundled with a
    /// resize or a tag attach is refused rather than partly executed, because executing the rebuild and
    /// silently skipping the rest would report a half-applied update as an applied one — and the half that
    /// ran is the irreversible half.
    /// </para>
    /// <para>
    /// A region change is named explicitly, ahead of the general count check that would also catch it, so the
    /// refusal an operator reads says what is actually true: a droplet cannot be moved, and no rebuild would
    /// have got it there.
    /// </para>
    /// </remarks>
    private static bool TryReadRebuildTarget(UpdatePlan plan, out string targetImage, out string refusal)
    {
        targetImage = string.Empty;

        if (plan.Changes.Any(c => string.Equals(c.Aspect, RegionAspect, StringComparison.Ordinal)))
        {
            refusal =
                "This rebuild was not carried out: the plan changes the droplet's region, and a droplet cannot be "
                + "moved between regions. DigitalOcean exposes no action that relocates one, and a rebuild would "
                + "erase the disk without moving the machine anywhere. Nothing was sent to DigitalOcean.";
            return false;
        }

        if (plan.Changes.Count != 1
            || !string.Equals(plan.Changes[0].Aspect, ImageAspect, StringComparison.Ordinal))
        {
            refusal =
                "This rebuild was not carried out: the DigitalOcean adapter executes a droplet rebuild and nothing "
                + $"else, and this plan describes {plan.Changes.Count} change(s) - "
                + string.Join("; ", plan.Changes.Select(c => c.Description))
                + ". Applying the part it understands and skipping the rest would report a half-applied update as "
                + "an applied one, and the part it understands is the one that cannot be undone, so nothing was "
                + "sent to DigitalOcean.";
            return false;
        }

        var desired = plan.Changes[0].Desired;
        if (string.IsNullOrWhiteSpace(desired))
        {
            refusal =
                "This rebuild was not carried out: the plan's image change names no target image, so there is "
                + "nothing to rebuild the droplet from. Nothing was sent to DigitalOcean.";
            return false;
        }

        targetImage = desired;
        refusal = string.Empty;
        return true;
    }
}
