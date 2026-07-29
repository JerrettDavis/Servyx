using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Azure.Provisioning;

namespace Servyx.Infrastructure.Azure.Tests.Provisioning;

/// <summary>
/// The <see cref="IMaintainer"/> half of the VM adapter: what it plans, what it refuses to plan, and — the
/// assertion the rest of this file exists to protect — what it says will happen to the machine's managed OS
/// disk.
/// </summary>
/// <remarks>
/// Every test here runs against the substituted Azure, so no network access, no subscription, and no service
/// principal beyond the fake one in the scenario is involved. Several assert on the <em>absence</em> of a
/// mutating request, which is a stronger claim than "the call failed": the API double throws on any ARM
/// request that is not a GET, so a plan that tried to resize or delete anything would fail the test at the
/// point it tried.
/// </remarks>
public class AzureVirtualMachineMaintenanceTests
{
    // ---------------------------------------------------------------------------------------------------
    // The adapter is a maintainer at all, and says so honestly
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_is_a_maintainer_and_the_two_ids_agree()
    {
        var provisioner = new AzureScenario().Provisioner();

        provisioner.Should().BeAssignableTo<IMaintainer>();
        ((IMaintainer)provisioner).ProvisionerId.Should().Be(AzureVirtualMachineProvisioner.Id);
    }

    [Fact]
    public void The_three_maintenance_capability_bits_are_all_claimed()
    {
        var capabilities = new AzureScenario().Provisioner().Capabilities;

        // UpdateInPlace: an ARM write to hardwareProfile.vmSize mutates the machine that already exists.
        capabilities.Should().HaveFlag(ProvisioningCapabilities.UpdateInPlace);

        // RecreateToUpdate: imageReference is fixed at creation, so an image change replaces the machine.
        capabilities.Should().HaveFlag(ProvisioningCapabilities.RecreateToUpdate);

        capabilities.Should().HaveFlag(ProvisioningCapabilities.DetectDrift);
    }

    [Fact]
    public void Planning_a_resize_is_not_the_same_promise_as_being_able_to_perform_one()
    {
        // The adapter plans a resize in detail and still does not claim Resize, because no code path in the
        // assembly writes hardwareProfile.vmSize to ARM. A caller must be able to tell those two apart.
        new AzureScenario().Provisioner().Capabilities
            .Should().NotHaveFlag(ProvisioningCapabilities.Resize);
    }

