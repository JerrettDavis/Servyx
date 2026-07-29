using System.Globalization;
using System.Net;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Azure.Provisioning;

/// <summary>
/// The <see cref="IDestructiveUpdateApplier"/> half of the VM adapter: the only code in this assembly that
/// deletes a customer's data on purpose.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before widening anything in this file.</strong> ARM has no operation that reimages a
/// virtual machine. <c>properties.storageProfile.imageReference</c> is fixed when the machine is created, so
/// the only route to a different image is to <em>delete this machine and create another</em> — and because
/// this adapter declares <c>deleteOption: Delete</c> on the OS disk at create time (for the reason argued on
/// <c>ArmOsDiskRequest</c>: an implicitly-created managed disk carries no tags and no sweep can ever find it),
/// deleting the machine deletes its managed OS disk with it. The installed game, its configuration and every
/// save file go with the disk and cannot be recovered from Azure afterwards.
/// <strong>No snapshot is taken. This adapter does not claim
/// <see cref="ProvisioningCapabilities.Snapshot"/> and it does not take one here, before, during or after.</strong>
/// </para>
/// <para>
/// <strong>The window this operation has and the DigitalOcean rebuild does not.</strong> A droplet rebuild is
/// one action on one resource: it either happens to the droplet or it does not, and the droplet exists
/// throughout. A replace is <em>two</em> ARM operations, and between them the machine does not exist. That is
/// worse than DigitalOcean's failure mode and it is not engineered away here, because it cannot be: ARM offers
/// no transaction across a delete and a create. What is done about it instead:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>The replacement is assembled in full before anything is deleted.</strong> The live machine is read
/// first, and every field the replacement needs — its size, its location, its tags, its NIC reference, its
/// admin username, its authorised SSH keys and its OS disk tier — is required to be present and is built into
/// the request body <em>before</em> the delete is submitted. A machine that cannot be described completely is
/// refused with nothing sent, so this path never deletes a machine it could not have recreated.
/// </description></item>
/// <item><description>
/// <strong>The network interface and the public IP address survive.</strong> Neither call names them: a VM
/// delete removes the VM, and this adapter deliberately declares no <c>deleteOption</c> on the VM's NIC
/// reference, so the NIC outlives the machine and takes the public address with it — the address is a
/// property of the address resource, which hangs off the NIC and not off the VM. The replacement is created
/// referencing <em>the same NIC id read off the machine being replaced</em>, so the host keeps the address it
/// had. That is the one thing that survives a replace.
/// </description></item>
/// <item><description>
/// <strong>If the create fails after the delete succeeded, the operator is told exactly that</strong>, in
/// those words, by a <see cref="UpdateExecutionResult.Failed"/> whose message states that the machine has
/// already been deleted, that its disk is already gone, that no replacement exists, and that the NIC and the
/// public address are still there and still billing. It is not reported as a refusal and it is not reported
/// as a timeout: nothing is still running, and the situation needs a person.
/// </description></item>
/// </list>
/// <para>
/// <strong>What the ledger says throughout, and why no row is written here.</strong> The replacement is
/// created at <em>the same ARM id</em> as the machine it replaces, carrying the same Servyx tags, read off
/// that machine and sent back in the create body. So there is no new identifier to learn and no untagged
/// window: the write-ahead ledger exists to make a resource whose id Servyx has not yet learned discoverable,
/// and this resource's id was recorded before this operation started. A crash anywhere in the window leaves
/// the existing row exactly where it was, naming an ARM id that now 404s — which is precisely what a sweep
/// needs, and is reported honestly by every read path: <see cref="RefreshAsync"/> answers
/// <see langword="null"/> and <see cref="DetectDriftAsync"/> answers a divergence under <c>existence</c>.
/// Nothing in this file makes Servyx claim a machine exists that does not, and nothing untracked is ever
/// created — the surviving NIC and public address remain tagged and remain sweepable throughout.
/// </para>
/// <para>
/// <strong>Nothing here can resize, and nothing in the resize path can replace.</strong> They are separate
/// interface members with separate implementations building separate request types.
/// <c>AzureVirtualMachineProvisioner.Resize.cs</c> sends a PATCH whose body is an
/// <c>ArmVirtualMachineResizeRequest</c> — one member, whose type has one member, whose type has one string —
/// so no argument anywhere can make a resize name an image. This file sends a DELETE and a PUT, and neither is
/// reachable from that entry point. A region change and a resource-group change remain unexecutable by both:
/// they are named and refused by <see cref="TryReadReplacementTarget"/> before anything is read.
/// </para>
/// <para>
/// <strong>Submission is not success, and here that distinction is sharper than anywhere else in this
/// codebase.</strong> A replace takes minutes. Only an <see cref="ArmOperationOutcome.Succeeded"/> observed on
/// each of the two operations becomes <see cref="UpdateExecutionResult.Completed"/>; an operation still
/// running when the polls are spent is <see cref="UpdateExecutionResult.TimedOut"/>, deliberately a different
/// type from <see cref="UpdateExecutionResult.Failed"/>, and the two messages instruct an operator to do
/// opposite things. Resubmitting a replace whose first attempt is still running would delete the machine the
/// first attempt had just finished creating.
/// </para>
/// <para>
/// <strong>There is no force path.</strong> No argument here skips a guard, and no combination of arguments
/// reaches an ARM call that the checks below would otherwise refuse.
/// </para>
/// </remarks>
public sealed partial class AzureVirtualMachineProvisioner : IDestructiveUpdateApplier
{
    /// <summary>
    /// The <see cref="PlannedChange.Aspect"/> that update planning gives a VM image difference. The one aspect
    /// this file can execute, matched exactly rather than by prefix.
    /// </summary>
    private const string ImageAspect = "image";

