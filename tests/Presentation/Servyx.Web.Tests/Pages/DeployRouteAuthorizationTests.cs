using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Application.Provisioning;
using Servyx.Domain.Provisioning;
using Servyx.Web.Components;
using Servyx.Web.Services;
using Servyx.Composition;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// <c>/deploy</c> is the route that can create real infrastructure and spend real money, so this file asks
/// the only question that matters about it: can an anonymous caller reach it?
/// </summary>
/// <remarks>
/// <para>
/// Unlike <c>DeployPageTests</c>, which renders <c>DeployPage</c> directly, these render the real
/// <c>Routes</c> component — Router, AuthenticationBoundary, AuthorizeRouteView and layout — and let it
/// resolve the <c>/deploy</c> route itself. That is what makes "the page was never even instantiated" an
/// observable fact rather than an assumption: the composition here has a provisioner, a ledger and an
/// executor all registered and ready, so if the router resolved the page for an anonymous caller, its
/// counters would move and its controls would be in the markup.
/// </para>
/// <para>
/// This covers the in-circuit half of the guarantee — a navigation that happens inside an already-open
/// circuit and therefore runs no middleware. The HTTP half, where an anonymous <c>GET /deploy</c> is turned
/// into a redirect by the fallback policy before any component exists, is asserted against the real running
/// app in <c>Integration/OperatorAuthenticationEndpointTests</c>.
/// </para>
/// </remarks>
public class DeployRouteAuthorizationTests : BunitContext
{
    private const string ProvisionerId = "docker-container";

    private static ProvisioningPlan Plan() => new(
        PlanId: "docker-container:servyx-preview:abc123def456",
        PlanHash: "abc123def456abc123def456abc123def456abc123def456abc123def456abcd",
        Stages: [new("create-container", ProvisionerId, "Create container 'servyx-preview'.")],
        EstimatedCost: CostEstimate.Unknown("Local Docker containers are not billed by a provider."),
        ExpiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// The most dangerous composition this app supports: provisioning on, a ledger, and a live executor.
    /// Everything needed to create a container is in the container — the only thing standing between an
    /// anonymous caller and it is the authentication wiring under test.
    /// </summary>
    private CountingDashboard ComposeFullyArmedDeployHost(
        RecordingProvisioningLedger ledger, out FakeProvisioner provisioner)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        provisioner = new FakeProvisioner(
            ProvisionerId,
            ProvisioningCapabilities.Create | ProvisioningCapabilities.Destroy,
            Plan());

        var dashboard = new CountingDashboard(
            new ProvisioningDashboardService([provisioner], ledger, new ProvisioningExecutor(ledger)));

        Services.AddSingleton(new ProvisioningGate(enabled: true));
        Services.AddSingleton<IProvisioningDashboard>(dashboard);
        Services.AddSingleton<IDashboardDataService>(new MockDashboardDataService());

        return dashboard;
    }

    /// <summary>
    /// A pass-through dashboard that counts the two reads <c>DeployPage.OnInitializedAsync</c> performs, so
    /// "the page was never instantiated" is asserted as a non-invocation rather than inferred from markup.
    /// </summary>
    private sealed class CountingDashboard(IProvisioningDashboard inner) : IProvisioningDashboard
    {
        public int ListProvisionersCalls { get; private set; }

        public int ListLedgerEntriesCalls { get; private set; }

        public bool LedgerConfigured => inner.LedgerConfigured;

        public bool ExecutionConfigured => inner.ExecutionConfigured;

        public IReadOnlyList<ProvisionerDescriptor> ListProvisioners()
        {
            ListProvisionersCalls++;
            return inner.ListProvisioners();
        }

        public Task<IReadOnlyList<ProvisioningLedgerEntry>> ListLedgerEntriesAsync(CancellationToken ct = default)
        {
            ListLedgerEntriesCalls++;
            return inner.ListLedgerEntriesAsync(ct);
        }

        public Task<ProvisioningPlan> PlanAsync(
            string provisionerId, ProvisioningRequest request, CancellationToken ct = default)
            => inner.PlanAsync(provisionerId, request, ct);

        public Task<ProvisioningApplyResult> ApplyAsync(
            string provisionerId,
            ProvisioningRequest request,
            string approvedPlanHash,
            string? jobId = null,
            CancellationToken ct = default)
            => inner.ApplyAsync(provisionerId, request, approvedPlanHash, jobId, ct);