    // ---------------------------------------------------------------------------------------------------
    // Planning issues no mutating call
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanUpdateAsync_issues_one_arm_read_and_no_mutating_request()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest());

        scenario.Api.ArmRequests.Should().ContainSingle();
        scenario.Api.ArmRequests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task DetectDriftAsync_issues_one_arm_read_and_no_mutating_request()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        await scenario.Provisioner().DetectDriftAsync(AzureScenario.RecordedHandle());

        scenario.Api.ArmRequests.Should().ContainSingle();
        scenario.Api.ArmRequests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Planning_a_replacement_still_issues_no_mutating_request()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        // The most destructive plan this adapter can produce, and it is still only a read.
        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["image"] = "Debian:debian-12:12:latest",
            }));

        plan!.DataImpact.Should().Be(DataImpact.Destroyed);
        scenario.Api.ArmRequests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    // ---------------------------------------------------------------------------------------------------
    // Nothing to change
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_machine_that_already_matches_the_request_needs_no_change_and_carries_no_stages()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest());

        plan.Should().NotBeNull();
        plan!.Strategy.Should().Be(UpdateStrategy.NoChangeRequired);
        plan.Changes.Should().BeEmpty();
        plan.Stages.Should().BeEmpty();
        plan.DataImpact.Should().Be(DataImpact.Preserved);
        plan.ProvisionerId.Should().Be(AzureVirtualMachineProvisioner.Id);
    }

    // ---------------------------------------------------------------------------------------------------
    // Size: the in-place resize, and the justification for calling the OS disk preserved
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_size_change_is_planned_in_place_and_preserves_the_os_disk()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(size: "Standard_D2s_v5"));

        plan!.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.DataImpact.Should().Be(DataImpact.Preserved);

        var change = plan.Changes.Single();
        change.Aspect.Should().Be("size");
        change.Current.Should().Be(AzureScenario.VmSize);
        change.Desired.Should().Be("Standard_D2s_v5");
        change.RequiresRecreate.Should().BeFalse();
    }

    [Fact]
    public async Task The_resize_stage_justifies_preserved_from_arms_own_resource_model()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(size: "Standard_D2s_v5"));

        var stage = plan!.Stages.Single(s => s.StageId == "resize-virtual-machine");

        // The Preserved claim is structural, not optimistic: the disk is a separate ARM resource the write
        // does not name.
        stage.Description.Should().Contain("properties.hardwareProfile.vmSize");
        stage.Description.Should().Contain("separate ARM resource");
        stage.Description.Should().Contain("neither names nor re-references");

        // And the interruption is stated rather than hidden behind "in place".
        stage.Description.Should().Contain("the workload is interrupted");

        var impact = plan.Stages.Single(s => s.StageId == "data-impact");
        impact.Description.Should().Contain("Data impact of this plan is Preserved");
        impact.Description.Should().Contain(AzureScenario.VmId);
    }

    // ---------------------------------------------------------------------------------------------------
    // Image: the replacement, and the destruction named in plain language
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_image_change_replaces_the_machine_and_the_plan_reports_the_data_as_destroyed()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["image"] = "Debian:debian-12:12:latest",
            }));

        plan!.DataImpact.Should().Be(DataImpact.Destroyed);
        plan.Strategy.Should().Be(UpdateStrategy.Recreate);

        var change = plan.Changes.Single();
        change.Aspect.Should().Be("image");
        change.Current.Should().Be(AzureScenario.ImageUrn);
        change.Desired.Should().Be("Debian:debian-12:12:latest");
        change.RequiresRecreate.Should().BeTrue();
    }

    [Fact]
    public async Task The_replacement_stages_say_the_disk_is_deleted_in_words_an_operator_can_act_on()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["image"] = "Debian:debian-12:12:latest",
            }));

        var delete = plan!.Stages.Single(s => s.StageId == "delete-virtual-machine");
        delete.Description.Should().Contain("imageReference is fixed when the machine is created");
        delete.Description.Should().Contain("deleted with it, by the deleteOption 'Delete'");
        delete.Description.Should().Contain("every save file");
        delete.Description.Should().Contain("cannot be recovered");

        var create = plan.Stages.Single(s => s.StageId == "create-replacement-virtual-machine");
        create.Description.Should().Contain("fresh copy of the image");

        var impact = plan.Stages.Single(s => s.StageId == "data-impact");
        impact.Description.Should().Contain("Data impact of this plan is Destroyed");
        impact.Description.Should().Contain("approving this plan is approving the deletion");
    }

    [Fact]
    public async Task The_disk_fate_is_read_off_the_live_machine_rather_than_assumed_from_create_time()
    {
        var scenario = new AzureScenario();

        // Somebody set the OS disk's deleteOption to Detach out of band. The bytes now survive a replacement -
        // but the replacement boots from a fresh disk, so nothing is attached to them. That is AtRisk, and the
        // adapter has to notice rather than repeating what it wrote months ago.
        scenario.RouteReadOnly(AzureScenario.VirtualMachineJson(osDiskDeleteOption: "Detach"));

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["image"] = "Debian:debian-12:12:latest",
            }));

        plan!.DataImpact.Should().Be(DataImpact.AtRisk);
        plan.Stages.Single(s => s.StageId == "data-impact").Description
            .Should().Contain("nothing will be attached to them");
    }

    [Fact]
    public async Task A_machine_reporting_no_delete_option_gets_the_destructive_answer_not_the_reassuring_one()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly(AzureScenario.VirtualMachineJson(osDiskDeleteOption: null));

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["image"] = "Debian:debian-12:12:latest",
            }));

        plan!.DataImpact.Should().Be(DataImpact.Destroyed);
        plan.Stages.Single(s => s.StageId == "delete-virtual-machine").Description
            .Should().Contain("the destructive reading is the one stated here");
    }

    [Fact]
    public async Task A_replacement_planned_alongside_a_resize_is_not_softened_by_it()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = "Debian:debian-12:12:latest" },
                size: "Standard_D2s_v5"));

        plan!.DataImpact.Should().Be(DataImpact.Destroyed);
        plan.Changes.Select(c => c.Aspect).Should().Contain(["size", "image"]);
    }

    // ---------------------------------------------------------------------------------------------------
    // Region and resource group: reported as unsupported, never silently planned
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_region_change_is_reported_as_unsupported_and_nothing_is_planned()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["region"] = "westeurope",
            }));

        var change = plan!.Changes.Single(c => c.Aspect == "region");
        change.Current.Should().Be(AzureScenario.Region);
        change.Desired.Should().Be("westeurope");
        change.RequiresRecreate.Should().BeTrue();

        var stage = plan.Stages.Single();
        stage.StageId.Should().Be("move-not-supported");
        stage.Description.Should().StartWith("NOT SUPPORTED:");
        stage.Description.Should().Contain("location is immutable");
        stage.Description.Should().Contain("No operation is planned here");

        plan.Stages.Should().NotContain(s =>
            s.StageId == "resize-virtual-machine" || s.StageId == "delete-virtual-machine");
        plan.DataImpact.Should().Be(DataImpact.Destroyed);
    }

    [Fact]
    public async Task A_resource_group_change_is_reported_as_unsupported_rather_than_planned_as_a_tag_rewrite()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resourceGroup"] = "rg-somewhere-else",
            }));

        // The group is part of the machine's ARM id. Planning only the bookkeeping tag rewrite - which is the
        // difference a naive comparison would see - would be planning something other than what was asked for.
        var change = plan!.Changes.Single(c => c.Aspect == "resourceGroup");
        change.Current.Should().Be(AzureScenario.ResourceGroup);
        change.Desired.Should().Be("rg-somewhere-else");
        change.RequiresRecreate.Should().BeTrue();

        var stage = plan.Stages.Single();
        stage.StageId.Should().Be("move-not-supported");
        stage.Description.Should().Contain("part of the machine's ARM id");
    }

    [Fact]
    public async Task An_unsupported_move_does_not_quietly_carry_the_other_changes_as_applicable_stages()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "westeurope" },
                size: "Standard_D2s_v5"));

        plan!.Changes.Select(c => c.Aspect).Should().Contain(["region", "size"]);

        var stage = plan.Stages.Single();
        stage.Description.Should().Contain("equally not applied");
        stage.Description.Should().Contain("Standard_D2s_v5");
    }

    // ---------------------------------------------------------------------------------------------------
    // Tags
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tag_only_change_is_an_in_place_update_that_preserves_the_disk()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tag:servyx.environment"] = "staging",
            }));

        plan!.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.DataImpact.Should().Be(DataImpact.Preserved);
        plan.Changes.Single().Aspect.Should().Be("tag servyx.environment");
        plan.Stages.Should().Contain(s => s.StageId == "retag-virtual-machine");
    }

    // ---------------------------------------------------------------------------------------------------
    // A machine ARM no longer has
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanUpdateAsync_returns_null_for_a_machine_arm_no_longer_has()
    {
        var scenario = new AzureScenario();
        scenario.RouteMissingVirtualMachine();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(),
            AzureScenario.PalworldVmRequest());

        plan.Should().BeNull();
    }

    [Fact]
    public async Task A_deleted_machine_is_reported_as_drift_rather_than_as_an_exception()
    {
        var scenario = new AzureScenario();
        scenario.RouteMissingVirtualMachine();

        var drift = await scenario.Provisioner().DetectDriftAsync(AzureScenario.RecordedHandle());

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
    public async Task An_untouched_machine_matches_the_handle_servyx_recorded()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        var drift = await scenario.Provisioner().DetectDriftAsync(AzureScenario.RecordedHandle());

        drift.Divergences.Should().BeEmpty();
        drift.Matches.Should().BeTrue();
        drift.Summary.Should().Contain("matches the resource Servyx provisioned");
    }

    [Fact]
    public async Task Every_changed_property_is_named_as_its_own_divergence()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly(AzureScenario.VirtualMachineJson(
            vmSize: "Standard_D8s_v5",
            imageUrn: "Debian:debian-12:12:latest",
            location: "westeurope"));

        var drift = await scenario.Provisioner().DetectDriftAsync(AzureScenario.RecordedHandle());

        drift.Matches.Should().BeFalse();
        drift.Divergences.Select(d => d.Aspect).Should().BeEquivalentTo(["region", "size", "image"]);

        drift.Divergences.Single(d => d.Aspect == "size").Description
            .Should().Be("size: expected Standard_B2s, found Standard_D8s_v5");
        drift.Divergences.Single(d => d.Aspect == "image").Description
            .Should().Be("image: expected Canonical:ubuntu-24_04-lts:server:latest, found Debian:debian-12:12:latest");
    }

    [Fact]
    public async Task An_arm_location_spelled_differently_is_not_reported_as_drift()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly(AzureScenario.VirtualMachineJson(location: "East US"));

        // ARM accepts 'eastus' and 'East US' as the same place. Reporting that as drift would be a false alarm
        // an operator would learn to ignore, which is worse than not checking at all.
        var drift = await scenario.Provisioner().DetectDriftAsync(AzureScenario.RecordedHandle());

        drift.Divergences.Should().NotContain(d => d.Aspect == "region");
    }

    [Fact]
    public async Task A_tag_edited_away_at_the_provider_is_reported_by_name()
    {
        var scenario = new AzureScenario();
        var stripped = new Dictionary<string, string>(AzureScenario.CanonicalVmTags, StringComparer.Ordinal);
        stripped.Remove("servyx.connector-id");
        scenario.RouteReadOnly(AzureScenario.VirtualMachineJson(tags: stripped));

        var drift = await scenario.Provisioner().DetectDriftAsync(AzureScenario.RecordedHandle());

        drift.Divergences.Should().ContainSingle();
        drift.Divergences.Single().Aspect.Should().Be("tag servyx.connector-id");
        drift.Divergences.Single().Found.Should().BeNull();
    }

    [Fact]
    public async Task A_handle_that_records_no_size_or_image_reports_them_as_unverifiable_rather_than_matching()
    {
        var scenario = new AzureScenario();
        scenario.RouteReadOnly();

        var drift = await scenario.Provisioner().DetectDriftAsync(
            AzureScenario.RecordedHandle(size: null, image: null));

        drift.Matches.Should().BeFalse();
        drift.Divergences.Select(d => d.Aspect).Should().BeEquivalentTo(["size", "image"]);
        drift.Divergences.Single(d => d.Aspect == "size").Description
            .Should().Be("size: Servyx recorded no expected value, found Standard_B2s");
    }

    [Fact]
    public async Task A_handle_from_another_provisioner_is_a_divergence_and_costs_no_api_call()
    {
        var scenario = new AzureScenario();

        var drift = await scenario.Provisioner().DetectDriftAsync(
            AzureScenario.RecordedHandle(provisionerId: "digitalocean-droplet"));

        drift.Divergences.Single().Aspect.Should().Be("provisioner");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_handle_naming_a_sibling_resource_rather_than_the_machine_is_out_of_scope_and_says_so()
    {
        var scenario = new AzureScenario();

        // The sweep returns NICs, public addresses and virtual networks as handles in their own right. This
        // maintainer answers about virtual machines, and a handle naming something else is reported as a
        // divergence rather than silently checked against the wrong thing - or worse, reported as a match.
        var drift = await scenario.Provisioner().DetectDriftAsync(
            AzureScenario.RecordedHandle(resourceId: AzureScenario.PublicIpId));

        var divergence = drift.Divergences.Single();
        divergence.Aspect.Should().Be("resource-kind");
        divergence.Expected.Should().Be("Microsoft.Compute/virtualMachines");
        divergence.Found.Should().Be(AzureScenario.PublicIpId);
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanUpdateAsync_declines_a_handle_that_does_not_name_a_machine_without_calling_arm()
    {
        var scenario = new AzureScenario();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            AzureScenario.RecordedHandle(resourceId: AzureScenario.NicId),
            AzureScenario.PalworldVmRequest());

        plan.Should().BeNull();
        scenario.Api.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Plan identity
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_plan_hash_covers_the_live_state_as_well_as_the_desired_state()
    {
        var request = AzureScenario.PalworldVmRequest(size: "Standard_D2s_v5");

        var first = new AzureScenario();
        first.RouteReadOnly();
        var fromB2s = await first.Provisioner().PlanUpdateAsync(AzureScenario.RecordedHandle(), request);

        var second = new AzureScenario();
        second.RouteReadOnly(AzureScenario.VirtualMachineJson(vmSize: "Standard_B1s"));
        var fromB1s = await second.Provisioner().PlanUpdateAsync(AzureScenario.RecordedHandle(), request);

        fromB1s!.PlanHash.Should().NotBe(fromB2s!.PlanHash);
    }

    [Fact]
    public async Task A_machine_with_no_marketplace_image_reference_reports_it_as_unknown_rather_than_inventing_one()
    {
        var scenario = new AzureScenario();

        // A machine created from a custom image or a gallery version carries no four-part marketplace URN.
        // Half-forming one would produce a difference the plan would then propose to "fix" by replacing the
        // machine, which is the most expensive false positive available here.
        scenario.RouteReadOnly(AzureScenario.VirtualMachineJson(imageUrn: null));

        var drift = await scenario.Provisioner().DetectDriftAsync(AzureScenario.RecordedHandle());

        var divergence = drift.Divergences.Single(d => d.Aspect == "image");
        divergence.Expected.Should().Be(AzureScenario.ImageUrn);
        divergence.Found.Should().BeNull();
        divergence.Description.Should().Be(
            "image: expected Canonical:ubuntu-24_04-lts:server:latest, found nothing");
    }
}
