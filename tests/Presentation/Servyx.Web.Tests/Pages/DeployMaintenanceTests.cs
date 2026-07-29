using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Application.Provisioning;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;
using Servyx.Web.Components.Pages.Deploy;
using Servyx.Web.Components.Shared;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// The maintenance half of <c>DeployPage</c>: drift status per ledger row, the update preview, and the
/// two-step confirmation that guards it. As in <see cref="DeployPageTests"/>, everything is bound to fakes
/// — no test here can reach a Docker daemon, a provider API, or a durable store.
/// </summary>
public class DeployMaintenanceTests : BunitContext
{
    private const string ProvisionerId = "docker-container";
    private const string InstanceId = "srv-001";

    /// <summary>
    /// The id the <em>provider</em> assigned, deliberately unlike <see cref="InstanceId"/>. Keeping the two
    /// visibly different is what makes these tests able to tell a handle built from the ledger's recorded
    /// provider id apart from one scavenged out of the row's <c>servyx.instance-id</c> tag.
    /// </summary>
    private const string ProviderResourceId = "c0ffee1234ab";

    private static readonly Guid RowId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static readonly DateTimeOffset RecordedAt = new(2026, 5, 4, 3, 2, 1, TimeSpan.Zero);

    /// <summary>The canonical Servyx identity a real ledger row carries, including the instance id.</summary>
    private static IReadOnlyDictionary<string, string> IdentifiedTags =>
        ServyxTagKeys.Build(InstanceId, "job-1", "docker-container-local");

    /// <summary>
    /// A ledger holding one row the provider has <em>confirmed</em> — the only state in which drift can be
    /// checked or an update planned, because it is the only state that carries a provider-assigned id.
    /// </summary>
    private static RecordingProvisioningLedger SeededLedger(IReadOnlyDictionary<string, string>? tags = null) =>
        new RecordingProvisioningLedger().SeedCreated(new ProvisionedResourceRow(
            LedgerRowId: RowId,
            Handle: new ResourceHandle(ProvisionerId, ProviderResourceId, null, tags ?? IdentifiedTags),
            JobId: "job-1",
            RecordedAt: RecordedAt,
            ConfirmedAt: RecordedAt.AddSeconds(42)));

    /// <summary>
    /// A ledger holding one row that is still an unresolved write-ahead intent: committed before the
    /// provider was contacted, so it has no provider-assigned id and nothing to inspect.
    /// </summary>
    private static RecordingProvisioningLedger SeededIntendedLedger() =>
        new RecordingProvisioningLedger().Seed(new ProvisioningIntent(
            LedgerRowId: RowId,
            ProvisionerId: ProvisionerId,
            Region: null,
            Tags: IdentifiedTags,
            JobId: "job-1",
            RecordedAt: RecordedAt));

