using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The <see cref="IUpdateApplier"/> half of the EC2 adapter: the one operation it will carry out, the many it
/// refuses, and — the assertions the rest of this file exists to protect — that a refusal issues no mutating
/// request and that a submitted step is never mistaken for a finished one.
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs against the substituted AWS endpoint, so no network access, no AWS account and no IAM
/// credential beyond the fake key pair in the scenario is involved. The refusal tests assert on the request
/// list itself — for the guards that run before any HTTP, that it is <em>empty</em>; for the one guard that
/// needs to read the machine, that it contains reads and no <c>POST</c> at all — because the claim being made
/// is about EC2's state and not about this process's.
/// </para>
/// <para>
/// <strong>What makes this suite bigger than its two siblings.</strong> A droplet resize is one action and an
/// Azure resize is one PATCH; an EC2 instance-type change is three calls — <c>StopInstances</c>,
/// <c>ModifyInstanceAttribute</c>, <c>StartInstances</c> — and the machine is deliberately powered off in the
/// middle of them. So the outcomes an operator has to be able to tell apart are not three but five, and the two
/// extra ones are the ones this file spends most of its assertions on: "the instance is stopped and was NOT
/// resized" and "the instance is stopped and WAS resized". The second is not a failed resize, and reporting it
/// as one would send somebody unpicking an update that actually worked instead of pressing start.
/// </para>
/// </remarks>
public class AwsEc2InstanceTypeExecutionTests
{
    /// <summary>The instance type these tests ask for. Distinct from <see cref="AwsScenario.InstanceType"/>.</summary>
    private const string TargetType = "t3.large";

    /// <summary>An AMI other than the one the scenario's instance runs, for the image-change refusal.</summary>
    private const string OtherAmi = "ami-0999888777666555";

    // -------------------------------------------------------------------------------------------------
    // The adapter is an update applier at all, and says so honestly
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_is_an_update_applier_and_the_two_ids_agree()
    {
        var provisioner = new AwsScenario().Provisioner();

        provisioner.Should().BeAssignableTo<IUpdateApplier>();
        ((IUpdateApplier)provisioner).ProvisionerId.Should().Be(AwsEc2Provisioner.Id);
    }

    [Fact]
    public void Executing_a_type_change_does_not_make_the_adapter_claim_the_resize_capability()
    {
        // The understated direction, matching the DigitalOcean adapter: the ability is discovered by the type
        // test above, which is checkable, rather than by a flag describing a broader promise.
        new AwsScenario().Provisioner().Capabilities
            .Should().NotHaveFlag(ProvisioningCapabilities.Resize);
    }

    // -------------------------------------------------------------------------------------------------
    // The one operation it performs: three calls, in one order, each observed before the next
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_type_change_issues_stop_then_modify_then_start_in_that_order()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTypeChange(scenario);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Completed>();

