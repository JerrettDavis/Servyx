using System.Reflection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Servyx.Application.Provisioning;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;

namespace Servyx.Application.Tests.Provisioning;

/// <summary>
/// Unit tests for <see cref="ProvisioningDashboardService"/>'s update path — update planning
/// (<see cref="IProvisioningDashboard.PlanUpdateAsync"/>) and the second member that can change anything
/// (<see cref="IProvisioningDashboard.ApplyUpdateAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// Every "nothing happened" assertion here is an invocation count, not a message match. The message is what
/// a user reads; the count is what proves no provider call and no ledger write occurred. Where a test claims
/// nothing was executed it asserts both that <see cref="IProvisioner.CreateOperation"/> — the only route to
/// a mutation — was never called and that the ledger holds no write-ahead row, which is the executor's very
/// first act.
/// </para>
/// <para>
/// The provisioner is an NSubstitute substitute implementing both <see cref="IProvisioner"/> and
/// <see cref="IMaintainer"/>, so nothing here knows Docker exists; the maintainer-less case uses a
/// substitute for <see cref="IProvisioner"/> alone, which is exactly the shape the other three adapters have.
/// </para>
/// </remarks>
public class ProvisioningDashboardUpdateTests
{
    private const string ProvisionerId = "docker-container";
    private const string UpdatePlanHash = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string OtherPlanHash = "2222222222222222222222222222222222222222222222222222222222222222";

