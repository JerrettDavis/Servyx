using System.Globalization;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// The <see cref="IUpdateApplier"/> half of the Lightsail adapter: the only code in this assembly that changes
/// an instance which already exists, and the only backing there has ever been for this adapter's
/// <see cref="ProvisioningCapabilities.UpdateInPlace"/> claim.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One operation, and that is the whole of it — not a first instalment.</strong> An
/// <see cref="UpdatePlan"/> from <c>AwsLightsailProvisioner.Maintenance.cs</c> can describe five differences:
/// a bundle change, a blueprint change, a region change, an availability-zone change, and a tag change. This
/// file implements the last one. The other four are refused here, with
/// <see cref="UpdateExecutionResult.Refused"/> and without a single mutating request — and, unlike the EC2
/// adapter's identically-shaped refusal of an image change, <em>three of the four are not gaps that a later
/// change could fill by implementing more</em>. AWS publishes no operation that changes an existing Lightsail
/// instance's bundle, none that moves it between regions, and none that moves it between zones; the blueprint
/// is fixed by the <c>CreateInstances</c> call that created the instance. So this file closes the gap between
/// the capability bit and the code completely, rather than narrowing it.
/// </para>
/// <para>
/// <strong>The canonical Servyx tags cannot be lost here, and the guarantee is structural twice over.</strong>
/// This matters more than it sounds: <c>ReconcileAsync</c> finds orphaned, still-billing instances by
/// <c>servyx.managed</c> and the identity keys beside it, so an instance that lost one becomes undiscoverable
/// while the bill keeps running. Two independent mechanisms prevent that, either of which would suffice:
/// </para>
/// <list type="number">
/// <item><description>
/// <strong>Removal is unreachable.</strong> The only Lightsail action that can delete a tag from an instance is
/// <c>UntagResource</c>, and it is not implemented in <see cref="LightsailJsonApiClient"/> at all. There is no
/// plan, no argument and no caller that can reach one, because there is nothing to reach.
/// </description></item>
/// <item><description>
/// <strong>Overwriting loses to the live instance.</strong> The request body is built from
/// <see cref="ServyxLightsailTags.ToTags"/>, i.e. <see cref="ServyxTagKeys.Build"/>, which writes the canonical
/// keys <em>last</em> over whatever the plan supplied — and the values it writes are read off the live
/// instance's own tags moments earlier, not taken from the plan. A plan asking to set
/// <c>servyx.managed=false</c> therefore could not produce a request that says so.
/// </description></item>
/// </list>
/// <para>
/// On top of both, <see cref="TryReadTagTargets"/> refuses outright any plan that so much as names a canonical
/// key among its changes, so such a plan is reported rather than silently corrected. The check is the part an
/// operator sees; the two structural properties are the part that holds if the check is ever removed.
/// </para>
/// <para>
/// <strong>Every guard except the last runs before any HTTP.</strong> A plan refused by one of those is a
/// statement about Lightsail's state and not merely about this process's: nothing was sent, no signature was
/// computed, and the AWS key pair was not resolved. The one guard that needs the network is the last, and it is
/// a <c>GetInstance</c> read — the instance's own tags are where the ownership identity that will be re-stamped
/// comes from, so it has to be read before anything is written.
/// </para>
/// <para>
/// <strong>Submission is not success, and the operation genuinely is asynchronous.</strong> Lightsail's
/// <c>TagResource</c> answers <c>200 OK</c> with an <c>operations</c> array of pending <c>Operation</c> records
/// carrying a <c>status</c> and an <c>isTerminal</c> flag — it does not answer with the retagged instance, and
/// it does not claim the tags are live. So the accepted response is inspected for an outright <c>Failed</c>
/// operation and then the instance is <em>re-read</em> until the tags are observed on it. A retag still
/// unobserved when the polls are spent is <see cref="UpdateExecutionResult.TimedOut"/>, deliberately not
/// <see cref="UpdateExecutionResult.Failed"/> and deliberately not a success. The canonical tags named in the
/// completed message are the ones read back off the live instance afterwards, so "the ownership marks survived"
/// is an observation rather than a restatement of what was sent.
/// </para>
/// <para>
/// <strong>Nothing here touches the machine.</strong> A Lightsail tag is metadata on the resource: this path
/// issues no stop, no start, no reboot and no delete, so unlike the EC2 instance-type change there is no
/// powered-off middle state and no outcome in which the workload is left down. That is why this file has three
/// outcomes where <c>AwsEc2Provisioner.InstanceType.cs</c> has five.
/// </para>
/// <para>
/// <strong>There is no force path.</strong> No argument here skips a guard, and no overload takes one.
/// </para>
/// </remarks>
public sealed partial class AwsLightsailProvisioner : IUpdateApplier
{
    /// <summary>
    /// The prefix update planning gives a tag difference's <see cref="PlannedChange.Aspect"/> — see
    /// <c>BuildUpdatePlan</c>, which spells it <c>$"tag {key}"</c>. The one aspect family this file can execute.
    /// </summary>
    internal const string TagAspectPrefix = "tag ";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Executes an approved tag change and nothing else. The sequence is: check every guard that can be checked
    /// without the network, read the instance once to confirm it exists and to take its ownership identity from
    /// its own tags, then issue one <c>TagResource</c> carrying the full canonical set, then poll the instance
    /// until the tags are observed on it — and only then re-read it so the caller is handed the resource that
    /// now exists rather than the one that was asked for.
    /// </para>
    /// <para>
    /// A Lightsail refusal of the submission surfaces as <see cref="UpdateExecutionResult.Failed"/> carrying
    /// AWS's own error text. Because nothing on this path stops the machine, every failure message can and does
    /// say plainly that the instance is untouched and still running.
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

