using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// Whether a control channel can be pinned to a Fargate service, and the evidence that it cannot.
/// </summary>
/// <remarks>
/// <para>
/// The RCON control channel is what lifts a shape-M resource to the <c>Operate</c> tier, and it needs an
/// address that outlives the provider replacing the workload. ACI has one when the container group carries a
/// <c>dnsNameLabel</c>. This adapter has none, and these assertions pin that as a <em>finding</em> rather
/// than as a gap: the interface is implemented, it always answers
/// <see cref="ControlChannelAddress.NoAddress"/>, and the reason names the two things (a load balancer, a
/// Cloud Map registration) that would have to be created for the answer to change.
/// </para>
/// <para>
/// Not even <see cref="ControlChannelAddress.Ephemeral"/>. That case is for an address that works today and
/// silently stops being right; a Fargate task's private IPv4 does not clear even the first half of that bar,
/// because <c>DescribeTasks</c> reports no public address at all and Servyx is not inside the task's
/// <c>awsvpc</c> subnet. Reporting it as merely non-durable would overstate how close this target is to
/// being operable.
/// </para>
/// </remarks>
public class AwsEcsFargateControlAddressTests
{
    [Fact]
    public async Task A_fargate_service_has_no_control_address_at_all()
    {
        var scenario = new EcsScenario();

        var address = await scenario.Provisioner().ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        address.Should().BeOfType<ControlChannelAddress.NoAddress>();
        address.OpenableHostOrNull().Should().BeNull();
    }

    [Fact]
    public async Task It_is_not_merely_ephemeral()
    {
        // The distinction is the whole point of having three cases. An ephemeral answer would say "you have
        // an address, it just moves"; the truth is that the only address in existence is also unroutable
        // from Servyx, so there is nothing to offer even for one command.
        var scenario = new EcsScenario();

        var address = await scenario.Provisioner().ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        address.Should().NotBeOfType<ControlChannelAddress.Ephemeral>();
        address.Should().NotBeOfType<ControlChannelAddress.Durable>();
    }

    [Fact]
    public async Task The_reason_names_what_would_have_to_be_created_for_the_answer_to_change()
    {
        var scenario = new EcsScenario();

        var address = await scenario.Provisioner().ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        var reason = address.Should().BeOfType<ControlChannelAddress.NoAddress>().Which.Reason;

        reason.Should().Be(AwsEcsFargateProvisioner.NoControlAddressReason);
        reason.Should().Contain("load balancer");
        reason.Should().Contain("Cloud Map");
        reason.Should().Contain("replace that task", "the address dies with a task the service exists to throw away");
        reason.Should().Contain("DescribeNetworkInterfaces", "the public address would need a service this adapter does not call");
    }

    [Fact]
    public async Task Answering_costs_no_request_of_any_kind()
    {
        // The answer does not depend on the service's state, so asking AWS would bill a caller for a round
        // trip that could not change it. No DescribeServices, no DescribeTasks, no signature, no credential.
        var scenario = new EcsScenario();

        await scenario.Provisioner().ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        scenario.Api.Requests.Should().BeEmpty();
        scenario.Secrets.Resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task The_answer_does_not_depend_on_the_handle_it_is_asked_about()
    {
        // A handle naming a service in another cluster is equally unserviceable. Inventing a second reason
        // for it would suggest the first one was situational, and it is not: it is a property of the shape.
        var scenario = new EcsScenario();
        var provisioner = scenario.Provisioner();

        var mine = await provisioner.ResolveControlAddressAsync(EcsScenario.RecordedHandle());
        var foreign = await provisioner.ResolveControlAddressAsync(
            EcsScenario.RecordedHandle(providerResourceId: EcsScenario.ForeignClusterServiceArn));

        mine.Should().Be(foreign);
    }

    [Fact]
    public async Task A_cancelled_request_is_still_honoured()
    {
        var scenario = new EcsScenario();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => scenario.Provisioner().ResolveControlAddressAsync(EcsScenario.RecordedHandle(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void The_adapter_answers_the_control_address_question_rather_than_leaving_it_unstated()
    {
        // Implementing the interface to answer "no" is the point. Omitting it would leave "this target still
        // cannot be operated" as an absence a reader has to notice, rather than a value a test can pin.
        new EcsScenario().Provisioner().Should().BeAssignableTo<IControlChannelAddressSource>();
    }

    [Fact]
    public async Task Answering_no_changes_nothing_about_the_resource_or_the_adapter()
    {
        var scenario = new EcsScenario();
        var provisioner = scenario.Provisioner();

        await provisioner.ResolveControlAddressAsync(EcsScenario.RecordedHandle());

        provisioner.Capabilities.Should().Be(
            ProvisioningCapabilities.Create
            | ProvisioningCapabilities.Destroy
            | ProvisioningCapabilities.TagQuery
            | ProvisioningCapabilities.EstimatesCost);

        AwsEcsFargateProvisioner.UnreachableReason.Should().Contain("not stable");
    }
}
