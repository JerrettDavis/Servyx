using System.Globalization;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Azure.Provisioning;

/// <summary>
/// The <see cref="IUpdateApplier"/> half of the VM adapter: the only code in this assembly that changes a
/// virtual machine which already exists.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before widening anything in this file.</strong> An <see cref="UpdatePlan"/> produced by
/// <c>AzureVirtualMachineProvisioner.Maintenance.cs</c> can describe four differences, and they are not equally
/// recoverable: a size change is an ARM property write on the existing resource, an image change is a
/// <em>delete and recreate</em> that takes the managed OS disk with it, and a region or resource-group change
/// is not an operation at all. This file implements the first one. It does not implement the other three, and
/// it does not implement the tag write either — every plan describing anything other than a lone size change is
/// refused here, with <see cref="UpdateExecutionResult.Refused"/> and without a single ARM call.
/// </para>
/// <para>
/// <strong>Refusing is not a gap to be filled in later by loosening the checks.</strong> The replacement is
/// absent because deleting a customer's managed OS disk deserves its own reviewed change, and until that review
/// happens the honest state of this adapter is that it cannot do it. A future change that adds replacement must
/// add it as its own operation with its own tests, not by relaxing <see cref="TryReadResizeTarget"/> until a
/// destructive plan slips through.
/// </para>
/// <para>
/// <strong>Every guard below runs before any HTTP.</strong> A refused plan is a statement about Azure's state,
/// not merely about this process's: nothing was sent — not even the token exchange, since that happens only
/// when a request is about to go out — so nothing can have half-run.
/// </para>
/// <para>
/// <strong>What the resize does to the machine, stated in full.</strong> The request writes only
/// <c>properties.hardwareProfile.vmSize</c>. The managed OS disk is a separate ARM resource the machine
/// references by id, and a PATCH that names only the hardware profile neither names that resource nor alters
/// the reference to it — which is the structural justification for the plan's
/// <see cref="DataImpact.Preserved"/>, argued at length in the maintenance half's remarks. Azure nevertheless
/// deallocates and restarts the machine to apply a new size, so the workload <em>is</em> interrupted. That is a
/// service interruption and not a data impact, and the completed message says both parts rather than letting
/// "in place" imply the machine never stopped.
/// </para>
/// <para>
/// <strong>Submission is not success.</strong> ARM answers the resize while the resize is still running — a
/// <c>200</c> whose provisioning state is <c>Updating</c>, or a <c>202</c> naming a long-running operation.
/// This file therefore never reports a success from the submission: it polls the operation to a conclusion and
/// only <see cref="ArmOperationOutcome.Succeeded"/> — observed, not assumed — becomes
/// <see cref="UpdateExecutionResult.Completed"/>. An operation still running when the polls are spent is
/// <see cref="UpdateExecutionResult.TimedOut"/>, deliberately a different answer from
/// <see cref="UpdateExecutionResult.Failed"/>: a failed resize may be retried, whereas re-submitting one that
/// is still running is a second mutation of a live machine.
/// </para>
/// <para>
/// <strong>There is no force path.</strong> No argument here skips a guard, and no combination of arguments
/// produces a request that names an image — see <c>ArmVirtualMachineResizeRequest</c>, which has no member that
/// could carry one.
/// </para>
/// </remarks>
public sealed partial class AzureVirtualMachineProvisioner : IUpdateApplier
{
    /// <summary>
    /// The <see cref="PlannedChange.Aspect"/> that update planning gives a VM size difference. The one aspect
    /// this file can execute, matched exactly rather than by prefix.
    /// </summary>
    private const string SizeAspect = "size";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Executes an approved size write and nothing else. The sequence is: check every guard (no HTTP), submit
    /// one PATCH naming only <c>properties.hardwareProfile.vmSize</c>, poll the operation ARM created for it to
    /// a conclusion, and — only once ARM has been observed reporting it succeeded — re-read the machine so the
    /// caller is handed the state that now exists rather than the state that was asked for.
    /// </para>
    /// <para>
    /// An ARM refusal of the submission (for instance the error Azure gives when the target size is not
    /// available in the machine's cluster or region) surfaces as <see cref="UpdateExecutionResult.Failed"/>
    /// carrying Azure's own error text. That refusal is an outcome, not a gap: the way past it is a different
    /// size or a different region, neither of which this adapter will choose on an operator's behalf.
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
        // reaching here with a mismatch means a caller skipped that step; the answer is still "nothing is sent"
        // rather than "the caller above is trusted".
        if (!string.Equals(revalidatedPlan.PlanHash, approvedPlanHash, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                "This update was not applied because the plan handed to the Azure adapter is not the plan that was "
                + $"approved: the approval names '{approvedPlanHash}' and the plan hashes to "
                + $"'{revalidatedPlan.PlanHash}'. Nothing was sent to Azure. Preview again and confirm the plan you "
                + "are then shown.");
        }