    private static ProvisioningPlan CreatePlan() => new(
        PlanId: "docker-container:servyx-preview:abc123",
        PlanHash: "abc123abc123abc123abc123abc123abc123abc123abc123abc123abc123abcd",
        Stages: [new("create-container", ProvisionerId, "Create container 'servyx-preview'.")],
        EstimatedCost: CostEstimate.Unknown("Local Docker containers are not billed by a provider."),
        ExpiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// An update plan whose single change is the image, so the preview has a genuine old -> new identity to
    /// render rather than a placeholder.
    /// </summary>
    private static UpdatePlan ImageUpdatePlan(DataImpact impact)
    {
        var recreate = impact != DataImpact.Preserved;

        return new UpdatePlan(
            planId: "docker-container:srv-001:update-1",
            planHash: $"updatehash-{impact}",
            provisionerId: ProvisionerId,
            strategy: recreate ? UpdateStrategy.Recreate : UpdateStrategy.InPlace,
            dataImpact: impact,
            changes: [new PlannedChange("image", "example:1.0", "example:2.0", recreate)],
            stages: recreate
                ?
                [
                    new("stop-container", ProvisionerId, "Stop container 'srv-001'."),
                    new("remove-container", ProvisionerId, "Remove container 'srv-001'."),
                    new("create-container", ProvisionerId, "Create container 'srv-001' from image 'example:2.0'."),
                ]
                : [new("retag-container", ProvisionerId, "Retag container 'srv-001' to image 'example:2.0'.")],
            expiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private static readonly IReadOnlyDictionary<string, string> Labels =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["servyx.managed"] = "true" };

    private static RecordingProvisioningOperation SucceedingOperation() =>
        new(new ProvisionedResource(
            Handle: new ResourceHandle(ProvisionerId, "c0ffee1234ab", null, Labels),
            ConnectorId: "docker-container-local",
            Target: new TargetDescriptor("docker", "npipe://./pipe/dockerDesktopLinuxEngine", null, null,
                new Dictionary<string, string>(StringComparer.Ordinal)),
            Facts: new ResourceFacts(null, "172.18.0.2", CostEstimate.Unknown("local docker"), DateTimeOffset.UnixEpoch)))
        {
            ProvisionerId = ProvisionerId,
            Tags = Labels,
        };

    /// <summary>Registers a provisioner that <em>is</em> an <see cref="IMaintainer"/>, plus an executor.</summary>
    private FakeMaintainingProvisioner EnableMaintenance(
        RecordingProvisioningLedger ledger,
        UpdatePlan? updatePlan = null,
        Func<ResourceHandle, DriftResult>? drift = null,
        RecordingProvisioningOperation? operation = null,
        bool withExecutor = true,
        bool gateEnabled = true)
    {
        var provisioner = new FakeMaintainingProvisioner(
            ProvisionerId,
            ProvisioningCapabilities.Create | ProvisioningCapabilities.DetectDrift
                | ProvisioningCapabilities.RecreateToUpdate,
            CreatePlan(),
            updatePlan ?? ImageUpdatePlan(DataImpact.Preserved),
            drift ?? (handle => new DriftResult(handle, [])),
            operation ?? SucceedingOperation());

        Services.AddSingleton(new ProvisioningGate(gateEnabled));
        Services.AddSingleton<IProvisioningDashboard>(new ProvisioningDashboardService(
            [provisioner],
            ledger,
            withExecutor ? new ProvisioningExecutor(ledger) : null));

        return provisioner;
    }

    private static IRenderedComponent<DeployPage> PlanUpdate(IRenderedComponent<DeployPage> cut)
    {
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='plan-update']").Should().NotBeEmpty());
        cut.Find("[data-testid='plan-update']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='update-confirm-step']").Should().NotBeEmpty());
        return cut;
    }

    // ── Drift ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LedgerRow_ShowsThatTheLiveResourceMatchesWhatServyxRecorded()
    {
        var provisioner = EnableMaintenance(SeededLedger());

        var cut = Render<DeployPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='ledger-drift']").Should().ContainSingle());

        var badge = cut.Find("[data-testid='ledger-drift']");
        badge.GetAttribute("data-drift").Should().Be("matches");
        badge.TextContent.Trim().Should().Be("matches");
        badge.ClassList.Should().Contain("svx-drift-matches");

        cut.FindAll("[data-testid='drift-divergence']").Should().BeEmpty();

