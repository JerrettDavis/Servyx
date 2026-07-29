using NSubstitute;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Provisioning;

namespace Servyx.Infrastructure.Ssh.Tests.Provisioning;

/// <summary>
/// Unit tests for <see cref="SshProcessProvisioner"/>'s <see cref="IMaintainer"/> half — update planning and
/// drift detection. Same house pattern as <see cref="SshProcessProvisionerTests"/>: the SSH host is a
/// substituted <see cref="Servyx.Domain.Transport.ITransport"/>/<see cref="Servyx.Domain.Transport.IExecutionTarget"/>
/// pair (see <see cref="SshHostDouble"/>), so no live SSH server is involved anywhere.
/// </summary>
/// <remarks>
/// The negative these tests exist for is the same one the Docker maintenance suite pins for its adapter,
/// restated for this shape: creation planning (<c>PlanAsync</c>) is pure computation over a request with no
/// call to audit, whereas both <see cref="IMaintainer"/> members must read the live marker (and, for drift,
/// the live filesystem), so "changes nothing" here is a claim about which calls are made rather than about
/// making none.
/// </remarks>
public class SshProcessMaintenanceTests
{
    private const string MarkerRoot = "/var/lib/servyx/instances";
    private const string MarkerPath = MarkerRoot + "/srv-0001.servyx.json";
    private const string DataDirectory = "/opt/palworld";
    private const string ExecutablePath = "/opt/palworld/PalServer.sh";

    /// <summary>
    /// Marks the host as holding an intact install matching <see cref="SshProcessProvisionerTests.PalworldNativeRequest"/>:
    /// the data directory and the executable inside it both exist. Provisioning itself never creates these in
    /// the test double (steamcmd is a generic recorded command, not something the double models the effects
    /// of), so tests seed them explicitly, the same way a real host would already have them after a
    /// successful install.
    /// </summary>
    private static void SeedIntactInstall(SshHostDouble host)
    {
        host.Directories.Add(DataDirectory);
        host.PutFile(ExecutablePath, "#!/bin/sh"u8.ToArray());
    }

    private static async Task<(ProvisionedResource Resource, SshHostDouble Host)> ProvisionAndSeedAsync(
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var (resource, host) = await SshProcessProvisionerTests.ProvisionAsync(extra);
        SeedIntactInstall(host);
        host.ClearRecordings();
        return (resource, host);
    }

