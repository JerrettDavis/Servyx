using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Servyx.Application.Provisioning;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;

namespace Servyx.Application.Tests.Provisioning;

/// <summary>
/// The dispatch to <see cref="IUpdateApplier"/> on
/// <see cref="IProvisioningDashboard.ApplyUpdateAsync"/>: that an adapter which can genuinely change an
/// existing resource is the thing that runs, and — the assertions that matter — that it is reachable only
/// after every refusal above it has been passed.
/// </summary>
/// <remarks>
/// Every "the adapter was not reached" claim here is an invocation count on the substituted
/// <see cref="IUpdateApplier"/>, not a message match: the count is what proves the plan-hash revalidation and
/// the data-impact acknowledgement are still in front of the only member that can mutate anything.
/// </remarks>
public class ProvisioningDashboardUpdateApplierTests
{
    private const string ProvisionerId = "digitalocean-droplet";
    private const string PlanHash = "3333333333333333333333333333333333333333333333333333333333333333";
    private const string OtherPlanHash = "4444444444444444444444444444444444444444444444444444444444444444";

    private static readonly IReadOnlyDictionary<string, string> Tags =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["servyx.managed"] = "true" };

    private static readonly ResourceHandle Handle = new(ProvisionerId, "3164494", "nyc3", Tags);

    private static readonly ProvisioningRequest Desired = new(
        GameDefinitionId: "palworld",
        DeploymentProfileId: "digitalocean",
        ConnectorId: "conn-1",
        Parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["size"] = "s-4vcpu-8gb" });

    // ---------------------------------------------------------------------------------------------------
    // The adapter runs, and it runs instead of the create path
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_adapter_that_can_apply_an_update_is_used_instead_of_the_create_operation()
    {
        var applier = Applier(new UpdateExecutionResult.Completed(Resource(), "Droplet 3164494 was resized."));
        var provisioner = Provisioner(applier);
        var (dashboard, ledger) = Dashboard(provisioner);

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, PlanHash, dataImpactAcknowledgement: null);

        var applied = result.Should().BeOfType<UpdateApplyResult.Applied>().Which;
        applied.PlanHash.Should().Be(PlanHash);
        applied.Strategy.Should().Be(UpdateStrategy.InPlace);
        applied.DataImpact.Should().Be(DataImpact.Preserved);
        applied.Resource.Handle.ProviderResourceId.Should().Be("3164494");

        await applier.Received(1).ApplyUpdateAsync(
            Arg.Any<ResourceHandle>(), Arg.Any<UpdatePlan>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // An update updates the resource that exists; it does not stand a second one up beside it.
        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await ledger.DidNotReceive().RecordIntentAsync(Arg.Any<ProvisioningIntent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_adapter_is_handed_the_hash_the_caller_approved()
    {
        var applier = Applier(new UpdateExecutionResult.Completed(Resource(), "Resized."));
        var (dashboard, _) = Dashboard(Provisioner(applier));

        await dashboard.ApplyUpdateAsync(ProvisionerId, Handle, Desired, PlanHash, dataImpactAcknowledgement: null);

        await applier.Received(1).ApplyUpdateAsync(
            Arg.Any<ResourceHandle>(),
            Arg.Is<UpdatePlan>(p => p != null && p.PlanHash == PlanHash),
            PlanHash,
            Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------------------------------------------
    // Everything that refuses still refuses, and refuses before the adapter is reached
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stale_plan_hash_is_refused_before_the_adapter_is_reached()
    {
        var applier = Applier(new UpdateExecutionResult.Completed(Resource(), "Resized."));
        var (dashboard, ledger) = Dashboard(Provisioner(applier));

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, OtherPlanHash, dataImpactAcknowledgement: null);

        result.Should().BeOfType<UpdateApplyResult.Stale>();

        await applier.DidNotReceive().ApplyUpdateAsync(
            Arg.Any<ResourceHandle>(), Arg.Any<UpdatePlan>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await ledger.DidNotReceive().RecordIntentAsync(Arg.Any<ProvisioningIntent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_acknowledgement_is_refused_before_the_adapter_is_reached()
    {
        var applier = Applier(new UpdateExecutionResult.Completed(Resource(), "Rebuilt."));
        var provisioner = Provisioner(applier, Plan(DataImpact.Destroyed, UpdateStrategy.Recreate));
        var (dashboard, _) = Dashboard(provisioner);

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, PlanHash, dataImpactAcknowledgement: null);

        result.Should().BeOfType<UpdateApplyResult.RequiresAcknowledgement>();

        await applier.DidNotReceive().ApplyUpdateAsync(
            Arg.Any<ResourceHandle>(), Arg.Any<UpdatePlan>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_plan_with_nothing_to_do_is_refused_before_the_adapter_is_reached()
    {
        var applier = Applier(new UpdateExecutionResult.Completed(Resource(), "Resized."));
        var provisioner = Provisioner(applier, Plan(DataImpact.Preserved, UpdateStrategy.NoChangeRequired));
        var (dashboard, _) = Dashboard(provisioner);

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, PlanHash, dataImpactAcknowledgement: null);

        result.Should().BeOfType<UpdateApplyResult.NoChangeRequired>();

        await applier.DidNotReceive().ApplyUpdateAsync(
            Arg.Any<ResourceHandle>(), Arg.Any<UpdatePlan>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------------------------------------------
    // The adapter's own non-success outcomes reach the caller with the adapter's own words
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_refusal_is_reported_as_a_failure_carrying_the_adapters_message() =>
        await AssertNonSuccessAsync(
            new UpdateExecutionResult.Refused("This update was not applied: the plan is not a resize."));

    [Fact]
    public async Task An_errored_action_is_reported_as_a_failure_carrying_the_adapters_message() =>
        await AssertNonSuccessAsync(
            new UpdateExecutionResult.Failed("DigitalOcean reported the resize action as errored."));

    [Fact]
    public async Task A_still_running_action_is_reported_as_a_failure_carrying_the_adapters_message() =>
        await AssertNonSuccessAsync(
            new UpdateExecutionResult.TimedOut("The resize was NOT confirmed and was NOT reported as failed."));

    private static async Task AssertNonSuccessAsync(UpdateExecutionResult outcome)
    {
        var (dashboard, ledger) = Dashboard(Provisioner(Applier(outcome)));

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, PlanHash, dataImpactAcknowledgement: null);

        var failed = result.Should().BeOfType<UpdateApplyResult.Failed>().Which;
        failed.Message.Should().Be(outcome.Message);

        // No write-ahead row was written and nothing was created, so there is no row for a sweep to resolve
        // and nothing that could have been orphaned - which is what these two values say.
        failed.LedgerRowId.Should().Be(Guid.Empty);
        failed.Compensated.Should().BeTrue();

        await ledger.DidNotReceive().RecordIntentAsync(Arg.Any<ProvisioningIntent>(), Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------

    private static UpdatePlan Plan(
        DataImpact dataImpact = DataImpact.Preserved,
        UpdateStrategy strategy = UpdateStrategy.InPlace)
    {
        var changes = strategy == UpdateStrategy.NoChangeRequired
            ? (IReadOnlyList<PlannedChange>)[]
            : [new PlannedChange("size", "s-2vcpu-4gb", "s-4vcpu-8gb", RequiresRecreate: strategy == UpdateStrategy.Recreate)];

        var stages = strategy == UpdateStrategy.NoChangeRequired
            ? (IReadOnlyList<ProvisioningStage>)[]
            : [new ProvisioningStage("resize-droplet", ProvisionerId, "Resize the droplet.")];

        return new UpdatePlan(
            planId: "digitalocean-droplet:update:3164494:333333333333",
            planHash: PlanHash,
            provisionerId: ProvisionerId,
            strategy: strategy,
            dataImpact: dataImpact,
            changes: changes,
            stages: stages,
            expiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private static ProvisionedResource Resource() => new(
        Handle: Handle,
        ConnectorId: "conn-1",
        Target: new TargetDescriptor(
            "ssh",
            "203.0.113.7:22",
            "secret://connector/conn-1/ssh/private-key",
            "/",
            new Dictionary<string, string>(StringComparer.Ordinal)),
        Facts: new ResourceFacts("203.0.113.7", null, CostEstimate.Unknown("test"), DateTimeOffset.UnixEpoch));

    private static IUpdateApplier Applier(UpdateExecutionResult outcome)
    {
        var applier = Substitute.For<IUpdateApplier>();
        applier.ProvisionerId.Returns(ProvisionerId);
        applier
            .ApplyUpdateAsync(
                Arg.Any<ResourceHandle>(), Arg.Any<UpdatePlan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(outcome));

        return applier;
    }

    /// <summary>
    /// A provisioner shaped like the DigitalOcean adapter: it plans updates, and it can apply one. The
    /// applier is a separate substitute so its invocation count is the evidence in every test above.
    /// </summary>
    private static IProvisioner Provisioner(IUpdateApplier applier, UpdatePlan? plan = null)
    {
        var planned = Task.FromResult<UpdatePlan?>(plan ?? Plan());

        var provisioner = Substitute.For<IProvisioner, IMaintainer, IUpdateApplier>();
        provisioner.ProvisionerId.Returns(ProvisionerId);
        provisioner.Capabilities.Returns(ProvisioningCapabilities.Create | ProvisioningCapabilities.UpdateInPlace);
        provisioner.CreateOperation(Arg.Any<ProvisioningRequest>()).Throws(new InvalidOperationException(
            "The update path must not build a create operation for an adapter that can apply the update itself."));

        ((IMaintainer)provisioner)
            .PlanUpdateAsync(Arg.Any<ResourceHandle>(), Arg.Any<ProvisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(planned);

        // Forwarded rather than duplicated, so "the dashboard called the applier" and "the dashboard called
        // this provisioner" are the same observation.
        ((IUpdateApplier)provisioner)
            .ApplyUpdateAsync(
                Arg.Any<ResourceHandle>(), Arg.Any<UpdatePlan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => applier.ApplyUpdateAsync(
                call.ArgAt<ResourceHandle>(0),
                call.ArgAt<UpdatePlan>(1),
                call.ArgAt<string>(2),
                call.ArgAt<CancellationToken>(3)));

        return provisioner;
    }

    private static (ProvisioningDashboardService Dashboard, IProvisioningLedger Ledger) Dashboard(
        IProvisioner provisioner)
    {
        var ledger = Substitute.For<IProvisioningLedger>();
        return (new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger)), ledger);
    }
}
