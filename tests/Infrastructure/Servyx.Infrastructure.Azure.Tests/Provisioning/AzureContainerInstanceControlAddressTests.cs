using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Azure.Provisioning;

namespace Servyx.Infrastructure.Azure.Tests.Provisioning;

/// <summary>
/// Where a control channel to an ACI container group connects, and — the harder half — whether that address
/// is still the right one after Azure restarts the group.
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of <c>AzureContainerInstanceProvisioner.UnreachableReason</c>. That text tells the
/// operator to reach the workload through RCON; these assertions cover the question that immediately
/// follows, and cover it with the distinction that matters: ACI's own documentation warns a container
/// group's public IP may change when the group restarts, so an adapter that answered with the IP would be
/// handing back an address that works in every test and breaks in production with nothing raised. A
/// <c>dnsNameLabel</c> produces an FQDN Azure re-points at the current IP, and that name — and only that
/// name — is durable.
/// </para>
/// <para>
/// Nothing here grants reachability. Every assertion about an address is paired with the resource remaining
/// <see cref="ResourceReachability.NoTransport"/>, because a control channel is not a transport and the
/// <c>Provision</c> tier stays out of reach whatever the address turns out to be.
/// </para>
/// </remarks>
public class AzureContainerInstanceControlAddressTests
{
    private static ResourceHandle Handle() => new(
        AzureContainerInstanceProvisioner.Id,
        AzureContainerInstanceScenario.GroupId,
        AzureContainerInstanceScenario.Region,
        AzureContainerInstanceScenario.CanonicalTags);

    [Fact]
    public async Task A_group_with_a_dns_label_offers_the_fqdn_as_a_durable_control_address()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var address = await scenario.Provisioner().ResolveControlAddressAsync(Handle());

