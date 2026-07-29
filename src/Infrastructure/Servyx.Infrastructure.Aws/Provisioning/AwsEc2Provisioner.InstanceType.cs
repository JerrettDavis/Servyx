using System.Globalization;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// The <see cref="IUpdateApplier"/> half of the EC2 adapter: the only code in this assembly that changes an
/// instance which already exists.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before widening anything in this file.</strong> An <see cref="UpdatePlan"/> produced by
/// <c>AwsEc2Provisioner.Maintenance.cs</c> can describe four differences, and they are not equally recoverable:
/// an instance-type change acts on the instance that already exists, an image change is a
/// <em>terminate and launch</em> whose cost to the data is decided by a <c>DeleteOnTermination</c> flag this
/// adapter never set, a region change is not an operation at all, and a tag change is a <c>CreateTags</c> call
/// this file does not make. This file implements the first one. It does not implement the other three — every
/// plan describing anything other than a lone instance-type change is refused here, with
/// <see cref="UpdateExecutionResult.Refused"/> and without a single mutating request.
/// </para>
/// <para>
/// <strong>Refusing is not a gap to be filled in later by loosening the checks.</strong> The replacement is
/// absent because terminating a customer's instance — and, depending on a flag read off the machine rather than
/// chosen by Servyx, deleting the EBS volume their saves live on — deserves its own reviewed change, and until
/// that review happens the honest state of this adapter is that it cannot do it. A future change that adds
/// replacement must add it as its own operation with its own tests, not by relaxing
/// <see cref="TryReadTypeTarget"/> until a destructive plan slips through.
/// </para>
/// <para>
/// <strong>Every guard except the last runs before any HTTP.</strong> A plan refused by one of those is a
/// statement about EC2's state and not merely about this process's: nothing was sent, no signature was
/// computed, and the key pair was not even resolved, so nothing can have half-run. The one guard that needs the
/// network is the last, and it is a <em>read</em> — see below.
/// </para>
/// <para>
/// <strong>The data-preservation claim is enumerated from the live machine, not carried in the plan.</strong>
/// Before anything is stopped, this file re-reads the instance and looks at its block device mapping. An
/// instance reporting no EBS volume at all is instance-store backed: EC2 cannot stop it — which is the
/// precondition <c>ModifyInstanceAttribute</c> needs — and its storage does not survive a stop in any case, so
/// such a plan is refused however reassuring its <see cref="DataImpact"/> claims to be. The volumes named in the
/// completed message are the ones read back off the instance <em>after</em> the whole cycle, so "the data
/// survived" is an observation rather than a restatement of the plan.
/// </para>
/// <para>
/// <strong>Three calls, and the middle of them leaves the machine deliberately powered off.</strong> This is
/// the structural difference from both sibling adapters, and the whole reason this file has more outcomes than
/// theirs. A droplet resize is one action; an Azure resize is one PATCH. EC2 refuses to write the
/// <c>instanceType</c> attribute of a running instance and offers no live equivalent, so the sequence is
/// <c>StopInstances</c>, <c>ModifyInstanceAttribute</c>, <c>StartInstances</c>. A failure after the stop leaves
/// the server <strong>down but intact</strong> — every file where it was, and the workload offline — which is a
/// materially different situation from a droplet resize that failed and left the machine running. So it is
/// reported as its own outcome, naming which of the three steps ran, rather than as a generic failure that
/// leaves an operator to guess whether their server is up.
/// </para>
/// <para>
/// <strong>Submission is not success, at every step.</strong> <c>StopInstances</c> answers with the instance in
/// <c>stopping</c> and <c>StartInstances</c> answers with it in <c>pending</c>; neither response describes a
/// finished operation, and a modify submitted against an instance that had not actually stopped would simply be
/// refused. So each step is polled to an observed conclusion before the next one is issued, and the attribute
/// write itself is confirmed by reading the instance's type back rather than by trusting the <c>200</c>. A step
/// still unfinished when the polls are spent is <see cref="UpdateExecutionResult.TimedOut"/>, deliberately not
/// <see cref="UpdateExecutionResult.Failed"/> and deliberately not a success.
/// </para>
/// <para>
/// <strong>There is no force path.</strong> No argument here skips a guard. In particular <c>StopInstances</c>
/// accepts a <c>Force</c> parameter that skips the guest's own shutdown — AWS's own documentation warns it can
/// corrupt the filesystem — and it is not a parameter of
/// <see cref="Ec2QueryApiClient.StopInstanceAsync"/>, so there is no value any caller could pass that would
/// produce one.
/// </para>
/// </remarks>
public sealed partial class AwsEc2Provisioner : IUpdateApplier
{
    /// <summary>
    /// The <see cref="PlannedChange.Aspect"/> that update planning gives an instance-type difference. The one
    /// aspect this file can execute, matched exactly rather than by prefix.
    /// </summary>
    private const string SizeAspect = "size";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Executes an approved instance-type change and nothing else. The sequence is: check every guard that can
    /// be checked without the network, read the instance once to confirm what is about to be stopped and what
    /// is attached to it, then stop / modify / start, polling each step to a conclusion before the next is
    /// issued — and only once EC2 has been observed reporting the instance <c>running</c> again, re-read it so
    /// the caller is handed the machine that now exists rather than the one that was asked for.
    /// </para>
    /// <para>
    /// An EC2 refusal of any of the three submissions surfaces as <see cref="UpdateExecutionResult.Failed"/>
    /// carrying AWS's own error text <em>and</em> naming which steps had already run, because after the stop
    /// those two facts are the difference between "nothing happened" and "your server is off".
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

