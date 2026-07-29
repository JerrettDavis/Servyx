using System.Globalization;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Docker.Provisioning;

/// <summary>
/// The recreate half of the Docker adapter: the only code in this assembly that replaces a container which
/// already exists, and the only code that can undo one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before widening anything in this file.</strong> Before it, a Docker update could be
/// planned but never applied, and nothing anywhere recorded what a container was. That combination is why a
/// rollback did not exist: <c>ResourceHandle</c> carries labels, so the ledger knew the image
/// (<see cref="ServyxResourceTags.ImageLabel"/>) and the root path and nothing about ports, environment or
/// mounts — and the container, which knew all of them, is exactly the thing a recreate deletes. A rollback
/// built on those records could have restored an image and would have had to invent the rest.
/// </para>
/// <para>
/// <strong>So the recording comes first.</strong> <see cref="DockerContainerProvisioner.PrepareUpdateAsync"/>
/// reads the live container into a <see cref="DockerContainerSnapshot"/> and stamps it, encoded, onto the
/// replacement as <see cref="ServyxResourceTags.PreviousSpecLabel"/>. An update that cannot capture that
/// snapshot is <em>refused</em> rather than applied without it: an update that silently destroys the only
/// copy of what the container was is the state this file exists to prevent.
/// <see cref="DockerContainerProvisioner.PlanRollbackAsync"/> reads it back, and refuses when it is absent.
/// </para>
/// <para>
/// <strong>A rollback is an update whose desired state was recorded rather than requested.</strong> It is
/// planned by the same <c>BuildUpdatePlan</c>, carries the same <see cref="UpdatePlan"/> invariants, asserts
/// its <see cref="DataImpact"/> from the same live-mount analysis, and is carried out by the same recreate
/// operation. There is no second, laxer path: if a rollback would drop or remap a mount, it says
/// <see cref="DataImpact.AtRisk"/> and demands the matching acknowledgement, exactly as an update does.
/// </para>
/// <para>
/// <strong>Volumes are never removed.</strong> The removal step is
/// <c>DockerContainerProvisioner.RemoveContainerAsync</c>, shared verbatim with the create path's
/// compensation, and it sets <c>RemoveVolumes = false</c>. There is no parameter on any member of this file
/// that reaches a removal with volumes included, which is why no plan produced here can honestly claim
/// <see cref="DataImpact.Destroyed"/>.
/// </para>
/// <para>
/// <strong>A rollback is recorded twice, and neither record is optional.</strong> The operation is an
/// <see cref="IProvisioningOperation"/>, so <c>Servyx.Application</c>'s <c>ProvisioningExecutor</c> commits a
/// write-ahead ledger row before the first mutating call and stamps the replacement container's id onto it
/// afterwards — the ledger therefore names the container that now exists rather than the one that was
/// replaced. And the restored container is stamped with
/// <see cref="ServyxResourceTags.RolledBackAtLabel"/> and
/// <see cref="ServyxResourceTags.RolledBackFromLabel"/> and, deliberately, <em>no</em>
/// <see cref="ServyxResourceTags.PreviousSpecLabel"/>. That absence is what makes a second consecutive
/// rollback refuse instead of quietly re-applying the update that was just undone.
/// </para>
/// <para>
/// <strong>There is no force path.</strong> Every check runs before any stop, remove, or create, and no
/// argument skips one. The approved plan hash is re-checked a second time inside the operation, immediately
/// before the first mutating call, so a gate that was passed and then went stale still stops the recreate
/// rather than merely having been consulted.
/// </para>
/// </remarks>
public sealed partial class DockerContainerProvisioner
{
    /// <summary>
    /// Plans the rollback of <paramref name="handle"/> to the state recorded when it was last updated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Reads and computes. No mutating engine call is issued, ever.</strong> The only Docker call on
    /// this path is a single <c>InspectContainerAsync</c>, and a handle belonging to another provisioner is
    /// answered without even that.
    /// </para>
    /// <para>
    /// <strong>It refuses rather than reconstructs.</strong> A container carrying no
    /// <see cref="ServyxResourceTags.PreviousSpecLabel"/> — one that has never been updated through
    /// <see cref="PrepareUpdateAsync"/>, or one that has already been rolled back — yields
    /// <see cref="DockerRollbackPlan.NoRecordedPriorState"/>. There is deliberately no branch that assembles a
    /// prior state out of the container's current values, its image label, or any default: a rollback that
    /// guesses is worse than none, because it looks like recovery and is not.
    /// </para>
    /// </remarks>
    /// <param name="handle">The container to plan a rollback for.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DockerRollbackPlan> PlanRollbackAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var (result, _, _) = await PlanRollbackCoreAsync(handle, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Confirms an already-approved rollback, handing back the inert operation that carries it out.
    /// </summary>
    /// <remarks>
    /// The same two approvals an update requires, in the same order and with no way past either: the plan is
    /// recomputed from the live container and its hash compared against <paramref name="approvedPlanHash"/>,
    /// and a plan that does not preserve persistent data additionally requires
    /// <paramref name="acknowledgedDataImpact"/> to name exactly the impact the recomputed plan states. Both
    /// run before anything is stopped, removed, or created, so a
    /// <see cref="DockerRecreateConfirmation.Refused"/> is a guarantee about the daemon's state.
    /// </remarks>
    /// <param name="handle">The container to roll back.</param>
    /// <param name="approvedPlanHash">The <see cref="UpdatePlan.PlanHash"/> the caller approved.</param>
    /// <param name="acknowledgedDataImpact">
    /// The data impact the caller accepted, or <see langword="null"/> when it approved only an ordinary
    /// preserving rollback. Exact equality both ways: acknowledging one impact never covers another, and
    /// acknowledging anything at all for a <see cref="DataImpact.Preserved"/> plan is itself a mismatch.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DockerRecreateConfirmation> PrepareRollbackAsync(
        ResourceHandle handle,
        string approvedPlanHash,
        DataImpact? acknowledgedDataImpact = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedPlanHash);

        var (result, prior, inspect) = await PlanRollbackCoreAsync(handle, ct).ConfigureAwait(false);

        if (result is not DockerRollbackPlan.Planned planned || prior is null)
        {
            return new DockerRecreateConfirmation.Refused(
                $"This rollback was not carried out. {result.Message} Nothing was sent to the Docker Engine.");
        }

        if (Refuse(planned.Plan, approvedPlanHash, acknowledgedDataImpact, "rollback") is { } refusal)
        {
            return refusal;
        }

        // A rollback records itself: the restored container is stamped with when it happened and with the
        // spec of the container it undid, and — the load-bearing part — with no PreviousSpecLabel. A second
        // consecutive rollback therefore finds no recorded prior state and refuses, rather than treating the
        // update it just undid as a state to restore and silently re-applying it.
        var bookkeeping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxResourceTags.RolledBackAtLabel] =
                _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
        };

        if (DockerContainerSnapshot.Capture(inspect) is { } undone)
        {
            bookkeeping[ServyxResourceTags.RolledBackFromLabel] = undone.Encode();
        }

        return new DockerRecreateConfirmation.Ready(
            new ContainerRecreateOperation(this, handle.ProviderResourceId, prior, bookkeeping, planned.Plan.PlanHash),
            planned.Plan,
            $"Container '{prior.ContainerName}' will be rolled back to the state recorded when it was last "
            + $"updated: image '{prior.Image}', {prior.Ports.Count} port(s) and {prior.Volumes.Count} mount(s). "
            + $"Its {prior.Volumes.Count} mount(s) are not removed, and the restored container records that it "
            + "was produced by a rollback, so rolling back again will find nothing to restore and refuse.");
    }

    /// <summary>
    /// Confirms an already-approved update, handing back the inert operation that recreates the container —
    /// and, first, records what the container is now so the update can later be rolled back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>An update that cannot be recorded is refused, not applied.</strong> If the live container
    /// cannot be read into a complete <see cref="DockerContainerSnapshot"/> — it is not Servyx-managed, or it
    /// is missing an identity label — this method returns
    /// <see cref="DockerRecreateConfirmation.Refused"/> and nothing runs. Applying anyway would delete the
    /// only copy of what the container was, which is exactly the situation that made a rollback impossible
    /// before this file existed.
    /// </para>
    /// <para>
    /// The approval discipline is identical to <see cref="PrepareRollbackAsync"/>'s, because it is the same
    /// code: recompute from the live container, compare the hash, then require the matching data-impact
    /// acknowledgement.
    /// </para>
    /// </remarks>
    /// <param name="handle">The container to update.</param>
    /// <param name="desired">The desired state, in the same parameter vocabulary <see cref="BuildSpec"/> reads.</param>
    /// <param name="approvedPlanHash">The <see cref="UpdatePlan.PlanHash"/> the caller approved.</param>
    /// <param name="acknowledgedDataImpact">The data impact the caller accepted, or <see langword="null"/> for a preserving update.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DockerRecreateConfirmation> PrepareUpdateAsync(
        ResourceHandle handle,
        ProvisioningRequest desired,
        string approvedPlanHash,
        DataImpact? acknowledgedDataImpact = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedPlanHash);

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new DockerRecreateConfirmation.Refused(
                $"This update was not carried out: the resource belongs to provisioner "
                + $"'{handle.ProvisionerId}', not to '{Id}'. Nothing was sent to the Docker Engine.");
        }

        var inspect = await InspectOrNullAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (inspect is null)
        {
            return new DockerRecreateConfirmation.Refused(
                $"This update was not carried out: the Docker Engine no longer knows a container "
                + $"'{handle.ProviderResourceId}', so there is nothing to recreate. Nothing was sent to the "
                + "Docker Engine.");
        }

        var target = BuildSpec(desired);
        var plan = BuildUpdatePlan(inspect, target);

        if (Refuse(plan, approvedPlanHash, acknowledgedDataImpact, "update") is { } refusal)
        {
            return refusal;
        }

        // The whole reason this method exists rather than the create operation being reused. The snapshot is
        // taken from the container that is about to be removed, and it is the only record of what that
        // container was; refusing here is the difference between "this update can be undone" and "this update
        // silently made itself permanent".
        var snapshot = DockerContainerSnapshot.Capture(inspect);
        if (snapshot is null)
        {
            return new DockerRecreateConfirmation.Refused(
                $"This update was not carried out: container '{handle.ProviderResourceId}' could not be read "
                + "into a complete record of what it is now — it is not labelled as a Servyx-managed container, "
                + "or the engine reports it without an image or a name. Recreating it would destroy the only "
                + "copy of its current specification and leave nothing to roll back to, so nothing was sent to "
                + "the Docker Engine.");
        }

        var bookkeeping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxResourceTags.PreviousSpecLabel] = snapshot.Encode(),
        };

        return new DockerRecreateConfirmation.Ready(
            new ContainerRecreateOperation(this, handle.ProviderResourceId, target, bookkeeping, plan.PlanHash),
            plan,
            $"Container '{target.ContainerName}' will be recreated from image '{target.Image}'. What it is now "
            + $"— image '{snapshot.Image}', {snapshot.Ports.Count} port(s), {snapshot.Volumes.Count} mount(s) "
            + $"and {snapshot.Environment.Count} environment variable(s) — is recorded on the replacement, so "
            + "this update can be rolled back.");
    }

    /// <summary>
    /// The two approvals, applied identically to an update and to a rollback. Returns the refusal, or
    /// <see langword="null"/> when both passed.
    /// </summary>
    private static DockerRecreateConfirmation.Refused? Refuse(
        UpdatePlan plan,
        string approvedPlanHash,
        DataImpact? acknowledgedDataImpact,
        string verb)
    {
        if (!string.Equals(plan.PlanHash, approvedPlanHash, StringComparison.Ordinal))
        {
            return new DockerRecreateConfirmation.Refused(
                $"This {verb} was not carried out because the approval is not for the plan the live container "
                + $"now produces: the approval names '{approvedPlanHash}' and the recomputed plan hashes to "
                + $"'{plan.PlanHash}'. Nothing was sent to the Docker Engine. Preview again and confirm the plan "
                + "you are then shown.");
        }

        if (plan.Strategy == UpdateStrategy.NoChangeRequired)
        {
            return new DockerRecreateConfirmation.Refused(
                $"This {verb} was not carried out: the container already matches the state asked for, so the "
                + "plan carries no stages and would do nothing. Recreating it anyway would interrupt a workload "
                + "for no change. Nothing was sent to the Docker Engine.");
        }

        // Exactly the rule Servyx.Application's DataImpactAcknowledgement enforces, restated here because
        // that type is internal to Application and this assembly references only Servyx.Domain. Both
        // directions are checked: a non-preserving plan with no acknowledgement is refused because the caller
        // has not accepted what would happen, and a preserving plan with one is refused because the caller is
        // approving something other than the plan that would run.
        var satisfied = plan.DataImpact == DataImpact.Preserved
            ? acknowledgedDataImpact is null
            : acknowledgedDataImpact == plan.DataImpact;

        if (!satisfied)
        {
            return new DockerRecreateConfirmation.Refused(
                $"This {verb} was not carried out: the plan states its impact on persistent data as "
                + $"{plan.DataImpact}, and the acknowledgement supplied was "
                + $"{(acknowledgedDataImpact is null ? "none" : acknowledgedDataImpact.Value.ToString())}. "
                + "Nothing was sent to the Docker Engine.");
        }

        return null;
    }

    /// <summary>
    /// The shared read behind <see cref="PlanRollbackAsync"/> and <see cref="PrepareRollbackAsync"/>, so both
    /// derive the prior state the same way rather than agreeing by convention.
    /// </summary>
    private async Task<(DockerRollbackPlan Result, DockerContainerSpec? Prior, ContainerInspectResponse? Inspect)>
        PlanRollbackCoreAsync(ResourceHandle handle, CancellationToken ct)
    {
        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            // Answered without touching the engine, mirroring DetectDriftAsync's refusal.
            return (
                new DockerRollbackPlan.Refused(
                    $"No rollback was planned: the resource belongs to provisioner '{handle.ProvisionerId}', not "
                    + $"to '{Id}'."),
                null,
                null);
        }

        var inspect = await InspectOrNullAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (inspect is null)
        {
            return (
                new DockerRollbackPlan.ResourceGone(
                    $"No rollback was planned: the Docker Engine no longer knows a container "
                    + $"'{handle.ProviderResourceId}'."),
                null,
                null);
        }

        var labels = ToOrdinalDictionary(inspect.Config?.Labels);
        if (!labels.TryGetValue(ServyxResourceTags.PreviousSpecLabel, out var recorded)
            || string.IsNullOrWhiteSpace(recorded))
        {
            return (
                new DockerRollbackPlan.NoRecordedPriorState(
                    $"Container '{handle.ProviderResourceId}' carries no recorded prior state "
                    + $"('{ServyxResourceTags.PreviousSpecLabel}'), so there is nothing to roll back to. That is "
                    + "the expected answer for a container that has never been updated through Servyx, and for "
                    + "one that has already been rolled back. Servyx will not reconstruct a previous state from "
                    + "the container's current values or from defaults."),
                null,
                inspect);
        }

        if (!DockerContainerSnapshot.TryDecode(recorded, out var snapshot))
        {
            return (
                new DockerRollbackPlan.Refused(
                    $"No rollback was planned: container '{handle.ProviderResourceId}' carries a "
                    + $"'{ServyxResourceTags.PreviousSpecLabel}' label that Servyx cannot read. A record that "
                    + "cannot be read is treated exactly like an absent one; nothing is inferred from it."),
                null,
                inspect);
        }

        var prior = snapshot.ToSpec();
        var plan = BuildUpdatePlan(inspect, prior);

        if (plan.Strategy == UpdateStrategy.NoChangeRequired)
        {
            return (
                new DockerRollbackPlan.Refused(
                    $"No rollback was planned: container '{handle.ProviderResourceId}' already matches the state "
                    + "recorded before its last update, so a rollback would recreate it for no change."),
                prior,
                inspect);
        }

        return (
            new DockerRollbackPlan.Planned(
                plan,
                $"Container '{prior.ContainerName}' can be rolled back to image '{prior.Image}' with "
                + $"{prior.Ports.Count} port(s), {prior.Volumes.Count} mount(s) and "
                + $"{prior.Environment.Count} environment variable(s), as recorded before its last update. "
                + $"Impact on persistent data: {plan.DataImpact}."),
            prior,
            inspect);
    }

    /// <summary>
    /// Stops a container, treating "the engine has never heard of it" as already stopped. Paired with
    /// <see cref="RemoveContainerAsync"/>, which is shared verbatim with the create path's compensation and
    /// never removes volumes.
    /// </summary>
    private async Task StopContainerAsync(string containerId, CancellationToken ct)
    {
        try
        {
            await _client.Containers
                .StopContainerAsync(containerId, new ContainerStopParameters(), ct)
                .ConfigureAwait(false);
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone; the removal below will report the same and the recreate carries on.
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    /// <summary>
    /// The one operation in this assembly that replaces a container which already exists. Nested inside the
    /// provisioner so it — and only it — can reach <see cref="BuildCreateParameters"/>, exactly as
    /// <c>ContainerCreateOperation</c> is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is an <see cref="IProvisioningOperation"/> rather than a method that talks to the daemon directly
    /// because that is what puts <c>Servyx.Application</c>'s <c>ProvisioningExecutor</c>, and therefore the
    /// write-ahead ledger row, in front of the first mutating call. A rollback is recorded before it happens
    /// and stamped with the replacement container's id afterwards, for the same reason a create is.
    /// </para>
    /// <para>
    /// <strong>Compensation cannot resurrect the container that was removed.</strong> It removes the
    /// replacement, which is all that is undoable — and states the limit rather than implying more. The data
    /// is nonetheless intact: the removal never removes volumes, so the mounts the original had are still on
    /// the host, and re-running the operation recreates a container attached to them.
    /// </para>
    /// </remarks>
    private sealed class ContainerRecreateOperation : IProvisioningOperation
    {
        private readonly DockerContainerProvisioner _owner;
        private readonly string _existingContainerId;
        private readonly DockerContainerSpec _target;
        private readonly IReadOnlyDictionary<string, string> _bookkeeping;
        private readonly string _approvedPlanHash;
        private string? _createdContainerId;

        internal ContainerRecreateOperation(
            DockerContainerProvisioner owner,
            string existingContainerId,
            DockerContainerSpec target,
            IReadOnlyDictionary<string, string> bookkeeping,
            string approvedPlanHash)
        {
            _owner = owner;
            _existingContainerId = existingContainerId;
            _target = target;
            _bookkeeping = bookkeeping;
            _approvedPlanHash = approvedPlanHash;
        }

        public string ProvisionerId => Id;

        public string? Region => DockerContainerProvisioner.Region;

        /// <summary>
        /// The identifying labels the ledger row records. Deliberately the spec's own labels only: the
        /// recorded prior spec is a potentially large blob describing a container that no longer exists, and
        /// the ledger's tags exist so an orphan sweep can <em>find</em> a resource, not to duplicate its
        /// history. The blob lives on the container, which is where a rollback reads it from.
        /// </summary>
        public IReadOnlyDictionary<string, string> Tags => LabelsFor(_target);

        public async Task<ProvisionedResource> CreateAsync(CancellationToken ct = default)
        {
            // The gate again, immediately before the first mutating call. PrepareUpdateAsync and
            // PrepareRollbackAsync both checked this, and both checked it against a container that may have
            // moved since. Re-checking here is what makes the approval a gate rather than a formality.
            var inspect = await _owner.InspectOrNullAsync(_existingContainerId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"The Docker Engine no longer knows container '{_existingContainerId}', so there is nothing "
                    + "to recreate. Nothing was stopped, removed, or created.");

            var plan = _owner.BuildUpdatePlan(inspect, _target);
            if (!string.Equals(plan.PlanHash, _approvedPlanHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Container '{_existingContainerId}' has changed since the plan was approved: the approval "
                    + $"names '{_approvedPlanHash}' and the container now produces '{plan.PlanHash}'. Nothing was "
                    + "stopped, removed, or created.");
            }

            await _owner.StopContainerAsync(_existingContainerId, ct).ConfigureAwait(false);

            // RemoveVolumes is false — see RemoveContainerAsync. The mounts survive this step, which is what
            // lets the replacement come back up attached to the same data.
            await _owner.RemoveContainerAsync(_existingContainerId, ct).ConfigureAwait(false);

            var created = await _owner._client.Containers
                .CreateContainerAsync(BuildCreateParameters(_target, _bookkeeping), ct)
                .ConfigureAwait(false);

            _createdContainerId = created?.ID
                ?? throw new InvalidOperationException("The Docker Engine returned no container id from CreateContainerAsync.");

            await _owner._client.Containers
                .StartContainerAsync(_createdContainerId, new ContainerStartParameters(), ct)
                .ConfigureAwait(false);

            var after = await _owner._client.Containers
                .InspectContainerAsync(_createdContainerId, ct)
                .ConfigureAwait(false);

            var labels = after?.Config?.Labels is { Count: > 0 }
                ? ToOrdinalDictionary(after.Config.Labels)
                : new Dictionary<string, string>(Tags, StringComparer.Ordinal);

            return new ProvisionedResource(
                Handle: new ResourceHandle(Id, _createdContainerId, Region, labels),
                ConnectorId: _target.Tags.ConnectorId,
                Target: _owner.BuildTargetDescriptor(_createdContainerId, _target.ContainerName, _target.RootPath),
                Facts: after is null
                    ? new ResourceFacts(null, null, CostEstimate.Unknown(CostSource), _owner._timeProvider.GetUtcNow())
                    : BuildFacts(after));
        }

        public async Task CompensateAsync(CancellationToken ct = default)
        {
            if (_createdContainerId is not null)
            {
                await _owner.RemoveContainerAsync(_createdContainerId, ct).ConfigureAwait(false);
            }

            // Nothing else is undoable. If the failure happened after the original was removed, that container
            // is gone for good — its volumes are not, because the removal never removes them. The ledger row
            // stays Intended so a sweep still has something to find, which is the executor's contract.
        }
    }
}