    /// <summary>
    /// The <see cref="PlannedChange.Aspect"/> of a region difference, named here only so the refusal for one
    /// can say what is actually wrong rather than counting changes at the operator.
    /// </summary>
    private const string RegionAspect = "region";

    /// <summary>The <see cref="PlannedChange.Aspect"/> of a resource-group difference, named for the same reason.</summary>
    private const string ResourceGroupAspect = "resourceGroup";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Executes an approved replacement and nothing else. The sequence is: check every guard that can be
    /// checked without asking Azure anything (no HTTP at all), read the live machine once, assemble the whole
    /// replacement from what that read returned, delete the machine and poll that operation to a conclusion,
    /// create the replacement at the same ARM id and poll that operation to a conclusion, and — only once ARM
    /// has been observed reporting both succeeded — re-read the machine so the caller is handed what now
    /// exists rather than what was asked for.
    /// </para>
    /// <para>
    /// Every refusal below the read is still a refusal that changed nothing: the read is a GET. Every refusal
    /// above it sent nothing at all, not even the token exchange.
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

        // Guard 1 - the approval must be for this exact plan, checked immediately before the first mutating
        // call with no provider call in between. A stale plan is refused and never executed: the machine the
        // approval described is not necessarily the machine that exists now, and this operation is not
        // undoable.
        if (!string.Equals(revalidatedPlan.PlanHash, approvedPlanHash, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                "This replacement was not carried out because the plan handed to the Azure adapter is not the plan "
                + $"that was approved: the approval names '{approvedPlanHash}' and the plan hashes to "
                + $"'{revalidatedPlan.PlanHash}'. Nothing was sent to Azure, no machine was deleted and no disk was "
                + "erased. Preview again and confirm the plan you are then shown.");
        }