    private static readonly IReadOnlyDictionary<string, string> Tags =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["servyx.managed"] = "true" };

    private static readonly ResourceHandle Handle = new(ProvisionerId, "c0ffee1234ab", null, Tags);

    private static readonly ProvisioningRequest Desired = new(
        GameDefinitionId: "palworld",
        DeploymentProfileId: "docker",
        ConnectorId: "docker-container-local",
        Parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = "servyx-preview" });

    private static UpdatePlan Plan(
        DataImpact dataImpact = DataImpact.Preserved,
        string hash = UpdatePlanHash,
        UpdateStrategy strategy = UpdateStrategy.Recreate)
    {
        var changes = strategy == UpdateStrategy.NoChangeRequired
            ? (IReadOnlyList<PlannedChange>)[]
            : [new PlannedChange("image", "palworld:0.3", "palworld:0.4", RequiresRecreate: true)];

        var stages = strategy == UpdateStrategy.NoChangeRequired
            ? (IReadOnlyList<ProvisioningStage>)[]
            : [new ProvisioningStage("recreate-container", ProvisionerId, "Stop, remove, and recreate the container.")];

        return new UpdatePlan(
            planId: $"{ProvisionerId}:servyx-preview:update:{hash[..12]}",
            planHash: hash,
            provisionerId: ProvisionerId,
            strategy: strategy,
            dataImpact: dataImpact,
            changes: changes,
            stages: stages,
            expiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private static ProvisionedResource Resource(string containerId = "beef5678cd90") => new(
        Handle: new ResourceHandle(ProvisionerId, containerId, null, Tags),
        ConnectorId: "docker-container-local",
        Target: new TargetDescriptor(
            "docker",
            "npipe://./pipe/dockerDesktopLinuxEngine",
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["containerId"] = containerId }),
        Facts: new ResourceFacts(null, "172.18.0.2", CostEstimate.Unknown("local docker"), DateTimeOffset.UnixEpoch));

    private static IProvisioningOperation Operation(ProvisionedResource? result = null)
    {
        var operation = Substitute.For<IProvisioningOperation>();
        operation.ProvisionerId.Returns(ProvisionerId);
        operation.Region.Returns((string?)null);
        operation.Tags.Returns(Tags);
        operation.CreateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(result ?? Resource()));
        return operation;
    }

    /// <summary>A provisioner that also implements <see cref="IMaintainer"/>, like the Docker adapter.</summary>
    private static IProvisioner Maintainer(
        UpdatePlan? plan = null,
        IProvisioningOperation? operation = null,
        bool resourceGone = false)
    {
        // Materialised before any Returns() call: building a substitute inside Returns() clobbers
        // NSubstitute's "last call" context.
        var plannedResult = Task.FromResult(resourceGone ? null : plan ?? Plan());
        var createdOperation = operation ?? Operation();

        var provisioner = Substitute.For<IProvisioner, IMaintainer>();
        provisioner.ProvisionerId.Returns(ProvisionerId);
        provisioner.Capabilities.Returns(
            ProvisioningCapabilities.Create | ProvisioningCapabilities.RecreateToUpdate | ProvisioningCapabilities.DetectDrift);
        provisioner.CreateOperation(Arg.Any<ProvisioningRequest>()).Returns(createdOperation);

        ((IMaintainer)provisioner)
            .PlanUpdateAsync(Arg.Any<ResourceHandle>(), Arg.Any<ProvisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(plannedResult);

        return provisioner;
    }

    /// <summary>A provisioner that does NOT implement <see cref="IMaintainer"/> — the SSH/DigitalOcean shape.</summary>
    private static IProvisioner PlainProvisioner(IProvisioningOperation? operation = null)
    {
        var createdOperation = operation ?? Operation();

        var provisioner = Substitute.For<IProvisioner>();
        provisioner.ProvisionerId.Returns(ProvisionerId);
        provisioner.Capabilities.Returns(ProvisioningCapabilities.Create);
        provisioner.CreateOperation(Arg.Any<ProvisioningRequest>()).Returns(createdOperation);
        return provisioner;
    }

    // ---------------------------------------------------------------------------------------------------
    // The happy path.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Applying_a_preserving_update_drives_the_operation_exactly_once_and_writes_a_ledger_row()
    {
        var ledger = Substitute.For<IProvisioningLedger>();
        var operation = Operation();
        var provisioner = Maintainer(Plan(DataImpact.Preserved), operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, UpdatePlanHash, dataImpactAcknowledgement: null, jobId: "job-42");

        var applied = result.Should().BeOfType<UpdateApplyResult.Applied>().Which;
        applied.Resource.Handle.ProviderResourceId.Should().Be("beef5678cd90");
        applied.PlanHash.Should().Be(UpdatePlanHash);
        applied.Strategy.Should().Be(UpdateStrategy.Recreate);
        applied.DataImpact.Should().Be(DataImpact.Preserved);

        provisioner.Received(1).CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.Received(1).CreateAsync(Arg.Any<CancellationToken>());
        await operation.DidNotReceive().CompensateAsync(Arg.Any<CancellationToken>());

        // Intent before effect, exactly as the create path: the row is committed before the provider call
        // and only advanced to Created after the provider confirmed an id.
        Received.InOrder(() =>
        {
            ledger.RecordIntentAsync(Arg.Any<ProvisioningIntent>(), Arg.Any<CancellationToken>());
            operation.CreateAsync(Arg.Any<CancellationToken>());
            ledger.MarkCreatedAsync(Arg.Any<Guid>(), "beef5678cd90", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task An_applied_update_records_the_job_id_and_tags_on_the_ledger_row()
    {
        var ledger = Substitute.For<IProvisioningLedger>();
        ProvisioningIntent? captured = null;
        ledger.RecordIntentAsync(Arg.Do<ProvisioningIntent>(i => captured = i), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var dashboard = new ProvisioningDashboardService([Maintainer()], ledger, new ProvisioningExecutor(ledger));

        await dashboard.ApplyUpdateAsync(ProvisionerId, Handle, Desired, UpdatePlanHash, null, "job-42");

        captured.Should().NotBeNull();
        captured!.JobId.Should().Be("job-42");
        captured.ProvisionerId.Should().Be(ProvisionerId);
        captured.Tags.Should().BeEquivalentTo(Tags);
    }

    // ---------------------------------------------------------------------------------------------------
    // Staleness — refused, with non-invocation as the assertion.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stale_update_plan_hash_is_refused_and_the_executor_is_never_invoked()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        // The maintainer now plans to something other than what the caller was shown.
        var provisioner = Maintainer(Plan(DataImpact.Preserved, OtherPlanHash), operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(ProvisionerId, Handle, Desired, UpdatePlanHash, null, "job-42");

        var stale = result.Should().BeOfType<UpdateApplyResult.Stale>().Which;
        stale.ExpectedPlanHash.Should().Be(UpdatePlanHash);
        stale.CurrentPlanHash.Should().Be(OtherPlanHash);

        // Non-invocation is the assertion, not the message.
        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        await operation.DidNotReceive().CompensateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty(
            "a refusal must not leave a write-ahead row, because nothing was attempted");
    }

    [Fact]
    public async Task A_stale_hash_is_refused_before_the_acknowledgement_is_even_considered()
    {
        // A correctly-acknowledged destructive plan is still refused when the hash moved: the two approvals
        // are independent, and neither compensates for the other.
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        var provisioner = Maintainer(Plan(DataImpact.Destroyed, OtherPlanHash), operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, UpdatePlanHash, DataImpactAcknowledgement.Destroyed());

        result.Should().BeOfType<UpdateApplyResult.Stale>();
        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Data-impact acknowledgement — the second, independent approval.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(DataImpact.AtRisk)]
    [InlineData(DataImpact.Destroyed)]
    public async Task A_non_preserving_plan_without_an_acknowledgement_never_reaches_the_executor(DataImpact impact)
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        var provisioner = Maintainer(Plan(impact), operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        // The plan hash is correct and this is the very same argument list that applies a Preserved plan.
        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, UpdatePlanHash, dataImpactAcknowledgement: null, jobId: "job-42");

        var refused = result.Should().BeOfType<UpdateApplyResult.RequiresAcknowledgement>().Which;
        refused.PlanDataImpact.Should().Be(impact);
        refused.AcknowledgedDataImpact.Should().BeNull();
        refused.PlanHash.Should().Be(UpdatePlanHash);

        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty(
            "approving the plan is not approving the data loss, so nothing may be attempted");
    }

    [Fact]
    public async Task Acknowledging_at_risk_does_not_authorise_a_destroying_plan()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        var provisioner = Maintainer(Plan(DataImpact.Destroyed), operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, UpdatePlanHash, DataImpactAcknowledgement.AtRisk());

        var refused = result.Should().BeOfType<UpdateApplyResult.RequiresAcknowledgement>().Which;
        refused.PlanDataImpact.Should().Be(DataImpact.Destroyed);
        refused.AcknowledgedDataImpact.Should().Be(DataImpact.AtRisk);

        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_acknowledgement_that_does_not_match_a_preserving_plan_is_refused_too()
    {
        // Over-approval is still a mismatch between what was acknowledged and what would run.
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        var provisioner = Maintainer(Plan(DataImpact.Preserved), operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, UpdatePlanHash, DataImpactAcknowledgement.Destroyed());

        result.Should().BeOfType<UpdateApplyResult.RequiresAcknowledgement>()
            .Which.PlanDataImpact.Should().Be(DataImpact.Preserved);

        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(DataImpact.AtRisk)]
    [InlineData(DataImpact.Destroyed)]
    public async Task A_matching_acknowledgement_is_what_lets_a_non_preserving_update_run(DataImpact impact)
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        var provisioner = Maintainer(Plan(impact), operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var acknowledgement = impact == DataImpact.AtRisk
            ? DataImpactAcknowledgement.AtRisk()
            : DataImpactAcknowledgement.Destroyed();

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, UpdatePlanHash, acknowledgement, "job-42");

        result.Should().BeOfType<UpdateApplyResult.Applied>().Which.DataImpact.Should().Be(impact);
        await operation.Received(1).CreateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void An_acknowledgement_cannot_be_constructed_for_a_preserving_impact()
    {
        // The type-level half of the guarantee: there is no public constructor and no factory that yields a
        // token for Preserved, so the token can never be the ordinary "yes, apply" argument.
        typeof(DataImpactAcknowledgement)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().BeEmpty("an acknowledgement must be minted through a factory named for the impact it accepts");

        var factories = typeof(DataImpactAcknowledgement)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(DataImpactAcknowledgement))
            .ToList();

        factories.Should().HaveCount(2);
        factories.Should().OnlyContain(m => m.GetParameters().Length == 0,
            "a For(DataImpact) factory would let a caller acknowledge whatever the plan happens to say");

        factories.Select(m => (DataImpactAcknowledgement)m.Invoke(null, null)!)
            .Select(a => a.Acknowledged)
            .Should().BeEquivalentTo(new[] { DataImpact.AtRisk, DataImpact.Destroyed });

        DataImpactAcknowledgement.AtRisk().Covers(DataImpact.Preserved).Should().BeFalse();
        DataImpactAcknowledgement.AtRisk().Covers(DataImpact.Destroyed).Should().BeFalse();
        DataImpactAcknowledgement.AtRisk().Covers(DataImpact.AtRisk).Should().BeTrue();
        DataImpactAcknowledgement.Destroyed().Covers(DataImpact.AtRisk).Should().BeFalse();
    }

    [Fact]
    public void The_acknowledgement_is_a_distinct_required_parameter_and_no_force_flag_exists()
    {
        var apply = typeof(IProvisioningDashboard)
            .GetMethod(nameof(IProvisioningDashboard.ApplyUpdateAsync))
            .Should().NotBeNull().And.Subject.As<MethodInfo>();

        var parameters = apply.GetParameters();

        parameters.Select(p => p.Name ?? string.Empty)
            .Any(n => n.Contains("force", StringComparison.OrdinalIgnoreCase)
                || n.Contains("override", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse("no force/override flag may exist anywhere on the apply path");

        var acknowledgement = parameters.Single(p => p.ParameterType == typeof(DataImpactAcknowledgement));

        // Its own type, and not the same argument that approves an ordinary update: the plan hash is a
        // string, so no value of the approval argument can ever inhabit the acknowledgement argument.
        acknowledgement.ParameterType.Should().NotBe(typeof(bool));
        acknowledgement.ParameterType.Should().NotBe(typeof(string));
        acknowledgement.HasDefaultValue.Should().BeFalse(
            "every caller must state its position on data impact rather than inheriting a default");
        parameters.Should().ContainSingle(p => p.ParameterType == typeof(DataImpactAcknowledgement));
    }

    // ---------------------------------------------------------------------------------------------------
    // Planning alone mutates nothing.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Planning_an_update_alone_issues_no_mutating_calls()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        var provisioner = Maintainer(Plan(DataImpact.AtRisk), operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.PlanUpdateAsync(ProvisionerId, Handle, Desired);

        var planned = result.Should().BeOfType<PlanUpdateResult.Planned>().Which;
        planned.Plan.PlanHash.Should().Be(UpdatePlanHash);
        planned.Plan.DataImpact.Should().Be(DataImpact.AtRisk);

        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        await operation.DidNotReceive().CompensateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty(
            "a preview must leave no write-ahead row, which is direct evidence the executor never ran");
    }

    // ---------------------------------------------------------------------------------------------------
    // A provisioner that is not a maintainer.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_provisioner_that_is_not_a_maintainer_reports_unsupported_and_touches_nothing()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        var provisioner = PlainProvisioner(operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.PlanUpdateAsync(ProvisionerId, Handle, Desired);

        result.Should().BeOfType<PlanUpdateResult.Unsupported>()
            .Which.ProvisionerId.Should().Be(ProvisionerId);
        result.Message.Should().Contain("does not support maintenance");

        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await provisioner.DidNotReceive().PlanAsync(Arg.Any<ProvisioningRequest>(), Arg.Any<CancellationToken>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Applying_an_update_to_a_provisioner_that_is_not_a_maintainer_reports_unsupported_and_executes_nothing()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        var provisioner = PlainProvisioner(operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(ProvisionerId, Handle, Desired, UpdatePlanHash, null);

        result.Should().BeOfType<UpdateApplyResult.Unsupported>()
            .Which.ProvisionerId.Should().Be(ProvisionerId);

        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // The resource is gone, and the do-nothing plan.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_resource_the_provider_no_longer_knows_is_reported_as_gone_rather_than_as_nothing_to_do()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        var provisioner = Maintainer(operation: operation, resourceGone: true);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        (await dashboard.PlanUpdateAsync(ProvisionerId, Handle, Desired))
            .Should().BeOfType<PlanUpdateResult.ResourceGone>()
            .Which.Handle.Should().Be(Handle);

        var applied = await dashboard.ApplyUpdateAsync(ProvisionerId, Handle, Desired, UpdatePlanHash, null);
        applied.Should().BeOfType<UpdateApplyResult.ResourceGone>();

        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_that_reports_no_change_required_is_not_executed()
    {
        // Such a plan carries no stages; running the create operation for it would stand up a second
        // resource beside the one that already matches.
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        var provisioner = Maintainer(
            Plan(DataImpact.Preserved, UpdatePlanHash, UpdateStrategy.NoChangeRequired), operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(ProvisionerId, Handle, Desired, UpdatePlanHash, null);

        result.Should().BeOfType<UpdateApplyResult.NoChangeRequired>()
            .Which.PlanHash.Should().Be(UpdatePlanHash);

        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Failure surfaces structurally.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_update_is_surfaced_with_its_ledger_row_and_the_row_stays_intended_for_a_sweep()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        operation.CreateAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("port 8211 is already allocated"));

        var dashboard = new ProvisioningDashboardService(
            [Maintainer(operation: operation)], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(ProvisionerId, Handle, Desired, UpdatePlanHash, null, "job-42");

        var failed = result.Should().BeOfType<UpdateApplyResult.Failed>().Which;
        failed.Message.Should().Contain("reconciliation");
        failed.LedgerRowId.Should().NotBe(Guid.Empty);
        failed.Compensated.Should().BeTrue();

        await operation.Received(1).CompensateAsync(Arg.Any<CancellationToken>());

        var intended = await ledger.ListIntendedAsync(ProvisionerId);
        intended.Should().ContainSingle("intent-before-effect must still hold when the update fails");
        intended[0].LedgerRowId.Should().Be(failed.LedgerRowId);
    }

    [Fact]
    public async Task A_failed_compensation_on_an_update_is_reported_as_a_possible_orphan()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        operation.CreateAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("create failed"));
        operation.CompensateAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("remove timed out"));

        var dashboard = new ProvisioningDashboardService(
            [Maintainer(operation: operation)], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(ProvisionerId, Handle, Desired, UpdatePlanHash, null);

        var failed = result.Should().BeOfType<UpdateApplyResult.Failed>().Which;
        failed.Compensated.Should().BeFalse();
        failed.Message.Should().Contain("may still exist at the provider");
    }

    // ---------------------------------------------------------------------------------------------------
    // Misconfiguration and argument validation.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Applying_an_update_through_a_dashboard_with_no_executor_throws_rather_than_quietly_doing_nothing()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        var provisioner = Maintainer(operation: operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger);

        dashboard.ExecutionConfigured.Should().BeFalse();

        var act = async () => await dashboard.ApplyUpdateAsync(ProvisionerId, Handle, Desired, UpdatePlanHash, null);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(nameof(ProvisioningExecutor));

        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Null_and_blank_update_arguments_are_rejected_before_anything_is_planned_or_changed()
    {
        var ledger = new InMemoryProvisioningLedger();
        var provisioner = Maintainer();
        var maintainer = (IMaintainer)provisioner;
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var blankProvisioner = async () => await dashboard.ApplyUpdateAsync(" ", Handle, Desired, UpdatePlanHash, null);
        var nullHandle = async () => await dashboard.ApplyUpdateAsync(ProvisionerId, null!, Desired, UpdatePlanHash, null);
        var nullDesired = async () => await dashboard.ApplyUpdateAsync(ProvisionerId, Handle, null!, UpdatePlanHash, null);
        var blankHash = async () => await dashboard.ApplyUpdateAsync(ProvisionerId, Handle, Desired, "   ", null);
        var blankPlanProvisioner = async () => await dashboard.PlanUpdateAsync(" ", Handle, Desired);

        await blankProvisioner.Should().ThrowAsync<ArgumentException>();
        await nullHandle.Should().ThrowAsync<ArgumentNullException>();
        await nullDesired.Should().ThrowAsync<ArgumentNullException>();
        await blankHash.Should().ThrowAsync<ArgumentException>();
        await blankPlanProvisioner.Should().ThrowAsync<ArgumentException>();

        await maintainer.DidNotReceive().PlanUpdateAsync(
            Arg.Any<ResourceHandle>(), Arg.Any<ProvisioningRequest>(), Arg.Any<CancellationToken>());
        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unregistered_provisioner_id_throws_on_the_update_path_and_touches_nothing()
    {
        var ledger = new InMemoryProvisioningLedger();
        var provisioner = Maintainer();
        var maintainer = (IMaintainer)provisioner;
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var plan = async () => await dashboard.PlanUpdateAsync("hetzner", Handle, Desired);
        var apply = async () => await dashboard.ApplyUpdateAsync("hetzner", Handle, Desired, UpdatePlanHash, null);

        (await plan.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("hetzner");
        (await apply.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("hetzner");

        await maintainer.DidNotReceive().PlanUpdateAsync(
            Arg.Any<ResourceHandle>(), Arg.Any<ProvisioningRequest>(), Arg.Any<CancellationToken>());
        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // The gate closed: Servyx:Provisioning:Enabled = false.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_provisioning_disabled_there_is_no_maintainer_to_discover_and_nothing_is_written()
    {
        // Servyx:Provisioning:Enabled=false means the composition root never calls
        // AddServyxDockerProvisioning(), so neither an IProvisioner nor an IMaintainer is registered — see
        // the remarks on DockerProvisioningServiceCollectionExtensions. This dashboard is that shape: an
        // empty provisioner set. The update members must behave exactly as the create members already do
        // for it, which is to say they must find nothing and change nothing.
        var ledger = new InMemoryProvisioningLedger();
        var dashboard = new ProvisioningDashboardService([], ledger, new ProvisioningExecutor(ledger));

        dashboard.ListProvisioners().Should().BeEmpty("a closed gate registers no provisioner to type-test");

        var plan = async () => await dashboard.PlanUpdateAsync(ProvisionerId, Handle, Desired);
        var apply = async () => await dashboard.ApplyUpdateAsync(ProvisionerId, Handle, Desired, UpdatePlanHash, null);

        // Identical to the create path's answer for an unregistered id: loud, and nothing attempted.
        (await plan.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("(none)");
        (await apply.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("(none)");

        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
        (await dashboard.ListLedgerEntriesAsync()).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // The destructive applier — the token half of the gate the DigitalOcean rebuild path sits behind.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A provisioner shaped like the DigitalOcean droplet adapter: a maintainer that can also execute a plan
    /// which destroys data. The substitute records every call, so "the adapter was never reached" is an
    /// invocation count here rather than a message match — the same standard the rest of this file holds to.
    /// </summary>
    private static IProvisioner DestructiveApplier(UpdatePlan? plan = null, UpdateExecutionResult? execution = null)
    {
        // Materialised before any Returns() call: building a substitute inside Returns() clobbers
        // NSubstitute's "last call" context, exactly as Maintainer above notes.
        var plannedResult = Task.FromResult<UpdatePlan?>(plan ?? Plan(DataImpact.Destroyed));
        var executionResult = Task.FromResult(
            execution ?? new UpdateExecutionResult.Completed(Resource(), "The droplet was rebuilt."));
        var createdOperation = Operation();

        var provisioner = Substitute.For<IProvisioner, IMaintainer, IDestructiveUpdateApplier>();
        provisioner.ProvisionerId.Returns(ProvisionerId);
        provisioner.Capabilities.Returns(ProvisioningCapabilities.Create | ProvisioningCapabilities.UpdateInPlace);
        provisioner.CreateOperation(Arg.Any<ProvisioningRequest>()).Returns(createdOperation);

        ((IMaintainer)provisioner)
            .PlanUpdateAsync(Arg.Any<ResourceHandle>(), Arg.Any<ProvisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(plannedResult);

        ((IDestructiveUpdateApplier)provisioner)
            .ApplyDestructiveUpdateAsync(
                Arg.Any<ResourceHandle>(),
                Arg.Any<UpdatePlan>(),
                Arg.Any<string>(),
                Arg.Any<DataImpact?>(),
                Arg.Any<CancellationToken>())
            .Returns(executionResult);

        return provisioner;
    }

    [Fact]
    public async Task A_destroying_plan_with_no_acknowledgement_never_reaches_the_destructive_applier()
    {
        var ledger = new InMemoryProvisioningLedger();
        var provisioner = DestructiveApplier();
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, UpdatePlanHash, dataImpactAcknowledgement: null);

        result.Should().BeOfType<UpdateApplyResult.RequiresAcknowledgement>()
            .Which.PlanDataImpact.Should().Be(DataImpact.Destroyed);

        // The count, not the message: an adapter that was called has already begun destroying data, whatever
        // this method went on to return.
        await ((IDestructiveUpdateApplier)provisioner).DidNotReceive().ApplyDestructiveUpdateAsync(
            Arg.Any<ResourceHandle>(),
            Arg.Any<UpdatePlan>(),
            Arg.Any<string>(),
            Arg.Any<DataImpact?>(),
            Arg.Any<CancellationToken>());

        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_at_risk_token_never_reaches_the_destructive_applier_for_a_destroying_plan()
    {
        var ledger = new InMemoryProvisioningLedger();
        var provisioner = DestructiveApplier();
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, UpdatePlanHash, DataImpactAcknowledgement.AtRisk());

        result.Should().BeOfType<UpdateApplyResult.RequiresAcknowledgement>();

        await ((IDestructiveUpdateApplier)provisioner).DidNotReceive().ApplyDestructiveUpdateAsync(
            Arg.Any<ResourceHandle>(),
            Arg.Any<UpdatePlan>(),
            Arg.Any<string>(),
            Arg.Any<DataImpact?>(),
            Arg.Any<CancellationToken>());

        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_stale_plan_never_reaches_the_destructive_applier_even_when_correctly_acknowledged()
    {
        var ledger = new InMemoryProvisioningLedger();
        var provisioner = DestructiveApplier(Plan(DataImpact.Destroyed, OtherPlanHash));
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, UpdatePlanHash, DataImpactAcknowledgement.Destroyed());

        result.Should().BeOfType<UpdateApplyResult.Stale>();

        await ((IDestructiveUpdateApplier)provisioner).DidNotReceive().ApplyDestructiveUpdateAsync(
            Arg.Any<ResourceHandle>(),
            Arg.Any<UpdatePlan>(),
            Arg.Any<string>(),
            Arg.Any<DataImpact?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_a_matching_destroyed_token_reaches_the_destructive_applier_and_it_carries_that_impact()
    {
        var ledger = new InMemoryProvisioningLedger();
        var provisioner = DestructiveApplier();
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, UpdatePlanHash, DataImpactAcknowledgement.Destroyed());

        result.Should().BeOfType<UpdateApplyResult.Applied>()
            .Which.DataImpact.Should().Be(DataImpact.Destroyed);

        // Exactly one call, carrying the approved hash the caller supplied and the impact the token named —
        // never a value the plan could have supplied on its own behalf.
        await ((IDestructiveUpdateApplier)provisioner).Received(1).ApplyDestructiveUpdateAsync(
            Handle,
            Arg.Is<UpdatePlan>(p => p != null && p.PlanHash == UpdatePlanHash),
            UpdatePlanHash,
            DataImpact.Destroyed,
            Arg.Any<CancellationToken>());

        // The destructive path writes no write-ahead row and creates nothing, so there is nothing to sweep.
        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_refusal_from_the_destructive_applier_is_surfaced_as_a_failure_with_no_ledger_row()
    {
        var ledger = new InMemoryProvisioningLedger();
        var provisioner = DestructiveApplier(
            execution: new UpdateExecutionResult.Refused("Nothing was sent to DigitalOcean."));
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, UpdatePlanHash, DataImpactAcknowledgement.Destroyed());

        var failed = result.Should().BeOfType<UpdateApplyResult.Failed>().Which;
        failed.Message.Should().Contain("Nothing was sent to DigitalOcean.");
        failed.LedgerRowId.Should().Be(Guid.Empty);
        failed.Compensated.Should().BeTrue();

        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_still_running_destructive_update_is_a_failure_result_carrying_its_own_words()
    {
        // TimedOut is not Completed, so it is never reported as Applied — and the adapter's message, which is
        // the thing that tells an operator "still reimaging" rather than "reimage failed", travels verbatim.
        var ledger = new InMemoryProvisioningLedger();
        var provisioner = DestructiveApplier(
            execution: new UpdateExecutionResult.TimedOut("The rebuild was NOT confirmed. Do NOT resubmit."));
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, UpdatePlanHash, DataImpactAcknowledgement.Destroyed());

        result.Should().NotBeOfType<UpdateApplyResult.Applied>();
        result.Should().BeOfType<UpdateApplyResult.Failed>()
            .Which.Message.Should().Contain("Do NOT resubmit");
    }

    [Fact]
    public async Task A_preserving_plan_never_reaches_the_destructive_applier()
    {
        // The destructive member is not an alternative route for ordinary updates: a Preserved plan carries
        // no token, and without one this branch is unreachable.
        var ledger = new InMemoryProvisioningLedger();
        var provisioner = DestructiveApplier(Plan(DataImpact.Preserved));
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyUpdateAsync(
            ProvisionerId, Handle, Desired, UpdatePlanHash, dataImpactAcknowledgement: null, jobId: "job-42");

        result.Should().BeOfType<UpdateApplyResult.Applied>();

        await ((IDestructiveUpdateApplier)provisioner).DidNotReceive().ApplyDestructiveUpdateAsync(
            Arg.Any<ResourceHandle>(),
            Arg.Any<UpdatePlan>(),
            Arg.Any<string>(),
            Arg.Any<DataImpact?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void The_destructive_applier_takes_no_force_flag_and_no_acknowledgement_default()
    {
        var apply = typeof(IDestructiveUpdateApplier)
            .GetMethod(nameof(IDestructiveUpdateApplier.ApplyDestructiveUpdateAsync))
            .Should().NotBeNull().And.Subject.As<MethodInfo>();

        var parameters = apply.GetParameters();

        parameters.Select(p => p.Name ?? string.Empty)
            .Any(n => n.Contains("force", StringComparison.OrdinalIgnoreCase)
                || n.Contains("override", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse("no force/override flag may exist anywhere on the destructive path");

        var acknowledgement = parameters.Single(p => p.ParameterType == typeof(DataImpact?));
        acknowledgement.HasDefaultValue.Should().BeFalse(
            "an adapter must be told what was acknowledged rather than inheriting a default");

        // The plan hash still travels as its own argument, so the acknowledgement cannot stand in for it.
        parameters.Should().Contain(p => p.ParameterType == typeof(string));
    }
}
