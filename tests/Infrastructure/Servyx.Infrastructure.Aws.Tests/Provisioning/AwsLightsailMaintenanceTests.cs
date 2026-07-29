using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The <see cref="IMaintainer"/> half of the Lightsail adapter: what it plans, what it refuses to plan, and
/// — the assertion the rest of this file exists to protect — what it says will happen to the machine's disk.
/// </summary>
/// <remarks>
/// <para>
/// Every test here runs against the substituted AWS endpoint, so no network access, no AWS account and no IAM
/// credential beyond the fake key pair in the scenario is involved.
/// </para>
/// <para>
/// <strong>"No mutating request" is asserted differently here than in the EC2 suite, and the difference is
/// real.</strong> The EC2 suite can assert <c>Method == GET</c>, because the Query API sends reads as GETs.
/// Lightsail speaks AWS JSON 1.1, in which <em>every</em> call is a <c>POST /</c> and the action is named by an
/// <c>X-Amz-Target</c> header — so a check on the HTTP verb would prove nothing at all here. What is asserted
/// instead is the action name, which is the only thing that distinguishes <c>GetInstance</c> from
/// <c>DeleteInstance</c> on this wire.
/// </para>
/// <para>
/// <strong>The finding this suite pins.</strong> Lightsail has no operation that changes an existing instance's
/// bundle — not an unimplemented one, an absent one. So a bundle change is reported unsupported and nothing is
/// planned for it, while the snapshot-and-restore procedure AWS actually documents is named in the refusal so an
/// operator can carry it out deliberately.
/// </para>
/// </remarks>
public class AwsLightsailMaintenanceTests
{
    private const string OtherBlueprint = "ubuntu_22_04";
    private const string OtherBundle = "large_3_0";
    private const string OtherRegion = "eu-west-1";
    private const string OtherZone = "us-east-1b";

    /// <summary>Every Lightsail action that changes something. None of these may appear in a planning log.</summary>
    private static readonly string[] MutatingActions =
    [
        "CreateInstances",
        "DeleteInstance",
        "TagResource",
        "UntagResource",
        "CreateInstanceSnapshot",
        "CreateInstancesFromSnapshot",
        "PutInstancePublicPorts",
    ];

    // ---------------------------------------------------------------------------------------------------
    // The adapter is a maintainer at all, and says so honestly
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_is_a_maintainer_and_the_two_ids_agree()
    {
        var provisioner = new LightsailScenario().Provisioner();

        provisioner.Should().BeAssignableTo<IMaintainer>();
        ((IMaintainer)provisioner).ProvisionerId.Should().Be(AwsLightsailProvisioner.Id);
    }

    [Fact]
    public void The_capability_set_is_exactly_the_seven_bits_the_adapter_implements() =>
        new LightsailScenario().Provisioner().Capabilities.Should().Be(
            ProvisioningCapabilities.Create
            | ProvisioningCapabilities.Destroy
            | ProvisioningCapabilities.TagQuery
            | ProvisioningCapabilities.EstimatesCost
            | ProvisioningCapabilities.UpdateInPlace
            | ProvisioningCapabilities.RecreateToUpdate
            | ProvisioningCapabilities.DetectDrift);

    [Theory]
    [InlineData(ProvisioningCapabilities.Resize)]
    [InlineData(ProvisioningCapabilities.Snapshot)]
    [InlineData(ProvisioningCapabilities.StaticAddress)]
    [InlineData(ProvisioningCapabilities.FirewallRules)]
    public void Every_capability_the_provisioner_does_not_implement_is_absent(ProvisioningCapabilities absent) =>
        // Resize is absent for a blunter reason than EC2's - there is nothing to implement, not merely nothing
        // implemented. Snapshot is absent even though the snapshot procedure is named in the bundle refusal:
        // naming a procedure is not being able to perform it.
        new LightsailScenario().Provisioner().Capabilities.Should().NotHaveFlag(absent);