        // Guard 2 - the plan and the resource must both belong to this provisioner. Executing another adapter's
        // plan against an ARM id would resize whichever machine happened to answer to it.
        if (!string.Equals(revalidatedPlan.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: the plan belongs to provisioner '{revalidatedPlan.ProvisionerId}', "
                + $"not to '{Id}'. Nothing was sent to Azure.");
        }

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: the resource belongs to provisioner '{handle.ProvisionerId}', not to "
                + $"'{Id}'. Nothing was sent to Azure.");
        }

        if (!IsVirtualMachineId(handle.ProviderResourceId))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: '{handle.ProviderResourceId}' is not the ARM id of a "
                + $"{VirtualMachineResourceType}, so there is no machine to resize. Nothing was sent to Azure.");
        }

        // Guard 3 - the plan must describe a lone resize, and nothing else at all.
        if (!TryReadResizeTarget(revalidatedPlan, out var targetSize, out var refusal))
        {
            return new UpdateExecutionResult.Refused(refusal);
        }

        var resourceId = handle.ProviderResourceId;

        ArmOperationSubmission submission;
        try
        {
            // The first and only mutating request on this path.
            submission = await _api.ResizeVirtualMachineAsync(resourceId, targetSize, ct).ConfigureAwait(false);
        }
        catch (AzureApiException ex)
        {
            // Translated, not swallowed, and carrying Azure's own words - the caller needs to be able to read
            // the provider's reason, not this adapter's paraphrase of it.
            return new UpdateExecutionResult.Failed(
                $"Azure refused the resize of '{resourceId}' to '{targetSize}'. The machine was not changed, and "
                + $"it was not stopped. {ex.Message}");
        }

        ArmOperationPoll poll;
        try
        {
            poll = await _api.PollOperationAsync(submission, ct).ConfigureAwait(false);
        }
        catch (AzureApiException ex)
        {
            // The submission went out and the status read did not come back. That is not a refusal and it is
            // not a confirmed failure of the resize itself, so it says so.
            return new UpdateExecutionResult.Failed(
                $"Azure accepted the resize of '{resourceId}' to '{targetSize}', but its progress could not be "
                + "read, so Servyx cannot say whether the resize ran. Do not resubmit before re-reading the "
                + $"machine's size. {ex.Message}");
        }

        return poll.Outcome switch
        {
            ArmOperationOutcome.Succeeded => await CompletedResizeAsync(handle, resourceId, targetSize, poll, ct)
                .ConfigureAwait(false),
            ArmOperationOutcome.Failed => new UpdateExecutionResult.Failed(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Azure reported the resize of '{resourceId}' to '{targetSize}' as '{poll.StatusText}' after "
                    + $"{poll.Polls} check(s). ")
                + poll.FailureText
                + " The resize did not complete. Re-read the machine's size, and its power state, before retrying:"
                + " a size change that fails partway can leave the machine deallocated."),
            _ => new UpdateExecutionResult.TimedOut(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Azure accepted the resize of '{resourceId}' to '{targetSize}' and was still reporting it as "
                    + $"'{poll.StatusText}' after {poll.Polls} check(s). ")
                + "The resize was NOT confirmed and was NOT reported as failed - it may still be running at Azure "
                + "and may yet complete. Do not resubmit: a second resize is a second mutation of a live machine, "
                + "not a retry. Watch the operation in the Azure portal, or re-read the machine, before acting "
                + "further."),
        };
    }

    /// <summary>
    /// Re-reads the machine after ARM has been observed reporting the resize succeeded, so the caller receives
    /// the machine as it now is.
    /// </summary>
    /// <remarks>
    /// A machine that cannot be re-read at this point is reported as a failure rather than as a success,
    /// because a success carries the resource and there is none to carry. The message says plainly that the
    /// resize itself did complete, so an operator is not sent looking for a resize that never ran.
    /// </remarks>
    private async Task<UpdateExecutionResult> CompletedResizeAsync(
        ResourceHandle handle,
        string resourceId,
        string targetSize,
        ArmOperationPoll poll,
        CancellationToken ct)
    {
        ProvisionedResource? resource;
        try
        {
            resource = await RefreshAsync(handle, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or AzureApiException)
        {
            return new UpdateExecutionResult.Failed(
                $"Azure reported the resize of '{resourceId}' as succeeded, so the machine IS now '{targetSize}' - "
                + "but it could not be read back afterwards, so Servyx cannot describe the machine that now "
                + $"exists. {ex.Message}");
        }

        if (resource is null)
        {
            return new UpdateExecutionResult.Failed(
                $"Azure reported the resize of '{resourceId}' as succeeded, so the machine IS now '{targetSize}' - "
                + "but Azure no longer describes that machine as a Servyx-managed one, so Servyx cannot describe "
                + "the machine that now exists. Reconcile before acting on it.");
        }

        return new UpdateExecutionResult.Completed(
            resource,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Virtual machine '{resourceId}' was resized to '{targetSize}'. Azure reported the operation as "
                + $"succeeded after {poll.Polls} check(s). ")
            + "The request wrote only properties.hardwareProfile.vmSize: the machine keeps its ARM id and stays "
            + "attached to the same managed OS disk, which is a separate ARM resource the request neither named "
            + "nor re-referenced, so every file on the machine is where it was. Azure deallocated and restarted "
            + "the machine to apply the new size, so the workload was interrupted - that is a service "
            + "interruption, not an impact on persistent data - and anything running on it needs to be confirmed "
            + "back up.");
    }

    /// <summary>
    /// Reads the size a plan asks for, or explains why this file will not execute the plan at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately strict: the plan must carry <em>exactly one</em> change, that change must be the
    /// <c>size</c> aspect, and the plan must describe itself as an in-place update that preserves data. A plan
    /// that also writes a tag is refused rather than partly executed, because executing the resize and silently
    /// skipping the tag would report a half-applied update as an applied one.
    /// </para>
    /// <para>
    /// The strategy and data-impact checks are redundant with the change check for every plan this adapter
    /// currently produces, and they are kept anyway: they are the two properties the person approving the plan
    /// actually read, so they are the two this file re-reads before acting. In particular no plan whose
    /// <see cref="DataImpact"/> is anything other than <see cref="DataImpact.Preserved"/> can reach an ARM call
    /// from here, whatever its changes claim — which is what keeps every image change, and every region or
    /// resource-group change, on the refusing side of this method.
    /// </para>
    /// </remarks>
    private static bool TryReadResizeTarget(UpdatePlan plan, out string targetSize, out string refusal)
    {
        targetSize = string.Empty;

        if (plan.Strategy != UpdateStrategy.InPlace)
        {
            refusal =
                $"This update was not applied: the plan's strategy is {plan.Strategy}, and the Azure adapter "
                + "executes only an in-place resize. An image change, a region change and a resource-group change "
                + "all require replacing the machine, which deletes its managed OS disk, and none of them is "
                + "implemented. Nothing was sent to Azure.";
            return false;
        }

        if (plan.DataImpact != DataImpact.Preserved)
        {
            refusal =
                $"This update was not applied: the plan states its impact on persistent data as {plan.DataImpact}, "
                + "and the Azure adapter executes only updates that preserve it. Replacing the machine - the "
                + "operation that changes a VM's image, its region or its resource group - deletes the managed OS "
                + "disk and is deliberately not implemented. Nothing was sent to Azure.";
            return false;
        }

        if (plan.Changes.Count != 1
            || !string.Equals(plan.Changes[0].Aspect, SizeAspect, StringComparison.Ordinal))
        {
            refusal =
                "This update was not applied: the Azure adapter executes a virtual machine resize and nothing "
                + $"else, and this plan describes {plan.Changes.Count} change(s) - "
                + string.Join("; ", plan.Changes.Select(c => c.Description))
                + ". Applying the part it understands and skipping the rest would report a half-applied update as "
                + "an applied one, so nothing was sent to Azure.";
            return false;
        }

        var desired = plan.Changes[0].Desired;
        if (string.IsNullOrWhiteSpace(desired))
        {
            refusal =
                "This update was not applied: the plan's size change names no target size, so there is nothing to "
                + "resize the machine to. Nothing was sent to Azure.";
            return false;
        }

        targetSize = desired;
        refusal = string.Empty;
        return true;
    }
}
