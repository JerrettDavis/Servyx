using System.Globalization;

using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Tests.Provisioning;

/// <summary>
/// The <see cref="IMaintainer"/> half of the droplet adapter: what it plans, what it refuses to plan, and —
/// the assertion the rest of this file exists to protect — what it says will happen to the machine's disk.
/// </summary>
/// <remarks>
/// Every test here runs against the substituted DigitalOcean API, so no network access, no account, and no
/// token beyond the fake one in the scenario is involved. Several of them assert on the <em>absence</em> of a
/// request, which is a stronger claim than "the call failed": the API double throws on any non-GET, so a plan
/// that tried to resize or rebuild anything would fail the test at the point it tried.
/// </remarks>
public class DigitalOceanDropletMaintenanceTests
{
    // ---------------------------------------------------------------------------------------------------
    // The adapter is a maintainer at all, and says so honestly
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_provisioner_is_a_maintainer_and_the_two_ids_agree()
    {
        var provisioner = new DigitalOceanScenario().Provisioner();

        provisioner.Should().BeAssignableTo<IMaintainer>();
        ((IMaintainer)provisioner).ProvisionerId.Should().Be(DigitalOceanDropletProvisioner.Id);
    }

    [Fact]
    public void The_three_maintenance_capability_bits_are_all_claimed()
    {
        var capabilities = new DigitalOceanScenario().Provisioner().Capabilities;

        // UpdateInPlace: a resize and a retag both act on the droplet that already exists.
        capabilities.Should().HaveFlag(ProvisioningCapabilities.UpdateInPlace);

        // RecreateToUpdate: an image change reimages the disk, and is filed here rather than under
        // UpdateInPlace even though DigitalOcean's rebuild keeps the droplet id - see the Capabilities remarks.
        capabilities.Should().HaveFlag(ProvisioningCapabilities.RecreateToUpdate);

        capabilities.Should().HaveFlag(ProvisioningCapabilities.DetectDrift);
    }

    [Fact]
    public void Planning_a_resize_is_not_the_same_promise_as_being_able_to_perform_one()
    {
        // The adapter plans a resize in detail and still does not claim Resize, because no code path in the
        // assembly issues POST /v2/droplets/{id}/actions. A caller must be able to tell those two apart.
        new DigitalOceanScenario().Provisioner().Capabilities
            .Should().NotHaveFlag(ProvisioningCapabilities.Resize);
    }