    // ---------------------------------------------------------------------------------------------------
    // Planning issues no mutating call
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanUpdateAsync_issues_exactly_one_read_and_no_mutating_action()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest());

        scenario.Api.Requests.Should().ContainSingle();
        scenario.Api.Requests.Should().OnlyContain(r => r.LightsailAction == "GetInstance");
        scenario.Api.Requests.Should().NotContain(r => MutatingActions.Contains(r.LightsailAction));
    }

    [Fact]
    public async Task DetectDriftAsync_issues_exactly_one_read_and_no_mutating_action()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        await scenario.Provisioner().DetectDriftAsync(LightsailScenario.MaintenanceHandle());

        scenario.Api.Requests.Should().ContainSingle();
        scenario.Api.Requests.Should().OnlyContain(r => r.LightsailAction == "GetInstance");
        scenario.Api.Requests.Should().NotContain(r => MutatingActions.Contains(r.LightsailAction));
    }

    [Fact]
    public async Task The_request_log_of_a_replacement_plan_contains_only_reads()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        // The most destructive plan this adapter can produce, and it is still only a read.
        var plan = await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherBlueprint }));

        plan!.DataImpact.Should().Be(DataImpact.Destroyed);
        scenario.Api.Requests.Should().OnlyContain(r => r.LightsailAction == "GetInstance");
        scenario.Api.Requests.Should().NotContain(r => MutatingActions.Contains(r.LightsailAction));
    }

    // ---------------------------------------------------------------------------------------------------
    // Nothing to change
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_instance_that_already_matches_the_request_needs_no_change_and_carries_no_stages()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest());

        plan.Should().NotBeNull();
        plan!.Strategy.Should().Be(UpdateStrategy.NoChangeRequired);
        plan.Changes.Should().BeEmpty();
        plan.Stages.Should().BeEmpty();
        plan.DataImpact.Should().Be(DataImpact.Preserved);
        plan.ProvisionerId.Should().Be(AwsLightsailProvisioner.Id);
    }

    // ---------------------------------------------------------------------------------------------------
    // Bundle: there is no such operation, so it is reported unsupported rather than planned as something else
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_bundle_change_is_reported_as_unsupported_and_nothing_is_planned()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest(size: OtherBundle));

        var change = plan!.Changes.Single(c => c.Aspect == "size");
        change.Current.Should().Be(LightsailScenario.BundleId);
        change.Desired.Should().Be(OtherBundle);
        change.RequiresRecreate.Should().BeTrue();

        plan.Strategy.Should().Be(UpdateStrategy.Recreate);

        var stage = plan.Stages.Single();
        stage.StageId.Should().Be("change-not-supported");
        stage.Description.Should().StartWith("NOT SUPPORTED:");
        stage.Description.Should().Contain("NO operation that changes an existing instance's bundle");
        stage.Description.Should().Contain("absent operation rather than an unimplemented one");

        // The refusal is total: no delete, no create, no "resize by replacement" is described.
        plan.Stages.Should().NotContain(s =>
            s.StageId == "delete-instance" || s.StageId == "create-replacement-instance");
    }

    [Fact]
    public async Task The_bundle_refusal_names_the_procedure_aws_actually_offers_and_its_two_real_limits()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest(size: OtherBundle));

        var stage = plan!.Stages.Single();

        // What AWS documents is a procedure, not an operation - and it is named so an operator can carry it out
        // deliberately rather than being told only that nothing is possible.
        stage.Description.Should().Contain("CreateInstanceSnapshot");
        stage.Description.Should().Contain("CreateInstancesFromSnapshot");
        stage.Description.Should().Contain("different instance");

        // Its two limits, stated rather than glossed.
        stage.Description.Should().Contain("only ever scales upward");
        stage.Description.Should().Contain("Snapshot capability it does not claim");

        // And the cheap substitute is named as refused rather than merely unmentioned.
        stage.Description.Should().Contain("lose every save file while looking like a resize");
    }

    [Fact]
    public async Task A_bundle_change_reports_destroyed_because_a_lightsail_delete_takes_the_disk_with_it()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest(size: OtherBundle));

        // The only route this adapter could describe is a delete-and-create, and a Lightsail bundle bakes the
        // SSD storage into the instance - there is no separate disk resource and no DeleteOnTermination
        // equivalent, so unlike EC2 there is nothing to look up and no AtRisk answer to give.
        plan!.DataImpact.Should().Be(DataImpact.Destroyed);
        plan.Stages.Single().Description.Should().Contain("Data impact of this plan is Destroyed");
        plan.Stages.Single().Description.Should().Contain("the bundle IS the disk");
    }

    [Fact]
    public async Task A_bundle_change_does_not_quietly_carry_a_blueprint_change_as_an_applicable_stage()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherBlueprint },
                size: OtherBundle));

        // Both differences are reported by name ...
        plan!.Changes.Select(c => c.Aspect).Should().Contain(["size", "image"]);

        // ... and neither is presented as something that could be applied to this instance. A replacement that
        // happened to pick up the new bundle on the way past would be exactly the substitution this file refuses.
        var stage = plan.Stages.Single();
        stage.StageId.Should().Be("change-not-supported");
        stage.Description.Should().Contain("equally not applied");
        stage.Description.Should().Contain(OtherBlueprint);
    }

    // ---------------------------------------------------------------------------------------------------
    // Blueprint: a replacement, and an unambiguously destructive one
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_blueprint_change_replaces_the_instance_and_reports_the_data_as_destroyed()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherBlueprint }));

        plan!.Strategy.Should().Be(UpdateStrategy.Recreate);
        plan.DataImpact.Should().Be(DataImpact.Destroyed);

        var change = plan.Changes.Single();
        change.Aspect.Should().Be("image");
        change.Current.Should().Be(LightsailScenario.BlueprintId);
        change.Desired.Should().Be(OtherBlueprint);
        change.RequiresRecreate.Should().BeTrue();
    }

    [Fact]
    public async Task The_replacement_stages_say_the_disk_is_deleted_and_why_no_flag_could_save_it()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherBlueprint }));

        var delete = plan!.Stages.Single(s => s.StageId == "delete-instance");

        delete.Description.Should().Contain("no operation that changes an existing instance's blueprint");
        delete.Description.Should().Contain("THIS DELETES THE MACHINE'S DISK");

        // The sharpest contrast with EC2 in the whole adapter, and it belongs in the text an operator reads.
        delete.Description.Should().Contain("no separate disk resource");
        delete.Description.Should().Contain("DeleteOnTermination-style flag");
        delete.Description.Should().Contain("every save file");

        var create = plan.Stages.Single(s => s.StageId == "create-replacement-instance");
        create.Description.Should().Contain(OtherBlueprint);
        create.Description.Should().Contain("name is reused");

        var impact = plan.Stages.Single(s => s.StageId == "data-impact");
        impact.Description.Should().Contain("Data impact of this plan is Destroyed");
        impact.Description.Should().Contain("does not claim the Snapshot capability");
    }

    // ---------------------------------------------------------------------------------------------------
    // Region and availability zone: reported as unsupported, never silently planned
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_region_change_is_reported_as_unsupported_and_nothing_is_planned()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = OtherRegion }));

        var change = plan!.Changes.Single(c => c.Aspect == "region");
        change.Current.Should().Be(LightsailScenario.Region);
        change.Desired.Should().Be(OtherRegion);
        change.RequiresRecreate.Should().BeTrue();

        var stage = plan.Stages.Single();
        stage.StageId.Should().Be("change-not-supported");
        stage.Description.Should().Contain("cannot be moved between regions");
        stage.Description.Should().Contain("SigV4 credential scope");
        plan.DataImpact.Should().Be(DataImpact.Destroyed);
    }

    [Fact]
    public async Task An_availability_zone_change_is_reported_as_unsupported_rather_than_silently_dropped()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["availabilityZone"] = OtherZone }));

        // BuildSpec reads availabilityZone, so a caller naming a different one is expressing a real intent. It
        // must be reported as unreachable rather than accepted into a plan that appears to satisfy it.
        var change = plan!.Changes.Single(c => c.Aspect == "availabilityZone");
        change.Current.Should().Be(LightsailScenario.AvailabilityZone);
        change.Desired.Should().Be(OtherZone);

        var stage = plan.Stages.Single();
        stage.StageId.Should().Be("change-not-supported");
        stage.Description.Should().Contain("no operation that moves an existing instance between zones");
    }

    // ---------------------------------------------------------------------------------------------------
    // Tags: the one thing that really is in place here
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tag_only_change_is_the_single_in_place_update_this_adapter_can_plan()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["tag:servyx.environment"] = "staging" }));

        plan!.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.DataImpact.Should().Be(DataImpact.Preserved);
        plan.Changes.Single().Aspect.Should().Be("tag servyx.environment");

        var stage = plan.Stages.Single(s => s.StageId == "retag-instance");
        stage.Description.Should().Contain("TagResource/UntagResource");
        stage.Description.Should().Contain("does not stop, restart");

        var impact = plan.Stages.Single(s => s.StageId == "data-impact");
        impact.Description.Should().Contain("Data impact of this plan is Preserved");
        impact.Description.Should().Contain("keeps its name, its address and the system disk");
    }

    // ---------------------------------------------------------------------------------------------------
    // An instance Lightsail no longer has
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanUpdateAsync_returns_null_for_an_instance_lightsail_no_longer_has()
    {
        var scenario = new LightsailScenario();
        scenario.RouteMissingInstance();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(),
            LightsailScenario.PalworldInstanceRequest());

        // Not an empty plan: there is nothing to update, and inventing a create plan here would turn an update
        // preview into a provisioning one.
        plan.Should().BeNull();
    }

    [Fact]
    public async Task A_deleted_instance_is_reported_as_drift_rather_than_as_an_exception()
    {
        var scenario = new LightsailScenario();
        scenario.RouteMissingInstance();

        var drift = await scenario.Provisioner().DetectDriftAsync(LightsailScenario.MaintenanceHandle());

        // Note the divergence from EC2, where a deleted instance keeps being described for about an hour and
        // "gone" has to be checked as a state. Lightsail answers NotFoundException, so there is one spelling of
        // gone here rather than two - and it is drift, not an exception.
        drift.Matches.Should().BeFalse();
        var divergence = drift.Divergences.Single();
        divergence.Aspect.Should().Be("existence");
        divergence.Expected.Should().Be("present");
        divergence.Found.Should().BeNull();
        drift.Summary.Should().Contain("has drifted");
    }

    // ---------------------------------------------------------------------------------------------------
    // Drift
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_untouched_instance_matches_the_handle_servyx_recorded()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var drift = await scenario.Provisioner().DetectDriftAsync(LightsailScenario.MaintenanceHandle());

        drift.Divergences.Should().BeEmpty();
        drift.Matches.Should().BeTrue();
        drift.Summary.Should().Contain("matches the resource Servyx provisioned");
    }

    [Fact]
    public async Task Every_changed_property_is_named_as_its_own_divergence()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly(LightsailScenario.InstanceJson(
            bundleId: OtherBundle,
            blueprintId: OtherBlueprint));

        var drift = await scenario.Provisioner().DetectDriftAsync(LightsailScenario.MaintenanceHandle());

        drift.Matches.Should().BeFalse();
        drift.Divergences.Select(d => d.Aspect).Should().BeEquivalentTo(["size", "image"]);

        drift.Divergences.Single(d => d.Aspect == "size").Description
            .Should().Be($"size: expected {LightsailScenario.BundleId}, found {OtherBundle}");
        drift.Divergences.Single(d => d.Aspect == "image").Description
            .Should().Be($"image: expected {LightsailScenario.BlueprintId}, found {OtherBlueprint}");
    }

    [Fact]
    public async Task A_handle_recorded_in_another_region_is_reported_as_a_region_divergence()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var drift = await scenario.Provisioner().DetectDriftAsync(
            LightsailScenario.MaintenanceHandle(region: OtherRegion));

        drift.Divergences.Single(d => d.Aspect == "region").Description
            .Should().Be($"region: expected {OtherRegion}, found {LightsailScenario.Region}");
    }

    [Fact]
    public async Task A_tag_edited_away_at_the_provider_is_reported_by_name()
    {
        var stripped = new Dictionary<string, string>(LightsailScenario.CanonicalTags, StringComparer.Ordinal);
        stripped.Remove("servyx.connector-id");

        var scenario = new LightsailScenario();
        scenario.RouteReadOnly(LightsailScenario.InstanceJson(tags: stripped));

        var drift = await scenario.Provisioner().DetectDriftAsync(LightsailScenario.MaintenanceHandle());

        drift.Divergences.Should().ContainSingle();
        drift.Divergences.Single().Aspect.Should().Be("tag servyx.connector-id");
        drift.Divergences.Single().Found.Should().BeNull();
    }

    [Fact]
    public async Task A_handle_that_records_no_size_or_image_reports_them_as_unverifiable_rather_than_matching()
    {
        var scenario = new LightsailScenario();
        scenario.RouteReadOnly();

        var drift = await scenario.Provisioner().DetectDriftAsync(
            LightsailScenario.MaintenanceHandle(size: null, image: null));

        drift.Matches.Should().BeFalse();
        drift.Divergences.Select(d => d.Aspect).Should().BeEquivalentTo(["size", "image"]);
        drift.Divergences.Single(d => d.Aspect == "size").Description
            .Should().Be($"size: Servyx recorded no expected value, found {LightsailScenario.BundleId}");
    }

    [Fact]
    public async Task A_handle_from_another_provisioner_is_a_divergence_and_costs_no_api_call()
    {
        var scenario = new LightsailScenario();

        var drift = await scenario.Provisioner().DetectDriftAsync(
            LightsailScenario.MaintenanceHandle(provisionerId: "aws-ec2"));

        drift.Divergences.Single().Aspect.Should().Be("provisioner");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_handle_carrying_no_instance_name_is_a_divergence_and_costs_no_api_call()
    {
        var scenario = new LightsailScenario();

        // A Lightsail instance's name is its identity, so a handle without one names nothing. Answered as a
        // divergence rather than as a match: "this is not a resource I can check" is not evidence it is intact.
        var drift = await scenario.Provisioner().DetectDriftAsync(
            LightsailScenario.MaintenanceHandle(providerResourceId: "   "));

        drift.Divergences.Single().Aspect.Should().Be("instance-name");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanUpdateAsync_returns_null_for_a_handle_carrying_no_instance_name_and_costs_no_api_call()
    {
        var scenario = new LightsailScenario();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(providerResourceId: "   "),
            LightsailScenario.PalworldInstanceRequest());

        plan.Should().BeNull();
        scenario.Api.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Plan identity
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_plan_hash_covers_the_live_state_as_well_as_the_desired_state()
    {
        var request = LightsailScenario.PalworldInstanceRequest(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = OtherBlueprint });

        var fromAmazonLinux = new LightsailScenario();
        fromAmazonLinux.RouteReadOnly();
        var first = await fromAmazonLinux.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(), request);

        var fromDebian = new LightsailScenario();
        fromDebian.RouteReadOnly(LightsailScenario.InstanceJson(blueprintId: "debian_12"));
        var second = await fromDebian.Provisioner().PlanUpdateAsync(
            LightsailScenario.MaintenanceHandle(), request);

        // Same desired state, different observed state: a caller re-showing a plan must be able to see that the
        // inputs no longer produce the plan it displayed.
        second!.PlanHash.Should().NotBe(first!.PlanHash);
    }
}
