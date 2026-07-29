using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Process.Provisioning;

namespace Servyx.Infrastructure.Process.Tests.Provisioning;

/// <summary>
/// Unit tests for <see cref="LocalProcessProvisioner"/>'s <see cref="IMaintainer"/> half — update planning and
/// drift detection.
/// </summary>
/// <remarks>
/// <para>
/// The negative these tests exist for is the same one the SSH and Docker maintenance suites pin for their
/// adapters, restated for this shape: creation planning (<c>PlanAsync</c>) is pure computation over a request
/// with no call to audit, whereas both <see cref="IMaintainer"/> members must read the live marker (and, for
/// drift, the live filesystem), so "changes nothing" here is a claim about which calls are made rather than
/// about making none.
/// </para>
/// <para>
/// Everything is composed under a temp directory, so the suite runs identically on Windows and on Linux: no
/// test writes a literal <c>/</c>- or <c>C:\</c>-rooted path, and the executable a drift check looks for is a
/// real file the fixture creates.
/// </para>
/// </remarks>
public class LocalProcessMaintenanceTests
{
    // ── Capabilities ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_declare_update_in_place_and_never_claim_recreate_to_update()
    {
        // The same pairing the SSH process adapter claims, and the opposite of Docker's: a marker rewrite and a
        // re-run of the install verbs mutate the install without discarding its provider identity, so there is
        // no recreate story to advertise.
        using var fixture = new LocalInstallFixture();

        var capabilities = fixture.Provisioner.Capabilities;

        capabilities.Should().HaveFlag(ProvisioningCapabilities.UpdateInPlace);
        capabilities.Should().HaveFlag(ProvisioningCapabilities.DetectDrift);
        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.RecreateToUpdate);
    }

    [Fact]
    public void The_provisioner_is_reachable_as_a_maintainer_and_an_applier_naming_the_same_provisioner_id()
    {
        using var fixture = new LocalInstallFixture();

        IMaintainer maintainer = fixture.Provisioner;
        IUpdateApplier applier = fixture.Provisioner;

        maintainer.ProvisionerId.Should().Be("local-process");
        applier.ProvisionerId.Should().Be("local-process");
    }

    // ── Update planning issues no command and mutates nothing ───────────────────────────────────

    [Fact]
    public async Task PlanUpdateAsync_issues_no_command_and_writes_deletes_and_creates_nothing()
    {
        using var fixture = new LocalInstallFixture();
        await fixture.InstallAsync();
        var before = fixture.Temp.Snapshot();

        var plan = await fixture.Provisioner.PlanUpdateAsync(
            new ResourceHandle("local-process", fixture.MarkerPath, null, await fixture.ReadMarkerAsync()),
            fixture.Request(LocalInstallFixture.With("executable", "./Other.sh")));

        plan.Should().NotBeNull();

        // Zero commands, asserted against the whole recorded log rather than an enumerated list, so a mutating
        // call added later cannot slip past.
        fixture.Host.Commands.Should().BeEmpty();
        fixture.Host.Order.Should().BeEmpty("planning writes nothing, deletes nothing and executes nothing");
        fixture.Temp.Snapshot().Should().Equal(before, "planning must not create a directory either");
    }

    [Fact]
    public async Task An_unknown_install_verb_is_rejected_at_update_plan_time_before_a_session_is_opened()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();

        var act = () => fixture.Provisioner.PlanUpdateAsync(
            resource.Handle,
            fixture.Request(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["install:2:verb"] = "curl-pipe-bash",
                ["install:2:path"] = "https://example.invalid/install.sh",
            }));

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*curl-pipe-bash*")
            .WithMessage("*steamcmd, ensure-dir*");

        fixture.Host.Connected.Should().BeEmpty("rejecting at plan time means nothing was reachable yet");
        fixture.Host.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanUpdateAsync_returns_null_when_the_marker_is_gone()
    {
        // Not "nothing needs to change": there is nothing to update, and inventing a create-from-scratch plan
        // would quietly turn an update preview into a provisioning one.
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();
        File.Delete(fixture.MarkerPath);

        (await fixture.Provisioner.PlanUpdateAsync(resource.Handle, fixture.Request())).Should().BeNull();
    }

    [Fact]
    public async Task PlanUpdateAsync_returns_null_for_a_file_that_is_not_servyx_managed()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();
        await File.WriteAllTextAsync(fixture.MarkerPath, "{\"something\":\"else\"}");

        (await fixture.Provisioner.PlanUpdateAsync(resource.Handle, fixture.Request())).Should().BeNull();
    }

    // ── The update plan ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unchanged_request_yields_a_plan_reporting_no_change_required()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();

        var plan = await fixture.Provisioner.PlanUpdateAsync(resource.Handle, fixture.Request());

        plan!.Strategy.Should().Be(UpdateStrategy.NoChangeRequired);
        plan.Changes.Should().BeEmpty();
        plan.Stages.Should().BeEmpty();
        plan.DataImpact.Should().Be(DataImpact.Preserved, "nothing would run, so nothing can happen to the data");
        plan.ProvisionerId.Should().Be("local-process");
    }

    [Fact]
    public async Task A_changed_executable_yields_an_in_place_plan_naming_the_executable_aspect()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();

        var plan = await fixture.Provisioner.PlanUpdateAsync(
            resource.Handle,
            fixture.Request(LocalInstallFixture.With("executable", "./PalServer-Linux-Shipping")));

        plan!.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.Changes.Should().ContainSingle(c => c.Aspect == "executable")
            .Which.Should().BeEquivalentTo(new
            {
                Current = LocalInstallFixture.Executable,
                Desired = "./PalServer-Linux-Shipping",
                RequiresRecreate = false,
            });

        plan.DataImpact.Should().Be(DataImpact.Preserved, "an executable swap does not threaten save data");
        plan.Stages.Select(s => s.StageId).Should().Equal("update-marker", "install-0-steamcmd", "install-1-ensure-dir");
        plan.Stages.Should().OnlyContain(s => s.ProvisionerId == "local-process");
    }

    [Fact]
    public async Task A_changed_tag_yields_an_in_place_plan_naming_the_tag_by_key()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();

        var plan = await fixture.Provisioner.PlanUpdateAsync(
            resource.Handle,
            fixture.Request(LocalInstallFixture.With("jobId", "job-99")));

        plan!.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.Changes.Should().ContainSingle(c => c.Aspect == $"tag {ServyxProcessMarker.JobIdTag}")
            .Which.Should().BeEquivalentTo(new { Current = "job-42", Desired = "job-99", RequiresRecreate = false });
    }

    [Fact]
    public async Task No_change_this_adapter_plans_ever_requires_a_recreate()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();

        var plan = await fixture.Provisioner.PlanUpdateAsync(
            resource.Handle,
            fixture.Request(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["executable"] = "./Other.sh",
                ["jobId"] = "job-99",
                ["dataDir"] = fixture.Temp.At("palworld-v2"),
                ["install:1:path"] = Path.Combine(fixture.Temp.At("palworld-v2"), "Pal"),
            }));

        plan!.Strategy.Should().Be(UpdateStrategy.InPlace, "this adapter has no RecreateToUpdate capability at all");
        plan.Changes.Should().NotBeEmpty();
        plan.Changes.Should().OnlyContain(c => !c.RequiresRecreate);
    }

    [Fact]
    public async Task An_update_plan_hash_is_deterministic_and_moves_when_the_desired_state_moves()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();

        var first = await fixture.Provisioner.PlanUpdateAsync(resource.Handle, fixture.Request());
        var second = await fixture.Provisioner.PlanUpdateAsync(resource.Handle, fixture.Request());
        var third = await fixture.Provisioner.PlanUpdateAsync(
            resource.Handle,
            fixture.Request(LocalInstallFixture.With("executable", "./Other.sh")));

        second!.PlanHash.Should().Be(first!.PlanHash);
        third!.PlanHash.Should().NotBe(first.PlanHash);
    }

    // ── Data impact ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_changed_data_directory_yields_at_least_at_risk_with_the_reasoning_visible_in_the_plan()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();
        var moved = fixture.Temp.At("palworld-v2");

        var plan = await fixture.Provisioner.PlanUpdateAsync(
            resource.Handle,
            fixture.Request(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dataDir"] = moved,
                ["install:1:path"] = Path.Combine(moved, "Pal", "Saved", "Config"),
            }));

        plan!.DataImpact.Should().Be(DataImpact.AtRisk);
        ((int)plan.DataImpact).Should().BeGreaterThanOrEqualTo((int)DataImpact.AtRisk, "never Preserved by default");
        Enum.IsDefined(plan.DataImpact).Should().BeTrue();

        plan.Changes.Should().ContainSingle(c => c.Aspect == "dataDir")
            .Which.Should().BeEquivalentTo(new { Current = fixture.DataDirectory, Desired = moved, RequiresRecreate = false });

        // The reasoning is in the plan an operator reads, not only in this adapter's source.
        var updateMarker = plan.Stages.Single(s => s.StageId == "update-marker");
        updateMarker.Description.Should().Contain(nameof(DataImpact.AtRisk));
        updateMarker.Description.Should().Contain(fixture.DataDirectory).And.Contain(moved).And.Contain("orphaned");
    }

    [Fact]
    public async Task An_unchanged_data_directory_is_preserved_even_when_other_fields_change()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();

        var plan = await fixture.Provisioner.PlanUpdateAsync(
            resource.Handle,
            fixture.Request(LocalInstallFixture.With("executable", "./Other.sh")));

        plan!.DataImpact.Should().Be(DataImpact.Preserved);
        plan.Stages.Single(s => s.StageId == "update-marker").Description
            .Should().Contain("without touching its saved data");
    }

    [Fact]
    public async Task No_update_plan_this_adapter_can_produce_ever_claims_it_would_destroy_data()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();
        var moved = fixture.Temp.At("palworld-v2");

        var plan = await fixture.Provisioner.PlanUpdateAsync(
            resource.Handle,
            fixture.Request(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dataDir"] = moved,
                ["install:1:path"] = Path.Combine(moved, "Pal"),
            }));

        plan!.DataImpact.Should().NotBe(DataImpact.Destroyed);
    }

    // ── Drift detection ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DetectDriftAsync_reports_a_match_for_an_untouched_install()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();

        var drift = await fixture.Provisioner.DetectDriftAsync(resource.Handle);

        drift.Matches.Should().BeTrue();
        drift.Divergences.Should().BeEmpty();
        drift.Summary.Should().Contain("matches the resource Servyx provisioned");
    }

    [Fact]
    public async Task DetectDriftAsync_issues_no_command_and_changes_nothing()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();
        var before = fixture.Temp.Snapshot();

        await fixture.Provisioner.DetectDriftAsync(resource.Handle);

        fixture.Host.Commands.Should().BeEmpty();
        fixture.Host.Order.Should().BeEmpty();
        fixture.Temp.Snapshot().Should().Equal(before);
    }

    [Fact]
    public async Task DetectDriftAsync_names_a_changed_tag_by_its_key()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();

        // Simulate the marker having been hand-edited or restored from a stale copy since Servyx wrote it.
        var edited = new Dictionary<string, string>(resource.Handle.Tags, StringComparer.Ordinal)
        {
            [ServyxProcessMarker.InstanceIdTag] = "srv-9999",
        };
        await File.WriteAllBytesAsync(fixture.MarkerPath, ServyxProcessMarker.Serialize(edited));

        var drift = await fixture.Provisioner.DetectDriftAsync(resource.Handle);

        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle(d => d.Aspect == $"tag {ServyxProcessMarker.InstanceIdTag}")
            .Which.Description.Should().Be($"tag {ServyxProcessMarker.InstanceIdTag}: expected srv-0001, found srv-9999");
        drift.Summary.Should().Contain("has drifted");
    }

    [Fact]
    public async Task DetectDriftAsync_names_a_missing_data_directory()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();
        Directory.Delete(fixture.DataDirectory, recursive: true);

        var drift = await fixture.Provisioner.DetectDriftAsync(resource.Handle);

        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().Contain(d => d.Aspect == "dataDir" && d.Expected == fixture.DataDirectory && d.Found == null);
    }

    [Fact]
    public async Task DetectDriftAsync_names_a_missing_executable_when_the_data_directory_still_exists()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();
        File.Delete(fixture.ExecutablePath);

        var drift = await fixture.Provisioner.DetectDriftAsync(resource.Handle);

        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Aspect = "executable",
                Expected = LocalInstallFixture.Executable,
                Found = (string?)null,
            });
    }

    [Fact]
    public async Task DetectDriftAsync_reports_a_vanished_marker_as_drift_rather_than_as_an_exception()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();
        File.Delete(fixture.MarkerPath);

        var drift = await fixture.Provisioner.DetectDriftAsync(resource.Handle);

        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle().Which.Aspect.Should().Be("marker");
    }

    [Fact]
    public async Task DetectDriftAsync_reports_an_unparseable_marker_as_drift_rather_than_as_a_match()
    {
        using var fixture = new LocalInstallFixture();
        var resource = await fixture.InstallAsync();
        await File.WriteAllTextAsync(fixture.MarkerPath, "not json at all");

        var drift = await fixture.Provisioner.DetectDriftAsync(resource.Handle);

        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle().Which.Aspect.Should().Be("marker");
    }

    [Fact]
    public async Task DetectDriftAsync_refuses_another_provisioners_handle_without_touching_the_machine()
    {
        using var fixture = new LocalInstallFixture();
        await fixture.InstallAsync();

        var drift = await fixture.Provisioner.DetectDriftAsync(
            new ResourceHandle("docker-container", "container-1", null, new Dictionary<string, string>(StringComparer.Ordinal)));

        // Reported as a divergence, not as a match: "this is not my resource" is not evidence it is intact.
        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle().Which.Aspect.Should().Be("provisioner");
        fixture.Host.Connected.Should().BeEmpty();
    }
}
