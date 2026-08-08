using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Application.Provisioning;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;
using Servyx.Web.Components.Pages.Deploy;
using Servyx.Web.Components.Shared;
using Servyx.Web.Services;
using Servyx.Composition;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// Binds <c>DeployPage</c> to fake provisioning services, never to live infrastructure — mirroring how
/// <c>DashboardTests</c> binds the dashboard to <c>MockDashboardDataService</c>. No test here can reach a
/// Docker daemon, a provider API, or a durable store.
/// </summary>
public class DeployPageTests : BunitContext
{
    private const string ProvisionerId = "docker-container";

    private static ProvisioningPlan UnknownCostPlan(CostEstimate? cost = null) => new(
        PlanId: "docker-container:servyx-preview:abc123def456",
        PlanHash: "abc123def456abc123def456abc123def456abc123def456abc123def456abcd",
        Stages:
        [
            new("create-container", ProvisionerId, "Create container 'servyx-preview' from image 'example:latest'."),
            new("publish-ports", ProvisionerId, "Publish 8211->8211/tcp to the host."),
            new("start-container", ProvisionerId, "Start container 'servyx-preview' and observe its assigned address."),
        ],
        EstimatedCost: cost ?? CostEstimate.Unknown("Local Docker containers are not billed by a provider."),
        ExpiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private FakeProvisioner EnableProvisioning(
        ProvisioningPlan? plan = null,
        IProvisioningLedger? ledger = null,
        ProvisioningCapabilities capabilities =
            ProvisioningCapabilities.Create | ProvisioningCapabilities.Destroy | ProvisioningCapabilities.TagQuery)
    {
        var provisioner = new FakeProvisioner(ProvisionerId, capabilities, plan ?? UnknownCostPlan());

        Services.AddSingleton(new ProvisioningGate(enabled: true));
        Services.AddSingleton<IProvisioningDashboard>(new ProvisioningDashboardService([provisioner], ledger));

        return provisioner;
    }

    /// <summary>
    /// The composition a host that has actually turned provisioning on has: a provisioner, a ledger, and a
    /// <see cref="ProvisioningExecutor"/> over that ledger — which is the only configuration in which the
    /// page renders a live Apply control at all.
    /// </summary>
    private FakeProvisioner EnableProvisioningWithExecution(
        RecordingProvisioningLedger ledger,
        RecordingProvisioningOperation operation,
        Func<ProvisioningRequest, ProvisioningPlan>? planFactory = null)
    {
        var provisioner = new FakeProvisioner(
            ProvisionerId,
            ProvisioningCapabilities.Create | ProvisioningCapabilities.Destroy | ProvisioningCapabilities.TagQuery,
            planFactory ?? (_ => UnknownCostPlan()),
            operation);

        Services.AddSingleton(new ProvisioningGate(enabled: true));
        Services.AddSingleton<IProvisioningDashboard>(
            new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger)));

