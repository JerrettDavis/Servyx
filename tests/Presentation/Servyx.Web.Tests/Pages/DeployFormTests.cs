using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Servyx.Application.Provisioning;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Provisioning;
using Servyx.Infrastructure.Docker.Provisioning;
using Servyx.Web.Components.Pages.Deploy;
using Servyx.Web.Services;
using Servyx.Composition;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// Drives <c>DeployPage</c>'s form against fake provisioners, never live infrastructure. Where
/// <c>DeployPageTests</c> pins the preview-then-confirm discipline, this file pins what the form <em>is</em>:
/// that it adapts to the selected provisioner, that Docker's request did not change when it started doing so,
/// and that an empty required control refuses instead of reaching a provider.
/// </summary>
public class DeployFormTests : BunitContext
{
    private const string DockerId = DockerContainerProvisioner.Id;
    private const string CloudId = DigitalOceanDropletProvisioner.Id;

    private static ProvisioningPlan PlanFor(string provisionerId) => new(
        PlanId: $"{provisionerId}:preview",
        PlanHash: "abc123def456abc123def456abc123def456abc123def456abc123def456abcd",
        Stages: [new("create", provisionerId, "Create the resource.")],
        EstimatedCost: CostEstimate.Unknown("not billed here"),
        ExpiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// The composition a host that turned provisioning on has, complete with a
    /// <see cref="ProvisioningExecutor"/> — so "nothing executed" is a claim about the real execution path
    /// being present and unused, not about it being absent.
    /// </summary>
    private (FakeProvisioner Docker, FakeProvisioner Cloud, RecordingProvisioningLedger Ledger) EnableBoth()
    {
        var ledger = new RecordingProvisioningLedger();
        var docker = new FakeProvisioner(DockerId, ProvisioningCapabilities.Create, PlanFor(DockerId));
        var cloud = new FakeProvisioner(CloudId, ProvisioningCapabilities.Create, PlanFor(CloudId));

        Services.AddSingleton(new ProvisioningGate(enabled: true));
        Services.AddSingleton<IProvisioningDashboard>(
            new ProvisioningDashboardService([docker, cloud], ledger, new ProvisioningExecutor(ledger)));

        return (docker, cloud, ledger);
    }

    private static void SelectProvisioner(IRenderedComponent<DeployPage> cut, string provisionerId)
    {
        cut.Find("[data-testid='provisioner-select']").Change(provisionerId);
        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='provisioner-form']").GetAttribute("data-schema-for")
                .Should().Be(provisionerId));
    }

    private static void Preview(IRenderedComponent<DeployPage> cut)
    {
        cut.Find("[data-testid='preview-plan']").Click();
    }

    // ── Docker, unchanged ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <strong>The compatibility assertion.</strong> Rendering the page and clicking Preview with nothing
    /// touched must send Docker the same seven parameters, with the same values, that the hardcoded
    /// <c>BuildRequest()</c> sent — plus the same profile and connector.
    /// </summary>
    [Fact]
    public void Docker_sends_the_same_request_it_always_did()
    {
        var (docker, _, _) = EnableBoth();

        var cut = Render<DeployPage>();
        Preview(cut);

        cut.WaitForAssertion(() => docker.LastRequest.Should().NotBeNull());

        var request = docker.LastRequest!;

        // No GameDefinitionCatalog is registered in this test's composition (see EnableBoth), which is the
        // honest "no game definitions loaded" state the game-selection work made real rather than a
        // hardcoded "palworld" no catalog ever justified. See DeployPageGameSelectionTests for the
        // catalog-driven preselection/selection behavior this pin does not exercise.
        request.GameDefinitionId.Should().BeEmpty();
        request.DeploymentProfileId.Should().Be("docker");
        request.ConnectorId.Should().Be("docker-container-local");

        request.Parameters.Keys.Should().BeEquivalentTo(
            ["image", "containerName", "instanceId", "jobId", "connectorId", "restartPolicy", "port:8211/tcp"]);

        request.Parameters["containerName"].Should().Be("servyx-preview");
        request.Parameters["image"].Should().Be(ProvisionerFormCatalog.FallbackContainerImage);
        request.Parameters["restartPolicy"].Should().Be("unless-stopped");
        request.Parameters["port:8211/tcp"].Should().Be("8211");
        request.Parameters["connectorId"].Should().Be("docker-container-local");

        // Fixed per page instance rather than per click, exactly as before, so an unchanged form re-previews
        // to the same plan hash.
        request.Parameters["instanceId"].Should().StartWith("preview-");
        request.Parameters["jobId"].Should().StartWith("job-");
    }

    /// <summary>
    /// The three original Docker controls are still the three original Docker controls, by the same test
    /// ids — plus a fourth, newer one (stop grace period) that starts blank here because no game definition
    /// is loaded in this composition (see <see cref="EnableBoth"/>) for a value to derive it from.
    /// </summary>
    [Fact]
    public void Docker_renders_the_same_three_fields_with_the_same_defaults()
    {
        EnableBoth();

        var cut = Render<DeployPage>();

        cut.Find("[data-testid='provisioner-form']").GetAttribute("data-schema-for").Should().Be(DockerId);
        cut.Find("[data-testid='container-name']").GetAttribute("value").Should().Be("servyx-preview");
        cut.Find("[data-testid='image']").GetAttribute("value")
            .Should().Be(ProvisionerFormCatalog.FallbackContainerImage);
        cut.Find("[data-testid='host-port']").GetAttribute("value").Should().Be("8211");
        cut.Find("[data-testid='stop-grace-period-seconds']").GetAttribute("value").Should().BeEmpty();

        cut.FindAll("[data-testid='provisioner-field']").Should().HaveCount(4);
        cut.FindAll("[data-testid='additional-parameters']").Should().BeEmpty();
    }

    /// <summary>Edited values still reach the request, including the port that is written into its own key.</summary>
    [Fact]
    public void Docker_carries_edited_values_into_the_request()
    {
        var (docker, _, _) = EnableBoth();

        var cut = Render<DeployPage>();
        cut.Find("[data-testid='container-name']").Change("palworld-1");
        cut.Find("[data-testid='image']").Change("example.invalid/pal:2");
        cut.Find("[data-testid='host-port']").Change("27015");
        Preview(cut);

        cut.WaitForAssertion(() => docker.LastRequest.Should().NotBeNull());

        docker.LastRequest!.Parameters["containerName"].Should().Be("palworld-1");
        docker.LastRequest.Parameters["image"].Should().Be("example.invalid/pal:2");
        docker.LastRequest.Parameters["port:27015/tcp"].Should().Be("27015");
        docker.LastRequest.Parameters.Should().NotContainKey("port:8211/tcp");
    }

    // ── Another provisioner, its own form ─────────────────────────────────────────────────────────────

    [Fact]
    public void Selecting_a_cloud_provisioner_renders_its_fields_and_not_dockers()
    {
        EnableBoth();

        var cut = Render<DeployPage>();
        SelectProvisioner(cut, CloudId);

        // Its own.
        cut.Find("[data-testid='name']").GetAttribute("value").Should().Be("servyx-preview");
        cut.Find("[data-testid='size']").Should().NotBeNull();
        cut.Find("[data-testid='region']").Should().NotBeNull();
        cut.Find("[data-testid='image']").GetAttribute("value").Should().Be("ubuntu-24-04-x64");

        // Not Docker's.
        cut.FindAll("[data-testid='container-name']").Should().BeEmpty();
        cut.FindAll("[data-testid='host-port']").Should().BeEmpty();
    }

    [Fact]
    public void A_complete_cloud_form_previews_a_plan()
    {
        var (docker, cloud, ledger) = EnableBoth();

        var cut = Render<DeployPage>();
        SelectProvisioner(cut, CloudId);
        Preview(cut);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='plan-stage']").Should().ContainSingle());
        cut.Find("[data-testid='plan-id']").TextContent.Should().Contain(CloudId);

        cloud.PlanCalls.Should().Be(1);
        docker.PlanCalls.Should().Be(0, "the Docker provisioner is not the selected one");

        var request = cloud.LastRequest!;
        request.DeploymentProfileId.Should().Be("machine");
        request.ConnectorId.Should().Be($"{CloudId}-local");
        request.Parameters.Keys.Should().BeEquivalentTo(
            ["name", "size", "region", "image", "instanceId", "jobId", "connectorId"]);

        // Still only a preview.
        cloud.CreateOperationCalls.Should().Be(0);
        cloud.Operation.CreateCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0);
    }

    // ── Refusals ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_required_field_refuses_by_name_and_reaches_no_provisioner()
    {
        var (_, cloud, ledger) = EnableBoth();

        var cut = Render<DeployPage>();
        SelectProvisioner(cut, CloudId);
        cut.Find("[data-testid='name']").Change(string.Empty);
        Preview(cut);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='plan-error']").Should().NotBeEmpty());

        var error = cut.Find("[data-testid='plan-error']").TextContent;
        error.Should().Contain("Droplet name");
        error.Should().Contain(CloudId);

        // No plan was produced, so no confirmation control exists to click…
        cut.FindAll("[data-testid='plan-preview']").Should().BeEmpty();
        cut.FindAll("[data-testid='confirm-step']").Should().BeEmpty();
        cut.FindAll("[data-testid='apply-plan']").Should().BeEmpty();

        // …and nothing downstream was reached at all: not the provisioner, not the executor.
        cloud.PlanCalls.Should().Be(0, "a refusal must not ask the provisioner anything");
        cloud.CreateOperationCalls.Should().Be(0);
        cloud.Operation.CreateCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0);
        ledger.MarkCreatedCalls.Should().Be(0);
    }

    /// <summary>
    /// A refusal is not sticky: filling the field in and previewing again works, and the error goes away.
    /// </summary>
    [Fact]
    public void Filling_the_missing_field_in_clears_the_refusal()
    {
        var (_, cloud, _) = EnableBoth();

        var cut = Render<DeployPage>();
        SelectProvisioner(cut, CloudId);
        cut.Find("[data-testid='name']").Change(string.Empty);
        Preview(cut);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='plan-error']").Should().NotBeEmpty());

        cut.Find("[data-testid='name']").Change("palworld-eu");
        Preview(cut);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='plan-stage']").Should().ContainSingle());
        cut.FindAll("[data-testid='plan-error']").Should().BeEmpty();
        cloud.LastRequest!.Parameters["name"].Should().Be("palworld-eu");
    }

    // ── Switching targets ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The leak that matters: <c>image</c> means a container image to Docker and a droplet image to
    /// DigitalOcean, so an edit made under one target must not be sent to the other.
    /// </summary>
    [Fact]
    public void Switching_provisioners_mid_edit_carries_nothing_across()
    {
        var (_, cloud, _) = EnableBoth();

        var cut = Render<DeployPage>();
        cut.Find("[data-testid='container-name']").Change("left-over");
        cut.Find("[data-testid='image']").Change("example.invalid/left-over:1");
        cut.Find("[data-testid='host-port']").Change("31337");

        SelectProvisioner(cut, CloudId);
        Preview(cut);

        cut.WaitForAssertion(() => cloud.LastRequest.Should().NotBeNull());

        var parameters = cloud.LastRequest!.Parameters;
        parameters.Should().NotContainKey("containerName");
        parameters.Should().NotContainKey("restartPolicy");
        parameters.Should().NotContainKey("port:31337/tcp");
        parameters["image"].Should().Be("ubuntu-24-04-x64", "the shared key must hold this target's default");
    }

    /// <summary>And back again: returning to Docker gives Docker's defaults, not the values left behind.</summary>
    [Fact]
    public void Switching_back_restores_the_targets_own_defaults()
    {
        EnableBoth();

        var cut = Render<DeployPage>();
        cut.Find("[data-testid='container-name']").Change("left-over");

        SelectProvisioner(cut, CloudId);
        SelectProvisioner(cut, DockerId);

        cut.Find("[data-testid='container-name']").GetAttribute("value").Should().Be("servyx-preview");
    }

    /// <summary>
    /// A plan previewed for one target is discarded when the target changes, so no confirmation control ever
    /// sits under a provisioner it was not computed for.
    /// </summary>
    [Fact]
    public void Switching_provisioners_discards_the_previewed_plan()
    {
        var (_, _, ledger) = EnableBoth();

        var cut = Render<DeployPage>();
        Preview(cut);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='confirm-step']").Should().NotBeEmpty());

        SelectProvisioner(cut, CloudId);

        cut.FindAll("[data-testid='plan-preview']").Should().BeEmpty();
        cut.FindAll("[data-testid='confirm-step']").Should().BeEmpty();
        cut.FindAll("[data-testid='apply-plan']").Should().BeEmpty();
        ledger.RecordIntentCalls.Should().Be(0);
    }

    // ── An adapter this build has no schema for ───────────────────────────────────────────────────────

    /// <summary>
    /// A provisioner the catalog has never heard of still gets a usable form rather than an empty one — and
    /// its free-form parameters reach the request.
    /// </summary>
    [Fact]
    public void An_undescribed_provisioner_is_still_deployable_through_a_free_form_editor()
    {
        var ledger = new RecordingProvisioningLedger();
        var unknown = new FakeProvisioner("hetzner-server", ProvisioningCapabilities.Create, PlanFor("hetzner-server"));

        Services.AddSingleton(new ProvisioningGate(enabled: true));
        Services.AddSingleton<IProvisioningDashboard>(new ProvisioningDashboardService([unknown], ledger));

        var cut = Render<DeployPage>();

        cut.FindAll("[data-testid='provisioner-field']").Should().BeEmpty();
        cut.Find("[data-testid='additional-parameters']").Change("serverType=cx41\nimage=ubuntu-24.04");
        Preview(cut);

        cut.WaitForAssertion(() => unknown.LastRequest.Should().NotBeNull());

        unknown.LastRequest!.Parameters["serverType"].Should().Be("cx41");
        unknown.LastRequest.Parameters["image"].Should().Be("ubuntu-24.04");
        unknown.LastRequest.Parameters["connectorId"].Should().Be("hetzner-server-local");
    }

    /// <summary>A free-form line that is not a pair is refused, and the provisioner is not asked.</summary>
    [Fact]
    public void A_malformed_free_form_line_refuses_before_the_provisioner_is_asked()
    {
        var ledger = new RecordingProvisioningLedger();
        var unknown = new FakeProvisioner("hetzner-server", ProvisioningCapabilities.Create, PlanFor("hetzner-server"));

        Services.AddSingleton(new ProvisioningGate(enabled: true));
        Services.AddSingleton<IProvisioningDashboard>(new ProvisioningDashboardService([unknown], ledger));

        var cut = Render<DeployPage>();
        cut.Find("[data-testid='additional-parameters']").Change("serverType cx41");
        Preview(cut);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='plan-error']").Should().NotBeEmpty());

        cut.Find("[data-testid='plan-error']").TextContent.Should().Contain("serverType cx41");
        unknown.PlanCalls.Should().Be(0);
        ledger.RecordIntentCalls.Should().Be(0);
    }

    // ── Flag off ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The closed gate is untouched by all of the above: no form, no schema-driven control, no textarea —
    /// still not a single input on the page.
    /// </summary>
    [Fact]
    public void FlagOff_renders_no_form_of_any_shape()
    {
        Services.AddSingleton(new ProvisioningGate(enabled: false));

        var cut = Render<DeployPage>();

        cut.FindAll("[data-testid='provisioner-form']").Should().BeEmpty();
        cut.FindAll("[data-testid='provisioner-field']").Should().BeEmpty();
        cut.FindAll("[data-testid='additional-parameters']").Should().BeEmpty();
        cut.FindAll("[data-testid='provisioner-form-description']").Should().BeEmpty();
        cut.FindAll("input").Should().BeEmpty();
        cut.FindAll("textarea").Should().BeEmpty();
        cut.FindAll("select").Should().BeEmpty();
        cut.FindAll("button").Should().BeEmpty();
    }
}
