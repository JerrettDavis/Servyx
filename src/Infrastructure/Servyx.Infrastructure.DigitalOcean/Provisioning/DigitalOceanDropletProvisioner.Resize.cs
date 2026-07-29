using System.Globalization;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Provisioning;

/// <summary>
/// The <see cref="IUpdateApplier"/> half of the droplet adapter: the only code in this assembly that changes
/// a droplet which already exists.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before widening anything in this file.</strong> An <see cref="UpdatePlan"/> produced by
/// <c>DigitalOceanDropletProvisioner.Maintenance.cs</c> can describe three differences, and they are not
/// equally recoverable: a size change is a reversible <c>disk: false</c> resize, an image change is a
/// <em>rebuild</em> that erases the boot disk, and a region change has no operation at all. This file
/// implements the first one. It does not implement the other two, and it does not implement the tag attach
/// either — every plan describing anything other than a lone size change is refused here, with
/// <see cref="UpdateExecutionResult.Refused"/> and without a single provider call.
/// </para>
/// <para>
/// <strong>Refusing is not a gap to be filled in later by loosening the checks.</strong> The rebuild is
/// absent because erasing a customer's game saves deserves its own reviewed change, and until that review
/// happens the honest state of this adapter is that it cannot do it. A future change that adds rebuild must
/// add it as its own operation with its own tests, not by relaxing
/// <see cref="TryReadResizeTarget"/> until a destructive plan slips through.
/// </para>
/// <para>
/// <strong>Every guard below runs before any HTTP.</strong> A refused plan is a statement about
/// DigitalOcean's state, not merely about this process's: nothing was sent, so nothing can have half-run.
/// </para>
/// <para>
/// <strong>Submission is not success.</strong> DigitalOcean answers the resize POST while the resize is
/// still queued. This file therefore never reports a success from the POST: it polls
/// <c>GET /v2/actions/{id}</c> and only <see cref="DropletActionOutcome.Completed"/> — observed, not
/// assumed — becomes <see cref="UpdateExecutionResult.Completed"/>. An action still running when the polls
/// are spent is <see cref="UpdateExecutionResult.TimedOut"/>, which is deliberately a different answer from
/// <see cref="UpdateExecutionResult.Failed"/>: a failed resize may be retried, whereas re-submitting one
/// that is still running is a second mutation of a live machine.
/// </para>
/// <para>
/// <strong>There is no force path.</strong> No argument here skips a guard, and no combination of arguments
/// produces a <c>disk: true</c> resize — see <see cref="ResizeDropletActionRequest"/>, whose <c>disk</c>
/// member has no setter.
/// </para>
/// </remarks>
public sealed partial class DigitalOceanDropletProvisioner : IUpdateApplier
{
    /// <summary>
    /// The <see cref="PlannedChange.Aspect"/> that update planning gives a droplet size difference. The one
    /// aspect this file can execute, matched exactly rather than by prefix.
    /// </summary>
    private const string SizeAspect = "size";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Executes an approved <c>disk: false</c> resize and nothing else. The sequence is: check every guard
    /// (no HTTP), submit one resize action, poll that action to a conclusion, and — only once DigitalOcean
    /// has been observed reporting it <c>completed</c> — re-read the droplet so the caller is handed the
    /// state that now exists rather than the state that was asked for.
    /// </para>
    /// <para>
    /// A provider refusal of the submission (for instance the 422 DigitalOcean answers with when the target
    /// size needs a bigger boot disk than the droplet has) surfaces as
    /// <see cref="UpdateExecutionResult.Failed"/> carrying DigitalOcean's own error text. That particular
    /// refusal is the intended outcome rather than a gap: the only way past it is the irreversible
    /// disk-inclusive resize, which this adapter will not issue.
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