        return provisioner;
    }

    /// <summary>An operation that succeeds, standing in for a container the Docker Engine really created.</summary>
    private static RecordingProvisioningOperation SucceedingOperation(string containerId = "c0ffee1234ab") =>
        new(CreatedResource(containerId)) { ProvisionerId = ProvisionerId, Tags = Labels };

    private static readonly IReadOnlyDictionary<string, string> Labels =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["servyx.managed"] = "true" };

    private static ProvisionedResource CreatedResource(string containerId) => new(
        Handle: new ResourceHandle(ProvisionerId, containerId, null, Labels),
        ConnectorId: "docker-container-local",
        Target: new TargetDescriptor(
            TransportId: "docker",
            Endpoint: "npipe://./pipe/dockerDesktopLinuxEngine",
            CredentialUrn: null,
            DockerContext: null,
            Options: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["containerId"] = containerId,
                ["rootPath"] = "/palworld",
            }),
        Facts: new ResourceFacts(null, "172.18.0.2", CostEstimate.Unknown("local docker"), DateTimeOffset.UnixEpoch));

    /// <summary>Previews a plan and waits for the confirmation step to appear.</summary>
    private static void Preview(IRenderedComponent<DeployPage> cut)
    {
        cut.Find("[data-testid='preview-plan']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='confirm-step']").Should().NotBeEmpty());
    }

    [Fact]
    public void FlagOff_RendersAnExplanation_AndNotASingleMutatingControl()
    {
        // Exactly what a default host has: a closed gate, and nothing else registered — no dashboard, no
        // provisioner, no ledger.
        Services.AddSingleton(new ProvisioningGate(enabled: false));

        var cut = Render<DeployPage>();

        var explanation = cut.Find("[data-testid='provisioning-disabled']");
        explanation.TextContent.Should().Contain(ProvisioningGate.ConfigurationKey);

        // No AuthenticationGate is registered here, and the page falls back to Enforced when it is absent —
        // so it must describe an authenticated instance rather than repeat the old "Servyx has no
        // authentication of any kind" copy, which stopped being true. The other branch is asserted by
        // WhenAuthenticationIsSwitchedOff_TheWarningsSayExactlyThat below.
        explanation.TextContent.Should().Contain("requires the operator password");
        explanation.TextContent.Should().NotContain("no authentication");

        // Not "the buttons are disabled" — there are no controls on the page at all.
        cut.FindAll("button").Should().BeEmpty();
        cut.FindAll("input").Should().BeEmpty();
        cut.FindAll("select").Should().BeEmpty();
        cut.FindAll("[data-testid='gated-button']").Should().BeEmpty();
        cut.FindAll("[data-testid='plan-section']").Should().BeEmpty();
        cut.FindAll("[data-testid='provisioner-row']").Should().BeEmpty();
    }

    [Fact]
    public void FlagOn_ListsProvisioners_WithTheirCapabilities()
    {
        EnableProvisioning();

        var cut = Render<DeployPage>();

        var rows = cut.FindAll("[data-testid='provisioner-row']");
        rows.Should().ContainSingle();
        rows[0].GetAttribute("data-provisioner-id").Should().Be(ProvisionerId);

        var text = rows[0].TextContent;
        text.Should().Contain("Create");
        text.Should().Contain("Destroy");
        text.Should().Contain("TagQuery");

        // The fake omits EstimatesCost, exactly as DockerContainerProvisioner does, and the page says so
        // rather than leaving the reader to discover it in the plan.
        text.Should().Contain("unknown");
    }

    [Fact]
    public void PlanPreview_RendersStages_WithoutEverExecutingAnything()
    {
        var ledger = new RecordingProvisioningLedger();
        var provisioner = EnableProvisioning(ledger: ledger);

        var cut = Render<DeployPage>();

        // Nothing is planned until the user asks: no plan is rendered on first load.
        cut.FindAll("[data-testid='plan-stage']").Should().BeEmpty();
        provisioner.PlanCalls.Should().Be(0);

        cut.Find("[data-testid='preview-plan']").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='plan-stage']").Should().HaveCount(3));

        var stages = cut.FindAll("[data-testid='plan-stage']");
        stages[0].GetAttribute("data-stage-id").Should().Be("create-container");
        stages[1].GetAttribute("data-stage-id").Should().Be("publish-ports");
        stages[2].GetAttribute("data-stage-id").Should().Be("start-container");
        stages[0].TextContent.Should().Contain("Create container 'servyx-preview'");

        provisioner.PlanCalls.Should().Be(1);

        // The negative that matters. ProvisioningExecutor.ExecuteAsync's very first act is to commit a
        // write-ahead intent row, and the only route to a provider mutation is CreateOperation ->
        // IProvisioningOperation.CreateAsync. All three counters being zero means no execution began.
        provisioner.CreateOperationCalls.Should().Be(0, "previewing a plan must never reach the create path");
        provisioner.Operation.CreateCalls.Should().Be(0, "no provider-mutating call may be made by a preview");
        ledger.RecordIntentCalls.Should().Be(0, "no write-ahead intent means ProvisioningExecutor never ran");
        ledger.MarkCreatedCalls.Should().Be(0);
    }

    [Fact]
    public void PlanPreview_RendersUnknownCostConfidence_AsTheLiteralWordUnknown()
    {
        EnableProvisioning();

        var cut = Render<DeployPage>();
        cut.Find("[data-testid='preview-plan']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='plan-stage']").Should().NotBeEmpty());

        var costs = cut.FindAll("[data-testid='cost-estimate']");
        costs.Should().NotBeEmpty();
        foreach (var cost in costs)
        {
            cost.TextContent.Trim().Should().Be("unknown");
        }

        cut.Find("[data-testid='cost-confidence']").TextContent.Trim()
            .Should().Be(nameof(CostConfidence.Unknown));

        // No fabricated zero anywhere the cost is shown.
        cut.Find("[data-testid='confirm-step']").TextContent.Should().NotContain("0.00");
    }

    [Fact]
    public void PlanPreview_ConfirmationStep_IsExplicit_AndItsApplyControlIsGated()
    {
        EnableProvisioning();

        var cut = Render<DeployPage>();
        cut.Find("[data-testid='preview-plan']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='confirm-step']").Should().NotBeEmpty());

        var confirm = cut.Find("[data-testid='confirm-step']");
        confirm.TextContent.Should().Contain("Nothing has been created");

        var apply = confirm.QuerySelector("[data-testid='gated-button']");
        apply.Should().NotBeNull();
        apply!.HasAttribute("disabled").Should().BeTrue();
        apply.GetAttribute("title").Should().Contain("not wired");
    }

    [Fact]
    public void FlagOn_WithNoLedger_SaysSoRatherThanShowingAnEmptyLedger()
    {
        EnableProvisioning();

        var cut = Render<DeployPage>();

        cut.FindAll("[data-testid='ledger-row']").Should().BeEmpty();
        cut.Find("[data-testid='ledger-unconfigured']").TextContent
            .Should().Contain("No provisioning ledger is configured");
    }

    [Fact]
    public void FlagOn_WithLedger_ListsEntriesWithTheirLifecycleState()
    {
        var ledger = new RecordingProvisioningLedger().Seed(new ProvisioningIntent(
            LedgerRowId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            ProvisionerId: ProvisionerId,
            Region: null,
            Tags: new Dictionary<string, string>(StringComparer.Ordinal),
            JobId: "job-1",
            RecordedAt: new DateTimeOffset(2026, 5, 4, 3, 2, 1, TimeSpan.Zero)));

        EnableProvisioning(ledger: ledger);

        var cut = Render<DeployPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='ledger-row']").Should().ContainSingle());

        var row = cut.Find("[data-testid='ledger-row']");
        row.TextContent.Should().Contain("11111111-2222-3333-4444-555555555555");
        row.QuerySelector("[data-testid='ledger-state']")!.TextContent.Trim()
            .Should().Be(nameof(ResourceLifecycleState.Intended));
    }

    [Fact]
    public void FlagOn_ButNoDashboardRegistered_SaysItIsNotWired_AndOffersNoControls()
    {
        Services.AddSingleton(new ProvisioningGate(enabled: true));

        var cut = Render<DeployPage>();

        cut.Find("[data-testid='provisioning-misconfigured']").TextContent
            .Should().Contain("IProvisioningDashboard");
        cut.FindAll("button").Should().BeEmpty();
    }

    // ── Apply ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOff_WithEveryProvisioningServiceRegistered_StillWritesNothingAndRendersNoApplyControl()
    {
        // The strong form of the flag-off guarantee. Unlike the first test in this file, here a
        // provisioner, a ledger and an executor ARE in the container — so if the page consulted anything
        // other than the gate, these counters would move.
        var ledger = new RecordingProvisioningLedger();
        var operation = SucceedingOperation();
        var provisioner = new FakeProvisioner(
            ProvisionerId,
            ProvisioningCapabilities.Create,
            UnknownCostPlan(),
            operation);

        Services.AddSingleton(new ProvisioningGate(enabled: false));
        Services.AddSingleton<IProvisioningDashboard>(
            new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger)));

        var cut = Render<DeployPage>();

        cut.Find("[data-testid='provisioning-disabled']").Should().NotBeNull();
        cut.FindAll("[data-testid='apply-plan']").Should().BeEmpty();
        cut.FindAll("[data-testid='gated-button']").Should().BeEmpty();
        cut.FindAll("button").Should().BeEmpty();

        provisioner.PlanCalls.Should().Be(0);
        provisioner.CreateOperationCalls.Should().Be(0);
        operation.CreateCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0, "a closed gate must produce no ledger write of any kind");
        ledger.MarkCreatedCalls.Should().Be(0);
    }

    [Fact]
    public void PlanPreview_WithExecutionConfigured_StillExecutesNothingUntilApplyIsClicked()
    {
        var ledger = new RecordingProvisioningLedger();
        var operation = SucceedingOperation();
        var provisioner = EnableProvisioningWithExecution(ledger, operation);

        var cut = Render<DeployPage>();
        Preview(cut);

        // The Apply control is present and live — and, crucially, has not been used.
        cut.Find("[data-testid='apply-plan']").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll("[data-testid='apply-success']").Should().BeEmpty();

        provisioner.PlanCalls.Should().Be(1);
        provisioner.CreateOperationCalls.Should().Be(0, "previewing must not reach the create path even when an executor exists");
        operation.CreateCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0, "no write-ahead intent means ProvisioningExecutor never ran");
        ledger.MarkCreatedCalls.Should().Be(0);
    }

    [Fact]
    public void Apply_RunsTheExecutorExactlyOnce_AndWritesALedgerRow()
    {
        var ledger = new RecordingProvisioningLedger();
        var operation = SucceedingOperation("c0ffee1234ab");
        var provisioner = EnableProvisioningWithExecution(ledger, operation);

        var cut = Render<DeployPage>();
        Preview(cut);

        cut.Find("[data-testid='apply-plan']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='apply-success']").Should().NotBeEmpty());

        // Exactly once, through the one sanctioned path.
        provisioner.CreateOperationCalls.Should().Be(1);
        operation.CreateCalls.Should().Be(1, "the provider-mutating call must happen exactly once per confirmation");
        operation.CompensateCalls.Should().Be(0);

        // Intent before effect, then resolved.
        ledger.RecordIntentCalls.Should().Be(1);
        ledger.MarkCreatedCalls.Should().Be(1);
        ledger.Intended.Should().BeEmpty("a confirmed creation leaves no unresolved row");
    }

    [Fact]
    public void Apply_ShowsTheCreatedResourceIdentity_AndItsTransportEndpoint()
    {
        var ledger = new RecordingProvisioningLedger();
        EnableProvisioningWithExecution(ledger, SucceedingOperation("c0ffee1234ab"));

        var cut = Render<DeployPage>();
        Preview(cut);

        cut.Find("[data-testid='apply-plan']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='apply-success']").Should().NotBeEmpty());

        cut.Find("[data-testid='applied-resource-id']").TextContent.Trim().Should().Be("c0ffee1234ab");
        cut.Find("[data-testid='applied-provisioner']").TextContent.Trim().Should().Be(ProvisionerId);
        cut.Find("[data-testid='applied-connector']").TextContent.Trim().Should().Be("docker-container-local");
        cut.Find("[data-testid='applied-transport']").TextContent.Trim().Should().Be("docker");
        cut.Find("[data-testid='applied-endpoint']").TextContent.Trim()
            .Should().Be("npipe://./pipe/dockerDesktopLinuxEngine");

        // Re-clicking must not create a second container.
        cut.Find("[data-testid='apply-plan']").HasAttribute("disabled").Should().BeTrue();

        // And the page stops claiming nothing was created, because something was.
        cut.Find("[data-testid='confirm-step']").TextContent
            .Should().NotContain("Nothing has been created");
    }

    [Fact]
    public void Apply_WithAPlanHashThatHasDrifted_IsRefused_AndTheExecutorIsNeverInvoked()
    {
        // The plan hash tracks the container name, exactly as DockerContainerProvisioner's does. The user
        // previews, then edits the name — so the plan they approved is no longer the plan that would run.
        var ledger = new RecordingProvisioningLedger();
        var operation = SucceedingOperation();
        var provisioner = EnableProvisioningWithExecution(
            ledger,
            operation,
            planFactory: request => PlanFor(request.Parameters["containerName"]));

        var cut = Render<DeployPage>();
        Preview(cut);

        cut.Find("[data-testid='plan-id']").TextContent.Should().Contain("servyx-preview");

        // Drift: the input the plan was computed from changes after the user approved it.
        cut.Find("[data-testid='container-name']").Change("servyx-something-else");

        cut.Find("[data-testid='apply-plan']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='apply-stale']").Should().NotBeEmpty());

        cut.Find("[data-testid='apply-stale']").TextContent.Should().Contain("stale");
        cut.Find("[data-testid='apply-stale']").TextContent.Should().Contain("Preview again");
        cut.FindAll("[data-testid='apply-success']").Should().BeEmpty();

        // The assertion that matters, and it is a non-invocation, not the presence of a message.
        provisioner.CreateOperationCalls.Should().Be(0, "a stale plan must never reach the create path");
        operation.CreateCalls.Should().Be(0, "no provider-mutating call may follow a stale-plan refusal");
        operation.CompensateCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0, "a refusal happens before ProvisioningExecutor is entered at all");
        ledger.MarkCreatedCalls.Should().Be(0);
        ledger.Intended.Should().BeEmpty();
    }

    [Fact]
    public void Apply_WhenTheProviderCreateFails_ShowsTheError_AndLeavesAReconcilableLedgerRow()
    {
        var ledger = new RecordingProvisioningLedger();
        var operation = new RecordingProvisioningOperation(
            _ => Task.FromException<ProvisionedResource>(new InvalidOperationException("port 8211 is already allocated")))
        {
            ProvisionerId = ProvisionerId,
            Tags = Labels,
        };

        var provisioner = EnableProvisioningWithExecution(ledger, operation);

        var cut = Render<DeployPage>();
        Preview(cut);

        cut.Find("[data-testid='apply-plan']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='apply-error']").Should().NotBeEmpty());

        var error = cut.Find("[data-testid='apply-error']");
        error.TextContent.Should().Contain("Provisioning failed");
        error.TextContent.Should().Contain("reconciliation");
        cut.FindAll("[data-testid='apply-success']").Should().BeEmpty();

        // Intent-before-effect still holds on the failure path: the attempt was tried and compensated…
        provisioner.CreateOperationCalls.Should().Be(1);
        operation.CreateCalls.Should().Be(1);
        operation.CompensateCalls.Should().Be(1);

        // …and the row a sweep must resolve is still there, still Intended, and now on screen.
        ledger.RecordIntentCalls.Should().Be(1);
        ledger.MarkCreatedCalls.Should().Be(0, "nothing was confirmed, so nothing may be marked Created");
        ledger.Intended.Should().ContainSingle();

        var rowId = ledger.Intended[0].LedgerRowId.ToString();
        error.QuerySelector("[data-testid='apply-error-ledger-row']")!.TextContent.Trim().Should().Be(rowId);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='ledger-row']").Should().ContainSingle());
        cut.Find("[data-testid='ledger-row']").TextContent.Should().Contain(rowId);
        cut.Find("[data-testid='ledger-row']").QuerySelector("[data-testid='ledger-state']")!.TextContent.Trim()
            .Should().Be(nameof(ResourceLifecycleState.Intended));
    }

    [Fact]
    public void Apply_DisablesTheControlAndShowsProgressWhileTheCallIsInFlight()
    {
        var gate = new TaskCompletionSource<ProvisionedResource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ledger = new RecordingProvisioningLedger();
        var operation = new RecordingProvisioningOperation(_ => gate.Task)
        {
            ProvisionerId = ProvisionerId,
            Tags = Labels,
        };

        EnableProvisioningWithExecution(ledger, operation);

        var cut = Render<DeployPage>();
        Preview(cut);

        cut.Find("[data-testid='apply-plan']").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='apply-progress']").Should().NotBeEmpty());
        cut.Find("[data-testid='apply-plan']").HasAttribute("disabled")
            .Should().BeTrue("the control must not be re-clickable while a create is in flight");

        gate.SetResult(CreatedResource("c0ffee1234ab"));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='apply-success']").Should().NotBeEmpty());
        cut.FindAll("[data-testid='apply-progress']").Should().BeEmpty();
        operation.CreateCalls.Should().Be(1);
    }

    [Fact]
    public void Apply_IsGatedRatherThanLive_WhenTheHostRegisteredNoExecutor()
    {
        // Same page, same plan, but the composition root supplied no ProvisioningExecutor. The page must
        // say so rather than render a control that would throw.
        var ledger = new RecordingProvisioningLedger();
        var provisioner = EnableProvisioning(ledger: ledger);

        var cut = Render<DeployPage>();
        Preview(cut);

        cut.FindAll("[data-testid='apply-plan']").Should().BeEmpty();
        var gated = cut.Find("[data-testid='gated-button']");
        gated.HasAttribute("disabled").Should().BeTrue();
        gated.GetAttribute("title").Should().Contain("not wired");

        provisioner.CreateOperationCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0);
    }

    [Fact]
    public void FlagOn_StatesThatProvisioningIsEnabled_AndWhoTheCapabilityBelongsTo()
    {
        EnableProvisioning();

        var cut = Render<DeployPage>();

        var banner = cut.Find("[data-testid='provisioning-enabled-warning']");
        banner.TextContent.Should().Contain(ProvisioningGate.ConfigurationKey);

        // With authentication in force — which is the default, and what an unregistered gate falls back to —
        // the page must not claim there is none. It names who the capability actually belongs to instead.
        banner.TextContent.Should().Contain("operator password");
        banner.TextContent.Should().NotContain("no authentication");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenAuthenticationIsSwitchedOff_TheWarningsSayExactlyThat(bool provisioningEnabled)
    {
        // The copy on this page may only claim Servyx is unauthenticated when it actually is. Both the
        // gate-closed and gate-open warnings are checked, because both used to assert it unconditionally.
        if (provisioningEnabled)
        {
            EnableProvisioning();
        }
        else
        {
            Services.AddSingleton(new ProvisioningGate(enabled: false));
        }

        Services.AddSingleton(new AuthenticationGate(enabled: false));

        var cut = Render<DeployPage>();

        var warning = cut.Find(provisioningEnabled
            ? "[data-testid='provisioning-enabled-warning']"
            : "[data-testid='provisioning-disabled']");

        warning.TextContent.Should().Contain("no authentication");
        warning.TextContent.Should().Contain(AuthenticationGate.ConfigurationKey);
    }

    [Fact]
    public void Apply_StillRendersUnknownCostAsUnknown_AfterASuccessfulApply()
    {
        var ledger = new RecordingProvisioningLedger();
        EnableProvisioningWithExecution(ledger, SucceedingOperation());

        var cut = Render<DeployPage>();
        Preview(cut);

        cut.Find("[data-testid='apply-plan']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='apply-success']").Should().NotBeEmpty());

        foreach (var cost in cut.FindAll("[data-testid='cost-estimate']"))
        {
            cost.TextContent.Trim().Should().Be("unknown");
        }

        cut.Find("[data-testid='cost-confidence']").TextContent.Trim()
            .Should().Be(nameof(CostConfidence.Unknown));
    }

    /// <summary>
    /// A plan whose hash — like <c>DockerContainerProvisioner</c>'s — is a function of the request, so
    /// editing an input genuinely changes it rather than a test pretending it did.
    /// </summary>
    private static ProvisioningPlan PlanFor(string containerName)
    {
        var hash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(containerName)));

        return new ProvisioningPlan(
            PlanId: $"{ProvisionerId}:{containerName}:{hash[..12]}",
            PlanHash: hash,
            Stages: [new("create-container", ProvisionerId, $"Create container '{containerName}'.")],
            EstimatedCost: CostEstimate.Unknown("Local Docker containers are not billed by a provider."),
            ExpiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    // ── Maintenance: the update-apply outcomes, and what the page actually sent ──────────────────────
    //
    // Complements DeployMaintenanceTests, which covers drift rendering, the update preview, and the
    // acknowledgement step's presence. What is asserted here and only here is (a) whether
    // IProvisioningDashboard.ApplyUpdateAsync was reached at all, and with which acknowledgement token,
    // rather than inferring it from the provisioner's downstream counters, and (b) that the refusal and
    // failure cases reach the screen instead of being swallowed.

    private const string RecordedInstanceId = "preview-instance-1";

    private static readonly Guid SeededRowId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private const ProvisioningCapabilities MaintainerCapabilities =
        ProvisioningCapabilities.Create
        | ProvisioningCapabilities.RecreateToUpdate
        | ProvisioningCapabilities.DetectDrift;

    /// <summary>
    /// The id the provider assigned the confirmed resource, deliberately unlike
    /// <see cref="RecordedInstanceId"/> so the two cannot be confused for one another on screen or in a
    /// handle.
    /// </summary>
    private const string RecordedProviderResourceId = "aa11bb22cc33";

    private static readonly DateTimeOffset RecordedAt = new(2026, 5, 4, 3, 2, 1, TimeSpan.Zero);

    /// <summary>The tags a real create path records at intent time.</summary>
    private static readonly IReadOnlyDictionary<string, string> RecordedTags =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.Managed] = ServyxTagKeys.ManagedValue,
            [ServyxTagKeys.InstanceId] = RecordedInstanceId,
            [ServyxTagKeys.JobId] = "job-1",
            [ServyxTagKeys.ConnectorId] = "docker-container-local",
        };

    /// <summary>
    /// A ledger holding one row the provider has confirmed — the only state that carries a
    /// provider-assigned id, and therefore the only state in which an update can be planned or applied.
    /// </summary>
    private static RecordingProvisioningLedger MaintenanceLedger() =>
        new RecordingProvisioningLedger().SeedCreated(new ProvisionedResourceRow(
            LedgerRowId: SeededRowId,
            Handle: new ResourceHandle(ProvisionerId, RecordedProviderResourceId, null, RecordedTags),
            JobId: "job-1",
            RecordedAt: RecordedAt,
            ConfirmedAt: RecordedAt.AddSeconds(42)));

    private static DriftResult Matching(ResourceHandle handle) => new(handle, []);

    /// <summary>
    /// An update plan whose single change is an image bump, so a test can assert the old → new pair the
    /// preview must render as well as the stages.
    /// </summary>
    private static UpdatePlan UpdatePlanFor(
        DataImpact impact,
        UpdateStrategy strategy = UpdateStrategy.Recreate,
        string planHash = "update-hash-at-preview")
    {
        // An InPlace plan may not carry a change that forces a recreate — UpdatePlan enforces it.
        var requiresRecreate = strategy == UpdateStrategy.Recreate;

        return new UpdatePlan(
            planId: "docker-container:servyx-preview:update",
            planHash: planHash,
            provisionerId: ProvisionerId,
            strategy: strategy,
            dataImpact: impact,
            changes: [new PlannedChange("image", "example:1.0", "example:2.0", requiresRecreate)],
            stages:
            [
                new("stop-container", ProvisionerId, "Stop container 'servyx-preview'."),
                new("remove-container", ProvisionerId, "Remove container 'servyx-preview'."),
                new("create-container", ProvisionerId, "Create container 'servyx-preview' from 'example:2.0'."),
            ],
            expiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private static FakeMaintainingProvisioner MaintainingProvisioner(
        Func<ResourceHandle, DriftResult> drift,
        UpdatePlan? updatePlan = null,
        RecordingProvisioningOperation? operation = null,
        Func<ResourceHandle, ProvisioningRequest, UpdatePlan?>? updatePlanFactory = null) =>
        updatePlanFactory is null
            ? new FakeMaintainingProvisioner(
                ProvisionerId, MaintainerCapabilities, UnknownCostPlan(), updatePlan, drift, operation)
            : new FakeMaintainingProvisioner(
                ProvisionerId, MaintainerCapabilities, _ => UnknownCostPlan(), updatePlanFactory, drift, operation);

    /// <summary>
    /// Registers the maintenance composition behind a <see cref="CountingDashboard"/>, so a test can assert
    /// the negative that matters directly — that <see cref="IProvisioningDashboard.ApplyUpdateAsync"/> was
    /// never reached — rather than inferring it from downstream counters alone.
    /// </summary>
    private CountingDashboard EnableMaintenance(
        IProvisioner provisioner,
        RecordingProvisioningLedger ledger)
    {
        var dashboard = new CountingDashboard(
            new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger)));

        Services.AddSingleton(new ProvisioningGate(enabled: true));
        Services.AddSingleton<IProvisioningDashboard>(dashboard);

        return dashboard;
    }

    /// <summary>Opens the update preview for the single ledger row and waits for its stages.</summary>
    private static void PlanUpdate(IRenderedComponent<DeployPage> cut)
    {
        cut.Find("[data-testid='plan-update']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='update-stage']").Should().NotBeEmpty());
    }

    [Fact]
    public void PlanUpdate_NeverReachesApplyUpdateAsync()
    {
        var ledger = MaintenanceLedger();
        var operation = SucceedingOperation();
        var provisioner = MaintainingProvisioner(Matching, UpdatePlanFor(DataImpact.AtRisk), operation);
        var dashboard = EnableMaintenance(provisioner, ledger);

        var cut = Render<DeployPage>();

        // Nothing is planned until the user asks.
        cut.FindAll("[data-testid='update-stage']").Should().BeEmpty();
        provisioner.PlanUpdateCalls.Should().Be(0);

        PlanUpdate(cut);

        cut.FindAll("[data-testid='update-stage']").Should().HaveCount(3);
        cut.Find("[data-testid='data-impact']").GetAttribute("data-impact")
            .Should().Be(nameof(DataImpact.AtRisk));

        // The negative that matters, asserted as a non-invocation of the exact member that can mutate —
        // not merely as the absence of a success box.
        dashboard.ApplyUpdateCalls.Should().Be(0, "previewing an update must never reach ApplyUpdateAsync");
        provisioner.PlanUpdateCalls.Should().Be(1);
        provisioner.CreateOperationCalls.Should().Be(0);
        operation.CreateCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0, "no write-ahead intent means ProvisioningExecutor never ran");
    }

    [Theory]
    [InlineData(DataImpact.AtRisk)]
    [InlineData(DataImpact.Destroyed)]
    public void ApplyUpdate_SendsNothingWithoutTheAcknowledgement_ThenSendsTheMatchingToken(DataImpact impact)
    {
        var ledger = MaintenanceLedger();
        var provisioner = MaintainingProvisioner(Matching, UpdatePlanFor(impact), SucceedingOperation());
        var dashboard = EnableMaintenance(provisioner, ledger);

        var cut = Render<DeployPage>();
        PlanUpdate(cut);

        // The acknowledgement is a separate control, not part of the confirm button.
        cut.Find("[data-testid='update-acknowledgement-step']")
            .QuerySelector("[data-testid='apply-update']")
            .Should().BeNull("the acknowledgement must not be part of the confirm control");

        // Un-acknowledged, the page sends no request at all — this is the assertion that the UI, and not
        // only the Application layer, refuses.
        cut.Find("[data-testid='apply-update']").Click();
        dashboard.ApplyUpdateCalls.Should().Be(0, "the UI must not send an apply without the acknowledgement");

        cut.Find("[data-testid='update-acknowledge']").Change(true);
        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='apply-update']").HasAttribute("disabled").Should().BeFalse());

        cut.Find("[data-testid='apply-update']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='update-success']").Should().NotBeEmpty());

        // The token that travelled is the one named after the impact the plan actually stated, and it was
        // minted from the checkbox rather than from the plan.
        dashboard.ApplyUpdateCalls.Should().Be(1);
        dashboard.LastAcknowledgement.Should().NotBeNull();
        dashboard.LastAcknowledgement!.Acknowledged.Should().Be(impact);
    }

    [Fact]
    public void PlanUpdate_ThatPreservesData_AppliesWithANullAcknowledgementToken()
    {
        var ledger = MaintenanceLedger();
        var operation = SucceedingOperation("f00dfeed5678");
        var provisioner = MaintainingProvisioner(
            Matching,
            UpdatePlanFor(DataImpact.Preserved, UpdateStrategy.InPlace),
            operation);
        var dashboard = EnableMaintenance(provisioner, ledger);

        var cut = Render<DeployPage>();
        PlanUpdate(cut);

        // A preserving plan asks for no second approval — there is no token for Preserved to mint.
        cut.FindAll("[data-testid='update-acknowledgement-step']").Should().BeEmpty();
        cut.Find("[data-testid='data-impact']").GetAttribute("data-severity")
            .Should().Be(DataImpactBanner.CalmSeverity);
        cut.Find("[data-testid='apply-update']").HasAttribute("disabled").Should().BeFalse();

        cut.Find("[data-testid='apply-update']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='update-success']").Should().NotBeEmpty());

        dashboard.ApplyUpdateCalls.Should().Be(1);
        dashboard.LastAcknowledgement.Should().BeNull("a preserving update is approved by supplying no token");

        cut.Find("[data-testid='updated-resource-id']").TextContent.Trim().Should().Be("f00dfeed5678");
        cut.Find("[data-testid='updated-strategy']").TextContent.Trim()
            .Should().Be(nameof(UpdateStrategy.InPlace));
        operation.CreateCalls.Should().Be(1);
        ledger.RecordIntentCalls.Should().Be(1, "intent is written before the provider is contacted");
    }

    [Fact]
    public void ApplyUpdate_WhenThePlanWentStale_ShowsBothHashes_AndExecutesNothing()
    {
        // The live resource changes under the operator between preview and confirmation: the same inputs
        // now hash to something else, exactly as an update plan is defined to.
        var planCalls = 0;
        var ledger = MaintenanceLedger();
        var operation = SucceedingOperation();
        var provisioner = MaintainingProvisioner(
            Matching,
            operation: operation,
            updatePlanFactory: (_, _) =>
            {
                planCalls++;
                return UpdatePlanFor(
                    DataImpact.Preserved,
                    UpdateStrategy.InPlace,
                    planCalls == 1 ? "update-hash-at-preview" : "update-hash-now");
            });
        var dashboard = EnableMaintenance(provisioner, ledger);

        var cut = Render<DeployPage>();
        PlanUpdate(cut);

        cut.Find("[data-testid='apply-update']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='update-stale']").Should().NotBeEmpty());

        var stale = cut.Find("[data-testid='update-stale']").TextContent;
        stale.Should().Contain("stale");
        stale.Should().Contain("update-hash-at-preview", "the approved hash must be named");
        stale.Should().Contain("update-hash-now", "the hash that would run now must be named");
        stale.Should().Contain("Preview again");

        cut.FindAll("[data-testid='update-success']").Should().BeEmpty();

        // The refusal happened before anything executed.
        dashboard.ApplyUpdateCalls.Should().Be(1);
        provisioner.CreateOperationCalls.Should().Be(0, "a stale update must never reach the create path");
        operation.CreateCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0, "a refusal happens before ProvisioningExecutor is entered");
    }

    [Fact]
    public void ApplyUpdate_WhenTheAcknowledgedImpactIsNoLongerThePlansImpact_IsRefusedAndSaysSo()
    {
        // Same hash, different impact on revalidation: the operator acknowledged AtRisk and the plan that
        // would actually run destroys data. Acknowledging one impact never authorises another.
        var planCalls = 0;
        var ledger = MaintenanceLedger();
        var operation = SucceedingOperation();
        var provisioner = MaintainingProvisioner(
            Matching,
            operation: operation,
            updatePlanFactory: (_, _) =>
            {
                planCalls++;
                return UpdatePlanFor(planCalls == 1 ? DataImpact.AtRisk : DataImpact.Destroyed);
            });
        var dashboard = EnableMaintenance(provisioner, ledger);

        var cut = Render<DeployPage>();
        PlanUpdate(cut);

        cut.Find("[data-testid='update-acknowledge']").Change(true);
        cut.Find("[data-testid='apply-update']").Click();
        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='update-requires-acknowledgement']").Should().NotBeEmpty());

        var refusal = cut.Find("[data-testid='update-requires-acknowledgement']").TextContent;
        refusal.Should().Contain(nameof(DataImpact.Destroyed));
        refusal.Should().Contain(nameof(DataImpact.AtRisk));
        refusal.Should().Contain("Nothing was changed");

        dashboard.ApplyUpdateCalls.Should().Be(1);
        provisioner.CreateOperationCalls.Should().Be(0);
        operation.CreateCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0);
    }

    [Fact]
    public void ApplyUpdate_WhenTheProviderFails_ShowsTheError_AndTheReconcilableLedgerRow()
    {
        var ledger = MaintenanceLedger();
        var operation = new RecordingProvisioningOperation(
            _ => Task.FromException<ProvisionedResource>(new InvalidOperationException("image pull failed")))
        {
            ProvisionerId = ProvisionerId,
            Tags = Labels,
        };

        var provisioner = MaintainingProvisioner(
            Matching,
            UpdatePlanFor(DataImpact.Preserved, UpdateStrategy.InPlace),
            operation);
        EnableMaintenance(provisioner, ledger);

        var cut = Render<DeployPage>();
        PlanUpdate(cut);

        cut.Find("[data-testid='apply-update']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='update-apply-error']").Should().NotBeEmpty());

        var error = cut.Find("[data-testid='update-apply-error']");
        error.TextContent.Should().Contain("Provisioning failed");
        error.TextContent.Should().Contain("reconciliation");
        cut.FindAll("[data-testid='update-success']").Should().BeEmpty();

        operation.CreateCalls.Should().Be(1);
        operation.CompensateCalls.Should().Be(1);
        error.TextContent.Should().NotContain(
            "Compensation did not complete",
            "compensation did complete here, and the page must not claim otherwise");

        // The row a sweep must resolve is the one the executor wrote, and it is named on screen.
        var failedRow = ledger.Intended.Should().ContainSingle(i => i.LedgerRowId != SeededRowId).Subject;
        error.QuerySelector("[data-testid='update-error-ledger-row']")!.TextContent.Trim()
            .Should().Be(failedRow.LedgerRowId.ToString());
        ledger.MarkCreatedCalls.Should().Be(0, "nothing was confirmed, so nothing may be marked Created");
    }

    /// <summary>
    /// A pass-through <see cref="IProvisioningDashboard"/> over the real
    /// <see cref="ProvisioningDashboardService"/> that counts calls to the update-apply member and captures
    /// the acknowledgement token supplied with them.
    /// </summary>
    /// <remarks>
    /// It wraps rather than replaces the real service on purpose: every refusal a test observes is the
    /// Application layer's own, not a simulation of it. The counter exists so "the UI did not send an apply
    /// request" can be asserted as a non-invocation of the exact member, rather than inferred from the
    /// provisioner's downstream counters.
    /// </remarks>
    private sealed class CountingDashboard : IProvisioningDashboard
    {
        private readonly IProvisioningDashboard _inner;

        public CountingDashboard(IProvisioningDashboard inner) => _inner = inner;

        /// <summary>How many times the mutating update member was reached. Must stay zero without an ack.</summary>
        public int ApplyUpdateCalls { get; private set; }

        /// <summary>The token supplied on the most recent apply, which is null for a preserving plan.</summary>
        public DataImpactAcknowledgement? LastAcknowledgement { get; private set; }

        public bool LedgerConfigured => _inner.LedgerConfigured;

        public bool ExecutionConfigured => _inner.ExecutionConfigured;

        public IReadOnlyList<ProvisionerDescriptor> ListProvisioners() => _inner.ListProvisioners();

        public Task<ProvisioningPlan> PlanAsync(
            string provisionerId, ProvisioningRequest request, CancellationToken ct = default)
            => _inner.PlanAsync(provisionerId, request, ct);

        public Task<ProvisioningApplyResult> ApplyAsync(
            string provisionerId,
            ProvisioningRequest request,
            string approvedPlanHash,
            string? jobId = null,
            CancellationToken ct = default)
            => _inner.ApplyAsync(provisionerId, request, approvedPlanHash, jobId, ct);

        public Task<PlanUpdateResult> PlanUpdateAsync(
            string provisionerId,
            ResourceHandle handle,
            ProvisioningRequest desired,
            CancellationToken ct = default)
            => _inner.PlanUpdateAsync(provisionerId, handle, desired, ct);

        public Task<UpdateApplyResult> ApplyUpdateAsync(
            string provisionerId,
            ResourceHandle handle,
            ProvisioningRequest desired,
            string approvedPlanHash,
            DataImpactAcknowledgement? dataImpactAcknowledgement,
            string? jobId = null,
            CancellationToken ct = default)
        {
            ApplyUpdateCalls++;
            LastAcknowledgement = dataImpactAcknowledgement;

            return _inner.ApplyUpdateAsync(
                provisionerId, handle, desired, approvedPlanHash, dataImpactAcknowledgement, jobId, ct);
        }

        public Task<DriftCheckResult> DetectDriftAsync(
            string provisionerId, ResourceHandle handle, CancellationToken ct = default)
            => _inner.DetectDriftAsync(provisionerId, handle, ct);

        public Task<IReadOnlyList<ProvisioningLedgerEntry>> ListLedgerEntriesAsync(CancellationToken ct = default)
            => _inner.ListLedgerEntriesAsync(ct);
    }
}