        // Read only: checking drift must never reach the create path.
        provisioner.DetectDriftCalls.Should().Be(1);
        provisioner.CreateOperationCalls.Should().Be(0);
        provisioner.Operation.CreateCalls.Should().Be(0);
    }

    [Fact]
    public void LedgerRow_ShowsDivergence_AndNamesEachOne_IncludingNamingDivergences()
    {
        var provisioner = EnableMaintenance(
            SeededLedger(),
            drift: handle => new DriftResult(handle,
            [
                new DriftDivergence("image", "example:1.0", "example:2.0"),
                new DriftDivergence($"label {ServyxTagKeys.InstanceId}", InstanceId, "srv-renamed"),
            ]));

        var cut = Render<DeployPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='ledger-drift']").Should().ContainSingle());

        var badge = cut.Find("[data-testid='ledger-drift']");
        badge.GetAttribute("data-drift").Should().Be("diverged");
        badge.TextContent.Should().Contain("diverged (2)");
        badge.ClassList.Should().Contain("svx-drift-diverged");

        var divergences = cut.FindAll("[data-testid='drift-divergence']");
        divergences.Should().HaveCount(2);
        divergences[0].GetAttribute("data-aspect").Should().Be("image");
        divergences[0].TextContent.Should().Contain("example:1.0").And.Contain("example:2.0");

        // The naming divergence is reported under the label's own aspect name, not folded into a total.
        divergences[1].GetAttribute("data-aspect").Should().Be($"label {ServyxTagKeys.InstanceId}");
        divergences[1].TextContent.Should().Contain("srv-renamed");

        provisioner.DetectDriftCalls.Should().Be(1);
    }

    [Fact]
    public void CreatedRow_IsCheckedAgainstTheProviderAssignedId_NotAgainstTheInstanceIdTag()
    {
        // The heart of the fix. The ledger row records both a servyx.instance-id tag ("srv-001") and the id
        // the provider actually assigned ("c0ffee1234ab"), and they are deliberately different — so the
        // handle the page checks drift against names which of the two it resolved from the ledger.
        ResourceHandle? checkedAgainst = null;
        var provisioner = EnableMaintenance(
            SeededLedger(),
            drift: handle =>
            {
                checkedAgainst = handle;
                return new DriftResult(handle, []);
            });

        var cut = Render<DeployPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='ledger-drift']").Should().ContainSingle());

        checkedAgainst.Should().NotBeNull();
        checkedAgainst!.ProviderResourceId.Should().Be(ProviderResourceId);
        checkedAgainst.ProviderResourceId.Should().NotBe(InstanceId,
            "the handle must come from the ledger's recorded provider id, not from a tag fallback");
        checkedAgainst.ProvisionerId.Should().Be(ProvisionerId);

        // And the id is on screen, so an operator can see what Servyx believes it owns.
        cut.Find("[data-testid='ledger-provider-resource-id']").TextContent.Trim()
            .Should().Be(ProviderResourceId);
        cut.Find("[data-testid='ledger-state']").TextContent.Trim()
            .Should().Be(nameof(ResourceLifecycleState.Created));

        provisioner.DetectDriftCalls.Should().Be(1);
    }

    [Fact]
    public void CreatedRow_CarryingNoInstanceIdTag_IsStillFullyCheckableAndUpdatable()
    {
        // Under the old tag-derived fallback this row rendered as "unknown — no recorded identity" and got
        // no controls at all. The provider id is recorded on the row itself, so the absent tag costs nothing.
        ResourceHandle? checkedAgainst = null;
        var provisioner = EnableMaintenance(
            SeededLedger(tags: new Dictionary<string, string>(StringComparer.Ordinal)),
            drift: handle =>
            {
                checkedAgainst = handle;
                return new DriftResult(handle, []);
            });

        var cut = Render<DeployPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='ledger-drift']").Should().ContainSingle());

        checkedAgainst.Should().NotBeNull();
        checkedAgainst!.ProviderResourceId.Should().Be(ProviderResourceId);

        cut.Find("[data-testid='ledger-drift']").GetAttribute("data-drift").Should().Be("matches");
        cut.FindAll("[data-testid='maintenance-not-created']").Should().BeEmpty();
        cut.FindAll("[data-testid='plan-update']").Should().ContainSingle();

        provisioner.DetectDriftCalls.Should().Be(1);
    }

    [Fact]
    public void IntendedRow_IsListedWithItsState_ButOffersNoDriftOrUpdateControls()
    {
        // A write-ahead row is committed before the provider is contacted, so it identifies nothing yet. It
        // still has to be visible — it may be an orphan billing right now — but nothing about it can be
        // inspected, and the page says which rather than reporting a match it never established.
        var provisioner = EnableMaintenance(SeededIntendedLedger());

        var cut = Render<DeployPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='ledger-row']").Should().ContainSingle());

        cut.Find("[data-testid='ledger-state']").TextContent.Trim()
            .Should().Be(nameof(ResourceLifecycleState.Intended));

        // No provider id exists, and none is fabricated from the row's tags.
        cut.FindAll("[data-testid='ledger-provider-resource-id']").Should().BeEmpty();
        cut.Find("[data-testid='ledger-provider-resource-none']").TextContent
            .Should().Contain("not assigned");

        var badge = cut.Find("[data-testid='ledger-drift']");
        badge.GetAttribute("data-drift").Should().Be("not-created");
        badge.TextContent.Should().Contain("unknown");
        badge.TextContent.Should().NotContain("matches");

        cut.Find("[data-testid='maintenance-not-created']").TextContent
            .Should().Contain(nameof(ResourceLifecycleState.Intended));

        cut.FindAll("[data-testid='plan-update']").Should().BeEmpty();
        cut.FindAll("[data-testid='apply-update']").Should().BeEmpty();

        provisioner.DetectDriftCalls.Should().Be(0, "there was no handle to check anything against");
        provisioner.PlanUpdateCalls.Should().Be(0);
    }

    // ── Unsupported provisioners ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProvisionerWithoutMaintainerSupport_SaysSoPlainly_AndOffersNoUpdateControls()
    {
        // FakeProvisioner implements IProvisioner and deliberately not IMaintainer, exactly like the SSH
        // and DigitalOcean adapters.
        var ledger = SeededLedger();
        Services.AddSingleton(new ProvisioningGate(enabled: true));
        Services.AddSingleton<IProvisioningDashboard>(new ProvisioningDashboardService(
            [new FakeProvisioner(ProvisionerId, ProvisioningCapabilities.Create, CreatePlan())],
            ledger,
            new ProvisioningExecutor(ledger)));

        var cut = Render<DeployPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='maintenance-unsupported']").Should().ContainSingle());

        cut.Find("[data-testid='maintenance-unsupported']").TextContent
            .Should().Contain(DeployPage.MaintenanceUnsupportedText);

        // Unknown, not clean.
        cut.Find("[data-testid='ledger-drift']").GetAttribute("data-drift").Should().Be("unsupported");
        cut.Find("[data-testid='ledger-drift']").TextContent.Should().NotContain("matches");

        // The control is replaced by the statement, not silently omitted alongside nothing.
        cut.FindAll("[data-testid='plan-update']").Should().BeEmpty();
        cut.FindAll("[data-testid='apply-update']").Should().BeEmpty();
        cut.FindAll("[data-testid='update-acknowledgement-step']").Should().BeEmpty();
    }

    // ── Update preview ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanUpdate_RendersTheNamedStages_AndTheOldToNewIdentity()
    {
        var provisioner = EnableMaintenance(SeededLedger(), ImageUpdatePlan(DataImpact.AtRisk));

        var cut = Render<DeployPage>();
        PlanUpdate(cut);

        var stages = cut.FindAll("[data-testid='update-stage']");
        stages.Should().HaveCount(3);
        stages[0].GetAttribute("data-stage-id").Should().Be("stop-container");
        stages[1].GetAttribute("data-stage-id").Should().Be("remove-container");
        stages[2].GetAttribute("data-stage-id").Should().Be("create-container");

        var change = cut.Find("[data-testid='update-change']");
        change.GetAttribute("data-aspect").Should().Be("image");
        change.QuerySelector("[data-testid='update-change-current']")!.TextContent.Trim().Should().Be("example:1.0");
        change.QuerySelector("[data-testid='update-change-desired']")!.TextContent.Trim().Should().Be("example:2.0");
        change.QuerySelector("[data-testid='update-change-recreate']").Should().NotBeNull();

        cut.Find("[data-testid='update-strategy']").TextContent.Trim()
            .Should().Be(nameof(UpdateStrategy.Recreate));

        // Previewing is a read: one drift check on load, one update plan on click, and nothing executed.
        provisioner.PlanUpdateCalls.Should().Be(1);
        provisioner.CreateOperationCalls.Should().Be(0, "previewing an update must never reach the create path");
        provisioner.Operation.CreateCalls.Should().Be(0);
    }

    [Fact]
    public void PlanUpdate_RendersAPreservedImpactCalmly_AndAsksForNoSeparateAcknowledgement()
    {
        EnableMaintenance(SeededLedger(), ImageUpdatePlan(DataImpact.Preserved));

        var cut = Render<DeployPage>();
        PlanUpdate(cut);

        var banner = cut.Find("[data-testid='data-impact']");
        banner.GetAttribute("data-impact").Should().Be(nameof(DataImpact.Preserved));
        banner.GetAttribute("data-severity").Should().Be(DataImpactBanner.CalmSeverity);
        banner.ClassList.Should().Contain("svx-data-impact-calm");
        banner.ClassList.Should().NotContain("svx-data-impact-danger");
        banner.HasAttribute("role").Should().BeFalse("a calm statement is not an alert");

        cut.FindAll("[data-testid='update-acknowledgement-step']").Should().BeEmpty();
    }

    [Theory]
    [InlineData(DataImpact.AtRisk)]
    [InlineData(DataImpact.Destroyed)]
    public void PlanUpdate_RendersARiskyImpactUnmissably_AndDemandsASeparateAcknowledgement(DataImpact impact)
    {
        EnableMaintenance(SeededLedger(), ImageUpdatePlan(impact));

        var cut = Render<DeployPage>();
        PlanUpdate(cut);

        var banner = cut.Find("[data-testid='data-impact']");
        banner.GetAttribute("data-impact").Should().Be(impact.ToString());

        // The assertion that matters is the *difference* from the safe case: a distinct severity, a
        // distinct class, and an assertive role — not merely that the word appeared somewhere.
        banner.GetAttribute("data-severity").Should().Be(DataImpactBanner.DangerSeverity);
        banner.ClassList.Should().Contain("svx-data-impact-danger");
        banner.ClassList.Should().NotContain("svx-data-impact-calm");
        banner.GetAttribute("role").Should().Be("alert");

        banner.QuerySelector("[data-testid='data-impact-headline']")!.TextContent
            .Should().Contain(impact == DataImpact.Destroyed ? "DESTROYED" : "AT RISK");

        // And the acknowledgement is its own step, present only for a risky plan.
        var step = cut.Find("[data-testid='update-acknowledgement-step']");
        step.GetAttribute("data-impact").Should().Be(impact.ToString());
        step.QuerySelector("[data-testid='update-acknowledge']").Should().NotBeNull();
    }

    // ── Confirm to apply ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_ForAPreservedPlan_NeedsOnlyTheOrdinaryConfirmStep()
    {
        var ledger = SeededLedger();
        var operation = SucceedingOperation();
        var provisioner = EnableMaintenance(ledger, ImageUpdatePlan(DataImpact.Preserved), operation: operation);

        var cut = Render<DeployPage>();
        PlanUpdate(cut);

        // No second step exists, and the single confirm control is live immediately.
        cut.FindAll("[data-testid='update-acknowledgement-step']").Should().BeEmpty();

        var apply = cut.Find("[data-testid='apply-update']");
        apply.HasAttribute("disabled").Should().BeFalse();

        // Still nothing has run at this point — the preview alone changes nothing.
        operation.CreateCalls.Should().Be(0);

        apply.Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='update-success']").Should().NotBeEmpty());

        provisioner.CreateOperationCalls.Should().Be(1);
        operation.CreateCalls.Should().Be(1, "exactly one confirmation, exactly one provider mutation");
        ledger.RecordIntentCalls.Should().Be(1, "the write-ahead row is committed before the provider call");
        ledger.MarkCreatedCalls.Should().Be(1);

        cut.Find("[data-testid='updated-data-impact']").TextContent.Trim()
            .Should().Be(nameof(DataImpact.Preserved));

        // Re-clicking must not run it a second time.
        cut.Find("[data-testid='apply-update']").HasAttribute("disabled").Should().BeTrue();
    }

    [Theory]
    [InlineData(DataImpact.AtRisk)]
    [InlineData(DataImpact.Destroyed)]
    public void Apply_ForARiskyPlan_IsBlockedUntilTheSeparateAcknowledgementIsCompleted(DataImpact impact)
    {
        var ledger = SeededLedger();
        var operation = SucceedingOperation();
        var provisioner = EnableMaintenance(ledger, ImageUpdatePlan(impact), operation: operation);

        var cut = Render<DeployPage>();
        PlanUpdate(cut);

        // Step one alone is not enough: the confirm control exists but cannot be used.
        cut.Find("[data-testid='apply-update']").HasAttribute("disabled")
            .Should().BeTrue("the click that approves a safe update must never be the click that destroys data");

        // Clicking anyway must do nothing at all. The negative is asserted as a non-invocation rather than
        // as a message on screen: the control is disabled, and ApplyUpdateAsync re-checks the
        // acknowledgement itself so driving the handler directly is refused too.
        cut.Find("[data-testid='apply-update']").Click();

        provisioner.CreateOperationCalls.Should().Be(0);
        operation.CreateCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0, "nothing may be written before the impact is acknowledged");
        cut.FindAll("[data-testid='update-success']").Should().BeEmpty();

        // Step two: the separate, deliberate acknowledgement.
        cut.Find("[data-testid='update-acknowledge']").Change(true);
        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='apply-update']").HasAttribute("disabled").Should().BeFalse());

        // Still nothing has run — acknowledging is not applying.
        operation.CreateCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0);

        cut.Find("[data-testid='apply-update']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='update-success']").Should().NotBeEmpty());

        operation.CreateCalls.Should().Be(1);
        cut.Find("[data-testid='updated-data-impact']").TextContent.Trim().Should().Be(impact.ToString());
    }

    [Fact]
    public async Task Apply_ForARiskyPlan_IsRefusedByTheApplicationLayerToo_WhenNoTokenIsSupplied()
    {
        // The UI guard is belt; this is braces. Calling the dashboard directly — the shape a caller with a
        // smaller surface has — is refused without the separately-typed acknowledgement.
        var ledger = SeededLedger();
        var operation = SucceedingOperation();
        var provisioner = EnableMaintenance(ledger, ImageUpdatePlan(DataImpact.Destroyed), operation: operation);

        var dashboard = Services.BuildServiceProvider().GetRequiredService<IProvisioningDashboard>();
        var handle = new ResourceHandle(ProvisionerId, InstanceId, null, IdentifiedTags);
        var request = new ProvisioningRequest("palworld", "docker", "docker-container-local",
            new Dictionary<string, string>(StringComparer.Ordinal));

        var result = await dashboard
            .ApplyUpdateAsync(ProvisionerId, handle, request, $"updatehash-{DataImpact.Destroyed}", null);

        result.Should().BeOfType<UpdateApplyResult.RequiresAcknowledgement>();
        provisioner.CreateOperationCalls.Should().Be(0);
        operation.CreateCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0);
    }

    // ── Flag off ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOff_RendersNoDriftStatusAndNoUpdateControls_EvenWithEverythingRegistered()
    {
        var ledger = SeededLedger();
        var operation = SucceedingOperation();
        var provisioner = EnableMaintenance(
            ledger,
            ImageUpdatePlan(DataImpact.Destroyed),
            operation: operation,
            gateEnabled: false);

        var cut = Render<DeployPage>();

        cut.Find("[data-testid='provisioning-disabled']").Should().NotBeNull();
        cut.FindAll("[data-testid='ledger-drift']").Should().BeEmpty();
        cut.FindAll("[data-testid='ledger-maintenance']").Should().BeEmpty();

        // The confirmed row and its provider-assigned id are not rendered either — a closed gate lists no
        // inventory at all, not merely no controls over it.
        cut.FindAll("[data-testid='ledger-row']").Should().BeEmpty();
        cut.FindAll("[data-testid='ledger-provider-resource-id']").Should().BeEmpty();
        cut.Markup.Should().NotContain(ProviderResourceId);
        cut.FindAll("[data-testid='plan-update']").Should().BeEmpty();
        cut.FindAll("[data-testid='apply-update']").Should().BeEmpty();
        cut.FindAll("[data-testid='update-acknowledgement-step']").Should().BeEmpty();
        cut.FindAll("[data-testid='data-impact']").Should().BeEmpty();
        cut.FindAll("button").Should().BeEmpty();

        // A closed gate does not even read.
        provisioner.DetectDriftCalls.Should().Be(0);
        provisioner.PlanUpdateCalls.Should().Be(0);
        provisioner.CreateOperationCalls.Should().Be(0);
        operation.CreateCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0);
    }
}
