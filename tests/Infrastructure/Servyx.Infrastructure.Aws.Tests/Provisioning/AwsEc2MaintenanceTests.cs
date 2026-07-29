using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The <see cref="IMaintainer"/> half of the EC2 adapter: what it plans, what it refuses to plan, and — the
/// assertion the rest of this file exists to protect — what it says will happen to the machine's disk.
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs against the substituted AWS endpoint, so no network access, no AWS account and no IAM
/// credential beyond the fake key pair in the scenario is involved. Several of them assert on the
/// <em>absence</em> of a mutating request, which is a stronger claim than "the call failed": the read-only route
/// throws on any non-GET, so a plan that tried to stop, modify, terminate or launch anything would fail the test
/// at the point it tried.
/// </para>
/// <para>
/// Two EC2-specific facts drive most of what is asserted below and neither has an analogue in the DigitalOcean
/// or Azure suites. First, a terminated instance stays visible to <c>DescribeInstances</c> for about an hour, so
/// "gone" is a state rather than a 404 and a drift check that trusted the API's willingness to answer would
/// report a match for a deleted machine. Second, this adapter sends no <c>BlockDeviceMapping</c>, so whether a
/// replacement destroys the caller's data is decided by a <c>DeleteOnTermination</c> flag Servyx never set —
/// which means the plan's <see cref="DataImpact"/> has three possible answers where Azure's has two, and the
/// third is the one where the flag cannot be read at all.
/// </para>
/// </remarks>
public class AwsEc2MaintenanceTests
{
    private const string OtherAmi = "ami-0999888777666555";
    private const string OtherRegion = "eu-west-1";
    private const string OtherInstanceType = "t3.large";

    // ---------------------------------------------------------------------------------------------------
    // The adapter is a maintainer at all, and says so honestly
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_is_a_maintainer_and_the_two_ids_agree()
    {
        var provisioner = new AwsScenario().Provisioner();

        provisioner.Should().BeAssignableTo<IMaintainer>();
        ((IMaintainer)provisioner).ProvisionerId.Should().Be(AwsEc2Provisioner.Id);
    }

    [Fact]
    public void The_capability_set_is_exactly_the_seven_bits_the_adapter_implements() =>
        new AwsScenario().Provisioner().Capabilities.Should().Be(
            ProvisioningCapabilities.Create
            | ProvisioningCapabilities.Destroy
            | ProvisioningCapabilities.TagQuery
            | ProvisioningCapabilities.EstimatesCost
            | ProvisioningCapabilities.UpdateInPlace
            | ProvisioningCapabilities.RecreateToUpdate
            | ProvisioningCapabilities.DetectDrift);

    [Theory]
    [InlineData(ProvisioningCapabilities.UpdateInPlace)]
    [InlineData(ProvisioningCapabilities.RecreateToUpdate)]
    [InlineData(ProvisioningCapabilities.DetectDrift)]
    public void Every_maintenance_bit_is_claimed(ProvisioningCapabilities claimed) =>
        // UpdateInPlace: an instance-type change (stop, ModifyInstanceAttribute, start) and a retag both act on
        // the instance that already exists. RecreateToUpdate: an image change can only replace it.
        new AwsScenario().Provisioner().Capabilities.Should().HaveFlag(claimed);