        address.Should().BeOfType<ControlChannelAddress.Durable>()
            .Which.Host.Should().Be(AzureContainerInstanceScenario.Fqdn);
        address.OpenableHostOrNull().Should().Be(AzureContainerInstanceScenario.Fqdn);
    }

    [Fact]
    public async Task The_durable_claim_says_why_it_is_durable_and_does_not_overstate_it()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var address = await scenario.Provisioner().ResolveControlAddressAsync(Handle());

        var justification = address.Should().BeOfType<ControlChannelAddress.Durable>().Which.Justification;

        justification.Should().Contain("dnsNameLabel");
        justification.Should().Contain(
            "StaticAddress",
            "the name surviving a restart is not the same claim as a static address, and the capability bit stays absent");
    }

    [Fact]
    public async Task The_fqdn_is_read_back_from_arm_rather_than_composed_from_the_label_and_the_region()
    {
        // A sovereign cloud uses a different suffix, so '{label}.{region}.azurecontainer.io' is a guess -
        // and a guessed control address is precisely the failure this path exists to avoid.
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup(
            AzureContainerInstanceScenario.GroupJson(fqdn: "palworld.chinaeast2.azurecontainer.cn"));

        var address = await scenario.Provisioner().ResolveControlAddressAsync(Handle());

        address.Should().BeOfType<ControlChannelAddress.Durable>()
            .Which.Host.Should().Be("palworld.chinaeast2.azurecontainer.cn");
    }

    [Fact]
    public async Task A_group_with_no_dns_label_offers_its_ip_as_ephemeral_and_never_as_durable()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup(AzureContainerInstanceScenario.GroupJson(fqdn: null));

        var address = await scenario.Provisioner().ResolveControlAddressAsync(Handle());

        var ephemeral = address.Should().BeOfType<ControlChannelAddress.Ephemeral>().Which;

        ephemeral.Host.Should().Be(AzureContainerInstanceScenario.PublicIp);
        ephemeral.Reason.Should().Contain("dnsNameLabel", "the operator needs the one change that would fix it");
        address.OpenableHostOrNull().Should().BeNull("an address that moves on restart is not one to pin a channel to");
    }

    [Fact]
    public async Task A_group_with_no_address_at_all_offers_none()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup(AzureContainerInstanceScenario.GroupJson(ip: null));

        var address = await scenario.Provisioner().ResolveControlAddressAsync(Handle());

        address.Should().BeOfType<ControlChannelAddress.NoAddress>()
            .Which.Reason.Should().Contain("no public address");
    }

    [Fact]
    public async Task A_handle_that_is_not_a_container_group_is_declined_without_calling_azure()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        var address = await scenario.Provisioner().ResolveControlAddressAsync(
            new ResourceHandle(
                AzureContainerInstanceProvisioner.Id,
                AzureContainerInstanceScenario.ForeignVmId,
                AzureContainerInstanceScenario.Region,
                AzureContainerInstanceScenario.CanonicalTags));

        address.Should().BeOfType<ControlChannelAddress.NoAddress>();
        scenario.Api.Requests.Should().BeEmpty("not even a token exchange, for a handle this adapter does not own");
    }

    [Fact]
    public async Task A_group_that_no_longer_exists_offers_no_address_rather_than_throwing()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.Api.Responder = request => request.IsTokenExchange
            ? AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.TokenJson())
            : AzureArmApiDouble.Empty(HttpStatusCode.NotFound);

        var address = await scenario.Provisioner().ResolveControlAddressAsync(Handle());

        // A capability answer is not an error - the same rule IProvisioner's remarks state for the verbs.
        address.Should().BeOfType<ControlChannelAddress.NoAddress>()
            .Which.Reason.Should().Contain("no longer exists");
    }

    [Fact]
    public async Task Resolving_an_address_reads_the_container_group_and_nothing_else()
    {
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();

        await scenario.Provisioner().ResolveControlAddressAsync(Handle());

        // The responder in this scenario throws for any path other than /containerGroups/, so a request to
        // Microsoft.Storage would have failed the test outright. This pins the shape of what was sent.
        scenario.Api.ArmRequests.Should().AllSatisfy(r =>
        {
            r.Method.Should().Be(HttpMethod.Get);
            r.Uri.AbsolutePath.Should().Contain("/containerGroups/");
        });

        scenario.Secrets.Resolved.Should().NotContain(AzureContainerInstanceScenario.StorageKeyUrn.Value);
    }

    [Fact]
    public async Task Having_a_durable_control_address_does_not_make_the_group_reachable()
    {
        // The invariant the whole design turns on. An operator can now talk to the game; Servyx still cannot
        // read a file, run a command, or name a transport, and the Provision tier remains permanently out of
        // reach because ACI has no compose file.
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();
        var provisioner = scenario.Provisioner();

        var address = await provisioner.ResolveControlAddressAsync(Handle());
        var refreshed = await provisioner.RefreshAsync(Handle());

        address.Should().BeOfType<ControlChannelAddress.Durable>();
        refreshed.Should().NotBeNull();
        refreshed!.Reachability.Should().BeOfType<ResourceReachability.NoTransport>()
            .Which.Reason.Should().Be(AzureContainerInstanceProvisioner.UnreachableReason);
        refreshed.TargetOrNull().Should().BeNull();
    }

    [Fact]
    public async Task Resolving_an_address_grants_no_new_capability_bit()
    {
        // StaticAddress in particular. The FQDN survives a restart; the address behind it still moves, and
        // claiming the bit would be a lie about something an operator would build automation on.
        var scenario = new AzureContainerInstanceScenario();
        scenario.RespondWithGroup();
        var provisioner = scenario.Provisioner();

        await provisioner.ResolveControlAddressAsync(Handle());

        provisioner.Capabilities.Should().Be(
            ProvisioningCapabilities.Create
            | ProvisioningCapabilities.Destroy
            | ProvisioningCapabilities.TagQuery
            | ProvisioningCapabilities.EstimatesCost);
    }

    [Fact]
    public void The_adapter_answers_the_control_address_question_at_all()
    {
        // Implementing the interface is the adapter stating it has considered the question. Its absence on a
        // shape-M adapter would mean the resource can be created, billed and destroyed and never operated.
        scenarioProvisioner().Should().BeAssignableTo<IControlChannelAddressSource>();

        static AzureContainerInstanceProvisioner scenarioProvisioner()
        {
            var scenario = new AzureContainerInstanceScenario();
            scenario.RespondWithGroup();
            return scenario.Provisioner();
        }
    }
}