        // Ordering, not presence: a modify before the stop is refused by EC2, and a start before the modify
        // brings the machine back up on the old type.
        MutatingActions(scenario).Should().Equal("StopInstances", "ModifyInstanceAttribute", "StartInstances");
    }

    [Fact]
    public async Task Each_of_the_three_steps_is_submitted_exactly_once()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(pollAttempts: 5);
        RouteTypeChange(scenario, stopStates: ["stopping", "stopping", "stopped"], startStates: ["pending", "running"]);

        await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        MutatingActions(scenario).Should().Equal("StopInstances", "ModifyInstanceAttribute", "StartInstances");
    }

    [Fact]
    public async Task The_stop_request_never_carries_the_force_parameter()
    {
        // StopInstances accepts Force, which skips the guest's own shutdown and can corrupt the filesystem.
        // There is no parameter on the client method that could produce one, and this is the wire-level proof.
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTypeChange(scenario);

        await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        var stop = scenario.Api.Requests.Single(r => string.Equals(r.Action, "StopInstances", StringComparison.Ordinal));
        stop.Body.Should().NotBeNull();
        stop.Body.Should().NotContain("Force");
        stop.ParameterOf("Force").Should().BeNull();
        stop.ParameterOf("InstanceId.1").Should().Be(AwsScenario.Ec2InstanceId);
    }

    [Fact]
    public async Task The_modify_request_names_the_instance_type_and_no_other_attribute()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTypeChange(scenario);

        await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        var modify = scenario.Api.Requests
            .Single(r => string.Equals(r.Action, "ModifyInstanceAttribute", StringComparison.Ordinal));

        modify.ParameterOf("InstanceId").Should().Be(AwsScenario.Ec2InstanceId);
        modify.ParameterOf("InstanceType.Value").Should().Be(TargetType);

        // ModifyInstanceAttribute writes one attribute per call. The safety claim of this adapter, on the wire:
        // a request that cannot describe a different machine.
        modify.Body.Should().NotContain("ImageId");
        modify.Body.Should().NotContain("BlockDeviceMapping");
        modify.Body.Should().NotContain("UserData");
    }

    [Fact]
    public async Task No_request_the_whole_path_issues_terminates_or_launches_anything()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTypeChange(scenario);

        await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        scenario.Api.Requests.Should().NotBeEmpty();
        MutatingActions(scenario).Should().NotContain("TerminateInstances");
        MutatingActions(scenario).Should().NotContain("RunInstances");
        MutatingActions(scenario).Should().NotContain("DeleteVolume");
    }

    // -------------------------------------------------------------------------------------------------
    // Refusals decided before any HTTP. Every one of these asserts a request count of zero.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stale_plan_hash_is_refused_and_issues_no_request_at_all()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            plan,
            approvedPlanHash: "0000000000000000000000000000000000000000000000000000000000000000");

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("not the plan that was approved");

        scenario.Api.Requests.Should().BeEmpty("a refused plan sends nothing to EC2");
        MutatingActions(scenario).Should().BeEmpty();
    }

    [Fact]
    public async Task A_stale_plan_hash_is_refused_even_when_nothing_has_ever_been_read()
    {
        // The strongest form of the same claim: this scenario has issued no request at all, ever, so the empty
        // request list cannot be an artefact of anything having been cleared. And because AWS's credential is
        // resolved per request rather than transmitted, a refusal is also proved not to have touched the key
        // pair - the assertion the sibling suites cannot make in this shape.
        var scenario = new AwsScenario();
        FailOnAnyRequest(scenario);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            HandBuiltPlan([TypeChange()], planHash: "abc123"),
            approvedPlanHash: "def456");

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
        scenario.Secrets.Resolved.Should().BeEmpty("a refusal does not even resolve the key pair");
    }

    [Fact]
    public async Task An_image_change_plan_is_refused_with_no_request_at_all()
    {
        // The real thing, planned by the real planner. EC2 fixes an instance's AMI at RunInstances time and
        // ModifyInstanceAttribute cannot alter it, so an image change is a terminate-and-launch whose cost to
        // the data is decided by a DeleteOnTermination flag this adapter never set. It is not implemented, and
        // this is the assertion that keeps it that way.
        var (scenario, provisioner, plan) = await PlannedAsync(
            size: AwsScenario.InstanceType,
            overrides: new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherAmi });

        plan.Changes.Should().ContainSingle().Which.Aspect.Should().Be("image");
        plan.Strategy.Should().Be(UpdateStrategy.Recreate);
        plan.DataImpact.Should().Be(DataImpact.Destroyed);

        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("only an in-place instance-type change");

        scenario.Api.Requests.Should().BeEmpty("a plan that would terminate the instance is never sent");
    }

    [Fact]
    public async Task An_image_change_bundled_with_a_type_change_is_refused_with_no_request_at_all()
    {
        // The combination is the dangerous one: the plan contains a change this file *can* execute. It is still
        // refused whole, because the planner's answer to an image difference is a replacement and there is no
        // executable subset of that.
        var (scenario, provisioner, plan) = await PlannedAsync(
            overrides: new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherAmi });

        plan.Changes.Select(c => c.Aspect).Should().BeEquivalentTo(["size", "image"]);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_image_change_claiming_to_preserve_data_is_still_refused_with_no_request_at_all()
    {
        // The planner cannot produce this, so it is built by hand: a plan that describes an image change while
        // claiming an in-place strategy and preserved data. The aspect check catches it even though both
        // properties an approver would have read say it is safe.
        var scenario = new AwsScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan([new PlannedChange("image", AwsScenario.ImageId, OtherAmi, RequiresRecreate: false)]);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("instance-type change and nothing else");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_region_change_plan_is_refused_with_no_request_at_all()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(
            size: AwsScenario.InstanceType,
            overrides: new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "eu-west-1" });

        plan.Strategy.Should().Be(UpdateStrategy.Recreate);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_belonging_to_another_provisioner_is_refused_with_no_request_at_all()
    {
        var scenario = new AwsScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan([TypeChange()], provisionerId: "digitalocean-droplet");

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("digitalocean-droplet");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_handle_belonging_to_another_provisioner_is_refused_with_no_request_at_all()
    {
        var scenario = new AwsScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan([TypeChange()]);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(provisionerId: "aws-lightsail"), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("aws-lightsail");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_handle_that_is_not_an_instance_id_is_refused_with_no_request_at_all()
    {
        var scenario = new AwsScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan([TypeChange()]);

        // A real EC2 id, and a real Servyx-owned resource - just not an instance. Stopping "whatever answers to
        // that id" is exactly what this guard exists to prevent.
        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(providerResourceId: AwsScenario.VolumeId), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("is not an EC2 instance id");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_that_reports_no_change_is_refused_with_no_request_at_all()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(size: AwsScenario.InstanceType);

        plan.Strategy.Should().Be(UpdateStrategy.NoChangeRequired);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_type_change_bundled_with_a_tag_write_is_refused_rather_than_partly_applied()
    {
        // Executing the half it understands and skipping the rest would report a half-applied update as an
        // applied one - and here it would also have taken the machine down and back up on the way.
        var scenario = new AwsScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan(
        [
            TypeChange(),
            new PlannedChange("tag servyx.owner", null, "ops", RequiresRecreate: false),
        ]);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("instance-type change and nothing else");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_that_does_not_preserve_data_is_refused_with_no_request_at_all()
    {
        var scenario = new AwsScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan([TypeChange()], dataImpact: DataImpact.AtRisk);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("AtRisk");

        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_type_change_naming_no_target_is_refused_with_no_request_at_all()
    {
        var scenario = new AwsScenario();
        FailOnAnyRequest(scenario);

        var plan = HandBuiltPlan([new PlannedChange("size", AwsScenario.InstanceType, null, RequiresRecreate: false)]);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------------
    // An instance that cannot be stopped, and the one guard that reads before it decides
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_instance_store_backed_instance_is_refused_by_the_planner_before_any_request()
    {
        // The planner's own answer: an instance whose block device mapping reports no EBS volume is AtRisk, so
        // the data-impact guard declines it and no request of any kind goes out.
        var (scenario, provisioner, plan) = await PlannedAsync(
            describeXml: AwsScenario.DescribeInstancesXml(null, AwsScenario.InstanceXml(withBlockDevice: false)));

        plan.DataImpact.Should().Be(DataImpact.AtRisk);
        FailOnAnyRequest(scenario);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_instance_store_backed_instance_is_refused_from_the_live_block_device_mapping()
    {
        // The executor's own check, isolated from the planner's. The plan handed in claims Preserved; the live
        // machine reports no EBS volume at all, which is where that claim has to be enumerated from - so the
        // live machine wins, and the read that establishes it is the only request made.
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(null, AwsScenario.InstanceXml(withBlockDevice: false)));

        var plan = HandBuiltPlan([TypeChange()]);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        var refused = result.Should().BeOfType<UpdateExecutionResult.Refused>().Which;
        refused.Message.Should().Contain("instance-store backed");
        refused.Message.Should().Contain("cannot stop such an instance");
        refused.Message.Should().Contain("Nothing was stopped");

        scenario.Api.Requests.Should().ContainSingle("the machine is read once, and that is all");
        MutatingActions(scenario).Should().BeEmpty("nothing that changes the instance was issued");
    }

    [Fact]
    public async Task An_instance_ec2_no_longer_has_is_refused_before_anything_is_stopped()
    {
        var scenario = new AwsScenario();
        scenario.RouteMissingInstance();

        var plan = HandBuiltPlan([TypeChange()]);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("InvalidInstanceID.NotFound");

        MutatingActions(scenario).Should().BeEmpty();
    }

    [Fact]
    public async Task A_terminated_instance_is_refused_before_anything_is_stopped()
    {
        // EC2 answers with a complete instance object for a machine that has stopped existing, for about an
        // hour. "Gone" is a state here, not a 404, and it has to be consulted as one before a stop is issued.
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(null, AwsScenario.InstanceXml(state: "terminated")));

        var plan = HandBuiltPlan([TypeChange()]);

        var result = await scenario.Provisioner().ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Refused>()
            .Which.Message.Should().Contain("terminated");

        MutatingActions(scenario).Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------------
    // Step 1: a stop that is not observed reaching 'stopped' never becomes a modify
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stop_that_never_reaches_stopped_does_not_proceed_to_the_modify()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(pollAttempts: 3);
        RouteTypeChange(scenario, stopStates: ["stopping"]);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        // The assertion this test exists for: the attribute write was never issued.
        MutatingActions(scenario).Should().Equal("StopInstances");
        MutatingActions(scenario).Should().NotContain("ModifyInstanceAttribute");
        MutatingActions(scenario).Should().NotContain("StartInstances");

        var timedOut = result.Should().BeOfType<UpdateExecutionResult.TimedOut>().Which;
        timedOut.Message.Should().Contain("after 3 check(s)");
        timedOut.Message.Should().Contain("The instance type was NOT changed");
        timedOut.Message.Should().Contain("Do not resubmit");

        // And the operator is told which side of the stop they are on: the machine is going down.
        timedOut.Message.Should().Contain("DOWN");
        timedOut.Message.Should().Contain(AwsScenario.VolumeId);
    }

    [Fact]
    public async Task A_stop_aws_refuses_reports_that_nothing_about_the_machine_changed()
    {
        const string AwsMessage = "The instance 'i-0abcdef1234567890' may not be stopped in its current state";

        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTypeChange(
            scenario,
            overrideResponder: request => string.Equals(request.Action, "StopInstances", StringComparison.Ordinal)
                ? AwsApiDouble.Xml(HttpStatusCode.BadRequest, AwsScenario.ErrorXml("IncorrectInstanceState", AwsMessage))
                : null);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        var failed = result.Should().BeOfType<UpdateExecutionResult.Failed>().Which;
        failed.Message.Should().Contain(AwsMessage);
        failed.Message.Should().Contain("The instance was NOT stopped");
        failed.Message.Should().Contain("the workload was not " + "interrupted");

        MutatingActions(scenario).Should().Equal("StopInstances");
    }

    // -------------------------------------------------------------------------------------------------
    // Step 2: stopped and NOT resized — the outcome neither sibling adapter has
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_modify_aws_refuses_says_the_instance_is_stopped_and_still_the_old_type()
    {
        const string AwsMessage = "The instance type 't3.large' is not supported in this availability zone";

        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTypeChange(
            scenario,
            overrideResponder: request =>
                string.Equals(request.Action, "ModifyInstanceAttribute", StringComparison.Ordinal)
                    ? AwsApiDouble.Xml(HttpStatusCode.BadRequest, AwsScenario.ErrorXml("Unsupported", AwsMessage))
                    : null);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        var failed = result.Should().BeOfType<UpdateExecutionResult.Failed>().Which;

        // The three questions, answered in order: is it up (no), did I lose anything (no), what now (start it).
        failed.Message.Should().Contain("THE INSTANCE IS STOPPED AND ITS TYPE WAS NOT CHANGED");
        failed.Message.Should().Contain("still '" + AwsScenario.InstanceType + "'");
        failed.Message.Should().Contain("OFFLINE");
        failed.Message.Should().Contain(AwsScenario.VolumeId);
        failed.Message.Should().Contain("start the instance");
        failed.Message.Should().Contain(AwsMessage);

        // The start was never issued: there is nothing correct to bring back up.
        MutatingActions(scenario).Should().Equal("StopInstances", "ModifyInstanceAttribute");
    }

    [Fact]
    public async Task A_modify_ec2_answers_false_to_is_not_read_as_success()
    {
        // A refusal that arrives with an HTTP 200. Reading <return>false</return> as success would start the
        // machine again on the old type and report the update as applied.
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTypeChange(
            scenario,
            overrideResponder: request =>
                string.Equals(request.Action, "ModifyInstanceAttribute", StringComparison.Ordinal)
                    ? AwsApiDouble.Xml(HttpStatusCode.OK, ReturnXml("ModifyInstanceAttributeResponse", "false"))
                    : null);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Failed>()
            .Which.Message.Should().Contain("THE INSTANCE IS STOPPED AND ITS TYPE WAS NOT CHANGED");

        MutatingActions(scenario).Should().Equal("StopInstances", "ModifyInstanceAttribute");
    }

    [Fact]
    public async Task A_modify_that_is_accepted_but_never_takes_effect_is_not_read_as_success()
    {
        // The attribute write is confirmed by reading the type back, not by trusting the 200 that acknowledged
        // it - the same rule the stop and the start follow, applied to the one step that looks synchronous.
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTypeChange(scenario, typeAfterModify: AwsScenario.InstanceType);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        result.Should().BeOfType<UpdateExecutionResult.Failed>()
            .Which.Message.Should().Contain("THE INSTANCE IS STOPPED AND ITS TYPE WAS NOT CHANGED");

        MutatingActions(scenario).Should().NotContain("StartInstances");
    }

    // -------------------------------------------------------------------------------------------------
    // Step 3: stopped and RESIZED — its own outcome, not a generic failure
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_start_aws_refuses_after_a_successful_modify_is_its_own_outcome()
    {
        const string AwsMessage = "Insufficient capacity for instance type t3.large in us-east-1a";

        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTypeChange(
            scenario,
            overrideResponder: request => string.Equals(request.Action, "StartInstances", StringComparison.Ordinal)
                ? AwsApiDouble.Xml(HttpStatusCode.BadRequest, AwsScenario.ErrorXml("InsufficientInstanceCapacity", AwsMessage))
                : null);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        var failed = result.Should().BeOfType<UpdateExecutionResult.Failed>().Which;

        // Not a generic failure: the resize worked, and saying so is the difference between a five-second fix
        // and unpicking an update that succeeded.
        failed.Message.Should().Contain("THE TYPE CHANGE SUCCEEDED AND THE INSTANCE IS STOPPED");
        failed.Message.Should().Contain("IS now '" + TargetType + "'");
        failed.Message.Should().Contain("Only the start did not complete");
        failed.Message.Should().Contain("Only the start needs retrying");
        failed.Message.Should().Contain("do NOT re-run this update");
        failed.Message.Should().Contain("OFFLINE");
        failed.Message.Should().Contain(AwsScenario.VolumeId);
        failed.Message.Should().Contain(AwsMessage);

        MutatingActions(scenario).Should().Equal("StopInstances", "ModifyInstanceAttribute", "StartInstances");
    }

    [Fact]
    public async Task A_start_that_never_reaches_running_is_neither_success_nor_a_failed_resize()
    {
        var (scenario, provisioner, plan) = await PlannedAsync(pollAttempts: 2);
        RouteTypeChange(scenario, startStates: ["pending"]);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        // Not success - the machine was never observed running.
        result.Should().NotBeOfType<UpdateExecutionResult.Completed>();

        var timedOut = result.Should().BeOfType<UpdateExecutionResult.TimedOut>().Which;
        timedOut.Message.Should().Contain("THE TYPE CHANGE SUCCEEDED AND THE INSTANCE IS STOPPED");
        timedOut.Message.Should().Contain("after 2 check(s)");
        timedOut.Message.Should().Contain("re-read the instance's state first");
    }

    [Fact]
    public async Task A_refused_start_and_an_unobserved_start_are_different_types()
    {
        var (refusedScenario, refusedProvisioner, refusedPlan) = await PlannedAsync();
        RouteTypeChange(
            refusedScenario,
            overrideResponder: request => string.Equals(request.Action, "StartInstances", StringComparison.Ordinal)
                ? AwsApiDouble.Xml(HttpStatusCode.BadRequest, AwsScenario.ErrorXml("InsufficientInstanceCapacity", "no capacity"))
                : null);
        var refused = await refusedProvisioner.ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(), refusedPlan, refusedPlan.PlanHash);

        var (runningScenario, runningProvisioner, runningPlan) = await PlannedAsync(pollAttempts: 2);
        RouteTypeChange(runningScenario, startStates: ["pending"]);
        var stillStarting = await runningProvisioner.ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(), runningPlan, runningPlan.PlanHash);

        // A refused start is over and can be retried; one that was accepted may still succeed on its own, and
        // resubmitting it is a second mutation rather than a retry.
        refused.Should().BeOfType<UpdateExecutionResult.Failed>();
        stillStarting.Should().BeOfType<UpdateExecutionResult.TimedOut>();
        refused.GetType().Should().NotBe(stillStarting.GetType());
    }

    // -------------------------------------------------------------------------------------------------
    // Success, and what it takes to earn it
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Success_requires_the_start_to_be_observed_running_not_merely_submitted()
    {
        // Identical routes but for the states the instance reports after the start. The only difference between
        // a success and a not-success on this path is an observation.
        var (submittedScenario, submittedProvisioner, submittedPlan) = await PlannedAsync(pollAttempts: 2);
        RouteTypeChange(submittedScenario, startStates: ["pending"]);
        var submitted = await submittedProvisioner.ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(), submittedPlan, submittedPlan.PlanHash);

        var (observedScenario, observedProvisioner, observedPlan) = await PlannedAsync(pollAttempts: 2);
        RouteTypeChange(observedScenario, startStates: ["pending", "running"]);
        var observed = await observedProvisioner.ApplyUpdateAsync(
            AwsScenario.MaintenanceHandle(), observedPlan, observedPlan.PlanHash);

        submitted.Should().NotBeOfType<UpdateExecutionResult.Completed>();
        observed.Should().BeOfType<UpdateExecutionResult.Completed>()
            .Which.Message.Should().Contain("after 2 check(s)");

        // Both submitted a start; only one of them saw it finish.
        MutatingActions(submittedScenario).Should().Contain("StartInstances");
        MutatingActions(observedScenario).Should().Contain("StartInstances");
    }

    [Fact]
    public async Task A_completed_type_change_hands_back_the_instance_as_it_now_is()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTypeChange(scenario);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        var completed = result.Should().BeOfType<UpdateExecutionResult.Completed>().Which;

        completed.Resource.Handle.ProviderResourceId.Should().Be(AwsScenario.Ec2InstanceId);
        completed.Resource.ConnectorId.Should().Be(AwsScenario.ConnectorId);
        completed.Resource.Facts.Cost.Should().NotBeNull();

        completed.Message.Should().Contain(AwsScenario.InstanceType);
        completed.Message.Should().Contain(TargetType);
        completed.Message.Should().Contain("ModifyInstanceAttribute");
    }

    [Fact]
    public async Task A_completed_type_change_names_the_volumes_it_read_back_off_the_live_instance()
    {
        var (scenario, provisioner, plan) = await PlannedAsync();
        RouteTypeChange(scenario);

        var result = await provisioner.ApplyUpdateAsync(AwsScenario.MaintenanceHandle(), plan, plan.PlanHash);

        var completed = result.Should().BeOfType<UpdateExecutionResult.Completed>().Which;

        // The data claim is enumerated, not asserted: the volume the live instance reports is named.
        completed.Message.Should().Contain(AwsScenario.VolumeId);
        completed.Message.Should().Contain("read back off the live block device mapping");
        completed.Message.Should().Contain("A stop is not a terminate");

        // And the two costs that are not data impacts are stated rather than hidden behind "in place".
        completed.Message.Should().Contain("DOWN for the whole stop/start");
        completed.Message.Should().Contain("public IPv4 address is ephemeral");
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a genuine <see cref="UpdatePlan"/> with the real planner, then clears the recorded requests so
    /// the execution assertions that follow count only what execution itself issued.
    /// </summary>
    private static async Task<(AwsScenario Scenario, AwsEc2Provisioner Provisioner, UpdatePlan Plan)> PlannedAsync(
        string size = TargetType,
        IReadOnlyDictionary<string, string>? overrides = null,
        int pollAttempts = 3,
        string? describeXml = null)
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(describeXml);

        var provisioner = scenario.Provisioner(statePollAttempts: pollAttempts);

        var plan = await provisioner.PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(overrides, size));

        plan.Should().NotBeNull();
        scenario.Api.Requests.Clear();

        return (scenario, provisioner, plan!);
    }

    /// <summary>The lone instance-type change every hand-built plan in this file is built around.</summary>
    private static PlannedChange TypeChange() =>
        new("size", AwsScenario.InstanceType, TargetType, RequiresRecreate: false);

    /// <summary>
    /// A plan built by hand, for the shapes the real planner cannot currently produce — a plan belonging to
    /// another provisioner, a type change bundled with a tag write, an image change that claims to preserve
    /// data.
    /// </summary>
    private static UpdatePlan HandBuiltPlan(
        IReadOnlyList<PlannedChange> changes,
        string planHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        string provisionerId = AwsEc2Provisioner.Id,
        UpdateStrategy strategy = UpdateStrategy.InPlace,
        DataImpact dataImpact = DataImpact.Preserved) =>
        new(
            planId: "test:update:1",
            planHash: planHash,
            provisionerId: provisionerId,
            strategy: strategy,
            dataImpact: dataImpact,
            changes: changes,
            stages: [new ProvisioningStage("change-instance-type", provisionerId, "Change the instance type.")],
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(15));

    /// <summary>Makes any HTTP request at all fail the test where it happens.</summary>
    private static void FailOnAnyRequest(AwsScenario scenario) =>
        scenario.Api.Responder = request => throw new InvalidOperationException(
            $"A refused update issued a {request.Method} request to '{request.Uri}' (Action='{request.Action}'). "
            + "It must send nothing at all.");

    /// <summary>The EC2 actions the adapter submitted as writes, in the order it submitted them.</summary>
    /// <remarks>
    /// Reads the <c>POST</c>s only. This client sends every read as a <c>GET</c> with its parameters in the
    /// query string and every write as a <c>POST</c> with a form body, so the verb is a reliable partition —
    /// and it is the partition the "zero mutating requests" claims in this file are made over.
    /// </remarks>
    private static List<string?> MutatingActions(AwsScenario scenario) =>
        scenario.Api.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Action).ToList();

    /// <summary>
    /// Routes the whole stop / modify / start exchange as EC2 answers it, walking the instance through the
    /// states a caller-supplied script names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately stateful, because the adapter's reads are all the same <c>DescribeInstances</c> GET and the
    /// only thing that distinguishes them is which of the three writes has happened. The instance therefore
    /// reports: <c>running</c> at the old type before the stop, then <paramref name="stopStates"/> (repeating
    /// the last), then <c>stopped</c> at <paramref name="typeAfterModify"/> once the attribute write lands,
    /// then <paramref name="startStates"/> (repeating the last).
    /// </para>
    /// <para>
    /// <paramref name="overrideResponder"/> lets one test replace the answer to one action — an AWS refusal of
    /// the stop, of the modify, or of the start — without restating the rest of the exchange.
    /// </para>
    /// </remarks>
    private static void RouteTypeChange(
        AwsScenario scenario,
        IReadOnlyList<string>? stopStates = null,
        IReadOnlyList<string>? startStates = null,
        string typeAfterModify = TargetType,
        Func<RecordedRequest, HttpResponseMessage?>? overrideResponder = null)
    {
        var stopScript = stopStates ?? ["stopping", "stopped"];
        var startScript = startStates ?? ["pending", "running"];

        var phase = 0;
        var stopIndex = 0;
        var startIndex = 0;
        var currentType = AwsScenario.InstanceType;

        scenario.Api.Responder = request =>
        {
            var overridden = overrideResponder?.Invoke(request);
            if (overridden is not null)
            {
                return overridden;
            }

            if (request.Method == HttpMethod.Post)
            {
                if (string.Equals(request.Action, "StopInstances", StringComparison.Ordinal))
                {
                    phase = 1;
                    return AwsApiDouble.Xml(HttpStatusCode.OK, StateChangeXml("StopInstancesResponse", "stopping", "running"));
                }

                if (string.Equals(request.Action, "ModifyInstanceAttribute", StringComparison.Ordinal))
                {
                    currentType = typeAfterModify;
                    phase = 2;
                    return AwsApiDouble.Xml(HttpStatusCode.OK, ReturnXml("ModifyInstanceAttributeResponse", "true"));
                }

                if (string.Equals(request.Action, "StartInstances", StringComparison.Ordinal))
                {
                    phase = 3;
                    return AwsApiDouble.Xml(HttpStatusCode.OK, StateChangeXml("StartInstancesResponse", "pending", "stopped"));
                }

                throw new InvalidOperationException(
                    $"The instance-type change path issued an unexpected write: Action='{request.Action}'.");
            }

            var state = phase switch
            {
                0 => "running",
                1 => Walk(stopScript, ref stopIndex),
                2 => "stopped",
                _ => Walk(startScript, ref startIndex),
            };

            return AwsApiDouble.Xml(
                HttpStatusCode.OK,
                AwsScenario.DescribeInstancesXml(null, AwsScenario.InstanceXml(state: state, instanceType: currentType)));
        };
    }

    /// <summary>Reads the next scripted state, repeating the last one once the script is spent.</summary>
    private static string Walk(IReadOnlyList<string> script, ref int index)
    {
        var value = script[Math.Min(index, script.Count - 1)];
        index++;
        return value;
    }

    /// <summary>A <c>StopInstancesResponse</c>/<c>StartInstancesResponse</c> envelope.</summary>
    private static string StateChangeXml(string root, string currentState, string previousState) =>
        Envelope(
            root,
            $"<instancesSet><item><instanceId>{AwsScenario.Ec2InstanceId}</instanceId>"
            + $"<currentState><code>0</code><name>{currentState}</name></currentState>"
            + $"<previousState><code>16</code><name>{previousState}</name></previousState></item></instancesSet>");

    /// <summary>A response whose whole payload is EC2's <c>return</c> flag.</summary>
    private static string ReturnXml(string root, string value) => Envelope(root, $"<return>{value}</return>");

    private static string Envelope(string root, string inner) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
        + $"<{root} xmlns=\"http://ec2.amazonaws.com/doc/2016-11-15/\">"
        + "<requestId>abcd1234-0000-0000-0000-000000000000</requestId>"
        + inner
        + $"</{root}>";
}
