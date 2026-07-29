using Servyx.Domain.Provisioning;

namespace Servyx.Domain.Tests.Provisioning;

/// <summary>
/// Tests for the maintenance-planning value types. These pin the structural guarantees rather than the
/// formatting: an update plan is what an operator reads before approving a destructive recreate, so the
/// ways it could quietly misdescribe itself are the things worth making inexpressible.
/// </summary>
public class MaintenanceModelsTests
{
    private static readonly ProvisioningStage Stage = new("stop-container", "docker-container", "Stop it.");

    private static UpdatePlan Plan(
        UpdateStrategy strategy = UpdateStrategy.Recreate,
        DataImpact dataImpact = DataImpact.AtRisk,
        IReadOnlyList<PlannedChange>? changes = null,
        IReadOnlyList<ProvisioningStage>? stages = null) => new(
        planId: "docker-container:update:palworld-server:abc123def456",
        planHash: "abc123def456",
        provisionerId: "docker-container",
        strategy: strategy,
        dataImpact: dataImpact,
        changes: changes ?? [new PlannedChange("image", "nginx:1.25", "nginx:1.27", RequiresRecreate: true)],
        stages: stages ?? [Stage],
        expiresAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void DataImpact_has_no_zero_member_so_an_unset_field_cannot_read_as_Preserved()
    {
        // This is the whole reason the enum starts at one. A plan whose data impact was never set would
        // otherwise be indistinguishable from one whose adapter enumerated the mounts and confirmed them.
        Enum.GetValues<DataImpact>().Should().NotContain(v => (int)v == 0);
        Enum.IsDefined(default(DataImpact)).Should().BeFalse();
        ((int)DataImpact.Preserved).Should().NotBe(0);
    }

    [Fact]
    public void UpdateStrategy_has_no_zero_member_either()
    {
        Enum.GetValues<UpdateStrategy>().Should().NotContain(v => (int)v == 0);
        Enum.IsDefined(default(UpdateStrategy)).Should().BeFalse();
    }

    [Fact]
    public void A_plan_cannot_be_constructed_with_an_unset_data_impact()
    {
        var act = () => Plan(dataImpact: default);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("dataImpact");
    }

    [Fact]
    public void A_plan_cannot_be_constructed_with_an_unset_strategy()
    {
        var act = () => Plan(strategy: default);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("strategy");
    }

    [Fact]
    public void A_plan_that_reports_no_change_required_cannot_also_carry_changes()
    {
        var act = () => Plan(UpdateStrategy.NoChangeRequired, DataImpact.Preserved, stages: []);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_plan_that_reports_no_change_required_cannot_carry_stages_that_would_run()
    {
        var act = () => Plan(UpdateStrategy.NoChangeRequired, DataImpact.Preserved, changes: []);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_plan_that_would_do_something_must_say_what_differs()
    {
        var act = () => Plan(UpdateStrategy.Recreate, DataImpact.AtRisk, changes: []);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_plan_carrying_a_recreate_forcing_change_cannot_describe_itself_as_in_place()
    {
        // The dangerous misdescription: an operator reading "in place" expects no downtime and no new
        // resource identity, and would approve on that basis.
        var act = () => Plan(
            UpdateStrategy.InPlace,
            DataImpact.Preserved,
            changes: [new PlannedChange("image", "nginx:1.25", "nginx:1.27", RequiresRecreate: true)]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_in_place_plan_is_constructible_when_nothing_forces_a_recreate()
    {
        var plan = Plan(
            UpdateStrategy.InPlace,
            DataImpact.Preserved,
            changes: [new PlannedChange("size", "s-1vcpu", "s-2vcpu", RequiresRecreate: false)]);

        plan.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.DataImpact.Should().Be(DataImpact.Preserved);
    }

    [Fact]
    public void A_no_change_plan_carries_nothing_that_would_run()
    {
        var plan = Plan(UpdateStrategy.NoChangeRequired, DataImpact.Preserved, changes: [], stages: []);

        plan.Changes.Should().BeEmpty();
        plan.Stages.Should().BeEmpty();
    }

    [Fact]
    public void A_planned_change_refuses_a_blank_aspect_because_an_unnamed_difference_cannot_be_reviewed()
    {
        var act = () => new PlannedChange("  ", "a", "b", RequiresRecreate: true);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_planned_change_renders_both_sides_including_absence()
    {
        new PlannedChange("image", "nginx:1.25", "nginx:1.27", true).Description
            .Should().Be("image: 'nginx:1.25' -> 'nginx:1.27'");

        new PlannedChange("label servyx.job-id", null, "job-42", true).Description
            .Should().Be("label servyx.job-id: (none) -> 'job-42'");
    }

    [Fact]
    public void Drift_matches_exactly_when_nothing_diverged_and_never_by_assertion()
    {
        var handle = new ResourceHandle("docker-container", "container-1", null, new Dictionary<string, string>());

        new DriftResult(handle, []).Matches.Should().BeTrue();
        new DriftResult(handle, [new DriftDivergence("image", "nginx:1.25", "nginx:1.27")]).Matches.Should().BeFalse();
    }

    [Fact]
    public void A_divergence_names_what_differs_and_both_values()
    {
        new DriftDivergence("image", "nginx:1.25", "nginx:1.27").Description
            .Should().Be("image: expected nginx:1.25, found nginx:1.27");
    }

    [Fact]
    public void A_missing_expectation_is_reported_rather_than_treated_as_a_match()
    {
        // A check that cannot prove a match must not claim one.
        var divergence = new DriftDivergence("image", null, "nginx:1.27");

        divergence.Description.Should().Be("image: Servyx recorded no expected value, found nginx:1.27");
        new DriftResult(
            new ResourceHandle("docker-container", "container-1", null, new Dictionary<string, string>()),
            [divergence]).Matches.Should().BeFalse();
    }

    [Fact]
    public void A_drift_summary_lists_every_divergence_by_name()
    {
        var result = new DriftResult(
            new ResourceHandle("docker-container", "container-1", null, new Dictionary<string, string>()),
            [
                new DriftDivergence("image", "nginx:1.25", "nginx:1.27"),
                new DriftDivergence("label servyx.job-id", "job-42", null),
            ]);

        result.Summary.Should()
            .Contain("image: expected nginx:1.25, found nginx:1.27")
            .And.Contain("label servyx.job-id: expected job-42, found nothing");
    }

    [Fact]
    public void A_divergence_refuses_a_blank_aspect()
    {
        var act = () => new DriftDivergence("", "a", "b");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_maintenance_capability_bits_are_independent_of_each_other_and_of_the_creation_bits()
    {
        var recreateOnly = ProvisioningCapabilities.RecreateToUpdate | ProvisioningCapabilities.DetectDrift;

        recreateOnly.Should().HaveFlag(ProvisioningCapabilities.RecreateToUpdate);
        recreateOnly.Should().HaveFlag(ProvisioningCapabilities.DetectDrift);
        recreateOnly.Should().NotHaveFlag(ProvisioningCapabilities.UpdateInPlace);
        recreateOnly.Should().NotHaveFlag(ProvisioningCapabilities.Create);
    }
}