        // Guard 2 - the plan and the resource must both belong to this provisioner. Executing another adapter's
        // plan against an ARM id would delete whichever machine happened to answer to it.
        if (!string.Equals(revalidatedPlan.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This replacement was not carried out: the plan belongs to provisioner "
                + $"'{revalidatedPlan.ProvisionerId}', not to '{Id}'. Nothing was sent to Azure.");
        }

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This replacement was not carried out: the resource belongs to provisioner "
                + $"'{handle.ProvisionerId}', not to '{Id}'. Nothing was sent to Azure.");
        }

        if (!IsVirtualMachineId(handle.ProviderResourceId))
        {
            return new UpdateExecutionResult.Refused(
                $"This replacement was not carried out: '{handle.ProviderResourceId}' is not the ARM id of a "
                + $"{VirtualMachineResourceType}, so there is no machine to replace. Nothing was sent to Azure.");
        }

        // Guard 3 - the second, independent approval. This is the same rule Servyx.Application's
        // DataImpactAcknowledgement enforces, restated here because that token type lives in Application and
        // this assembly references only Servyx.Domain. It is checked in both directions and it is an exact
        // match: an acknowledgement of AtRisk does not authorise a Destroyed plan, an acknowledgement of
        // Preserved authorises nothing at all, and no acknowledgement authorises nothing at all.
        if (revalidatedPlan.DataImpact != DataImpact.Destroyed)
        {
            return new UpdateExecutionResult.Refused(
                $"This replacement was not carried out: the plan states its impact on persistent data as "
                + $"{revalidatedPlan.DataImpact}, and replacing this machine deletes the managed OS disk it is "
                + $"attached to, so only a plan that states {DataImpact.Destroyed} can be executed here. A plan "
                + "claiming anything milder than what would actually happen is a reason to stop, not to proceed. "
                + "Nothing was sent to Azure.");
        }

        if (acknowledgedDataImpact != DataImpact.Destroyed)
        {
            return new UpdateExecutionResult.Refused(
                $"This replacement was not carried out: the plan states its impact on persistent data as "
                + $"{revalidatedPlan.DataImpact} and the acknowledgement supplied was "
                + $"{(acknowledgedDataImpact is null ? "none" : acknowledgedDataImpact.Value.ToString())}. "
                + "Replacing this machine deletes it and the managed OS disk it is attached to, so everything "
                + "stored on it goes; it runs only when someone has separately accepted exactly that - an "
                + $"acknowledgement of {DataImpact.AtRisk}, or none at all, is not an approval of data loss. "
                + "Nothing was sent to Azure and no machine was deleted.");
        }

        // Guard 4 - the plan must describe a lone image change, and nothing else at all.
        if (!TryReadReplacementTarget(revalidatedPlan, out var targetImage, out var refusal))
        {
            return new UpdateExecutionResult.Refused(refusal);
        }

        var resourceId = handle.ProviderResourceId;

        // The one read. Everything the replacement will be built from comes from here, so that the machine
        // that gets created is this machine with its image changed - not a machine reconstructed from a
        // request that was never made in this call.
        ArmVirtualMachine? vm;
        try
        {
            vm = await _api.GetResourceAsync<ArmVirtualMachine>(resourceId, ct).ConfigureAwait(false);
        }
        catch (AzureApiException ex)
        {
            return new UpdateExecutionResult.Failed(
                $"The machine '{resourceId}' could not be read, so Servyx cannot describe the replacement it would "
                + $"have to create and will not delete a machine it cannot recreate. Nothing was deleted. {ex.Message}");
        }

        if (vm is null)
        {
            return new UpdateExecutionResult.Refused(
                $"This replacement was not carried out: Azure no longer has a machine at '{resourceId}', so there is "
                + "nothing to replace. Nothing was deleted and nothing was created. Reconcile before acting further.");
        }

        // Guard 5 - the machine must be describable in full, before anything is deleted. This is the guard that
        // keeps the delete-then-create window survivable: a replacement is only ever started when the request
        // that ends it has already been built.
        if (!TryBuildReplacement(vm, resourceId, targetImage, out var replacement, out var buildRefusal))
        {
            return new UpdateExecutionResult.Refused(buildRefusal);
        }

        return await ExecuteReplacementAsync(handle, resourceId, targetImage, replacement, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The two mutating calls, and the six outcomes they can produce between them.
    /// </summary>
    /// <remarks>
    /// Split out from the guards above so that the reading order of this file matches the risk order: every
    /// line above this method runs without deleting anything, and every line in it runs after a decision to
    /// delete has been fully made.
    /// </remarks>
    private async Task<UpdateExecutionResult> ExecuteReplacementAsync(
        ResourceHandle handle,
        string resourceId,
        string targetImage,
        ArmVirtualMachineRequest replacement,
        CancellationToken ct)
    {
        ArmOperationSubmission? deletion;
        try
        {
            // The first mutating request on this path, and the irreversible one. Everything above ran without
            // touching the network except for one GET.
            deletion = await _api.DeleteVirtualMachineAsync(resourceId, ct).ConfigureAwait(false);
        }
        catch (AzureApiException ex)
        {
            return new UpdateExecutionResult.Failed(
                $"Azure refused the deletion of '{resourceId}'. The machine was NOT deleted, its OS disk was NOT "
                + $"erased, and no replacement was created. Nothing about the host has changed. {ex.Message}");
        }

        if (deletion is null)
        {
            // ARM 404'd the delete, having answered the read moments earlier. Something else is acting on this
            // machine, and creating a machine at an id Servyx no longer knows the state of is not a replace.
            return new UpdateExecutionResult.Failed(
                $"Azure answered the deletion of '{resourceId}' with 404: the machine was there when Servyx read it "
                + "and was gone by the time Servyx asked for it to be deleted, so something outside Servyx is acting "
                + "on it. No replacement was created - creating one now would not be replacing the machine that was "
                + "approved for replacement. Reconcile before acting further.");
        }

        ArmOperationPoll deletionPoll;
        try
        {
            deletionPoll = await _api.PollOperationAsync(deletion, ct).ConfigureAwait(false);
        }
        catch (AzureApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // ARM named no operation to watch and the fallback read of the resource 404'd. For a delete that is
            // the terminal success: the machine is gone. Observed, not assumed.
            deletionPoll = new ArmOperationPoll(ArmOperationOutcome.Succeeded, "Succeeded", null, Polls: 1);
        }
        catch (AzureApiException ex)
        {
            return new UpdateExecutionResult.Failed(
                $"Azure accepted the deletion of '{resourceId}' but its progress could not be read, so Servyx cannot "
                + "say whether the machine still exists. No replacement has been created. Do NOT resubmit before "
                + $"re-reading the machine: if the delete did run, its OS disk is already gone. {ex.Message}");
        }

        if (deletionPoll.Outcome == ArmOperationOutcome.Failed)
        {
            return new UpdateExecutionResult.Failed(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Azure reported the deletion of '{resourceId}' as '{deletionPoll.StatusText}' after "
                    + $"{deletionPoll.Polls} check(s). ")
                + deletionPoll.FailureText
                + " No replacement was created. The delete was accepted before it failed, so the machine may be in a"
                + " partly-deleted state and its OS disk may or may not still exist - read the machine before acting"
                + " further, and do not assume its contents survived.");
        }

        if (deletionPoll.Outcome != ArmOperationOutcome.Succeeded)
        {
            return new UpdateExecutionResult.TimedOut(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Azure accepted the deletion of '{resourceId}' and was still reporting it as "
                    + $"'{deletionPoll.StatusText}' after {deletionPoll.Polls} check(s). ")
                + "The delete was NOT confirmed and was NOT reported as failed - deleting a machine takes minutes and "
                + "this one is most likely still running at Azure. No replacement has been created, and Servyx will "
                + "not create one against a machine it has not seen disappear. That is a different situation from a "
                + "failure and calls for the opposite response: do NOT resubmit, because a second replace submitted "
                + "while the first is still running would delete the machine the first one is about to create. Watch "
                + "the operation in the Azure portal, or re-read the machine, before acting further.");
        }

        // From here the machine is gone and so is its OS disk. Every remaining branch says so out loud, because
        // from here there is no outcome in which the previous machine's contents still exist.
        ArmOperationSubmission creation;
        try
        {
            creation = await _api.CreateVirtualMachineAsync(resourceId, replacement, ct).ConfigureAwait(false);
        }
        catch (AzureApiException ex)
        {
            return new UpdateExecutionResult.Failed(GoneWithNoReplacement(resourceId, targetImage, ex.Message));
        }

        ArmOperationPoll creationPoll;
        try
        {
            creationPoll = await _api.PollOperationAsync(creation, ct).ConfigureAwait(false);
        }
        catch (AzureApiException ex)
        {
            return new UpdateExecutionResult.Failed(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The machine '{resourceId}' HAS BEEN DELETED and its managed OS disk was deleted with it, so "
                    + $"everything stored on it is gone. Azure then accepted the creation of the replacement from "
                    + $"image '{targetImage}', but its progress could not be read, so Servyx cannot say whether a "
                    + $"machine exists at that id now. Read the machine before acting further; do not resubmit, "
                    + $"because a replace submitted now may delete a replacement that did come up. {ex.Message}"));
        }

        return creationPoll.Outcome switch
        {
            ArmOperationOutcome.Succeeded => await CompletedReplacementAsync(
                handle, resourceId, targetImage, deletionPoll, creationPoll, ct).ConfigureAwait(false),
            ArmOperationOutcome.Failed => new UpdateExecutionResult.Failed(
                GoneWithNoReplacement(
                    resourceId,
                    targetImage,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Azure reported the creation as '{creationPoll.StatusText}' after {creationPoll.Polls} "
                        + $"check(s). ")
                    + creationPoll.FailureText)),
            _ => new UpdateExecutionResult.TimedOut(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The machine '{resourceId}' HAS BEEN DELETED and its managed OS disk was deleted with it, so "
                    + $"everything stored on it is gone. Azure accepted the creation of the replacement from image "
                    + $"'{targetImage}' and was still reporting it as '{creationPoll.StatusText}' after "
                    + $"{creationPoll.Polls} check(s). ")
                + "The replacement was NOT confirmed and was NOT reported as failed - creating a machine takes "
                + "minutes and this one is most likely still coming up and may yet succeed. That is a different "
                + "situation from a failure and calls for the opposite response: do NOT resubmit, because a second "
                + "replace would delete the replacement this one is in the middle of creating, along with anything "
                + "already restored onto it. Watch the operation in the Azure portal, or re-read the machine, before "
                + "acting further. The network interface and the public IP address were never deleted, so the host's "
                + "address is unchanged."),
        };
    }

    /// <summary>
    /// The message for every outcome in which the machine is gone and no replacement stands in its place.
    /// </summary>
    /// <remarks>
    /// Written once and used by both such branches so the two cannot drift apart. It is deliberately blunt
    /// about the order of events: an operator reading it needs to know first that the machine is gone, and
    /// second that nothing replaced it — the reverse order would let "the create failed" be read as "so
    /// nothing happened".
    /// </remarks>
    private static string GoneWithNoReplacement(string resourceId, string targetImage, string detail) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The machine '{resourceId}' HAS BEEN DELETED and its managed OS disk was deleted with it, so the "
            + $"installed game, its configuration and every save file are gone and cannot be recovered. The "
            + $"replacement from image '{targetImage}' was then NOT created, so there is no machine at that id now. ")
        + detail
        + " This is not a failure that left things as they were: the delete happened and the create did not. The "
        + "network interface and the public IP address were never named by either call and are still there - still "
        + "carrying their Servyx tags, still billing, and still holding the host's address - so a retry can create a "
        + "machine at the same id that keeps that address. Do this deliberately, after reading the resource group.";

    /// <summary>
    /// Re-reads the machine after ARM has been observed reporting both operations succeeded, so the caller
    /// receives the machine as it now is.
    /// </summary>
    /// <remarks>
    /// A machine that cannot be re-read at this point is reported as a failure rather than as a success,
    /// because a success carries the resource and there is none to carry. The message says plainly that the
    /// replacement itself did happen and that the previous machine's disk is therefore already gone, so nobody
    /// reads a missing resource as evidence that the destruction did not occur.
    /// </remarks>
    private async Task<UpdateExecutionResult> CompletedReplacementAsync(
        ResourceHandle handle,
        string resourceId,
        string targetImage,
        ArmOperationPoll deletionPoll,
        ArmOperationPoll creationPoll,
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
                $"Azure reported both halves of the replacement of '{resourceId}' as succeeded, so the previous "
                + $"machine and its managed OS disk ARE gone and a replacement from image '{targetImage}' does "
                + "exist - but it could not be read back afterwards, so Servyx cannot describe the machine that now "
                + $"exists. {ex.Message}");
        }

        if (resource is null)
        {
            return new UpdateExecutionResult.Failed(
                $"Azure reported both halves of the replacement of '{resourceId}' as succeeded, so the previous "
                + $"machine and its managed OS disk ARE gone - but Azure no longer describes a Servyx-managed "
                + "machine at that id, so Servyx cannot describe what now exists. Reconcile before acting on it.");
        }

        return new UpdateExecutionResult.Completed(
            resource,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Virtual machine '{resourceId}' was replaced so that it now runs image '{targetImage}'. ARM has no "
                + $"operation that reimages a machine, so this was a delete and a create: Azure reported the delete "
                + $"as succeeded after {deletionPoll.Polls} check(s) and the create as succeeded after "
                + $"{creationPoll.Polls} check(s). ")
            + "The previous machine's managed OS disk was deleted with it: everything the machine held before this "
            + "ran - the installed game, its configuration, and every save file - is gone and cannot be recovered. "
            + "No snapshot was taken, before or after. The replacement boots from a fresh copy of the image and the "
            + "game has to be installed and restored again. What survived: the machine keeps its ARM id, its name, "
            + "its size, its tags and its network interface, and because the network interface and the public IP "
            + "address were never deleted the host keeps the address it had. What did not survive, besides the disk: "
            + "any cloud-init the original machine was created with, because ARM does not return customData on a "
            + "read and it therefore cannot be carried across.");
    }

    /// <summary>
    /// Reads the image a plan asks for, or explains why this file will not execute the plan at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately strict, and strict in the same shape as the resize path: the plan must describe itself as
    /// a <see cref="UpdateStrategy.Recreate"/>, must carry <em>exactly one</em> change, and that change must
    /// be the <c>image</c> aspect. A replacement bundled with a resize or a retag is refused rather than
    /// partly executed, because executing the part this file understands and skipping the rest would report a
    /// half-applied update as an applied one — and the part it understands is the irreversible one.
    /// </para>
    /// <para>
    /// A region change and a resource-group change are named explicitly, ahead of the general count check that
    /// would also catch them, so the refusal an operator reads says what is actually true: an ARM resource's
    /// location is immutable and its resource group is part of its identity, so neither is reachable by
    /// replacing the machine <em>at this id</em> — which is the only thing this file can do.
    /// </para>
    /// <para>
    /// The target is parsed as a four-part Azure image URN here, before anything is deleted, for the same
    /// reason the create path parses it before anything is created: a malformed URN discovered after the
    /// delete would be discovered with the machine already gone.
    /// </para>
    /// </remarks>
    private static bool TryReadReplacementTarget(UpdatePlan plan, out string targetImage, out string refusal)
    {
        targetImage = string.Empty;

        if (plan.Changes.Any(c => string.Equals(c.Aspect, RegionAspect, StringComparison.Ordinal)))
        {
            refusal =
                "This replacement was not carried out: the plan changes the machine's region, and an ARM resource's "
                + "location is immutable. There is no Azure operation that moves a virtual machine to another "
                + "region, and replacing it at this ARM id would delete the machine and its disk without moving "
                + "anything anywhere. Nothing was sent to Azure.";
            return false;
        }

        if (plan.Changes.Any(c => string.Equals(c.Aspect, ResourceGroupAspect, StringComparison.Ordinal)))
        {
            refusal =
                "This replacement was not carried out: the plan changes the machine's resource group, and the group "
                + "is part of a resource's ARM id, so a different group names a different resource rather than a "
                + "changed one. Replacing the machine at this ARM id would leave it exactly where it is. Nothing was "
                + "sent to Azure.";
            return false;
        }

        if (plan.Strategy != UpdateStrategy.Recreate)
        {
            refusal =
                $"This replacement was not carried out: the plan's strategy is {plan.Strategy}, and this entry point "
                + "executes only a replacement - the delete-and-create ARM requires for an image change. A plan that "
                + "does not describe itself as a replacement must not be executed as one. Nothing was sent to Azure.";
            return false;
        }

        if (plan.Changes.Count != 1
            || !string.Equals(plan.Changes[0].Aspect, ImageAspect, StringComparison.Ordinal))
        {
            refusal =
                "This replacement was not carried out: the Azure adapter executes a virtual machine image "
                + $"replacement and nothing else, and this plan describes {plan.Changes.Count} change(s) - "
                + string.Join("; ", plan.Changes.Select(c => c.Description))
                + ". Applying the part it understands and skipping the rest would report a half-applied update as an "
                + "applied one, and the part it understands is the one that cannot be undone, so nothing was sent to "
                + "Azure.";
            return false;
        }

        var desired = plan.Changes[0].Desired;
        if (string.IsNullOrWhiteSpace(desired))
        {
            refusal =
                "This replacement was not carried out: the plan's image change names no target image, so there is "
                + "nothing to create the replacement from. Nothing was sent to Azure.";
            return false;
        }

        try
        {
            _ = AzureVirtualMachineSpec.ParseImageUrn(desired);
        }
        catch (ArgumentException ex)
        {
            refusal =
                "This replacement was not carried out: the plan's target image is not a four-part Azure image URN, "
                + "so ARM could not create a machine from it - and discovering that after the delete would mean "
                + $"discovering it with the machine already gone. Nothing was sent to Azure. {ex.Message}";
            return false;
        }

        targetImage = desired;
        refusal = string.Empty;
        return true;
    }

    /// <summary>
    /// Assembles the replacement's create request from the live machine, or explains what about the live
    /// machine makes a faithful replacement impossible to describe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This runs before the delete, and that ordering is the safety property.</strong> Every field is
    /// required rather than defaulted: a missing SSH key would produce a machine nobody can log in to, a
    /// missing NIC reference would produce a machine with no address, and a missing disk tier would silently
    /// change the machine's storage performance. Guessing any of them would mean deleting a working machine in
    /// order to create a different one, so each absence is a refusal made while the machine is still there.
    /// </para>
    /// <para>
    /// <strong>What is carried across, and where each value comes from.</strong> All of it comes from the live
    /// machine — not from a spec, not from a <see cref="ProvisioningRequest"/>, and not from the plan, which
    /// describes a difference rather than a machine. The image reference is the single field the plan
    /// contributes, and it is the single field that differs from what was read.
    /// </para>
    /// <para>
    /// <strong>What cannot be carried across, stated rather than hidden.</strong> ARM does not return
    /// <c>osProfile.customData</c> on a read, so cloud-init supplied when the original machine was created is
    /// not reapplied to the replacement. This adapter never authors cloud-init and never has, so nothing here
    /// invents one; the completed message says the omission out loud.
    /// </para>
    /// </remarks>
    private static bool TryBuildReplacement(
        ArmVirtualMachine vm,
        string resourceId,
        string targetImage,
        out ArmVirtualMachineRequest replacement,
        out string refusal)
    {
        replacement = null!;

        var tags = ServyxAzureTags.FromArmTags(vm.Tags);
        if (!ServyxAzureTags.IsManaged(tags))
        {
            refusal =
                $"This replacement was not carried out: the machine at '{resourceId}' does not carry the Servyx "
                + "management tag, so Servyx will not delete it. Nothing was deleted and nothing was created.";
            return false;
        }

        var location = NullIfBlank(vm.Location);
        var size = NullIfBlank(vm.Properties?.HardwareProfile?.VmSize);
        var liveImage = LiveImageUrn(vm);
        var osDisk = vm.Properties?.StorageProfile?.OsDisk;
        var diskTier = NullIfBlank(osDisk?.ManagedDisk?.StorageAccountType);
        var osProfile = vm.Properties?.OsProfile;
        var adminUsername = NullIfBlank(osProfile?.AdminUsername);
        var computerName = NullIfBlank(osProfile?.ComputerName) ?? NullIfBlank(vm.Name);

        var keys = (osProfile?.LinuxConfiguration?.Ssh?.PublicKeys ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k.KeyData))
            .ToList();

        var nics = (vm.Properties?.NetworkProfile?.NetworkInterfaces ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n.Id))
            .Select(n => n.Id!)
            .ToList();

        if (string.Equals(liveImage, targetImage, StringComparison.OrdinalIgnoreCase))
        {
            refusal =
                $"This replacement was not carried out: the machine at '{resourceId}' already runs image "
                + $"'{targetImage}', so replacing it would delete a machine and its disk in order to arrive back "
                + "where it started. Nothing was deleted and nothing was created.";
            return false;
        }

        var missing = new List<string>();
        if (location is null)
        {
            missing.Add("its location");
        }

        if (size is null)
        {
            missing.Add("its size (properties.hardwareProfile.vmSize)");
        }

        if (diskTier is null)
        {
            missing.Add("its OS disk tier (properties.storageProfile.osDisk.managedDisk.storageAccountType)");
        }

        if (adminUsername is null)
        {
            missing.Add("its admin username (properties.osProfile.adminUsername)");
        }

        if (computerName is null)
        {
            missing.Add("its computer name (properties.osProfile.computerName)");
        }

        if (keys.Count == 0)
        {
            missing.Add(
                "any authorised SSH public key (properties.osProfile.linuxConfiguration.ssh.publicKeys); this "
                + "adapter disables password authentication, so a replacement created without one would be a "
                + "machine nobody can log in to");
        }

        if (nics.Count == 0)
        {
            missing.Add(
                "any network interface (properties.networkProfile.networkInterfaces); a replacement created "
                + "without one would have no address, and the surviving address is the one thing a replacement "
                + "keeps");
        }

        if (missing.Count > 0)
        {
            refusal =
                $"This replacement was not carried out: Azure's description of the machine at '{resourceId}' does "
                + "not include " + string.Join("; ", missing)
                + ". Servyx builds a replacement out of the machine it is replacing and will not guess a field, "
                + "because guessing one means deleting a working machine in order to create a different one. "
                + "Nothing was deleted and nothing was created.";
            return false;
        }

        var image = AzureVirtualMachineSpec.ParseImageUrn(targetImage);

        replacement = new ArmVirtualMachineRequest
        {
            Location = location!,
            Tags = ServyxAzureTags.Validate(tags),
            Properties = new ArmVirtualMachineRequestProperties
            {
                HardwareProfile = new ArmHardwareProfileRequest { VmSize = size! },
                StorageProfile = new ArmStorageProfileRequest
                {
                    // The one field the plan contributes, and the only difference from what was read back.
                    ImageReference = new ArmImageReference
                    {
                        Publisher = image.Publisher,
                        Offer = image.Offer,
                        Sku = image.Sku,
                        Version = image.Version,
                    },
                    OsDisk = new ArmOsDiskRequest
                    {
                        CreateOption = "FromImage",

                        // Re-declared rather than copied from the live machine on purpose. This is the cascade
                        // that makes a later teardown able to remove the disk at all - an implicitly-created
                        // managed disk carries no tags and no sweep can find it - so a replacement that
                        // inherited a 'Detach' would quietly create the unsweepable orphan the create path
                        // exists to prevent. It also matches what the plan's stages told the approver.
                        DeleteOption = OsDiskDeleteOption,
                        ManagedDisk = new ArmManagedDiskRequest { StorageAccountType = diskTier! },
                    },
                },
                OsProfile = new ArmOsProfileRequest
                {
                    ComputerName = computerName!,
                    AdminUsername = adminUsername!,

                    // ARM does not return customData on a read, so there is nothing to carry across and nothing
                    // is invented. Stated in the completed message rather than left for someone to discover.
                    CustomData = null,
                    LinuxConfiguration = new ArmLinuxConfigurationRequest
                    {
                        // Asserted rather than copied, for the same reason the delete option is: this adapter's
                        // machines authenticate by key, and a replacement that enabled password login because a
                        // read did not mention the field would be a weaker machine than the one it replaced.
                        DisablePasswordAuthentication = true,
                        Ssh = new ArmSshConfigurationRequest
                        {
                            PublicKeys = keys
                                .Select(k => new ArmSshPublicKeyRequest
                                {
                                    Path = NullIfBlank(k.Path) ?? $"/home/{adminUsername}/.ssh/authorized_keys",
                                    KeyData = k.KeyData!,
                                })
                                .ToList(),
                        },
                    },
                },
                NetworkProfile = new ArmNetworkProfileRequest
                {
                    // The same NIC ids the machine being replaced referenced, so the replacement comes up on the
                    // same interface and therefore at the same public address. Nothing here creates or deletes a
                    // network interface.
                    NetworkInterfaces = nics
                        .Select((id, index) => new ArmNetworkInterfaceReference
                        {
                            Id = id,
                            Properties = new ArmNetworkInterfaceReferenceProperties { Primary = index == 0 },
                        })
                        .ToList(),
                },
            },
        };

        refusal = string.Empty;
        return true;
    }
}