        public Task<PlanUpdateResult> PlanUpdateAsync(
            string provisionerId, ResourceHandle handle, ProvisioningRequest desired, CancellationToken ct = default)
            => inner.PlanUpdateAsync(provisionerId, handle, desired, ct);

        public Task<UpdateApplyResult> ApplyUpdateAsync(
            string provisionerId,
            ResourceHandle handle,
            ProvisioningRequest desired,
            string approvedPlanHash,
            DataImpactAcknowledgement? dataImpactAcknowledgement,
            string? jobId = null,
            CancellationToken ct = default)
            => inner.ApplyUpdateAsync(
                provisionerId, handle, desired, approvedPlanHash, dataImpactAcknowledgement, jobId, ct);

        public Task<DriftCheckResult> DetectDriftAsync(
            string provisionerId, ResourceHandle handle, CancellationToken ct = default)
            => inner.DetectDriftAsync(provisionerId, handle, ct);
    }

    private void NavigateToDeploy()
        => Services.GetRequiredService<NavigationManager>().NavigateTo("deploy");

    private string CurrentUri => Services.GetRequiredService<NavigationManager>().Uri;

    [Fact]
    public void AnAnonymousCallerAtDeploy_GetsNoPage_NoControls_AndIsSentToLogin()
    {
        var ledger = new RecordingProvisioningLedger();
        var dashboard = ComposeFullyArmedDeployHost(ledger, out var provisioner);
        Services.AddSingleton(new AuthenticationGate(enabled: true));
        AddAuthorization().SetNotAuthorized();

        NavigateToDeploy();
        var cut = Render<Routes>();

        // Not "the buttons are disabled" and not "the page said no" — the page is not there.
        cut.FindAll("[data-testid='provisioner-list']").Should().BeEmpty();
        cut.FindAll("[data-testid='provisioner-row']").Should().BeEmpty();
        cut.FindAll("[data-testid='plan-section']").Should().BeEmpty();
        cut.FindAll("[data-testid='provisioning-enabled-warning']").Should().BeEmpty();
        cut.FindAll("[data-testid='ledger-section']").Should().BeEmpty();
        cut.FindAll("button").Should().BeEmpty();
        cut.FindAll("input").Should().BeEmpty();
        cut.FindAll("select").Should().BeEmpty();
        cut.Markup.Should().NotContain("Deploy");

        // DeployPage.OnInitializedAsync does both of these on first render; zeroes mean it never ran.
        dashboard.ListProvisionersCalls.Should().Be(0, "the deploy page must never have been instantiated");
        dashboard.ListLedgerEntriesCalls.Should().Be(0);
        provisioner.PlanCalls.Should().Be(0);
        provisioner.CreateOperationCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0);

        CurrentUri.Should().Contain("/login");
        CurrentUri.Should().Contain("returnUrl=%2Fdeploy");
    }

    [Fact]
    public void TheSignedInOperatorAtDeploy_GetsTheRealPage()
    {
        // The other half of the proof: the gate is not simply breaking the route for everyone.
        var ledger = new RecordingProvisioningLedger();
        ComposeFullyArmedDeployHost(ledger, out _);
        Services.AddSingleton(new AuthenticationGate(enabled: true));
        AddAuthorization().SetAuthorized("operator");

        NavigateToDeploy();
        var cut = Render<Routes>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='provisioner-row']").Should().ContainSingle());
        cut.Find("[data-testid='plan-section']").Should().NotBeNull();
        CurrentUri.Should().NotContain("/login");
    }

    [Fact]
    public void WithAuthenticationSwitchedOff_DeployIsReachableWithoutALogin()
    {
        // The documented bypass, asserted rather than assumed — and the exact configuration Program.cs logs
        // about at Critical when provisioning is on, which it is here.
        var ledger = new RecordingProvisioningLedger();
        ComposeFullyArmedDeployHost(ledger, out _);
        Services.AddSingleton(new AuthenticationGate(enabled: false));
        AddAuthorization().SetNotAuthorized();

        NavigateToDeploy();
        var cut = Render<Routes>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='provisioner-row']").Should().ContainSingle());
        CurrentUri.Should().NotContain("/login");
    }
}