        // Guard 1 - the approval must be for this exact plan, checked immediately before anything else. The
        // dashboard already compared these two, so reaching here with a mismatch means a caller skipped that
        // step; the answer is still "nothing is sent" rather than "the caller above is trusted".
        if (!string.Equals(revalidatedPlan.PlanHash, approvedPlanHash, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                "This update was not applied because the plan handed to the AWS EC2 adapter is not the plan that "
                + $"was approved: the approval names '{approvedPlanHash}' and the plan hashes to "
                + $"'{revalidatedPlan.PlanHash}'. Nothing was sent to EC2 and no instance was stopped. Preview "
                + "again and confirm the plan you are then shown.");
        }

        // Guard 2 - the plan and the resource must both belong to this provisioner. Executing another adapter's
        // plan against an instance id would stop whichever machine happened to answer to it.
        if (!string.Equals(revalidatedPlan.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: the plan belongs to provisioner '{revalidatedPlan.ProvisionerId}', "
                + $"not to '{Id}'. Nothing was sent to EC2.");
        }

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: the resource belongs to provisioner '{handle.ProvisionerId}', not "
                + $"to '{Id}'. Nothing was sent to EC2.");
        }

        if (!IsInstanceId(handle.ProviderResourceId))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: '{handle.ProviderResourceId}' is not an EC2 instance id ('i-...'), "
                + "so there is no instance to change the type of. An EBS volume id is not an instance id, and this "
                + "adapter will not guess which machine was meant. Nothing was sent to EC2.");
        }

        // Guard 3 - the plan must describe a lone instance-type change, and nothing else at all. This is the
        // guard that keeps every image change - the terminate-and-launch this adapter does not implement - on
        // the refusing side, and it runs before any request.
        if (!TryReadTypeTarget(revalidatedPlan, out var targetType, out var refusal))
        {
            return new UpdateExecutionResult.Refused(refusal);
        }

        var instanceId = handle.ProviderResourceId;

        // Guard 4 - the only guard that touches the network, and it touches it with a GET. What is about to be
        // stopped has to be described before it is stopped: the plan's Preserved claim is re-derived here from
        // the live block device mapping rather than taken on trust.
        Ec2Instance? instance;
        try
        {
            instance = await _api.DescribeInstanceAsync(instanceId, ct).ConfigureAwait(false);
        }
        catch (AwsApiException ex)
        {
            return new UpdateExecutionResult.Failed(
                $"Instance {instanceId} could not be read, so Servyx cannot say which EBS volumes would have to "
                + "survive the stop/start and will not stop a machine it cannot describe. Nothing was changed "
                + $"and the instance was NOT stopped. {ex.Message}");
        }

        if (instance is null || instance.IsGone)
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: EC2 no longer has instance {instanceId} "
                + (instance is null
                    ? "(it answered InvalidInstanceID.NotFound), "
                    : $"(it reports the state '{instance.State}'), ")
                + "so there is no instance to change the type of. Nothing was stopped. Reconcile before acting "
                + "further.");
        }

        if (instance.BlockDevices.Count == 0)
        {
            // The plan said Preserved; the live machine cannot support that, and the live machine wins. An
            // instance-store backed instance cannot be stopped at all, so the resize's own precondition fails
            // before its data claim even matters.
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: instance {instanceId} reports no EBS volume at all, so it is "
                + "instance-store backed. EC2 cannot stop such an instance - which is the precondition "
                + "ModifyInstanceAttribute needs before it will write the instance type - and its storage does "
                + "not survive a stop in any case. The claim that this update preserves the machine's data has "
                + "to be enumerated from the live block device mapping, and there is nothing there to "
                + "enumerate. Nothing was stopped.");
        }

        return await ExecuteTypeChangeAsync(handle, instance, targetType, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The three mutating calls, and the outcomes they can produce between them.
    /// </summary>
    /// <remarks>
    /// Split out from the guards above so that the reading order of this file matches the risk order: every
    /// line above this method runs without changing anything, and every line in it runs after the decision to
    /// power the machine down has been fully made. The steps are strictly ordered and each is <em>observed</em>
    /// before the next is issued, because a modify submitted against an instance that has not finished stopping
    /// is refused by EC2 and a start submitted against one that has not been modified would silently bring the
    /// machine back on the old type.
    /// </remarks>
    private async Task<UpdateExecutionResult> ExecuteTypeChangeAsync(
        ResourceHandle handle,
        Ec2Instance instance,
        string targetType,
        CancellationToken ct)
    {
        var instanceId = instance.InstanceId;
        var liveType = instance.InstanceType ?? "(unknown)";
        var volumes = DescribeVolumes(instance);

        // ---- Step 1 of 3: stop. The first mutating request on this path. ----
        try
        {
            await _api.StopInstanceAsync(instanceId, ct).ConfigureAwait(false);
        }
        catch (AwsApiException ex)
        {
            return new UpdateExecutionResult.Failed(
                $"AWS refused the request to stop instance {instanceId}, which is the first of the three steps "
                + "an instance-type change needs. The instance was NOT stopped, its type was NOT changed and "
                + $"it is still '{liveType}'. Nothing about the machine changed and the workload was not "
                + $"interrupted. {ex.Message}");
        }

        Ec2InstancePoll stop;
        try
        {
            stop = await PollForStateAsync(instanceId, Ec2QueryApiClient.StoppedState, ct).ConfigureAwait(false);
        }
        catch (AwsApiException ex)
        {
            return new UpdateExecutionResult.Failed(
                $"EC2 accepted the stop of instance {instanceId}, and its progress could not then be read, so "
                + "Servyx cannot say whether the machine stopped. The type change was NOT made - "
                + "ModifyInstanceAttribute was never called - so the instance is still "
                + $"'{liveType}', but it may be stopping or already stopped and the workload may therefore be "
                + $"DOWN. Re-read the instance's state before acting further. {ex.Message}");
        }

        if (stop.Outcome == Ec2PollOutcome.Gone)
        {
            return new UpdateExecutionResult.Failed(
                $"EC2 accepted the stop of instance {instanceId} and then reported the instance as "
                + $"'{stop.State ?? "gone"}' - it is being terminated or has been, which a stop does not do. "
                + "The type change was NOT made. Something outside this update is destroying the machine; "
                + $"{volumes} may or may not survive it, depending on DeleteOnTermination. Reconcile before "
                + "acting further.");
        }

        if (stop.Outcome != Ec2PollOutcome.Satisfied)
        {
            return new UpdateExecutionResult.TimedOut(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"EC2 accepted the stop of instance {instanceId} and was still reporting it as "
                    + $"'{stop.State ?? "(no state)"}' after {stop.Polls} check(s). ")
                + string.Create(
                    CultureInfo.InvariantCulture,
                    $"The instance type was NOT changed: ModifyInstanceAttribute requires a stopped instance and was never called, so the instance is still '{liveType}'. ")
                + "The machine is on its way DOWN, or already down, so the workload is or is about to be offline - "
                + "that is the cost of a step that was submitted and not observed finishing. No data was written "
                + "to: "
                + volumes
                + " are still attached, because a stop is not a terminate. Do not resubmit this update. Re-read "
                + "the instance's state; if you want the workload back on the old type, start the instance, and if "
                + "you still want the new type, retry once EC2 reports it stopped.");
        }

        // ---- Step 2 of 3: the attribute write. Legal only now, because the stop was observed. ----
        bool accepted;
        try
        {
            accepted = await _api.ModifyInstanceTypeAsync(instanceId, targetType, ct).ConfigureAwait(false);
        }
        catch (AwsApiException ex)
        {
            return new UpdateExecutionResult.Failed(StoppedButNotResized(instanceId, liveType, targetType, volumes, ex.Message));
        }

        if (!accepted)
        {
            return new UpdateExecutionResult.Failed(StoppedButNotResized(
                instanceId,
                liveType,
                targetType,
                volumes,
                "EC2 answered the ModifyInstanceAttribute request with 'return: false', which is a refusal that "
                + "arrived with an HTTP 200 rather than an error."));
        }

        // The attribute write is confirmed by reading the type back, not by trusting the 200 that acknowledged
        // it. This is the same rule the stop and the start follow, applied to the one step whose API response
        // looks synchronous.
        Ec2InstancePoll modified;
        try
        {
            modified = await PollForTypeAsync(instanceId, targetType, ct).ConfigureAwait(false);
        }
        catch (AwsApiException ex)
        {
            return new UpdateExecutionResult.Failed(StoppedButNotResized(
                instanceId,
                liveType,
                targetType,
                volumes,
                "EC2 accepted the ModifyInstanceAttribute request, and the instance could not then be read back, so "
                + "Servyx cannot confirm the type was written. " + ex.Message));
        }

        if (modified.Outcome != Ec2PollOutcome.Satisfied)
        {
            return new UpdateExecutionResult.Failed(StoppedButNotResized(
                instanceId,
                liveType,
                targetType,
                volumes,
                modified.Outcome == Ec2PollOutcome.Gone
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"EC2 accepted the ModifyInstanceAttribute request and then reported the instance as '{modified.State ?? "gone"}'.")
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"EC2 accepted the ModifyInstanceAttribute request but was still reporting the instance type as something other than '{targetType}' after {modified.Polls} check(s), so the write cannot be said to have taken effect.")));
        }

        // ---- Step 3 of 3: start. From here on the type change HAS happened. ----
        try
        {
            await _api.StartInstanceAsync(instanceId, ct).ConfigureAwait(false);
        }
        catch (AwsApiException ex)
        {
            return new UpdateExecutionResult.Failed(ResizedButNotStarted(
                instanceId,
                liveType,
                targetType,
                volumes,
                "AWS refused the request to start it. " + ex.Message,
                retryTheStart: true));
        }

        Ec2InstancePoll start;
        try
        {
            start = await PollForStateAsync(instanceId, Ec2QueryApiClient.RunningState, ct).ConfigureAwait(false);
        }
        catch (AwsApiException ex)
        {
            return new UpdateExecutionResult.TimedOut(ResizedButNotStarted(
                instanceId,
                liveType,
                targetType,
                volumes,
                "EC2 accepted the start and its progress could not then be read, so Servyx cannot say whether the "
                + "machine came back up. " + ex.Message,
                retryTheStart: false));
        }

        if (start.Outcome == Ec2PollOutcome.Gone)
        {
            return new UpdateExecutionResult.Failed(ResizedButNotStarted(
                instanceId,
                liveType,
                targetType,
                volumes,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"EC2 accepted the start and then reported the instance as '{start.State ?? "gone"}' - it is being terminated or has been, which a start does not do. Something outside this update is destroying the machine."),
                retryTheStart: false));
        }

        if (start.Outcome != Ec2PollOutcome.Satisfied)
        {
            return new UpdateExecutionResult.TimedOut(ResizedButNotStarted(
                instanceId,
                liveType,
                targetType,
                volumes,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"EC2 accepted the start and was still reporting the instance as '{start.State ?? "(no state)"}' after {start.Polls} check(s), so it was never observed running."),
                retryTheStart: false));
        }

        return await CompletedTypeChangeAsync(handle, start.Instance!, liveType, targetType, start.Polls, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads the instance after EC2 has been observed reporting it running again, so the caller receives the
    /// machine as it now is.
    /// </summary>
    /// <remarks>
    /// An instance that cannot be re-read at this point is reported as a failure rather than as a success,
    /// because a success carries the resource and there is none to carry. The message says plainly that the
    /// type change itself did complete and that the machine is up, so an operator is not sent looking for a
    /// resize that never ran or restarting a machine that is already running.
    /// </remarks>
    private async Task<UpdateExecutionResult> CompletedTypeChangeAsync(
        ResourceHandle handle,
        Ec2Instance running,
        string liveType,
        string targetType,
        int polls,
        CancellationToken ct)
    {
        ProvisionedResource? resource;
        try
        {
            resource = await RefreshAsync(handle, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or AwsApiException)
        {
            return new UpdateExecutionResult.Failed(
                $"EC2 reported instance {running.InstanceId} as running at instance type '{targetType}', so the "
                + "type change IS done and the machine IS up - but it could not be read back afterwards, so "
                + $"Servyx cannot describe the machine that now exists. {ex.Message}");
        }

        if (resource is null)
        {
            return new UpdateExecutionResult.Failed(
                $"EC2 reported instance {running.InstanceId} as running at instance type '{targetType}', so the "
                + "type change IS done and the machine IS up - but EC2 no longer describes that instance as a "
                + "Servyx-managed one, so Servyx cannot describe the machine that now exists. Reconcile before "
                + "acting on it.");
        }

        return new UpdateExecutionResult.Completed(
            resource,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Instance {running.InstanceId} was stopped, its instance type was changed from '{liveType}' to '{targetType}' with ModifyInstanceAttribute, and it was started again. EC2 was observed reporting it running after {polls} check(s) - all three steps ran and all three were confirmed, not merely submitted. ")
            + "A stop is not a terminate: the instance kept its id and "
            + DescribeVolumes(running)
            + " are still attached, read back off the live block device mapping after the cycle rather than "
            + "assumed, because DeleteOnTermination is consulted on termination and on nothing else. Two things "
            + "did change and neither is an impact on persistent data: the workload was DOWN for the whole "
            + "stop/start and anything running on the machine needs to be confirmed back up, and the instance's "
            + "public IPv4 address is ephemeral, so it WILL be a different address now - this adapter allocates "
            + "no Elastic IP and does not claim the StaticAddress capability. Anything pinned to the old address "
            + "- a DNS record, a server list entry, a player's favourites - has to be updated.");
    }

    /// <summary>
    /// The message for every way the sequence can end between the stop and the attribute write: the instance is
    /// stopped, and it is still the old type.
    /// </summary>
    /// <remarks>
    /// Its own method because it is the outcome neither sibling adapter has. The operator's first question is
    /// "is my server up", and the answer is no; the second is "did I lose anything", and the answer is no; the
    /// third is "what do I do now", and the answer is a start, which is the one thing the message must not bury.
    /// </remarks>
    private static string StoppedButNotResized(
        string instanceId,
        string liveType,
        string targetType,
        string volumes,
        string providerMessage) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"THE INSTANCE IS STOPPED AND ITS TYPE WAS NOT CHANGED. Instance {instanceId} was stopped - that step succeeded and was confirmed - and the change from '{liveType}' to '{targetType}' then did not happen, so the machine is still '{liveType}'. ")
        + providerMessage
        + " The workload is OFFLINE until the instance is started again. Nothing was lost: the instance kept its "
        + "id and "
        + volumes
        + " are still attached, because a stop is not a terminate and DeleteOnTermination is consulted on "
        + "termination and on nothing else. To restore service on the old type, start the instance. To retry the "
        + "resize, note that the instance is already stopped, so only the attribute write and the start remain - "
        + "re-running this update will submit a stop against an already-stopped instance, which is harmless but "
        + "is not what needs fixing.";

    /// <summary>
    /// The message for every way the sequence can end after the attribute write: the instance is stopped, and
    /// it is the new type.
    /// </summary>
    /// <remarks>
    /// The distinction this file exists to keep. "Stopped and resized" is not a failed resize — the resize
    /// succeeded — and telling an operator that is the difference between restarting one instance and unpicking
    /// an update they think went wrong.
    /// </remarks>
    private static string ResizedButNotStarted(
        string instanceId,
        string liveType,
        string targetType,
        string volumes,
        string providerMessage,
        bool retryTheStart) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"THE TYPE CHANGE SUCCEEDED AND THE INSTANCE IS STOPPED. Instance {instanceId} was stopped, and its instance type IS now '{targetType}' rather than '{liveType}' - that write was confirmed by reading the instance back. Only the start did not complete. ")
        + providerMessage
        + " The workload is OFFLINE until the instance is running. Nothing was lost: the instance kept its id and "
        + volumes
        + " are still attached, because a stop is not a terminate. "
        + (retryTheStart
            ? "Only the start needs retrying - do NOT re-run this update, because the stop and the attribute "
              + "write have both already happened and re-running them changes nothing except the time the "
              + "machine spends down. Start the instance."
            : "The start was submitted and may yet succeed on its own, so do not submit a second one blindly: "
              + "re-read the instance's state first. If it is still stopped, the only step outstanding is the "
              + "start - not this whole update.")
        + " Note the instance's public IPv4 address is ephemeral and will be a different address once it is "
        + "running again.";

    /// <summary>Waits for the instance to report a lifecycle state, using this provisioner's state-poll settings.</summary>
    private Task<Ec2InstancePoll> PollForStateAsync(string instanceId, string state, CancellationToken ct) =>
        _api.PollInstanceAsync(
            instanceId,
            instance => string.Equals(instance.State, state, StringComparison.Ordinal),
            _statePollInterval,
            _statePollAttempts,
            _timeProvider,
            ct);

    /// <summary>Waits for the instance to report an instance type, using this provisioner's state-poll settings.</summary>
    private Task<Ec2InstancePoll> PollForTypeAsync(string instanceId, string instanceType, CancellationToken ct) =>
        _api.PollInstanceAsync(
            instanceId,
            instance => string.Equals(instance.InstanceType, instanceType, StringComparison.Ordinal),
            _statePollInterval,
            _statePollAttempts,
            _timeProvider,
            ct);

    /// <summary>
    /// Reads the instance type a plan asks for, or explains why this file will not execute the plan at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately strict: the plan must carry <em>exactly one</em> change, that change must be the
    /// <c>size</c> aspect, and the plan must describe itself as an in-place update that preserves data. A plan
    /// that also writes a tag is refused rather than partly executed, because executing the type change and
    /// silently skipping the tag would report a half-applied update as an applied one — and a half-applied
    /// update here is one that also took the machine down and back up.
    /// </para>
    /// <para>
    /// The strategy and data-impact checks are redundant with the change check for every plan this adapter
    /// currently produces, and they are kept anyway: they are the two properties the person approving the plan
    /// actually read, so they are the two this file re-reads before acting. In particular no plan whose
    /// <see cref="DataImpact"/> is anything other than <see cref="DataImpact.Preserved"/> can reach an EC2 call
    /// from here, whatever its changes claim — which is what keeps every image change, every region change and
    /// every instance-store instance on the refusing side of this method.
    /// </para>
    /// </remarks>
    private static bool TryReadTypeTarget(UpdatePlan plan, out string targetType, out string refusal)
    {
        targetType = string.Empty;

        if (plan.Strategy != UpdateStrategy.InPlace)
        {
            refusal =
                $"This update was not applied: the plan's strategy is {plan.Strategy}, and the AWS EC2 adapter "
                + "executes only an in-place instance-type change. An image change and a region change both "
                + "require replacing the instance - EC2 fixes an instance's AMI at RunInstances time and moves no "
                + "instance between regions - and neither is implemented. Nothing was sent to EC2.";
            return false;
        }

        if (plan.DataImpact != DataImpact.Preserved)
        {
            refusal =
                $"This update was not applied: the plan states its impact on persistent data as {plan.DataImpact}, "
                + "and the AWS EC2 adapter executes only updates that preserve it. Replacing the instance - the "
                + "operation that changes an EC2 instance's image - terminates it, and what that does to the EBS "
                + "volume the data lives on is decided by a DeleteOnTermination flag this adapter never set and "
                + "can only read back. It is deliberately not implemented. Nothing was sent to EC2.";
            return false;
        }

        if (plan.Changes.Count != 1
            || !string.Equals(plan.Changes[0].Aspect, SizeAspect, StringComparison.Ordinal))
        {
            refusal =
                "This update was not applied: the AWS EC2 adapter executes an instance-type change and nothing "
                + $"else, and this plan describes {plan.Changes.Count} change(s) - "
                + string.Join("; ", plan.Changes.Select(c => c.Description))
                + ". Applying the part it understands and skipping the rest would report a half-applied update as "
                + "an applied one - and would take the machine down and back up on the way - so nothing was sent "
                + "to EC2.";
            return false;
        }

        var desired = plan.Changes[0].Desired;
        if (string.IsNullOrWhiteSpace(desired))
        {
            refusal =
                "This update was not applied: the plan's instance-type change names no target type, so there is "
                + "nothing to change the instance to. Nothing was sent to EC2.";
            return false;
        }

        targetType = desired;
        refusal = string.Empty;
        return true;
    }
}