    // ---------------------------------------------------------------------------------------------------
    // Planning issues no mutating call
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanUpdateAsync_issues_exactly_one_read_and_no_mutating_request()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest());

        scenario.Api.Requests.Should().ContainSingle();
        scenario.Api.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task DetectDriftAsync_issues_exactly_one_read_and_no_mutating_request()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        await scenario.Provisioner().DetectDriftAsync(DigitalOceanScenario.RecordedHandle());

        scenario.Api.Requests.Should().ContainSingle();
        scenario.Api.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Planning_a_rebuild_still_issues_no_mutating_request()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        // The most destructive plan this adapter can produce, and it is still only a read.
        var plan = await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = "debian-12-x64" }));

        plan!.DataImpact.Should().Be(DataImpact.Destroyed);
        scenario.Api.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    // ---------------------------------------------------------------------------------------------------
    // Nothing to change
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_droplet_that_already_matches_the_request_needs_no_change_and_carries_no_stages()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest());

        plan.Should().NotBeNull();
        plan!.Strategy.Should().Be(UpdateStrategy.NoChangeRequired);
        plan.Changes.Should().BeEmpty();
        plan.Stages.Should().BeEmpty();
        plan.DataImpact.Should().Be(DataImpact.Preserved);
        plan.ProvisionerId.Should().Be(DigitalOceanDropletProvisioner.Id);
    }

    // ---------------------------------------------------------------------------------------------------
    // Size: the disk-preserving resize, and the justification for saying so
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_size_change_is_planned_as_an_in_place_resize_that_preserves_the_disk()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest(size: "s-4vcpu-8gb"));

        plan!.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.DataImpact.Should().Be(DataImpact.Preserved);

        var change = plan.Changes.Single();
        change.Aspect.Should().Be("size");
        change.Current.Should().Be("s-2vcpu-4gb");
        change.Desired.Should().Be("s-4vcpu-8gb");
        change.RequiresRecreate.Should().BeFalse();
    }

    [Fact]
    public async Task The_resize_stage_says_which_form_of_resize_it_is_and_refuses_the_irreversible_one()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest(size: "s-4vcpu-8gb"));

        var stage = plan!.Stages.Single(s => s.StageId == "resize-droplet");

        // DigitalOcean's resize takes a disk boolean. The safe form is named explicitly, so a reader is never
        // left to assume which one a "resize" means.
        stage.Description.Should().Contain("disk flag set to false");
        stage.Description.Should().Contain("CPU-and-memory-only");

        // And the dangerous form is named as refused rather than merely unmentioned.
        stage.Description.Should().Contain("NOT planned here and never will be");
        stage.Description.Should().Contain("cannot be undone");

        // The Preserved claim is backed by the live disk this adapter actually read, not by an assumption.
        stage.Description.Should().Contain(
            string.Create(CultureInfo.InvariantCulture, $"{DigitalOceanScenario.DiskGigabytes} GB boot disk"));
    }

    [Fact]
    public async Task The_data_impact_stage_of_a_resize_names_the_droplet_and_the_disk_that_survive()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest(size: "s-4vcpu-8gb"));

        var stage = plan!.Stages.Single(s => s.StageId == "data-impact");

        stage.Description.Should().Contain("Data impact of this plan is Preserved");
        stage.Description.Should().Contain(DigitalOceanScenario.DropletId.ToString(CultureInfo.InvariantCulture));
        stage.Description.Should().Contain("No step above writes to that disk or detaches it");
    }

    // ---------------------------------------------------------------------------------------------------
    // Image: the rebuild, and the destruction named in plain language
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_image_change_is_a_rebuild_and_the_plan_reports_the_data_as_destroyed()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = "debian-12-x64" }));

        plan!.DataImpact.Should().Be(DataImpact.Destroyed);
        plan.Strategy.Should().Be(UpdateStrategy.Recreate);

        var change = plan.Changes.Single();
        change.Aspect.Should().Be("image");
        change.Current.Should().Be(DigitalOceanScenario.ImageSlug);
        change.Desired.Should().Be("debian-12-x64");
        change.RequiresRecreate.Should().BeTrue();
    }

    [Fact]
    public async Task The_rebuild_stage_says_the_disk_is_erased_in_words_an_operator_can_act_on()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = "debian-12-x64" }));

        var stage = plan!.Stages.Single(s => s.StageId == "rebuild-droplet");

        // Plain language, not jargon: no reader should have to know what "rebuild" means to a cloud API in
        // order to understand that their saves are about to be deleted.
        stage.Description.Should().Contain("ERASES THE DROPLET'S DISK");
        stage.Description.Should().Contain("every save file");
        stage.Description.Should().Contain("cannot be recovered");

        var impact = plan.Stages.Single(s => s.StageId == "data-impact");
        impact.Description.Should().Contain("Data impact of this plan is Destroyed");
        impact.Description.Should().Contain("approving this plan is approving the deletion");
    }

    [Fact]
    public async Task A_rebuild_planned_alongside_a_resize_is_not_softened_by_it()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = "debian-12-x64" },
                size: "s-4vcpu-8gb"));

        // The worst answer wins. A plan that both resizes and rebuilds still destroys the data.
        plan!.DataImpact.Should().Be(DataImpact.Destroyed);
        plan.Changes.Select(c => c.Aspect).Should().Contain(["size", "image"]);
        plan.Stages.Select(s => s.StageId).Should().Contain(["rebuild-droplet", "resize-droplet"]);
    }

    // ---------------------------------------------------------------------------------------------------
    // Region: reported as unsupported, never silently planned
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_region_change_is_reported_as_unsupported_and_nothing_is_planned()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "fra1" }));

        var change = plan!.Changes.Single(c => c.Aspect == "region");
        change.Current.Should().Be("nyc3");
        change.Desired.Should().Be("fra1");
        change.RequiresRecreate.Should().BeTrue();

        var stage = plan.Stages.Single();
        stage.StageId.Should().Be("region-change-not-supported");
        stage.Description.Should().StartWith("NOT SUPPORTED:");
        stage.Description.Should().Contain("cannot be moved between regions");

        // The refusal is total: no resize, no rebuild, no destroy-and-recreate is described.
        plan.Stages.Should().NotContain(s => s.StageId == "resize-droplet" || s.StageId == "rebuild-droplet");
        plan.DataImpact.Should().Be(DataImpact.Destroyed);
    }

    [Fact]
    public async Task A_region_change_does_not_quietly_carry_the_other_changes_as_applicable_stages()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "fra1" },
                size: "s-4vcpu-8gb"));

        // Both differences are reported by name ...
        plan!.Changes.Select(c => c.Aspect).Should().Contain(["region", "size"]);

        // ... and neither is presented as something that could be applied to this droplet.
        var stage = plan.Stages.Single();
        stage.StageId.Should().Be("region-change-not-supported");
        stage.Description.Should().Contain("equally not applied");
        stage.Description.Should().Contain("s-4vcpu-8gb");
    }

    // ---------------------------------------------------------------------------------------------------
    // Tags
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tag_only_change_is_an_in_place_update_that_preserves_the_disk()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["tag:servyx.environment"] = "staging" }));

        plan!.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.DataImpact.Should().Be(DataImpact.Preserved);
        plan.Changes.Single().Aspect.Should().Be("tag servyx.environment");
        plan.Stages.Should().Contain(s => s.StageId == "retag-droplet");
    }

    // ---------------------------------------------------------------------------------------------------
    // A droplet the provider no longer has
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanUpdateAsync_returns_null_for_a_droplet_digitalocean_no_longer_has()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteMissingDroplet();

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(),
            DigitalOceanScenario.PalworldDropletRequest());

        // Not an empty plan: there is nothing to update, and inventing a create plan here would turn an update
        // preview into a provisioning one.
        plan.Should().BeNull();
    }

    [Fact]
    public async Task A_deleted_droplet_is_reported_as_drift_rather_than_as_an_exception()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteMissingDroplet();

        var drift = await scenario.Provisioner().DetectDriftAsync(DigitalOceanScenario.RecordedHandle());

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
    public async Task An_untouched_droplet_matches_the_handle_servyx_recorded()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        var drift = await scenario.Provisioner().DetectDriftAsync(DigitalOceanScenario.RecordedHandle());

        drift.Divergences.Should().BeEmpty();
        drift.Matches.Should().BeTrue();
        drift.Summary.Should().Contain("matches the resource Servyx provisioned");
    }

    [Fact]
    public async Task Every_changed_property_is_named_as_its_own_divergence()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly(DigitalOceanScenario.DropletEnvelopeJson(
            region: "fra1",
            sizeSlug: "s-8vcpu-16gb",
            imageSlug: "debian-12-x64"));

        var drift = await scenario.Provisioner().DetectDriftAsync(DigitalOceanScenario.RecordedHandle());

        drift.Matches.Should().BeFalse();
        drift.Divergences.Select(d => d.Aspect).Should().BeEquivalentTo(["region", "size", "image"]);

        drift.Divergences.Single(d => d.Aspect == "size").Description
            .Should().Be("size: expected s-2vcpu-4gb, found s-8vcpu-16gb");
        drift.Divergences.Single(d => d.Aspect == "image").Description
            .Should().Be("image: expected ubuntu-24-04-x64, found debian-12-x64");
        drift.Divergences.Single(d => d.Aspect == "region").Description
            .Should().Be("region: expected nyc3, found fra1");
    }

    [Fact]
    public async Task A_tag_edited_away_at_the_provider_is_reported_by_name()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly(DigitalOceanScenario.DropletEnvelopeJson(
            tags: ["servyx_managed:true", "servyx_instance-id:srv-0001", "servyx_job-id:job-42"]));

        var drift = await scenario.Provisioner().DetectDriftAsync(DigitalOceanScenario.RecordedHandle());

        drift.Divergences.Should().ContainSingle();
        drift.Divergences.Single().Aspect.Should().Be("tag servyx.connector-id");
        drift.Divergences.Single().Found.Should().BeNull();
    }

    [Fact]
    public async Task A_handle_that_records_no_size_or_image_reports_them_as_unverifiable_rather_than_matching()
    {
        var scenario = new DigitalOceanScenario();
        scenario.RouteReadOnly();

        var drift = await scenario.Provisioner().DetectDriftAsync(
            DigitalOceanScenario.RecordedHandle(size: null, image: null));

        // A check that cannot prove a match must not claim one, so both are reported with a null expectation
        // rather than quietly passing.
        drift.Matches.Should().BeFalse();
        drift.Divergences.Select(d => d.Aspect).Should().BeEquivalentTo(["size", "image"]);
        drift.Divergences.Single(d => d.Aspect == "size").Description
            .Should().Be("size: Servyx recorded no expected value, found s-2vcpu-4gb");
    }

    [Fact]
    public async Task A_handle_from_another_provisioner_is_a_divergence_and_costs_no_api_call()
    {
        var scenario = new DigitalOceanScenario();

        var drift = await scenario.Provisioner().DetectDriftAsync(
            DigitalOceanScenario.RecordedHandle(provisionerId: "docker-container"));

        drift.Divergences.Single().Aspect.Should().Be("provisioner");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_handle_whose_id_is_not_a_droplet_id_is_a_divergence_and_costs_no_api_call()
    {
        var scenario = new DigitalOceanScenario();
        var handle = new ResourceHandle(
            DigitalOceanDropletProvisioner.Id,
            "not-a-droplet-id",
            "nyc3",
            new Dictionary<string, string>(StringComparer.Ordinal));

        var drift = await scenario.Provisioner().DetectDriftAsync(handle);

        drift.Divergences.Single().Aspect.Should().Be("droplet-id");
        scenario.Api.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Plan identity
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_plan_hash_covers_the_live_state_as_well_as_the_desired_state()
    {
        var request = DigitalOceanScenario.PalworldDropletRequest(size: "s-4vcpu-8gb");

        var fromSmallDroplet = new DigitalOceanScenario();
        fromSmallDroplet.RouteReadOnly();
        var first = await fromSmallDroplet.Provisioner().PlanUpdateAsync(DigitalOceanScenario.RecordedHandle(), request);

        var fromDifferentDroplet = new DigitalOceanScenario();
        fromDifferentDroplet.RouteReadOnly(DigitalOceanScenario.DropletEnvelopeJson(sizeSlug: "s-1vcpu-2gb"));
        var second = await fromDifferentDroplet.Provisioner().PlanUpdateAsync(DigitalOceanScenario.RecordedHandle(), request);

        // Same desired state, different observed state: a caller re-showing a plan must be able to see that the
        // inputs no longer produce the plan it displayed.
        second!.PlanHash.Should().NotBe(first!.PlanHash);
    }

    [Fact]
    public async Task An_image_named_by_id_rather_than_by_slug_is_compared_in_the_form_the_request_uses()
    {
        var scenario = new DigitalOceanScenario();

        // A custom image or snapshot has no slug at all, so the live reference is its numeric id - and a
        // request naming that id must compare equal rather than reporting a permanent, unfixable difference.
        scenario.RouteReadOnly(DigitalOceanScenario.DropletEnvelopeJson(imageSlug: null, imageId: 987654321));

        var plan = await scenario.Provisioner().PlanUpdateAsync(
            DigitalOceanScenario.RecordedHandle(image: "987654321"),
            DigitalOceanScenario.PalworldDropletRequest(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = "987654321" }));

        plan!.Strategy.Should().Be(UpdateStrategy.NoChangeRequired);
    }
}
