using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Application.Provisioning;
using Servyx.Domain.Connectors;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Aws.Provisioning;
using Servyx.Infrastructure.Azure.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Provisioning;
using Servyx.Infrastructure.Process.Provisioning;
using Servyx.Infrastructure.Ssh.Provisioning;
using Servyx.Web.Components.Pages.Deploy;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;
using Servyx.Web.Tests.Services;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// Renders <c>DeployPage</c> over the provisioners the real registration path actually produces, so
/// "/deploy can now offer every target" is a rendered fact rather than a claim about a service collection.
/// </summary>
/// <remarks>
/// Nothing here reaches a provider. Building a plan or applying one would, so no test in this file does
/// either: the page's provisioner list and its select are populated from <c>ProvisionerId</c> and
/// <c>Capabilities</c>, both of which are pure property reads on every adapter.
/// </remarks>
public class DeployProvisionerListTests : BunitContext
{
    private const string DockerProvisionerId = "docker-container";

    /// <summary>Every id the composition root can put on the page, in the order the page lists them.</summary>
    private static readonly string[] EveryProvisionerId =
    [
        DockerProvisionerId,
        SshProcessProvisioner.Id,
        LocalProcessProvisioner.Id,
        DigitalOceanDropletProvisioner.Id,
        AzureVirtualMachineProvisioner.Id,
        AwsEc2Provisioner.Id,
        AwsLightsailProvisioner.Id,
    ];

    /// <summary>
    /// Builds the container <c>Program.cs</c> would build for <paramref name="settings"/> and binds the page
    /// to a dashboard over whatever provisioners came out of it.
    /// </summary>
    private ServiceProvider Arrange(Dictionary<string, string?> settings)
    {
        var configuration = ProvisionerWiringTests.Config(settings);
        var gate = ProvisioningGate.FromConfiguration(configuration);

        var transport = Substitute.For<ITransport>();
        transport.TransportId.Returns("docker");

        var host = new ServiceCollection();
        host.AddLogging();
        host.AddSingleton<ISecretStore>(new RecordingSecretStore());
        host.AddSingleton(Substitute.For<IHostKeyVerifier>());
        host.AddSingleton(transport);
        host.AddSingleton<IProvisioner>(new FakeProvisioner(
            DockerProvisionerId,
            ProvisioningCapabilities.Create | ProvisioningCapabilities.Destroy,
            Plan()));
        host.AddServyxConfiguredProvisioners(ProvisionerWiringOptions.FromConfiguration(configuration, gate));

        var provider = host.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Services.AddSingleton(gate);
        Services.AddSingleton<IProvisioningDashboard>(
            new ProvisioningDashboardService(provider.GetServices<IProvisioner>()));

        return provider;
    }

    private static ProvisioningPlan Plan() => new(
        PlanId: "docker-container:servyx-preview:abc123def456",
        PlanHash: "abc123def456abc123def456abc123def456abc123def456abc123def456abcd",
        Stages: [new("create-container", DockerProvisionerId, "Create container 'servyx-preview'.")],
        EstimatedCost: CostEstimate.Unknown("Local Docker containers are not billed by a provider."),
        ExpiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static IReadOnlyList<string> ListedIds(IRenderedComponent<DeployPage> page) =>
    [
        .. page.FindAll("[data-testid='provisioner-row']")
            .Select(row => row.GetAttribute("data-provisioner-id") ?? string.Empty),
    ];

    [Fact]
    public void The_page_lists_every_registered_provisioner_by_id()
    {
        using var provider = Arrange(ProvisionerWiringTests.AllEnabled());

        var page = Render<DeployPage>();

        ListedIds(page).Should().BeEquivalentTo(EveryProvisionerId);
    }

    [Fact]
    public void Every_listed_provisioner_is_also_selectable_as_a_plan_target()
    {
        // The list and the select are the two places an id has to appear for a target to be reachable at
        // all. A provisioner that renders in one and not the other is not offered, it is advertised.
        using var provider = Arrange(ProvisionerWiringTests.AllEnabled());

        var page = Render<DeployPage>();

        var options = page.Find("[data-testid='provisioner-select']")
            .QuerySelectorAll("option")
            .Select(o => o.GetAttribute("value") ?? string.Empty)
            .ToList();

        options.Should().BeEquivalentTo(EveryProvisionerId);
        options.Should().BeEquivalentTo(ListedIds(page));
    }

    [Fact]
    public void With_nothing_individually_enabled_the_page_offers_exactly_what_it_offered_before()
    {
        using var provider = Arrange(ProvisionerWiringTests.GateOpen());

        var page = Render<DeployPage>();

        ListedIds(page).Should().Equal(DockerProvisionerId);
        page.FindAll("[data-testid='no-provisioners']").Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(ProvisionerWiringTests.EveryProvisioner), MemberType = typeof(ProvisionerWiringTests))]
    public void Enabling_one_provisioner_adds_exactly_one_row_to_the_page(string provisionerKey, string expectedId)
    {
        var settings = ProvisionerWiringTests.GateOpen();
        foreach (var (key, value) in ProvisionerWiringTests.MinimalSettings(provisionerKey))
        {
            settings[key] = value;
        }

        using var provider = Arrange(settings);

        var page = Render<DeployPage>();

        ListedIds(page).Should().BeEquivalentTo([DockerProvisionerId, expectedId]);
    }

    [Fact]
    public void A_closed_gate_renders_the_disabled_state_no_matter_what_is_configured()
    {
        var settings = ProvisionerWiringTests.AllEnabled();
        settings.Remove("Servyx:Provisioning:Enabled");

        using var provider = Arrange(settings);

        var page = Render<DeployPage>();

        page.FindAll("[data-testid='provisioning-disabled']").Should().ContainSingle();
        page.FindAll("[data-testid='provisioner-list']").Should().BeEmpty();
    }
}
