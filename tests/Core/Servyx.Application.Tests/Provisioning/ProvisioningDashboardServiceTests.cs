using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Servyx.Application.Provisioning;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;

namespace Servyx.Application.Tests.Provisioning;

/// <summary>
/// Unit tests for <see cref="ProvisioningDashboardService"/>'s apply path — the single member on
/// <see cref="IProvisioningDashboard"/> that can create anything.
/// </summary>
/// <remarks>
/// The provisioner is an NSubstitute substitute, so nothing here knows Docker exists. The ledger is the
/// real <see cref="InMemoryProvisioningLedger"/> wherever a test needs to see what a sweep would find, so
/// the write-ahead ordering under test is the real executor's, not a simulation of it.
/// </remarks>
public class ProvisioningDashboardServiceTests
{
    private const string ProvisionerId = "docker-container";
    private const string PlanHash = "abc123def456abc123def456abc123def456abc123def456abc123def456abcd";

    private static readonly IReadOnlyDictionary<string, string> Tags =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["servyx.managed"] = "true" };

    private static readonly ProvisioningRequest Request = new(
        GameDefinitionId: "palworld",
        DeploymentProfileId: "docker",
        ConnectorId: "docker-container-local",
        Parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = "servyx-preview" });

    private static ProvisioningPlan Plan(string hash = PlanHash) => new(
        PlanId: $"{ProvisionerId}:servyx-preview:{hash[..12]}",
        PlanHash: hash,
        Stages: [new("create-container", ProvisionerId, "Create container 'servyx-preview'.")],
        EstimatedCost: CostEstimate.Unknown("Local Docker containers are not billed by a provider."),
        ExpiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static ProvisionedResource Resource(string containerId = "c0ffee1234ab") => new(
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

    private static IProvisioner Provisioner(ProvisioningPlan? plan = null, IProvisioningOperation? operation = null)
    {
        // Both are materialised before any Returns() call: building a substitute inside Returns() clobbers
        // NSubstitute's "last call" context.
        var plannedResult = Task.FromResult(plan ?? Plan());
        var createdOperation = operation ?? Operation();

        var provisioner = Substitute.For<IProvisioner>();
        provisioner.ProvisionerId.Returns(ProvisionerId);
        provisioner.Capabilities.Returns(ProvisioningCapabilities.Create | ProvisioningCapabilities.Destroy);
        provisioner.PlanAsync(Arg.Any<ProvisioningRequest>(), Arg.Any<CancellationToken>()).Returns(plannedResult);
        provisioner.CreateOperation(Arg.Any<ProvisioningRequest>()).Returns(createdOperation);
        return provisioner;
    }

    [Fact]
    public async Task Apply_drives_the_operation_exactly_once_and_writes_a_ledger_row()
    {
        var ledger = Substitute.For<IProvisioningLedger>();
        var operation = Operation();
        var provisioner = Provisioner(operation: operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyAsync(ProvisionerId, Request, PlanHash, "job-42");

        result.Should().BeOfType<ProvisioningApplyResult.Applied>()
            .Which.Resource.Handle.ProviderResourceId.Should().Be("c0ffee1234ab");

        provisioner.Received(1).CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.Received(1).CreateAsync(Arg.Any<CancellationToken>());
        await operation.DidNotReceive().CompensateAsync(Arg.Any<CancellationToken>());

        Received.InOrder(() =>
        {
            ledger.RecordIntentAsync(Arg.Any<ProvisioningIntent>(), Arg.Any<CancellationToken>());
            operation.CreateAsync(Arg.Any<CancellationToken>());
            ledger.MarkCreatedAsync(Arg.Any<Guid>(), "c0ffee1234ab", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Apply_records_the_job_id_on_the_ledger_row()
    {
        var ledger = Substitute.For<IProvisioningLedger>();
        ProvisioningIntent? captured = null;
        ledger.RecordIntentAsync(Arg.Do<ProvisioningIntent>(i => captured = i), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var dashboard = new ProvisioningDashboardService([Provisioner()], ledger, new ProvisioningExecutor(ledger));

        await dashboard.ApplyAsync(ProvisionerId, Request, PlanHash, "job-42");

        captured.Should().NotBeNull();
        captured!.JobId.Should().Be("job-42");
        captured.ProvisionerId.Should().Be(ProvisionerId);
        captured.Tags.Should().BeEquivalentTo(Tags);
    }

    [Fact]
    public async Task A_stale_plan_hash_is_refused_and_nothing_downstream_of_the_check_is_ever_invoked()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        // The provisioner now plans to something else than what the caller was shown.
        var provisioner = Provisioner(Plan("0000000000000000000000000000000000000000000000000000000000000000"), operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyAsync(ProvisionerId, Request, PlanHash, "job-42");

        var stale = result.Should().BeOfType<ProvisioningApplyResult.Stale>().Which;
        stale.ExpectedPlanHash.Should().Be(PlanHash);
        stale.CurrentPlanHash.Should().Be("0000000000000000000000000000000000000000000000000000000000000000");
        stale.Message.Should().Contain("stale").And.Contain("Preview again");

        // Non-invocation is the assertion, not the message. CreateOperation is the only route to a
        // provider mutation, and RecordIntentAsync is the executor's very first act.
        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        await operation.DidNotReceive().CompensateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty(
            "a refusal must not leave a write-ahead row, because nothing was attempted");
    }

    [Fact]
    public async Task There_is_no_argument_that_lets_a_caller_apply_a_stale_plan()
    {
        // Guards the no-force-flag rule by construction: the only hash-bearing parameter is the one that is
        // compared, so no overload or value can bypass the comparison. An empty/blank hash is rejected
        // outright rather than being treated as "don't check".
        var ledger = new InMemoryProvisioningLedger();
        var dashboard = new ProvisioningDashboardService([Provisioner()], ledger, new ProvisioningExecutor(ledger));

        var blank = async () => await dashboard.ApplyAsync(ProvisionerId, Request, "   ");
        await blank.Should().ThrowAsync<ArgumentException>();

        var applyParameters = typeof(IProvisioningDashboard).GetMethods()
            .Where(m => m.Name == nameof(IProvisioningDashboard.ApplyAsync))
            .SelectMany(m => m.GetParameters())
            .Select(p => p.Name ?? string.Empty)
            .ToList();

        applyParameters.Should().NotBeEmpty();
        applyParameters
            .Any(n => n.Contains("force", StringComparison.OrdinalIgnoreCase)
                || n.Contains("override", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse("no force/override flag may exist anywhere on the apply path");
    }

    [Fact]
    public async Task A_failed_create_is_surfaced_with_its_ledger_row_and_the_row_stays_intended_for_a_sweep()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        operation.CreateAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("port 8211 is already allocated"));

        var dashboard = new ProvisioningDashboardService(
            [Provisioner(operation: operation)], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyAsync(ProvisionerId, Request, PlanHash, "job-42");

        var failed = result.Should().BeOfType<ProvisioningApplyResult.Failed>().Which;
        failed.Message.Should().Contain("Provisioning failed").And.Contain("reconciliation");
        failed.LedgerRowId.Should().NotBe(Guid.Empty);
        failed.Compensated.Should().BeTrue();

        await operation.Received(1).CompensateAsync(Arg.Any<CancellationToken>());

        var intended = await ledger.ListIntendedAsync(ProvisionerId);
        intended.Should().ContainSingle("intent-before-effect must still hold when the create fails");
        intended[0].LedgerRowId.Should().Be(failed.LedgerRowId);
        intended[0].Tags.Should().BeEquivalentTo(Tags);
    }

    [Fact]
    public async Task A_failed_compensation_is_reported_as_a_possible_orphan_rather_than_a_clean_failure()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        operation.CreateAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("create failed"));
        operation.CompensateAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("remove timed out"));

        var dashboard = new ProvisioningDashboardService(
            [Provisioner(operation: operation)], ledger, new ProvisioningExecutor(ledger));

        var result = await dashboard.ApplyAsync(ProvisionerId, Request, PlanHash);

        var failed = result.Should().BeOfType<ProvisioningApplyResult.Failed>().Which;
        failed.Compensated.Should().BeFalse();
        failed.Message.Should().Contain("may still exist at the provider");
    }

    [Fact]
    public async Task Applying_through_a_dashboard_with_no_executor_throws_rather_than_quietly_doing_nothing()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        var provisioner = Provisioner(operation: operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger);

        dashboard.ExecutionConfigured.Should().BeFalse();

        var act = async () => await dashboard.ApplyAsync(ProvisionerId, Request, PlanHash);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(nameof(ProvisioningExecutor));

        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Applying_with_an_unregistered_provisioner_id_throws_and_touches_nothing()
    {
        var ledger = new InMemoryProvisioningLedger();
        var provisioner = Provisioner();
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var act = async () => await dashboard.ApplyAsync("hetzner", Request, PlanHash);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("hetzner");

        await provisioner.DidNotReceive().PlanAsync(Arg.Any<ProvisioningRequest>(), Arg.Any<CancellationToken>());
        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
    }

    [Fact]
    public async Task Previewing_alone_never_reaches_the_executor_even_when_one_is_configured()
    {
        var ledger = new InMemoryProvisioningLedger();
        var operation = Operation();
        var provisioner = Provisioner(operation: operation);
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var plan = await dashboard.PlanAsync(ProvisionerId, Request);

        plan.PlanHash.Should().Be(PlanHash);
        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
        await operation.DidNotReceive().CreateAsync(Arg.Any<CancellationToken>());
        (await ledger.ListIntendedAsync(ProvisionerId)).Should().BeEmpty(
            "a preview must leave no write-ahead row, which is direct evidence the executor never ran");
    }

    [Fact]
    public async Task Null_and_blank_arguments_are_rejected_before_anything_is_planned_or_created()
    {
        var ledger = new InMemoryProvisioningLedger();
        var provisioner = Provisioner();
        var dashboard = new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger));

        var blankProvisioner = async () => await dashboard.ApplyAsync(" ", Request, PlanHash);
        var nullRequest = async () => await dashboard.ApplyAsync(ProvisionerId, null!, PlanHash);

        await blankProvisioner.Should().ThrowAsync<ArgumentException>();
        await nullRequest.Should().ThrowAsync<ArgumentNullException>();

        await provisioner.DidNotReceive().PlanAsync(Arg.Any<ProvisioningRequest>(), Arg.Any<CancellationToken>());
        provisioner.DidNotReceive().CreateOperation(Arg.Any<ProvisioningRequest>());
    }

    [Fact]
    public void A_dashboard_with_an_executor_reports_that_it_can_apply()
    {
        var ledger = new InMemoryProvisioningLedger();

        new ProvisioningDashboardService([Provisioner()], ledger, new ProvisioningExecutor(ledger))
            .ExecutionConfigured.Should().BeTrue();
        new ProvisioningDashboardService([Provisioner()], ledger).ExecutionConfigured.Should().BeFalse();
        new ProvisioningDashboardService([Provisioner()]).LedgerConfigured.Should().BeFalse();
    }
}