    // ── Capabilities ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_declare_update_in_place_and_never_claim_recreate_to_update()
    {
        // The opposite pairing from Docker: a marker rewrite and a re-run of steamcmd mutate the install
        // without discarding its provider identity (the marker path never changes), so there is no
        // recreate story to advertise.
        var capabilities = SshProcessProvisionerTests.Provisioner(new SshHostDouble()).Capabilities;

        capabilities.Should().HaveFlag(ProvisioningCapabilities.UpdateInPlace);
        capabilities.Should().HaveFlag(ProvisioningCapabilities.DetectDrift);
        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.RecreateToUpdate);
    }

    [Fact]
    public void The_provisioner_is_reachable_as_a_maintainer_naming_the_same_provisioner_id()
    {
        IMaintainer maintainer = SshProcessProvisionerTests.Provisioner(new SshHostDouble());

        maintainer.ProvisionerId.Should().Be("ssh-process");
    }

    // ── Update planning issues no mutating command ──────────────────────────────────────────────

    [Fact]
    public async Task PlanUpdateAsync_issues_no_mutating_command()
    {
        var (resource, host) = await ProvisionAndSeedAsync();

        var plan = await SshProcessProvisionerTests.Provisioner(host)
            .PlanUpdateAsync(resource.Handle, SshProcessProvisionerTests.PalworldNativeRequest());

        plan.Should().NotBeNull();

        // Planning reads the marker (and disposes the session). It never writes, deletes, or executes
        // anything on the host — asserted against the whole call log so a mutating call added later cannot
        // slip past an enumerated list.
        host.Session.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Should().OnlyContain(name => name == "ExistsAsync" || name == "OpenReadAsync" || name == "DisposeAsync");

        await host.Session.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
        await host.Session.DidNotReceive().DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>());
        await host.Session.DidNotReceive().ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlanUpdateAsync_returns_null_when_the_marker_is_gone()
    {
        // Not "nothing needs to change": there is nothing to update, and inventing a create-from-scratch plan
        // would quietly turn an update preview into a provisioning one.
        var (resource, host) = await ProvisionAndSeedAsync();
        host.Files.Remove(MarkerPath);

        var plan = await SshProcessProvisionerTests.Provisioner(host)
            .PlanUpdateAsync(resource.Handle, SshProcessProvisionerTests.PalworldNativeRequest());

        plan.Should().BeNull();
    }

    [Fact]
    public async Task PlanUpdateAsync_returns_null_for_a_file_that_is_not_servyx_managed()
    {
        var (resource, host) = await ProvisionAndSeedAsync();
        host.Files[MarkerPath] = "{\"something\":\"else\"}"u8.ToArray();

        var plan = await SshProcessProvisionerTests.Provisioner(host)
            .PlanUpdateAsync(resource.Handle, SshProcessProvisionerTests.PalworldNativeRequest());

        plan.Should().BeNull();
    }

    // ── The update plan ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unchanged_request_yields_a_plan_reporting_no_change_required()
    {
        var (resource, host) = await ProvisionAndSeedAsync();

        var plan = await SshProcessProvisionerTests.Provisioner(host)
            .PlanUpdateAsync(resource.Handle, SshProcessProvisionerTests.PalworldNativeRequest());

        plan!.Strategy.Should().Be(UpdateStrategy.NoChangeRequired);
        plan.Changes.Should().BeEmpty();
        plan.Stages.Should().BeEmpty();
        plan.DataImpact.Should().Be(DataImpact.Preserved, "nothing would run, so nothing can happen to the data");
        plan.ProvisionerId.Should().Be("ssh-process");
    }

    [Fact]
    public async Task A_changed_executable_yields_an_in_place_plan_naming_the_executable_aspect()
    {
        var (resource, host) = await ProvisionAndSeedAsync();

        var plan = await SshProcessProvisionerTests.Provisioner(host).PlanUpdateAsync(
            resource.Handle,
            SshProcessProvisionerTests.PalworldNativeRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["executable"] = "./PalServer-Linux-Shipping",
            }));

        plan!.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.Changes.Should().ContainSingle(c => c.Aspect == "executable")
            .Which.Should().BeEquivalentTo(new { Current = "./PalServer.sh", Desired = "./PalServer-Linux-Shipping", RequiresRecreate = false });

        // Unchanged data directory: the executable swap does not threaten save data.
        plan.DataImpact.Should().Be(DataImpact.Preserved);
        plan.Stages.Select(s => s.StageId).Should().Contain("update-marker");
        plan.Stages.Should().OnlyContain(s => s.ProvisionerId == "ssh-process");
    }

    [Fact]
    public async Task A_changed_tag_yields_an_in_place_plan_naming_the_tag_by_key()
    {
        var (resource, host) = await ProvisionAndSeedAsync();

        var plan = await SshProcessProvisionerTests.Provisioner(host).PlanUpdateAsync(
            resource.Handle,
            SshProcessProvisionerTests.PalworldNativeRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["jobId"] = "job-99",
            }));

        plan!.Strategy.Should().Be(UpdateStrategy.InPlace);
        plan.Changes.Should().ContainSingle(c => c.Aspect == $"tag {ServyxProcessMarker.JobIdTag}")
            .Which.Should().BeEquivalentTo(new { Current = "job-42", Desired = "job-99", RequiresRecreate = false });
    }

    [Fact]
    public async Task No_change_this_adapter_plans_ever_requires_a_recreate()
    {
        var (resource, host) = await ProvisionAndSeedAsync();

        var plan = await SshProcessProvisionerTests.Provisioner(host).PlanUpdateAsync(
            resource.Handle,
            SshProcessProvisionerTests.PalworldNativeRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["executable"] = "./Other.sh",
                ["jobId"] = "job-99",
                ["dataDir"] = "/opt/palworld-v2",
            }));

        plan!.Strategy.Should().Be(UpdateStrategy.InPlace, "this adapter has no RecreateToUpdate capability at all");
        plan.Changes.Should().NotBeEmpty();
        plan.Changes.Should().OnlyContain(c => !c.RequiresRecreate);
    }

    // ── Data impact ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_changed_data_directory_yields_at_least_at_risk_with_the_reasoning_visible_in_the_plan()
    {
        var (resource, host) = await ProvisionAndSeedAsync();

        var plan = await SshProcessProvisionerTests.Provisioner(host).PlanUpdateAsync(
            resource.Handle,
            SshProcessProvisionerTests.PalworldNativeRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dataDir"] = "/opt/palworld-v2",
                ["install:1:path"] = "/opt/palworld-v2/Pal/Saved/Config/LinuxServer",
            }));

        plan!.DataImpact.Should().Be(DataImpact.AtRisk);
        Enum.IsDefined(plan.DataImpact).Should().BeTrue();

        plan.Changes.Should().ContainSingle(c => c.Aspect == "dataDir")
            .Which.Should().BeEquivalentTo(new { Current = DataDirectory, Desired = "/opt/palworld-v2", RequiresRecreate = false });

        var updateMarker = plan.Stages.Single(s => s.StageId == "update-marker");
        updateMarker.Description.Should().Contain(nameof(DataImpact.AtRisk));
        updateMarker.Description.Should().Contain(DataDirectory).And.Contain("/opt/palworld-v2").And.Contain("orphaned");
    }

    [Fact]
    public async Task An_unchanged_data_directory_is_preserved_even_when_other_fields_change()
    {
        var (resource, host) = await ProvisionAndSeedAsync();

        var plan = await SshProcessProvisionerTests.Provisioner(host).PlanUpdateAsync(
            resource.Handle,
            SshProcessProvisionerTests.PalworldNativeRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["executable"] = "./Other.sh",
            }));

        plan!.DataImpact.Should().Be(DataImpact.Preserved);
        plan.Stages.Single(s => s.StageId == "update-marker").Description
            .Should().Contain("without touching its saved data");
    }

    [Fact]
    public async Task No_update_plan_this_adapter_can_produce_ever_claims_it_would_destroy_data()
    {
        var (resource, host) = await ProvisionAndSeedAsync();

        var plan = await SshProcessProvisionerTests.Provisioner(host).PlanUpdateAsync(
            resource.Handle,
            SshProcessProvisionerTests.PalworldNativeRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dataDir"] = "/opt/palworld-v2",
            }));

        plan!.DataImpact.Should().NotBe(DataImpact.Destroyed);
    }

    [Fact]
    public async Task An_update_plan_hash_is_deterministic_and_moves_when_the_desired_state_moves()
    {
        var (resource, host) = await ProvisionAndSeedAsync();
        var provisioner = SshProcessProvisionerTests.Provisioner(host);

        var first = await provisioner.PlanUpdateAsync(resource.Handle, SshProcessProvisionerTests.PalworldNativeRequest());
        var second = await provisioner.PlanUpdateAsync(resource.Handle, SshProcessProvisionerTests.PalworldNativeRequest());
        var third = await provisioner.PlanUpdateAsync(
            resource.Handle,
            SshProcessProvisionerTests.PalworldNativeRequest(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["executable"] = "./Other.sh",
            }));

        second!.PlanHash.Should().Be(first!.PlanHash);
        third!.PlanHash.Should().NotBe(first.PlanHash);
    }

    // ── Drift detection ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DetectDriftAsync_reports_a_match_for_an_untouched_install()
    {
        var (resource, host) = await ProvisionAndSeedAsync();

        var drift = await SshProcessProvisionerTests.Provisioner(host).DetectDriftAsync(resource.Handle);

        drift.Matches.Should().BeTrue();
        drift.Divergences.Should().BeEmpty();
        drift.Summary.Should().Contain("matches the resource Servyx provisioned");
    }

    [Fact]
    public async Task DetectDriftAsync_names_a_changed_tag_by_its_key()
    {
        var (resource, host) = await ProvisionAndSeedAsync();

        // Simulate the marker having been hand-edited or restored from a stale copy since Servyx wrote it.
        var edited = new Dictionary<string, string>(resource.Handle.Tags, StringComparer.Ordinal)
        {
            [ServyxProcessMarker.InstanceIdTag] = "srv-9999",
        };
        host.Files[MarkerPath] = ServyxProcessMarker.Serialize(edited);

        var drift = await SshProcessProvisionerTests.Provisioner(host).DetectDriftAsync(resource.Handle);

        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle(d => d.Aspect == $"tag {ServyxProcessMarker.InstanceIdTag}")
            .Which.Description.Should().Be($"tag {ServyxProcessMarker.InstanceIdTag}: expected srv-0001, found srv-9999");
    }

    [Fact]
    public async Task DetectDriftAsync_names_a_missing_data_directory()
    {
        var (resource, host) = await ProvisionAndSeedAsync();
        host.Directories.Remove(DataDirectory);
        host.Files.Remove(ExecutablePath);

        var drift = await SshProcessProvisionerTests.Provisioner(host).DetectDriftAsync(resource.Handle);

        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().Contain(d => d.Aspect == "dataDir" && d.Expected == DataDirectory && d.Found == null);
    }

    [Fact]
    public async Task DetectDriftAsync_names_a_missing_executable_when_the_data_directory_still_exists()
    {
        var (resource, host) = await ProvisionAndSeedAsync();
        host.Files.Remove(ExecutablePath);

        var drift = await SshProcessProvisionerTests.Provisioner(host).DetectDriftAsync(resource.Handle);

        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Aspect = "executable", Expected = "./PalServer.sh", Found = (string?)null });
    }

    [Fact]
    public async Task DetectDriftAsync_reports_a_vanished_marker_as_drift_rather_than_as_an_exception()
    {
        var (resource, host) = await ProvisionAndSeedAsync();
        host.Files.Remove(MarkerPath);

        var drift = await SshProcessProvisionerTests.Provisioner(host).DetectDriftAsync(resource.Handle);

        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle().Which.Aspect.Should().Be("marker");
    }

    [Fact]
    public async Task DetectDriftAsync_refuses_another_provisioners_handle_without_touching_the_host()
    {
        var host = new SshHostDouble();
        var provisioner = SshProcessProvisionerTests.Provisioner(host);

        var drift = await provisioner.DetectDriftAsync(
            new ResourceHandle("docker-container", "container-1", null, new Dictionary<string, string>()));

        // Reported as a divergence, not as a match: "this is not my resource" is not evidence it is intact.
        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle().Which.Aspect.Should().Be("provisioner");
        host.Connected.Should().BeEmpty();
    }
}