        // Guard 1 - the approval must be for this exact plan. The dashboard already compared these two, so
        // reaching here with a mismatch means a caller skipped that step; the answer is still "nothing is
        // sent" rather than "the caller above is trusted".
        if (!string.Equals(revalidatedPlan.PlanHash, approvedPlanHash, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                "This update was not applied because the plan handed to the DigitalOcean adapter is not the plan "
                + $"that was approved: the approval names '{approvedPlanHash}' and the plan hashes to "
                + $"'{revalidatedPlan.PlanHash}'. Nothing was sent to DigitalOcean. Preview again and confirm the "
                + "plan you are then shown.");
        }

        // Guard 2 - the plan and the resource must both belong to this provisioner. Executing another
        // adapter's plan against a droplet id would resize whichever droplet happened to share that number.
        if (!string.Equals(revalidatedPlan.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: the plan belongs to provisioner '{revalidatedPlan.ProvisionerId}', "
                + $"not to '{Id}'. Nothing was sent to DigitalOcean.");
        }

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: the resource belongs to provisioner '{handle.ProvisionerId}', not to "
                + $"'{Id}'. Nothing was sent to DigitalOcean.");
        }

        if (!TryReadDropletId(handle.ProviderResourceId, out var dropletId))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: '{handle.ProviderResourceId}' is not a DigitalOcean droplet id, so "
                + "there is no droplet to resize. Nothing was sent to DigitalOcean.");
        }

        // Guard 3 - the plan must describe a lone resize, and nothing else at all.
        if (!TryReadResizeTarget(revalidatedPlan, out var targetSize, out var refusal))
        {
            return new UpdateExecutionResult.Refused(refusal);
        }

        DropletActionResource action;
        try
        {
            // The first and only mutating request on this path.
            action = await _api.ResizeDropletAsync(dropletId, targetSize, ct).ConfigureAwait(false);
        }
        catch (DigitalOceanApiException ex)
        {
            // Translated, not swallowed, and carrying DigitalOcean's own words - the caller needs to be able
            // to read the provider's reason, not this adapter's paraphrase of it.
            return new UpdateExecutionResult.Failed(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"DigitalOcean refused the resize of droplet {dropletId} to '{targetSize}'. The droplet was not "
                    + $"changed. {ex.Message}"));
        }

        var poll = await _api
            .PollActionAsync(action.Id, _actionPollInterval, _actionPollAttempts, _timeProvider, ct)
            .ConfigureAwait(false);

        return poll.Outcome switch
        {
            DropletActionOutcome.Completed => await CompletedResizeAsync(handle, dropletId, targetSize, poll, ct)
                .ConfigureAwait(false),
            DropletActionOutcome.Errored => new UpdateExecutionResult.Failed(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"DigitalOcean reported resize action {poll.ActionId} on droplet {dropletId} as errored. ")
                + (poll.Message is { Length: > 0 } message
                    ? string.Create(CultureInfo.InvariantCulture, $"DigitalOcean's message: {message} ")
                    : "DigitalOcean supplied no explanation with the action. ")
                + "The resize did not complete. Re-read the droplet's size before retrying."),
            _ => new UpdateExecutionResult.TimedOut(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"DigitalOcean accepted resize action {poll.ActionId} on droplet {dropletId} and was still "
                    + $"reporting it as '{poll.Status ?? "(no status)"}' after {poll.Polls} check(s). ")
                + "The resize was NOT confirmed and was NOT reported as failed - it may still be running at "
                + "DigitalOcean and may yet complete. Do not resubmit: a second resize is a second mutation of a "
                + "live machine, not a retry. Watch the action at DigitalOcean, or re-read the droplet, before "
                + "acting further."),
        };
    }

    /// <summary>
    /// Re-reads the droplet after DigitalOcean has been observed reporting the resize complete, so the
    /// caller receives the machine as it now is.
    /// </summary>
    /// <remarks>
    /// A droplet that cannot be re-read at this point is reported as a failure rather than as a success,
    /// because a success carries the resource and there is none to carry. The message says plainly that the
    /// resize itself did complete, so an operator is not sent looking for a resize that never ran.
    /// </remarks>
    private async Task<UpdateExecutionResult> CompletedResizeAsync(
        ResourceHandle handle,
        long dropletId,
        string targetSize,
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
                    $"DigitalOcean reported resize action {poll.ActionId} on droplet {dropletId} as completed, so "
                    + $"the droplet IS now '{targetSize}' - but it could not be read back afterwards, so Servyx "
                    + $"cannot describe the machine that now exists. {ex.Message}"));
        }

        if (resource is null)
        {
            return new UpdateExecutionResult.Failed(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"DigitalOcean reported resize action {poll.ActionId} on droplet {dropletId} as completed, so "
                    + $"the droplet IS now '{targetSize}' - but DigitalOcean no longer describes that droplet as a ")
                + "Servyx-managed one, so Servyx cannot describe the machine that now exists. Reconcile before "
                + "acting on it.");
        }

        return new UpdateExecutionResult.Completed(
            resource,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Droplet {dropletId} was resized to '{targetSize}'. DigitalOcean reported action {poll.ActionId} as "
                + $"completed after {poll.Polls} check(s). ")
            + "The resize was the CPU-and-memory-only form, so the boot disk was not written to and every file on "
            + "the machine is where it was.");
    }

    /// <summary>
    /// Reads the size a plan asks for, or explains why this file will not execute the plan at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately strict: the plan must carry <em>exactly one</em> change, that change must be the
    /// <c>size</c> aspect, and the plan must describe itself as an in-place update that preserves data. A
    /// plan that also attaches a tag is refused rather than partly executed, because executing the resize
    /// and silently skipping the tag would report a half-applied update as an applied one.
    /// </para>
    /// <para>
    /// The strategy and data-impact checks are redundant with the change check for every plan this adapter
    /// currently produces, and they are kept anyway: they are the two properties the person approving the
    /// plan actually read, so they are the two this file re-reads before acting. In particular no plan whose
    /// <see cref="DataImpact"/> is anything other than <see cref="DataImpact.Preserved"/> can reach a
    /// provider call from here, whatever its changes claim.
    /// </para>
    /// </remarks>
    private static bool TryReadResizeTarget(UpdatePlan plan, out string targetSize, out string refusal)
    {
        targetSize = string.Empty;

        if (plan.Strategy != UpdateStrategy.InPlace)
        {
            refusal =
                $"This update was not applied: the plan's strategy is {plan.Strategy}, and the DigitalOcean adapter "
                + "executes only an in-place resize. Nothing was sent to DigitalOcean.";
            return false;
        }

        if (plan.DataImpact != DataImpact.Preserved)
        {
            refusal =
                $"This update was not applied: the plan states its impact on persistent data as {plan.DataImpact}, "
                + "and the DigitalOcean adapter executes only updates that preserve it. A rebuild - the operation "
                + "that changes a droplet's image - erases the boot disk and is deliberately not implemented. "
                + "Nothing was sent to DigitalOcean.";
            return false;
        }

        if (plan.Changes.Count != 1
            || !string.Equals(plan.Changes[0].Aspect, SizeAspect, StringComparison.Ordinal))
        {
            refusal =
                "This update was not applied: the DigitalOcean adapter executes a droplet resize and nothing else, "
                + $"and this plan describes {plan.Changes.Count} change(s) - "
                + string.Join("; ", plan.Changes.Select(c => c.Description))
                + ". Applying the part it understands and skipping the rest would report a half-applied update as "
                + "an applied one, so nothing was sent to DigitalOcean.";
            return false;
        }

        var desired = plan.Changes[0].Desired;
        if (string.IsNullOrWhiteSpace(desired))
        {
            refusal =
                "This update was not applied: the plan's size change names no target size, so there is nothing to "
                + "resize the droplet to. Nothing was sent to DigitalOcean.";
            return false;
        }

        targetSize = desired;
        refusal = string.Empty;
        return true;
    }
}