        // Guard 1 - the approval must be for this exact plan, checked immediately before anything else and
        // therefore immediately before the mutating call. The dashboard already compared these two, so reaching
        // here with a mismatch means a caller skipped that step; the answer is still "nothing is sent".
        if (!string.Equals(revalidatedPlan.PlanHash, approvedPlanHash, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                "This update was not applied because the plan handed to the AWS Lightsail adapter is not the plan "
                + $"that was approved: the approval names '{approvedPlanHash}' and the plan hashes to "
                + $"'{revalidatedPlan.PlanHash}'. Nothing was sent to Lightsail and no tag was written. Preview "
                + "again and confirm the plan you are then shown.");
        }

        // Guard 2 - the plan and the resource must both belong to this provisioner. Executing another adapter's
        // plan against a name would retag whichever Lightsail instance happened to answer to it.
        if (!string.Equals(revalidatedPlan.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: the plan belongs to provisioner '{revalidatedPlan.ProvisionerId}', "
                + $"not to '{Id}'. Nothing was sent to Lightsail.");
        }

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: the resource belongs to provisioner '{handle.ProvisionerId}', not "
                + $"to '{Id}'. Nothing was sent to Lightsail.");
        }

        if (string.IsNullOrWhiteSpace(handle.ProviderResourceId))
        {
            return new UpdateExecutionResult.Refused(
                "This update was not applied: the resource handle carries no Lightsail instance name, and a "
                + "Lightsail instance's name is its identity - there is no other identifier TagResource could "
                + "key on and this adapter will not guess which instance was meant. Nothing was sent to "
                + "Lightsail.");
        }

        // Guard 3 - the plan must describe tag changes and nothing else at all. This is the guard that keeps
        // every bundle change and every blueprint change on the refusing side, and it runs before any request.
        if (!TryReadTagTargets(revalidatedPlan, out var requested, out var refusal))
        {
            return new UpdateExecutionResult.Refused(refusal);
        }

        var instanceName = handle.ProviderResourceId;

        // Guard 4 - the only guard that touches the network, and it touches it with a read. The ownership
        // identity that will be re-stamped is taken from the live instance's own tags here, so this read is not
        // merely a sanity check: it is where the values that protect the orphan sweep come from.
        LightsailInstance? instance;
        try
        {
            instance = await _api.GetInstanceAsync(instanceName, ct).ConfigureAwait(false);
        }
        catch (AwsApiException ex)
        {
            return new UpdateExecutionResult.Failed(
                $"Instance '{instanceName}' could not be read, so Servyx cannot confirm which Servyx ownership "
                + "tags it currently carries and will not write tags onto an instance it cannot describe. No tag "
                + $"was written and the instance was not touched. {ex.Message}");
        }

        if (instance is null)
        {
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: Lightsail no longer has an instance named '{instanceName}' (it "
                + "answered NotFoundException), so there is nothing to retag. Nothing was written. Reconcile "
                + "before acting further.");
        }

        var identity = ServyxLightsailTags.FromTags(instance.Tags);
        if (identity is null)
        {
            // The plan's tag change is only safe because the canonical keys can be re-stamped from the live
            // instance. An instance that is not (or is no longer) fully Servyx-tagged has no identity to
            // re-stamp, and inventing one would attribute a machine to whatever the plan happened to name.
            return new UpdateExecutionResult.Refused(
                $"This update was not applied: Lightsail instance '{instanceName}' does not carry a complete set "
                + "of Servyx ownership tags, so there is no live identity for this adapter to preserve while it "
                + "writes the requested tags. Writing them anyway would mean taking the ownership marks from the "
                + "plan instead of from the machine, which is how an instance ends up attributed to the wrong "
                + "server - or to none, in which case the orphan sweep stops finding it and it bills "
                + "indefinitely. Nothing was written. Reconcile this instance first.");
        }

        // The structural half of the ownership guarantee: ServyxTagKeys.Build writes the canonical keys LAST,
        // over whatever the plan supplied, and the values are the live instance's own. Nothing the plan can say
        // survives this call in a canonical slot.
        var tags = identity.ToTags(requested);

        if (!TryValidateTags(tags, out var tagRefusal))
        {
            return new UpdateExecutionResult.Refused(tagRefusal);
        }

        return await ExecuteTagChangeAsync(handle, instance, tags, requested, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The single mutating call, and the outcomes it can produce.
    /// </summary>
    /// <remarks>
    /// Split out from the guards above so that the reading order of this file matches the risk order: every line
    /// above this method runs without changing anything. Unlike the EC2 instance-type change, there is exactly
    /// one mutating request here and it never powers the machine down, so there is no partially-applied state to
    /// describe — either the tags are observed on the instance or they are not yet.
    /// </remarks>
    private async Task<UpdateExecutionResult> ExecuteTagChangeAsync(
        ResourceHandle handle,
        LightsailInstance instance,
        IReadOnlyDictionary<string, string> tags,
        IReadOnlyDictionary<string, string> requested,
        CancellationToken ct)
    {
        var instanceName = instance.Name;
        var changed = string.Join(
            ", ",
            requested.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}"));

        // ---- The one mutating request on this path. ----
        IReadOnlyList<LightsailOperation> operations;
        try
        {
            operations = await _api
                .TagResourceAsync(AwsLightsailRequests.TagResource(instanceName, tags), ct)
                .ConfigureAwait(false);
        }
        catch (AwsApiException ex)
        {
            return new UpdateExecutionResult.Failed(
                $"AWS refused the request to change the tags of Lightsail instance '{instanceName}'. No tag was "
                + "written. The instance itself was not touched at all - a tag change does not stop, restart or "
                + $"otherwise affect the machine - so it is still running and nothing was interrupted. {ex.Message}");
        }

        if (operations.FirstOrDefault(o => o.IsFailure) is { } failed)
        {
            return new UpdateExecutionResult.Failed(
                $"Lightsail accepted the request to change the tags of instance '{instanceName}' with an HTTP 200 "
                + "and then reported the operation itself as Failed, which is a refusal that did not arrive as an "
                + $"error status. {failed.FailureText} The instance was not stopped or restarted and is still "
                + "running. Its Servyx ownership tags are unaffected: this adapter cannot call UntagResource, so "
                + "no failure on this path can have removed one.");
        }

        var poll = await PollForTagsAsync(instanceName, tags, ct).ConfigureAwait(false);

        if (poll.Instance is null)
        {
            return poll.ReadFailure is not null
                ? new UpdateExecutionResult.TimedOut(
                    $"Lightsail accepted the tag change for instance '{instanceName}' and the instance could not "
                    + "then be read back, so Servyx cannot say whether the tags are live. The change may well "
                    + "have taken effect. The machine was not touched either way. Re-read the instance before "
                    + $"resubmitting. {poll.ReadFailure}")
                : new UpdateExecutionResult.Failed(
                    $"Lightsail accepted the tag change and then stopped reporting instance '{instanceName}' at "
                    + "all - it answered NotFoundException, which a tag change does not cause. Something outside "
                    + "this update is destroying the instance, and a deleted Lightsail instance takes its bundled "
                    + "system disk with it. Reconcile before acting further.");
        }

        if (poll.Outcome != LightsailTagPollOutcome.Satisfied)
        {
            return new UpdateExecutionResult.TimedOut(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Lightsail accepted the tag change for instance '{instanceName}' and was still not reporting the requested tags on it after {poll.Polls} check(s), so the write was never observed taking effect. ")
                + $"The change requested was: {changed}. The machine was not touched - a tag change does not stop "
                + "or restart it - so the workload is unaffected either way, and the instance's Servyx ownership "
                + "tags are intact because this adapter cannot remove one. Re-read the instance's tags before "
                + "resubmitting; a TagResource that did land is harmless to repeat, but repeating it is not what "
                + "needs checking.");
        }

        return await CompletedTagChangeAsync(handle, poll.Instance, changed, poll.Polls, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads the instance after Lightsail has been observed reporting the new tags, so the caller receives
    /// the resource as it now is.
    /// </summary>
    /// <remarks>
    /// The canonical ownership tags named in the message are enumerated from the instance that was observed,
    /// not from the request that was sent — which is the difference between reporting that they survived and
    /// promising it. An instance that cannot be re-read at this point is reported as a failure rather than as a
    /// success, because a success carries a resource and there is none to carry; the message says plainly that
    /// the tags were written and the machine is untouched, so nobody is sent looking for a retag that never ran.
    /// </remarks>
    private async Task<UpdateExecutionResult> CompletedTagChangeAsync(
        ResourceHandle handle,
        LightsailInstance retagged,
        string changed,
        int polls,
        CancellationToken ct)
    {
        var surviving = ServyxTagKeys.Canonical
            .Where(k => retagged.Tags.ContainsKey(k))
            .ToList();

        if (surviving.Count != ServyxTagKeys.Canonical.Count)
        {
            // Unreachable through this adapter - the request carried all four and UntagResource is not
            // implemented - so this can only mean something else edited the instance's tags concurrently. It is
            // reported rather than assumed away, because the cost of being wrong is an undiscoverable bill.
            var missing = ServyxTagKeys.Canonical.Where(k => !retagged.Tags.ContainsKey(k));

            return new UpdateExecutionResult.Failed(
                $"The tag change was written to Lightsail instance '{retagged.Name}' and the instance was then "
                + "observed missing Servyx ownership tag(s) it must carry: "
                + string.Join(", ", missing)
                + ". This adapter cannot have removed them - it never calls UntagResource, and the request it "
                + "sent carried every canonical key at the value the live instance already had - so something "
                + "outside this update is editing the instance's tags. Until they are restored the orphan sweep "
                + "cannot find this instance and it will keep billing unnoticed. The machine itself is running "
                + "and untouched.");
        }

        ProvisionedResource? resource;
        try
        {
            resource = await RefreshAsync(handle, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or AwsApiException)
        {
            return new UpdateExecutionResult.Failed(
                $"The tag change to Lightsail instance '{retagged.Name}' IS done and was confirmed by reading the "
                + "tags back - but the instance could not then be read again, so Servyx cannot describe the "
                + $"resource that now exists. The machine was never stopped or restarted. {ex.Message}");
        }

        if (resource is null)
        {
            return new UpdateExecutionResult.Failed(
                $"The tag change to Lightsail instance '{retagged.Name}' IS done and was confirmed by reading the "
                + "tags back - but Lightsail no longer describes that instance as a Servyx-managed one, so Servyx "
                + "cannot describe the resource that now exists. The machine was never stopped or restarted. "
                + "Reconcile before acting on it.");
        }

        return new UpdateExecutionResult.Completed(
            resource,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Lightsail instance '{retagged.Name}' was retagged with one TagResource call, and the new tags were observed on the instance itself after {polls} check(s) - the change was confirmed, not merely submitted. ")
            + $"The change applied was: {changed}. "
            + "The machine was not stopped, restarted or otherwise touched: a Lightsail tag is metadata on the "
            + "resource, so the workload stayed up throughout, the instance kept its name, its address and its "
            + "bundled system disk, and no data was at risk at any point. Its Servyx ownership tags - "
            + string.Join(", ", surviving)
            + " - are all present, read back off the live instance after the change rather than assumed: those "
            + "are the keys the orphan sweep finds a billing instance by, and this adapter cannot remove one "
            + "because it never calls UntagResource.");
    }

    /// <summary>
    /// Polls the instance until every tag in <paramref name="expected"/> is observed on it, using this
    /// provisioner's poll settings.
    /// </summary>
    /// <remarks>
    /// Reads the effect rather than the operation record. Lightsail's <c>TagResource</c> answers with pending
    /// operations, and a terminal-looking operation status is still the provider describing its own bookkeeping;
    /// the tags appearing on the instance is the thing the plan actually promised.
    /// </remarks>
    private async Task<LightsailTagPoll> PollForTagsAsync(
        string instanceName,
        IReadOnlyDictionary<string, string> expected,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _addressPollAttempts; attempt++)
        {
            LightsailInstance? instance;
            try
            {
                instance = await _api.GetInstanceAsync(instanceName, ct).ConfigureAwait(false);
            }
            catch (AwsApiException ex)
            {
                return new LightsailTagPoll(LightsailTagPollOutcome.Unreadable, attempt, null, ex.Message);
            }

            if (instance is null)
            {
                return new LightsailTagPoll(LightsailTagPollOutcome.Gone, attempt, null, null);
            }

            if (expected.All(t =>
                instance.Tags.TryGetValue(t.Key, out var value)
                && string.Equals(value, t.Value, StringComparison.Ordinal)))
            {
                return new LightsailTagPoll(LightsailTagPollOutcome.Satisfied, attempt, instance, null);
            }

            if (attempt < _addressPollAttempts)
            {
                await Task.Delay(_addressPollInterval, _timeProvider, ct).ConfigureAwait(false);
            }
            else
            {
                return new LightsailTagPoll(LightsailTagPollOutcome.Unsatisfied, attempt, instance, null);
            }
        }

        // Unreachable: _addressPollAttempts is validated >= 1 at construction, so the loop always returns.
        return new LightsailTagPoll(LightsailTagPollOutcome.Unsatisfied, 0, null, null);
    }

    /// <summary>
    /// Reads the tags a plan asks to write, or explains why this file will not execute the plan at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately strict: the plan must describe itself as an in-place update that preserves data, must carry
    /// at least one change, and <em>every</em> change must be a tag write. A plan that also changes the bundle
    /// or the blueprint is refused rather than partly executed — executing the tag part and silently skipping
    /// the rest would report a half-applied update as an applied one, and the part being skipped would be the
    /// part a person approved a machine replacement for.
    /// </para>
    /// <para>
    /// The strategy and data-impact checks are redundant with the aspect check for every plan this adapter
    /// currently produces, and they are kept anyway: they are the two properties the person approving the plan
    /// actually read, so they are the two this file re-reads before acting. In particular no plan whose
    /// <see cref="DataImpact"/> is anything other than <see cref="DataImpact.Preserved"/> can reach a Lightsail
    /// call from here, whatever its changes claim — which is what keeps every blueprint change, every bundle
    /// change and every placement change on the refusing side of this method even if one were hand-built to
    /// misdescribe itself.
    /// </para>
    /// <para>
    /// A change with no <see cref="PlannedChange.Desired"/> value is a tag <em>removal</em>, and it is refused
    /// rather than executed: removal is <c>UntagResource</c>, which this adapter does not implement at all.
    /// </para>
    /// </remarks>
    private static bool TryReadTagTargets(
        UpdatePlan plan,
        out IReadOnlyDictionary<string, string> requested,
        out string refusal)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        requested = tags;

        if (plan.Strategy != UpdateStrategy.InPlace)
        {
            refusal =
                $"This update was not applied: the plan's strategy is {plan.Strategy}, and the AWS Lightsail "
                + "adapter executes only an in-place tag change. A blueprint change requires replacing the "
                + "instance - Lightsail fixes an instance's blueprint at CreateInstances time - and a bundle, "
                + "region or zone change is not an operation AWS offers for a Lightsail instance at all. None of "
                + "them is implemented here. Nothing was sent to Lightsail.";
            return false;
        }

        if (plan.DataImpact != DataImpact.Preserved)
        {
            refusal =
                $"This update was not applied: the plan states its impact on persistent data as {plan.DataImpact}, "
                + "and the AWS Lightsail adapter executes only updates that preserve it. Every route to a "
                + "different blueprint, bundle, region or zone ends in DeleteInstance, and a Lightsail bundle "
                + "bakes the SSD storage into the instance - there is no separate disk resource and nothing "
                + "survives the delete - so those are deliberately not implemented. Nothing was sent to "
                + "Lightsail.";
            return false;
        }

        if (plan.Changes.Count == 0)
        {
            refusal =
                "This update was not applied: the plan describes no changes at all, so there is nothing to write. "
                + "Nothing was sent to Lightsail.";
            return false;
        }

        var notTags = plan.Changes
            .Where(c => !c.Aspect.StartsWith(TagAspectPrefix, StringComparison.Ordinal))
            .ToList();

        if (notTags.Count > 0)
        {
            refusal =
                "This update was not applied: the AWS Lightsail adapter executes a tag change and nothing else, "
                + $"and this plan describes {notTags.Count} change(s) that are not tag changes - "
                + string.Join("; ", notTags.Select(c => c.Description))
                + ". A bundle change is not merely unimplemented here: AWS publishes no operation that changes an "
                + "existing Lightsail instance's bundle, and what it documents instead is a snapshot-and-restore "
                + "procedure that produces a different instance. A blueprint change means deleting this instance "
                + "- and its bundled system disk with it - and creating another. Applying the tag part and "
                + "skipping the rest would report a half-applied update as an applied one, so nothing was sent to "
                + "Lightsail.";
            return false;
        }

        foreach (var change in plan.Changes)
        {
            var key = change.Aspect[TagAspectPrefix.Length..].Trim();

            if (key.Length == 0)
            {
                refusal =
                    "This update was not applied: the plan carries a tag change that names no tag key, so there "
                    + "is nothing to write. Nothing was sent to Lightsail.";
                return false;
            }

            if (ServyxTagKeys.Canonical.Contains(key, StringComparer.Ordinal))
            {
                refusal =
                    $"This update was not applied: the plan asks to write the Servyx ownership tag '{key}', and "
                    + "the AWS Lightsail adapter will not change one as part of an ordinary tag update. Those "
                    + "keys - "
                    + string.Join(", ", ServyxTagKeys.Canonical)
                    + " - are what the orphan sweep finds a still-billing instance by, so an instance whose "
                    + "ownership marks are rewritten to something else becomes undiscoverable while it keeps "
                    + "costing money. Re-attributing an instance is a decision for a person and a separate "
                    + "reviewed operation, not a side effect of a retag. Nothing was sent to Lightsail. (Had this "
                    + "check not caught it, the request would still have carried the live instance's own "
                    + "ownership values: they are written last, over anything a plan supplies.)";
                return false;
            }

            if (string.IsNullOrWhiteSpace(change.Desired))
            {
                refusal =
                    $"This update was not applied: the plan asks to remove the tag '{key}', and removing a "
                    + "Lightsail tag is UntagResource, which this adapter does not implement and cannot call. "
                    + "That absence is deliberate: it is what makes it structurally impossible for any update "
                    + "here to delete a Servyx ownership tag from a live instance. Nothing was sent to Lightsail.";
                return false;
            }

            if (tags.ContainsKey(key))
            {
                refusal =
                    $"This update was not applied: the plan describes the tag '{key}' more than once, so there is "
                    + "no single value to write. Nothing was sent to Lightsail.";
                return false;
            }

            tags[key] = change.Desired;
        }

        refusal = string.Empty;
        return true;
    }

    /// <summary>
    /// Checks the whole outgoing tag set against Lightsail's own key/value rules, returning a refusal rather
    /// than throwing.
    /// </summary>
    /// <remarks>
    /// <see cref="ServyxLightsailTags.Validate"/> throws, which is right for the create path — a plan built from
    /// an untaggable identity is a caller defect caught before anything exists. Here the same rules have to
    /// produce a <see cref="UpdateExecutionResult.Refused"/> instead, because <see cref="IUpdateApplier"/>
    /// returns rather than throws for every outcome the provider can produce, and because a value that reached
    /// this point came off a live instance or an approved plan rather than from a caller's argument.
    /// </remarks>
    private static bool TryValidateTags(IReadOnlyDictionary<string, string> tags, out string refusal)
    {
        foreach (var pair in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (pair.Key.StartsWith(ServyxLightsailTags.ReservedKeyPrefix, StringComparison.OrdinalIgnoreCase)
                || !ServyxLightsailTags.IsTaggableKey(pair.Key)
                || !ServyxLightsailTags.IsTaggableValue(pair.Value))
            {
                refusal =
                    $"This update was not applied: the tag '{pair.Key}' is not expressible as a Lightsail tag, so "
                    + "the whole request would be refused by AWS. A key must be 1-"
                    + $"{ServyxLightsailTags.MaxTagKeyLength} characters and a value 1-"
                    + $"{ServyxLightsailTags.MaxTagValueLength}, both of letters, digits, whitespace or "
                    + $"{ServyxLightsailTags.AdditionalAllowedCharacters}, and neither may use the "
                    + $"'{ServyxLightsailTags.ReservedKeyPrefix}' prefix AWS reserves for itself. Nothing was "
                    + "sent to Lightsail.";
                return false;
            }
        }

        refusal = string.Empty;
        return true;
    }
}

/// <summary>How a wait for a retag to become visible on the instance ended.</summary>
internal enum LightsailTagPollOutcome
{
    /// <summary>Every expected tag was observed on the live instance.</summary>
    Satisfied,

    /// <summary>The instance was readable throughout and the tags never appeared within the poll budget.</summary>
    Unsatisfied,

    /// <summary>Lightsail stopped reporting the instance at all, which a tag change does not cause.</summary>
    Gone,

    /// <summary>The instance could not be read, so nothing can be said about whether the tags landed.</summary>
    Unreadable,
}

/// <summary>The result of waiting for a retag to become visible.</summary>
/// <param name="Outcome">How the wait ended.</param>
/// <param name="Polls">How many reads were made.</param>
/// <param name="Instance">The instance as last read, or <see langword="null"/> if it could not be read at all.</param>
/// <param name="ReadFailure">Lightsail's own words about a read failure, when there was one.</param>
internal sealed record LightsailTagPoll(
    LightsailTagPollOutcome Outcome,
    int Polls,
    LightsailInstance? Instance,
    string? ReadFailure);
