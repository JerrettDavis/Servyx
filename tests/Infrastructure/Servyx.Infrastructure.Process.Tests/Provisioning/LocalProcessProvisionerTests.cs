using NSubstitute;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Process.Provisioning;

namespace Servyx.Infrastructure.Process.Tests.Provisioning;

/// <summary>
/// Unit tests for <see cref="LocalProcessProvisioner"/>. Every test owns a temp directory that stands in for
/// the machine's install and marker roots, so nothing is written outside it and nothing depends on a program
/// being installed.
/// </summary>
public class LocalProcessProvisionerTests
{
    /// <summary>
    /// The exact string an injection attempt would use. It appears here as ordinary data — a Steam app id and
    /// an environment value — and must remain one inert argv element.
    /// </summary>
    private const string Hostile = "; rm -rf /";

    /// <summary>Everything one test needs: a temp root, the paths derived from it, and the recording host.</summary>
    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Temp = new TempDirectory("provisioner");
            MarkerRoot = Temp.At("instances");
            DataDirectory = Temp.At("palworld");
            Host = new RecordingLocalHost();
        }

        internal TempDirectory Temp { get; }

        internal string MarkerRoot { get; }

        internal string DataDirectory { get; }

        internal RecordingLocalHost Host { get; }

        internal string MarkerPath => Path.Combine(MarkerRoot, "srv-0001.servyx.json");

        internal LocalProcessProvisioner Provisioner() =>
            new(Host, machineId: "test-machine", credentialUrn: null, transportOptions: null, markerRoot: MarkerRoot);

        /// <summary>
        /// A realistic request modelled on the <c>native-steamcmd</c> profile of
        /// <c>definitions/palworld-docker.yaml</c>, including its two install verbs.
        /// </summary>
        internal ProvisioningRequest Request(IReadOnlyDictionary<string, string>? extra = null)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instanceId"] = "srv-0001",
                ["jobId"] = "job-42",
                ["connectorId"] = "local-palworld",
                ["executable"] = "./PalServer.sh",
                ["dataDir"] = DataDirectory,
                ["install:0:verb"] = "steamcmd",
                ["install:0:appId"] = "2394010",
                ["install:0:validate"] = "true",
                ["install:1:verb"] = "ensure-dir",
                ["install:1:path"] = Path.Combine(DataDirectory, "Pal", "Saved", "Config", "LinuxServer"),
                ["env:SERVER_NAME"] = "Servyx Test Server",
            };

            if (extra is not null)
            {
                foreach (var pair in extra)
                {
                    parameters[pair.Key] = pair.Value;
                }
            }

            return new ProvisioningRequest("palworld", "native-steamcmd", ConnectorId: null, parameters);
        }

        internal Task<ProvisionedResource> ProvisionAsync(IReadOnlyDictionary<string, string>? extra = null) =>
            Provisioner().CreateOperation(LocalProcessProvisioner.BuildSpec(Request(extra))).CreateAsync();

        public void Dispose() => Temp.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------
    // Identity and capabilities
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ProvisionerId_is_local_process()
    {
        using var fixture = new Fixture();

        fixture.Provisioner().ProvisionerId.Should().Be("local-process");
    }

    [Fact]
    public void Capabilities_advertise_create_destroy_and_tag_query()
    {
        using var fixture = new Fixture();

        var capabilities = fixture.Provisioner().Capabilities;

        capabilities.Should().HaveFlag(ProvisioningCapabilities.Create);
        capabilities.Should().HaveFlag(ProvisioningCapabilities.Destroy);
        capabilities.Should().HaveFlag(ProvisioningCapabilities.TagQuery);
    }

    [Theory]
    [InlineData(ProvisioningCapabilities.FirewallRules)]
    [InlineData(ProvisioningCapabilities.EstimatesCost)]
    public void Capabilities_claim_nothing_this_provisioner_does_not_do(ProvisioningCapabilities capability)
    {
        // Nothing here touches the machine's firewall, and a process on a machine Servyx did not rent has no
        // provider-billed price. Claiming either would tell a caller something happened that did not.
        using var fixture = new Fixture();

        fixture.Provisioner().Capabilities.Should().NotHaveFlag(capability);
    }

    [Fact]
    public void Constructor_rejects_a_transport_that_is_not_the_local_one()
    {
        var wrong = Substitute.For<ITransport>();
        wrong.TransportId.Returns("ssh");

        var act = () => new LocalProcessProvisioner(wrong);

        act.Should().Throw<ArgumentException>().WithMessage("*local*ssh*");
    }

    [Fact]
    public void A_marker_root_that_is_not_fully_qualified_is_rejected_at_construction()
    {
        using var fixture = new Fixture();

        var act = () => new LocalProcessProvisioner(fixture.Host, markerRoot: "relative/instances");

        act.Should().Throw<ArgumentException>().WithMessage("*fully-qualified*");
    }

    // ---------------------------------------------------------------------------------------------------
    // Planning
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanAsync_opens_no_session_and_issues_no_command_so_planning_cannot_mutate_anything()
    {
        using var fixture = new Fixture();
        var provisioner = fixture.Provisioner();
        fixture.Host.ClearRecordings();

        var plan = await provisioner.PlanAsync(fixture.Request());

        plan.Should().NotBeNull();
        fixture.Host.Connected.Should().BeEmpty();
        fixture.Host.Commands.Should().BeEmpty();
        fixture.Host.Order.Should().BeEmpty();

        // Nothing was created on disk either — planning is pure computation over the request.
        Directory.Exists(fixture.MarkerRoot).Should().BeFalse();
        Directory.Exists(fixture.DataDirectory).Should().BeFalse();
        fixture.Temp.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_stages_correspond_to_the_definitions_install_verbs_in_execution_order()
    {
        using var fixture = new Fixture();

        var plan = await fixture.Provisioner().PlanAsync(fixture.Request());

        plan.Stages.Select(s => s.StageId).Should().Equal("write-marker", "install-0-steamcmd", "install-1-ensure-dir");
        plan.Stages.Should().OnlyContain(s => s.ProvisionerId == "local-process");
        plan.Stages[0].Description.Should().Contain(fixture.MarkerPath).And.Contain("srv-0001").And.Contain("job-42");
        plan.Stages[1].Description.Should().Contain("2394010").And.Contain(fixture.DataDirectory).And.Contain("validating");
        plan.Stages[2].Description.Should().Contain("LinuxServer");
    }

    [Fact]
    public async Task PlanAsync_does_not_fabricate_a_cost_for_a_machine_servyx_did_not_rent()
    {
        using var fixture = new Fixture();

        var plan = await fixture.Provisioner().PlanAsync(fixture.Request());

        plan.EstimatedCost.Confidence.Should().Be(CostConfidence.Unknown);
        plan.EstimatedCost.Hourly.Should().BeNull();
        plan.EstimatedCost.Monthly.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_install_verb_is_rejected_at_plan_time_and_never_executed()
    {
        using var fixture = new Fixture();
        var provisioner = fixture.Provisioner();
        fixture.Host.ClearRecordings();

        var act = () => provisioner.PlanAsync(fixture.Request(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["install:2:verb"] = "curl-pipe-bash",
            ["install:2:path"] = "https://example.invalid/install.sh",
        }));

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*curl-pipe-bash*")
            .WithMessage("*steamcmd, ensure-dir*");

        fixture.Host.Connected.Should().BeEmpty("rejecting at plan time means nothing was reachable yet");
    }

    [Fact]
    public async Task An_install_entry_with_no_verb_is_rejected_at_plan_time()
    {
        using var fixture = new Fixture();

        var act = () => fixture.Provisioner().PlanAsync(fixture.Request(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["install:2:path"] = fixture.Temp.At("extra"),
        }));

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*install:2:verb*");
    }

    [Fact]
    public void An_instance_id_that_would_escape_the_marker_root_is_rejected_when_the_spec_is_built()
    {
        using var fixture = new Fixture();

        var act = () => LocalProcessProvisioner.BuildSpec(fixture.Request(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instanceId"] = "../../etc/cron.d/servyx",
        }));

        act.Should().Throw<ArgumentException>().WithMessage("*marker filename*");
    }

    [Fact]
    public void A_data_directory_that_is_not_fully_qualified_is_rejected_when_the_spec_is_built()
    {
        // Local-specific, and the sharpest divergence from the SSH adapter: a relative dataDir would land
        // wherever Servyx happened to be launched from. Rejected while building the spec, i.e. at plan time.
        using var fixture = new Fixture();

        var act = () => LocalProcessProvisioner.BuildSpec(fixture.Request(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dataDir"] = "palworld",
        }));

        act.Should().Throw<ArgumentException>().WithMessage("*fully-qualified*");
    }

    // ---------------------------------------------------------------------------------------------------
    // Installing
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_executed_install_step_is_an_argv_array_never_a_shell_string()
    {
        using var fixture = new Fixture();

        await fixture.ProvisionAsync();

        var steamcmd = fixture.Host.Commands.Single(c => c.Executable == "steamcmd");
        steamcmd.Arguments.Should().Equal(
            "+force_install_dir", fixture.DataDirectory, "+login", "anonymous", "+app_update", "2394010", "validate", "+quit");

        // Nothing else ran at all: ensure-dir spawns no process on a local target.
        fixture.Host.Commands.Should().ContainSingle();
        fixture.Host.Commands.Should().OnlyContain(c => !c.Executable.Contains(' '));
        fixture.Host.Commands.SelectMany(c => c.Arguments)
            .Should().OnlyContain(a => !a.Contains("&&") && !a.Contains('|'));
    }

    [Fact]
    public async Task A_hostile_app_id_stays_one_inert_argv_element_and_is_never_concatenated()
    {
        using var fixture = new Fixture();

        await fixture.ProvisionAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["install:0:appId"] = Hostile,
            ["env:SERVER_NAME"] = Hostile,
        });

        var steamcmd = fixture.Host.Commands.Single(c => c.Executable == "steamcmd");

        steamcmd.Arguments.Should().Equal(
            "+force_install_dir", fixture.DataDirectory, "+login", "anonymous", "+app_update", Hostile, "validate", "+quit");
        steamcmd.Arguments.Should().ContainSingle(a => a == Hostile);
        steamcmd.EnvironmentOverrides!["SERVER_NAME"].Should().Be(Hostile);

        // It is either the whole element or absent — nothing merged it into a larger token.
        steamcmd.Arguments.Should().OnlyContain(a => a == Hostile || !a.Contains("rm -rf"));
    }

    [Fact]
    public async Task The_marker_is_written_before_any_install_verb_runs()
    {
        // A container is labelled by the same atomic call that creates it. A marker file is a separate write,
        // so the equivalent guarantee has to be bought by ordering: after the marker exists, every later
        // failure still leaves something on the machine that a sweep can find.
        using var fixture = new Fixture();

        await fixture.ProvisionAsync();

        fixture.Host.Order.Should().Equal($"write:{fixture.MarkerPath}", "exec:steamcmd");
    }

    [Fact]
    public async Task The_marker_records_the_instance_job_connector_and_root_path_and_lands_on_disk()
    {
        using var fixture = new Fixture();

        var resource = await fixture.ProvisionAsync();

        File.Exists(fixture.MarkerPath).Should().BeTrue("the marker is a real file on the machine, not a fiction of a double");

        var tags = ServyxProcessMarker.Deserialize(await File.ReadAllBytesAsync(fixture.MarkerPath));

        tags.Should().NotBeNull();
        tags![ServyxProcessMarker.ManagedTag].Should().Be("true");
        tags[ServyxProcessMarker.InstanceIdTag].Should().Be("srv-0001");
        tags[ServyxProcessMarker.JobIdTag].Should().Be("job-42");
        tags[ServyxProcessMarker.ConnectorIdTag].Should().Be("local-palworld");
        tags[ServyxProcessMarker.RootPathTag].Should().Be(fixture.DataDirectory);
        tags[ServyxProcessMarker.ProvisionerIdTag].Should().Be("local-process");
        tags[ServyxProcessMarker.ExecutableTag].Should().Be("./PalServer.sh");

        resource.Handle.ProviderResourceId.Should().Be(fixture.MarkerPath);
        resource.Handle.Tags.Should().BeEquivalentTo(tags);
        resource.ConnectorId.Should().Be("local-palworld");
    }

    [Fact]
    public async Task An_extra_tag_can_never_shadow_a_mandatory_servyx_tag()
    {
        using var fixture = new Fixture();

        await fixture.ProvisionAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"tag:{ServyxProcessMarker.ManagedTag}"] = "false",
            [$"tag:{ServyxProcessMarker.InstanceIdTag}"] = "somebody-elses-server",
            ["tag:team"] = "ops",
        });

        var tags = ServyxProcessMarker.Deserialize(await File.ReadAllBytesAsync(fixture.MarkerPath))!;

        tags[ServyxProcessMarker.ManagedTag].Should().Be("true");
        tags[ServyxProcessMarker.InstanceIdTag].Should().Be("srv-0001");
        tags["team"].Should().Be("ops");
    }

    [Fact]
    public async Task The_ensure_dir_verb_creates_its_directory_without_starting_a_program()
    {
        using var fixture = new Fixture();

        await fixture.ProvisionAsync();

        Directory.Exists(Path.Combine(fixture.DataDirectory, "Pal", "Saved", "Config", "LinuxServer")).Should().BeTrue();
        fixture.Host.Commands.Should().NotContain(c => c.Executable.Contains("mkdir", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_data_directory_is_created_before_any_command_needs_it_as_a_working_directory()
    {
        using var fixture = new Fixture();

        await fixture.ProvisionAsync();

        Directory.Exists(fixture.DataDirectory).Should().BeTrue();
        Directory.Exists(fixture.MarkerRoot).Should().BeTrue();
    }

    [Fact]
    public async Task A_failing_install_step_aborts_the_install_and_leaves_the_marker_for_the_sweep_to_find()
    {
        using var fixture = new Fixture();
        fixture.Host.ExecHandler = command => command.Executable == "steamcmd"
            ? new CommandResult(8, string.Empty, "steamcmd: disk full", TimeSpan.Zero)
            : new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);

        var operation = fixture.Provisioner().CreateOperation(LocalProcessProvisioner.BuildSpec(fixture.Request()));

        var act = () => operation.CreateAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*install-0-steamcmd*")
            .WithMessage("*disk full*");

        // The marker was already on the machine when the install died, so the half-finished install is still
        // discoverable — the whole reason the marker is written first.
        File.Exists(fixture.MarkerPath).Should().BeTrue();
        (await fixture.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide("local-process")))
            .Select(h => h.ProviderResourceId).Should().Equal(fixture.MarkerPath);

        // The second install verb never ran: a failing step aborts rather than continuing.
        Directory.Exists(Path.Combine(fixture.DataDirectory, "Pal")).Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------------------
    // Refresh, reconcile, destroy
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_reads_the_marker_back_for_a_known_instance()
    {
        using var fixture = new Fixture();
        var resource = await fixture.ProvisionAsync();

        var refreshed = await fixture.Provisioner().RefreshAsync(resource.Handle);

        refreshed.Should().NotBeNull();
        refreshed!.Handle.ProviderResourceId.Should().Be(fixture.MarkerPath);
        refreshed.Handle.ProvisionerId.Should().Be("local-process");
        refreshed.Handle.Tags.Should().BeEquivalentTo(resource.Handle.Tags);
        refreshed.ConnectorId.Should().Be("local-palworld");
        refreshed.Facts.PrivateAddress.Should().Be("test-machine");
        refreshed.Facts.Cost.Confidence.Should().Be(CostConfidence.Unknown);
    }

    [Fact]
    public async Task RefreshAsync_returns_null_when_the_marker_is_gone()
    {
        using var fixture = new Fixture();
        var resource = await fixture.ProvisionAsync();
        File.Delete(fixture.MarkerPath);

        (await fixture.Provisioner().RefreshAsync(resource.Handle)).Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_returns_null_for_a_file_that_is_not_servyx_managed()
    {
        using var fixture = new Fixture();
        var resource = await fixture.ProvisionAsync();
        await File.WriteAllTextAsync(fixture.MarkerPath, "{\"something\":\"else\"}");

        (await fixture.Provisioner().RefreshAsync(resource.Handle)).Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_discovers_every_marker_under_the_marker_root()
    {
        using var fixture = new Fixture();
        await fixture.ProvisionAsync();

        await File.WriteAllBytesAsync(
            Path.Combine(fixture.MarkerRoot, "srv-0002.servyx.json"),
            ServyxProcessMarker.Serialize(ServyxProcessMarker.For("srv-0002", "job-43", "local-palworld").ToTags()));

        var handles = await fixture.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide("local-process"));

        handles.Select(h => h.ProviderResourceId).Should().Equal(
            Path.Combine(fixture.MarkerRoot, "srv-0001.servyx.json"),
            Path.Combine(fixture.MarkerRoot, "srv-0002.servyx.json"));
        handles.Should().OnlyContain(h => h.ProvisionerId == "local-process" && h.Region == null);
        handles[1].Tags[ServyxProcessMarker.InstanceIdTag].Should().Be("srv-0002");
    }

    [Fact]
    public async Task ReconcileAsync_never_reports_a_file_it_could_not_confirm_is_servyx_managed()
    {
        // The filename suffix is the cheap filter; re-reading servyx.managed is this process's own guarantee.
        // A sweep acting on a false positive deletes someone else's install.
        using var fixture = new Fixture();
        await fixture.ProvisionAsync();

        await File.WriteAllTextAsync(Path.Combine(fixture.MarkerRoot, "not-ours.servyx.json"), "{\"servyx.managed\":\"false\"}");
        await File.WriteAllTextAsync(Path.Combine(fixture.MarkerRoot, "garbage.servyx.json"), "not json at all");
        await File.WriteAllTextAsync(Path.Combine(fixture.MarkerRoot, "readme.txt"), "ignore me");
        Directory.CreateDirectory(Path.Combine(fixture.MarkerRoot, "a-directory.servyx.json"));

        var handles = await fixture.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide("local-process"));

        handles.Select(h => h.ProviderResourceId).Should().Equal(fixture.MarkerPath);
    }

    [Fact]
    public async Task ReconcileAsync_ignores_a_scope_that_belongs_to_another_provisioner()
    {
        using var fixture = new Fixture();
        await fixture.ProvisionAsync();
        fixture.Host.ClearRecordings();

        var handles = await fixture.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide("docker-container"));

        handles.Should().BeEmpty();
        fixture.Host.Connected.Should().BeEmpty("a sweep for someone else's provisioner must not even open a session");
    }

    [Fact]
    public async Task ReconcileAsync_reports_no_orphans_on_a_machine_that_has_never_been_installed_to()
    {
        using var fixture = new Fixture();

        var handles = await fixture.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide("local-process"));

        handles.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_sweeps_the_marker_root_the_scope_names_rather_than_the_constructed_one()
    {
        using var fixture = new Fixture();
        await fixture.ProvisionAsync();

        var otherRoot = fixture.Temp.At("other-instances");
        Directory.CreateDirectory(otherRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(otherRoot, "srv-0009.servyx.json"),
            ServyxProcessMarker.Serialize(ServyxProcessMarker.For("srv-0009", "job-99", "local-palworld").ToTags()));

        var handles = await fixture.Provisioner().ReconcileAsync(new OrphanScope.MarkerDirectory("local-process", otherRoot));

        handles.Select(h => h.ProviderResourceId).Should().Equal(Path.Combine(otherRoot, "srv-0009.servyx.json"));

        // The constructed root was genuinely not swept — its marker is still there and simply out of scope.
        File.Exists(fixture.MarkerPath).Should().BeTrue();
    }

    [Fact]
    public async Task ReconcileAsync_falls_back_to_the_constructed_marker_root_when_the_scope_names_none()
    {
        // Matches SshProcessProvisioner exactly: ProviderWide is not refused by a marker-backed adapter, it
        // simply means "the directory this provisioner writes to".
        using var fixture = new Fixture();
        await fixture.ProvisionAsync();

        var otherRoot = fixture.Temp.At("other-instances");
        Directory.CreateDirectory(otherRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(otherRoot, "srv-0009.servyx.json"),
            ServyxProcessMarker.Serialize(ServyxProcessMarker.For("srv-0009", "job-99", "local-palworld").ToTags()));

        var handles = await fixture.Provisioner().ReconcileAsync(new OrphanScope.ProviderWide("local-process"));

        handles.Select(h => h.ProviderResourceId).Should().Equal(fixture.MarkerPath);
    }

    [Fact]
    public async Task A_scope_supplied_marker_root_is_normalised_the_same_way_a_constructed_one_is()
    {
        using var fixture = new Fixture();
        var otherRoot = fixture.Temp.At("other-instances");
        Directory.CreateDirectory(otherRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(otherRoot, "srv-0009.servyx.json"),
            ServyxProcessMarker.Serialize(ServyxProcessMarker.For("srv-0009", "job-99", "local-palworld").ToTags()));

        var handles = await fixture.Provisioner()
            .ReconcileAsync(new OrphanScope.MarkerDirectory("local-process", otherRoot + Path.DirectorySeparatorChar));

        handles.Select(h => h.ProviderResourceId).Should().Equal(Path.Combine(otherRoot, "srv-0009.servyx.json"));
    }

    [Theory]
    [InlineData("relative/instances")]
    [InlineData("./instances")]
    [InlineData("instances")]
    public async Task A_scope_supplied_marker_root_faces_the_same_validation_as_a_constructed_one(string markerRoot)
    {
        // A scope is not a route around the rule. The check runs before any session is opened, so a malformed
        // root cannot get as far as listing a directory.
        using var fixture = new Fixture();
        var provisioner = fixture.Provisioner();

        var act = () => provisioner.ReconcileAsync(new OrphanScope.MarkerDirectory("local-process", markerRoot));

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*fully-qualified*");
        fixture.Host.Connected.Should().BeEmpty();
    }

    [Fact]
    public async Task A_scope_supplied_marker_root_never_changes_where_an_install_writes_its_marker()
    {
        using var fixture = new Fixture();
        await fixture.ProvisionAsync();

        File.Exists(fixture.MarkerPath).Should().BeTrue();
        fixture.Provisioner().MarkerRoot.Should().Be(fixture.MarkerRoot);
    }

    [Fact]
    public async Task DestroyAsync_removes_the_marker_and_reports_whether_it_was_there()
    {
        using var fixture = new Fixture();
        var resource = await fixture.ProvisionAsync();
        var provisioner = fixture.Provisioner();

        var first = await provisioner.DestroyAsync(resource.Handle);
        var second = await provisioner.DestroyAsync(resource.Handle);

        first.Should().BeTrue();
        second.Should().BeFalse();
        File.Exists(fixture.MarkerPath).Should().BeFalse();
    }

    [Fact]
    public async Task DestroyAsync_deliberately_leaves_the_data_directory_alone()
    {
        // Symmetric with the Docker provisioner's RemoveVolumes: false — destroying the Servyx handle to a
        // workload must never destroy a user's saves as a side effect.
        using var fixture = new Fixture();
        var resource = await fixture.ProvisionAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.DataDirectory, "world.sav"), "precious");
        fixture.Host.ClearRecordings();

        await fixture.Provisioner().DestroyAsync(resource.Handle);

        fixture.Host.Order.Should().Equal($"delete:{fixture.MarkerPath}");
        fixture.Host.Commands.Should().BeEmpty("no rm, no recursive delete, nothing but the marker");
        File.Exists(Path.Combine(fixture.DataDirectory, "world.sav")).Should().BeTrue();
        Directory.Exists(fixture.DataDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task CompensateAsync_removes_the_marker_even_when_create_never_reported_success()
    {
        using var fixture = new Fixture();
        var operation = fixture.Provisioner().CreateOperation(LocalProcessProvisioner.BuildSpec(fixture.Request()));

        await operation.CreateAsync();
        fixture.Host.ClearRecordings();

        await operation.CompensateAsync();

        fixture.Host.Order.Should().Equal($"delete:{fixture.MarkerPath}");
        File.Exists(fixture.MarkerPath).Should().BeFalse();
    }

    [Fact]
    public void The_operations_tags_are_readable_before_create_so_the_executor_can_commit_them_first()
    {
        using var fixture = new Fixture();
        var operation = fixture.Provisioner().CreateOperation(LocalProcessProvisioner.BuildSpec(fixture.Request()));

        operation.ProvisionerId.Should().Be("local-process");
        operation.Region.Should().BeNull();
        operation.Tags[ServyxProcessMarker.ManagedTag].Should().Be("true");
        operation.Tags[ServyxProcessMarker.InstanceIdTag].Should().Be("srv-0001");
        fixture.Host.Connected.Should().BeEmpty("reading the tags must not open a session");
        fixture.Temp.Snapshot().Should().BeEmpty("reading the tags must not create anything");
    }
}