    [Fact]
    public void Planning_a_type_change_is_not_the_same_promise_as_being_able_to_perform_one()
    {
        // The adapter plans the stop/ModifyInstanceAttribute/start sequence in detail and still does not claim
        // Resize, because no code path in the assembly issues ModifyInstanceAttribute. Nor Snapshot, which the
        // destructive plans below tell an operator to use before approving them.
        var capabilities = new AwsScenario().Provisioner().Capabilities;

        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.Resize);
        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.Snapshot);
    }

    // ---------------------------------------------------------------------------------------------------
    // Planning issues no mutating call
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanUpdateAsync_issues_exactly_one_read_and_no_mutating_request()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest());

        scenario.Api.Requests.Should().ContainSingle();
        scenario.Api.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
        scenario.Api.Requests.Should().OnlyContain(r => r.Action == "DescribeInstances");
    }

    [Fact]
    public async Task DetectDriftAsync_issues_exactly_one_read_and_no_mutating_request()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        await scenario.Provisioner().DetectDriftAsync(AwsScenario.MaintenanceHandle());

        scenario.Api.Requests.Should().ContainSingle();
        scenario.Api.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
        scenario.Api.Requests.Should().OnlyContain(r => r.Action == "DescribeInstances");
    }

    [Fact]
    public async Task The_request_log_of_a_replacement_plan_contains_only_reads()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        // The most destructive plan this adapter can produce, and it is still only a read. Nothing is stopped,
        // modified, terminated or launched - the read-only route would throw on any of those.
        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherAmi }));

        plan!.DataImpact.Should().Be(DataImpact.Destroyed);
        scenario.Api.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
        scenario.Api.Requests.Should().NotContain(r =>
            r.Action == "TerminateInstances"
            || r.Action == "RunInstances"
            || r.Action == "StopInstances"
            || r.Action == "ModifyInstanceAttribute"
            || r.Action == "CreateTags");
    }

    // ---------------------------------------------------------------------------------------------------
    // Nothing to change
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_instance_that_already_matches_the_request_needs_no_change_and_carries_no_stages()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest());

        plan.Should().NotBeNull();
        plan!.Strategy.Should().Be(UpdateStrategy.NoChangeRequired);
        plan.Changes.Should().BeEmpty();
        plan.Stages.Should().BeEmpty();
        plan.DataImpact.Should().Be(DataImpact.Preserved);
        plan.ProvisionerId.Should().Be(AwsEc2Provisioner.Id);
    }

    // ---------------------------------------------------------------------------------------------------
    // Instance type: the stop/modify/start cycle, and the volumes that demonstrably survive it
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_instance_type_change_is_planned_in_place_and_preserves_the_attached_volumes()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(size: OtherInstanceType));

        plan!.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.DataImpact.Should().Be(DataImpact.Preserved);

        var change = plan.Changes.Single();
        change.Aspect.Should().Be("size");
        change.Current.Should().Be(AwsScenario.InstanceType);
        change.Desired.Should().Be(OtherInstanceType);
        change.RequiresRecreate.Should().BeFalse();
    }

    [Fact]
    public async Task The_type_change_stage_names_the_operation_the_stop_it_requires_and_the_address_that_changes()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(size: OtherInstanceType));

        var stage = plan!.Stages.Single(s => s.StageId == "change-instance-type");

        // The operation is named, and so is the precondition a reader would otherwise have to know.
        stage.Description.Should().Contain("ModifyInstanceAttribute");
        stage.Description.Should().Contain("Stop instance");
        stage.Description.Should().Contain("start it again");

        // The Preserved claim is backed by the volume this adapter actually read off the live instance.
        stage.Description.Should().Contain(AwsScenario.VolumeId);
        stage.Description.Should().Contain("DeleteOnTermination is consulted on termination");

        // And the two real costs are stated rather than hidden behind "Preserved", which describes persistent
        // data and not availability or addressing.
        stage.Description.Should().Contain("WILL be a different address");
        stage.Description.Should().Contain("StaticAddress capability");
    }

    [Fact]
    public async Task The_data_impact_stage_of_a_type_change_names_the_instance_and_the_volume_that_survive()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(size: OtherInstanceType));

        var stage = plan!.Stages.Single(s => s.StageId == "data-impact");

        stage.Description.Should().Contain("Data impact of this plan is Preserved");
        stage.Description.Should().Contain(AwsScenario.Ec2InstanceId);
        stage.Description.Should().Contain(AwsScenario.VolumeId);
        stage.Description.Should().Contain("No step above terminates the instance");
    }

    [Fact]
    public async Task An_instance_with_no_ebs_volume_cannot_claim_a_preserved_type_change()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(
            null,
            AwsScenario.InstanceXml(withBlockDevice: false)));

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(size: OtherInstanceType));

        // No EBS volume means an instance-store-backed instance: it cannot be stopped at all, which is the
        // precondition ModifyInstanceAttribute needs, and its storage does not survive a stop. There is nothing
        // this adapter can show being carried across, so it does not claim anything is.
        plan!.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.DataImpact.Should().Be(DataImpact.AtRisk);

        var stage = plan.Stages.Single(s => s.StageId == "data-impact");
        stage.Description.Should().Contain("Data impact of this plan is AtRisk");
        stage.Description.Should().Contain("instance-store backed");
        stage.Description.Should().Contain("cannot be stopped");
    }

    // ---------------------------------------------------------------------------------------------------
    // Image: a replacement, and a data impact read off a flag this adapter never set
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_image_change_replaces_the_instance_and_reports_destroyed_when_the_volume_dies_with_it()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(
            null,
            AwsScenario.InstanceXml(deleteOnTermination: "true")));

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherAmi }));

        plan!.Strategy.Should().Be(UpdateStrategy.Recreate);
        plan.DataImpact.Should().Be(DataImpact.Destroyed);

        var change = plan.Changes.Single();
        change.Aspect.Should().Be("image");
        change.Current.Should().Be(AwsScenario.ImageId);
        change.Desired.Should().Be(OtherAmi);
        change.RequiresRecreate.Should().BeTrue();
    }

    [Fact]
    public async Task The_replacement_stages_say_why_no_gentler_operation_exists_and_what_the_terminate_deletes()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(
            null,
            AwsScenario.InstanceXml(deleteOnTermination: "true")));

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherAmi }));

        var terminate = plan!.Stages.Single(s => s.StageId == "terminate-instance");

        // Why there is no in-place alternative, stated rather than left as an absence.
        terminate.Description.Should().Contain("ModifyInstanceAttribute cannot alter it");

        // Plain language, not jargon: nobody should have to know what "terminate" means to a cloud API in order
        // to understand that their saves are about to be deleted.
        terminate.Description.Should().Contain("THIS DELETES THE MACHINE'S DISK");
        terminate.Description.Should().Contain("DeleteOnTermination=true");
        terminate.Description.Should().Contain("every save file");
        terminate.Description.Should().Contain("cannot be recovered");

        var launch = plan.Stages.Single(s => s.StageId == "launch-replacement-instance");
        launch.Description.Should().Contain(OtherAmi);
        launch.Description.Should().Contain("different instance id");

        var impact = plan.Stages.Single(s => s.StageId == "data-impact");
        impact.Description.Should().Contain("Data impact of this plan is Destroyed");
        impact.Description.Should().Contain("Approving this plan is approving the deletion");
        impact.Description.Should().Contain("does not claim the Snapshot capability");
    }

    [Fact]
    public async Task An_image_change_reports_at_risk_when_the_volume_is_left_behind_rather_than_deleted()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(
            null,
            AwsScenario.InstanceXml(deleteOnTermination: "false")));

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherAmi }));

        // The bytes survive; nothing is attached to them. That is exactly what AtRisk describes, and it is not
        // the same answer as Preserved - the replacement boots on a fresh volume.
        plan!.DataImpact.Should().Be(DataImpact.AtRisk);

        var terminate = plan.Stages.Single(s => s.StageId == "terminate-instance");
        terminate.Description.Should().Contain("DeleteOnTermination=false");
        terminate.Description.Should().Contain("bill per GB-month");

        var impact = plan.Stages.Single(s => s.StageId == "data-impact");
        impact.Description.Should().Contain("Data impact of this plan is AtRisk");
        impact.Description.Should().Contain("attached to them");
    }

    [Fact]
    public async Task An_unreadable_delete_on_termination_flag_is_at_risk_and_never_preserved()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(
            null,
            AwsScenario.InstanceXml(deleteOnTermination: null)));

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherAmi }));

        // This adapter sends no BlockDeviceMapping, so the flag is the AMI's default and there is no create-time
        // choice of Servyx's own to fall back on. With no evidence in either direction the answer is the one
        // that says the data cannot be shown to survive attached - never the reassuring one.
        plan!.DataImpact.Should().Be(DataImpact.AtRisk);
        plan.DataImpact.Should().NotBe(DataImpact.Preserved);

        var terminate = plan.Stages.Single(s => s.StageId == "terminate-instance");
        terminate.Description.Should().Contain("cannot be determined from the live instance");
        terminate.Description.Should().Contain("sends no BlockDeviceMapping");

        var impact = plan.Stages.Single(s => s.StageId == "data-impact");
        impact.Description.Should().Contain("AtRisk is not reassurance");
    }

    [Fact]
    public async Task A_replacement_does_not_also_plan_a_separate_type_change_because_the_launch_applies_it()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherAmi },
                size: OtherInstanceType));

        // Both differences are reported by name ...
        plan!.Changes.Select(c => c.Aspect).Should().Contain(["image", "size"]);

        // ... but a stop/modify/start on a machine that is about to be terminated is not a step anybody would
        // run, so it is not described. The replacement launch states that it carries the type instead.
        plan.Stages.Should().NotContain(s => s.StageId == "change-instance-type");
        plan.Stages.Single(s => s.StageId == "launch-replacement-instance").Description
            .Should().Contain(OtherInstanceType);

        // And the worst answer still wins: a type change alongside a replacement does not soften it.
        plan.DataImpact.Should().Be(DataImpact.Destroyed);
    }

    // ---------------------------------------------------------------------------------------------------
    // Region: reported as unsupported, never silently planned
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_region_change_is_reported_as_unsupported_and_nothing_is_planned()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = OtherRegion }));

        var change = plan!.Changes.Single(c => c.Aspect == "region");
        change.Current.Should().Be(AwsScenario.Region);
        change.Desired.Should().Be(OtherRegion);
        change.RequiresRecreate.Should().BeTrue();

        var stage = plan.Stages.Single();
        stage.StageId.Should().Be("region-change-not-supported");
        stage.Description.Should().StartWith("NOT SUPPORTED:");
        stage.Description.Should().Contain("cannot be moved between regions");
        stage.Description.Should().Contain("SigV4 credential scope");

        // The refusal is total: no type change, no replacement, no terminate-and-launch is described.
        plan.Stages.Should().NotContain(s =>
            s.StageId == "change-instance-type"
            || s.StageId == "terminate-instance"
            || s.StageId == "launch-replacement-instance");
    }

    [Fact]
    public async Task A_region_change_does_not_quietly_carry_the_other_changes_as_applicable_stages()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = OtherRegion },
                size: OtherInstanceType));

        // Both differences are reported by name ...
        plan!.Changes.Select(c => c.Aspect).Should().Contain(["region", "size"]);

        // ... and neither is presented as something that could be applied to this instance.
        var stage = plan.Stages.Single();
        stage.StageId.Should().Be("region-change-not-supported");
        stage.Description.Should().Contain("equally not applied");
        stage.Description.Should().Contain(OtherInstanceType);
    }

    [Fact]
    public async Task The_unsupported_region_plan_states_its_data_impact_from_the_same_flag_a_replacement_reads()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(
            null,
            AwsScenario.InstanceXml(deleteOnTermination: "false")));

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = OtherRegion }));

        // Reaching another region would mean a terminate, so the answer comes from the same live flag rather
        // than from a blanket assumption about what "cannot be moved" costs.
        plan!.DataImpact.Should().Be(DataImpact.AtRisk);
        plan.Stages.Single().Description.Should().Contain("Data impact of this plan is AtRisk");
    }

    // ---------------------------------------------------------------------------------------------------
    // Tags
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tag_only_change_is_an_in_place_update_that_preserves_the_volumes()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["tag:servyx.environment"] = "staging" }));

        plan!.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.DataImpact.Should().Be(DataImpact.Preserved);
        plan.Changes.Single().Aspect.Should().Be("tag servyx.environment");

        var stage = plan.Stages.Single(s => s.StageId == "retag-instance");
        stage.Description.Should().Contain("CreateTags/DeleteTags");
        stage.Description.Should().Contain("does not stop, restart");
    }

    // ---------------------------------------------------------------------------------------------------
    // An instance EC2 no longer has - and the AWS-specific one it still describes but has deleted
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanUpdateAsync_returns_null_for_an_instance_ec2_no_longer_has()
    {
        var scenario = new AwsScenario();
        scenario.RouteMissingInstance();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest());

        // Not an empty plan: there is nothing to update, and inventing a launch plan here would turn an update
        // preview into a provisioning one.
        plan.Should().BeNull();
    }

    [Theory]
    [InlineData("terminated")]
    [InlineData("shutting-down")]
    public async Task PlanUpdateAsync_returns_null_for_an_instance_ec2_still_describes_but_has_killed(string state)
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(null, AwsScenario.InstanceXml(state: state)));

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(),
            AwsScenario.PalworldInstanceRequest(size: OtherInstanceType));

        // EC2 answered with a complete instance object - tags, type, addresses and all - for a machine that no
        // longer exists. Planning a stop/modify/start against it would describe operations on nothing.
        plan.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_instance_is_reported_as_drift_rather_than_as_an_exception()
    {
        var scenario = new AwsScenario();
        scenario.RouteMissingInstance();

        var drift = await scenario.Provisioner().DetectDriftAsync(AwsScenario.MaintenanceHandle());

        drift.Matches.Should().BeFalse();
        var divergence = drift.Divergences.Single();
        divergence.Aspect.Should().Be("existence");
        divergence.Expected.Should().Be("present");
        divergence.Found.Should().BeNull();
        drift.Summary.Should().Contain("has drifted");
    }

    [Theory]
    [InlineData("terminated")]
    [InlineData("shutting-down")]
    public async Task A_terminated_instance_is_drift_and_names_the_state_rather_than_reporting_nothing(string state)
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(null, AwsScenario.InstanceXml(state: state)));

        var drift = await scenario.Provisioner().DetectDriftAsync(AwsScenario.MaintenanceHandle());

        // The whole point: EC2 keeps a terminated instance visible for about an hour, complete with its tags and
        // its type, so every other comparison in this method would pass. A check that trusted the API's
        // willingness to answer would report a match for a machine somebody deleted a minute ago.
        drift.Matches.Should().BeFalse();

        var divergence = drift.Divergences.Single();
        divergence.Aspect.Should().Be("existence");
        divergence.Expected.Should().Be("present");

        // Named, not nulled - so a caller can tell "EC2 says this machine was deleted" from the 404 above, which
        // is what the same instance answers about an hour later.
        divergence.Found.Should().Be(state);
    }

    // ---------------------------------------------------------------------------------------------------
    // Drift
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_untouched_instance_matches_the_handle_servyx_recorded()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        var drift = await scenario.Provisioner().DetectDriftAsync(AwsScenario.MaintenanceHandle());

        drift.Divergences.Should().BeEmpty();
        drift.Matches.Should().BeTrue();
        drift.Summary.Should().Contain("matches the resource Servyx provisioned");
    }

    [Fact]
    public async Task Every_changed_property_is_named_as_its_own_divergence()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(
            null,
            AwsScenario.InstanceXml(instanceType: OtherInstanceType, imageId: OtherAmi)));

        var drift = await scenario.Provisioner().DetectDriftAsync(AwsScenario.MaintenanceHandle());

        drift.Matches.Should().BeFalse();
        drift.Divergences.Select(d => d.Aspect).Should().BeEquivalentTo(["size", "image"]);

        drift.Divergences.Single(d => d.Aspect == "size").Description
            .Should().Be($"size: expected {AwsScenario.InstanceType}, found {OtherInstanceType}");
        drift.Divergences.Single(d => d.Aspect == "image").Description
            .Should().Be($"image: expected {AwsScenario.ImageId}, found {OtherAmi}");
    }

    [Fact]
    public async Task A_handle_recorded_in_another_region_is_reported_as_a_region_divergence()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        var drift = await scenario.Provisioner().DetectDriftAsync(
            AwsScenario.MaintenanceHandle(region: OtherRegion));

        // The live value is not a guess: an instance answered for by this provisioner's endpoint is in this
        // provisioner's region, because the region is in the hostname.
        drift.Divergences.Single(d => d.Aspect == "region").Description
            .Should().Be($"region: expected {OtherRegion}, found {AwsScenario.Region}");
    }

    [Fact]
    public async Task A_tag_edited_away_at_the_provider_is_reported_by_name()
    {
        var stripped = new Dictionary<string, string>(AwsScenario.CanonicalInstanceTags, StringComparer.Ordinal);
        stripped.Remove("servyx.connector-id");

        var scenario = new AwsScenario();
        scenario.RouteReadOnly(AwsScenario.DescribeInstancesXml(null, AwsScenario.InstanceXml(tags: stripped)));

        var drift = await scenario.Provisioner().DetectDriftAsync(AwsScenario.MaintenanceHandle());

        // Not cosmetic: ReconcileAsync finds orphans by exactly these tags, and a per-second-billed instance it
        // cannot see bills forever.
        drift.Divergences.Should().ContainSingle();
        drift.Divergences.Single().Aspect.Should().Be("tag servyx.connector-id");
        drift.Divergences.Single().Found.Should().BeNull();
    }

    [Fact]
    public async Task A_handle_that_records_no_size_or_image_reports_them_as_unverifiable_rather_than_matching()
    {
        var scenario = new AwsScenario();
        scenario.RouteReadOnly();

        var drift = await scenario.Provisioner().DetectDriftAsync(
            AwsScenario.MaintenanceHandle(size: null, image: null));

        // A check that cannot prove a match must not claim one, so both are reported with a null expectation
        // rather than quietly passing. This is also the answer for the handle this adapter produces today, which
        // records neither - see AwsScenario.MaintenanceHandle's remarks.
        drift.Matches.Should().BeFalse();
        drift.Divergences.Select(d => d.Aspect).Should().BeEquivalentTo(["size", "image"]);
        drift.Divergences.Single(d => d.Aspect == "size").Description
            .Should().Be($"size: Servyx recorded no expected value, found {AwsScenario.InstanceType}");
    }

    [Fact]
    public async Task A_handle_from_another_provisioner_is_a_divergence_and_costs_no_api_call()
    {
        var scenario = new AwsScenario();

        var drift = await scenario.Provisioner().DetectDriftAsync(
            AwsScenario.MaintenanceHandle(provisionerId: "docker-container"));

        drift.Divergences.Single().Aspect.Should().Be("provisioner");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_handle_naming_a_volume_rather_than_an_instance_is_a_divergence_and_costs_no_api_call()
    {
        var scenario = new AwsScenario();

        // ReconcileAsync reports both instances and volumes, so a volume handle is a thing a caller genuinely
        // holds. It is answered as "not my kind of resource", not silently treated as a machine.
        var drift = await scenario.Provisioner().DetectDriftAsync(
            AwsScenario.MaintenanceHandle(providerResourceId: AwsScenario.VolumeId));

        drift.Divergences.Single().Aspect.Should().Be("instance-id");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanUpdateAsync_returns_null_for_a_volume_handle_and_costs_no_api_call()
    {
        var scenario = new AwsScenario();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AwsScenario.MaintenanceHandle(providerResourceId: AwsScenario.VolumeId),
            AwsScenario.PalworldInstanceRequest());

        plan.Should().BeNull();
        scenario.Api.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Plan identity
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_plan_hash_covers_the_live_state_as_well_as_the_desired_state()
    {
        var request = AwsScenario.PalworldInstanceRequest(size: OtherInstanceType);

        var fromMedium = new AwsScenario();
        fromMedium.RouteReadOnly();
        var first = await fromMedium.Provisioner().PlanUpdateAsync(AwsScenario.MaintenanceHandle(), request);

        var fromSmall = new AwsScenario();
        fromSmall.RouteReadOnly(AwsScenario.DescribeInstancesXml(
            null,
            AwsScenario.InstanceXml(instanceType: "t3.small")));
        var second = await fromSmall.Provisioner().PlanUpdateAsync(AwsScenario.MaintenanceHandle(), request);

        // Same desired state, different observed state: a caller re-showing a plan must be able to see that the
        // inputs no longer produce the plan it displayed.
        second!.PlanHash.Should().NotBe(first!.PlanHash);
    }

    [Fact]
    public async Task The_plan_hash_covers_the_delete_on_termination_flag_because_the_data_impact_depends_on_it()
    {
        var request = AwsScenario.PalworldInstanceRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherAmi });

        var deleting = new AwsScenario();
        deleting.RouteReadOnly(AwsScenario.DescribeInstancesXml(
            null,
            AwsScenario.InstanceXml(deleteOnTermination: "true")));
        var first = await deleting.Provisioner().PlanUpdateAsync(AwsScenario.MaintenanceHandle(), request);

        var keeping = new AwsScenario();
        keeping.RouteReadOnly(AwsScenario.DescribeInstancesXml(
            null,
            AwsScenario.InstanceXml(deleteOnTermination: "false")));
        var second = await keeping.Provisioner().PlanUpdateAsync(AwsScenario.MaintenanceHandle(), request);

        // The two plans differ only in a flag on the live instance, and that flag is the whole difference
        // between "your saves are deleted" and "your saves survive detached". A hash that ignored it would let a
        // caller approve the first while looking at the second.
        first!.DataImpact.Should().Be(DataImpact.Destroyed);
        second!.DataImpact.Should().Be(DataImpact.AtRisk);
        second.PlanHash.Should().NotBe(first.PlanHash);
    }
}
