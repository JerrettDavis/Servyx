using NSubstitute;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Provisioning;

namespace Servyx.Infrastructure.Ssh.Tests.Provisioning;

/// <summary>
/// Unit tests for <see cref="SshProcessProvisioner"/>. Follows the house pattern: the SSH host is a
/// substituted <see cref="ITransport"/>/<see cref="IExecutionTarget"/> pair (see <see cref="SshHostDouble"/>),
/// so no live SSH server is involved anywhere.
/// </summary>
public class SshProcessProvisionerTests
{
    private const string Endpoint = "steam@palworld-host.internal:22";
    private const string MarkerRoot = "/var/lib/servyx/instances";
    private const string MarkerPath = MarkerRoot + "/srv-0001.servyx.json";

    /// <summary>
    /// The exact string an injection attempt would use. It appears in these tests as ordinary data — a
    /// directory path and a data directory — and must remain one inert argv element everywhere it goes.
    /// </summary>
    private const string Hostile = "; rm -rf /";

    /// <summary>
    /// A realistic request modelled on the <c>native-steamcmd</c> profile of
    /// <c>definitions/palworld-docker.yaml</c>, including its two install verbs verbatim.
    /// </summary>
    internal static ProvisioningRequest PalworldNativeRequest(IReadOnlyDictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instanceId"] = "srv-0001",
            ["jobId"] = "job-42",
            ["connectorId"] = "ssh-palworld",
            // executable: { linux: "./PalServer.sh" }
            ["executable"] = "./PalServer.sh",
            ["dataDir"] = "/opt/palworld",
            // install: [ { verb: steamcmd, appId: 2394010, validate: true } ]
            ["install:0:verb"] = "steamcmd",
            ["install:0:appId"] = "2394010",
            ["install:0:validate"] = "true",
            // install: [ ..., { verb: ensure-dir, path: "${DATA_DIR}/Pal/Saved/Config/LinuxServer" } ]
            ["install:1:verb"] = "ensure-dir",
            ["install:1:path"] = "/opt/palworld/Pal/Saved/Config/LinuxServer",
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

    internal static SshProcessProvisioner Provisioner(SshHostDouble host, string endpoint = Endpoint) =>
        new(host.Transport, endpoint, credentialUrn: null, transportOptions: null, markerRoot: MarkerRoot);

    /// <summary>Runs the real provisioner against a substituted host and returns what it handed back.</summary>
    internal static async Task<(ProvisionedResource Resource, SshHostDouble Host)> ProvisionAsync(
        IReadOnlyDictionary<string, string>? extra = null,
        string endpoint = Endpoint)
    {
        var host = new SshHostDouble();
        var provisioner = Provisioner(host, endpoint);
        var spec = SshProcessProvisioner.BuildSpec(PalworldNativeRequest(extra));

        var resource = await provisioner.CreateOperation(spec).CreateAsync();
        return (resource, host);
    }

    [Fact]
    public void ProvisionerId_is_ssh_process()
    {
        Provisioner(new SshHostDouble()).ProvisionerId.Should().Be("ssh-process");
    }

    [Fact]
    public void Capabilities_advertise_create_destroy_and_tag_query()
    {
        var capabilities = Provisioner(new SshHostDouble()).Capabilities;

        capabilities.Should().HaveFlag(ProvisioningCapabilities.Create);
        capabilities.Should().HaveFlag(ProvisioningCapabilities.Destroy);
        capabilities.Should().HaveFlag(ProvisioningCapabilities.TagQuery);
    }

    [Fact]
    public void Capabilities_do_not_claim_firewall_rules_because_nothing_here_touches_the_hosts_firewall()
    {
        // Docker's provisioner claims this bit because publishing a port is a real, implemented act. This one
        // installs files and nothing else, so claiming it would tell a caller a port had been opened when it
        // had not.
        Provisioner(new SshHostDouble()).Capabilities
            .Should().NotHaveFlag(ProvisioningCapabilities.FirewallRules);
    }

    [Fact]
    public void Capabilities_do_not_claim_cost_estimation_because_an_unrented_host_has_no_provider_price()
    {
        Provisioner(new SshHostDouble()).Capabilities
            .Should().NotHaveFlag(ProvisioningCapabilities.EstimatesCost);
    }

    [Fact]
    public void Constructor_rejects_a_transport_that_is_not_the_ssh_one()
    {
        var wrong = Substitute.For<ITransport>();
        wrong.TransportId.Returns("docker");

        var act = () => new SshProcessProvisioner(wrong, Endpoint);

        act.Should().Throw<ArgumentException>().WithMessage("*ssh*docker*");
    }

    [Fact]
    public void Constructor_rejects_an_unparseable_endpoint_rather_than_failing_halfway_through_an_install()
    {
        var host = new SshHostDouble();

        var act = () => new SshProcessProvisioner(host.Transport, "steam@:70000");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task PlanAsync_opens_no_connection_and_issues_no_command_so_planning_cannot_mutate_anything()
    {
        var host = new SshHostDouble();
        var provisioner = Provisioner(host);
        host.ClearRecordings();

        var plan = await provisioner.PlanAsync(PalworldNativeRequest());

        plan.Should().NotBeNull();
        host.Transport.ReceivedCalls().Should().BeEmpty();
        host.Session.ReceivedCalls().Should().BeEmpty();
        host.Connected.Should().BeEmpty();
        host.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_stages_correspond_to_the_definitions_install_verbs_in_execution_order()
    {
        var plan = await Provisioner(new SshHostDouble()).PlanAsync(PalworldNativeRequest());

        plan.Stages.Select(s => s.StageId).Should().Equal("write-marker", "install-0-steamcmd", "install-1-ensure-dir");
        plan.Stages.Should().OnlyContain(s => s.ProvisionerId == "ssh-process");
        plan.Stages[0].Description.Should().Contain(MarkerPath).And.Contain("srv-0001").And.Contain("job-42");
        plan.Stages[1].Description.Should().Contain("2394010").And.Contain("/opt/palworld").And.Contain("validating");
        plan.Stages[2].Description.Should().Contain("/opt/palworld/Pal/Saved/Config/LinuxServer");
    }

    [Fact]
    public async Task PlanAsync_does_not_fabricate_a_cost_for_a_host_servyx_did_not_rent()
    {
        var plan = await Provisioner(new SshHostDouble()).PlanAsync(PalworldNativeRequest());

        plan.EstimatedCost.Confidence.Should().Be(CostConfidence.Unknown);
        plan.EstimatedCost.Hourly.Should().BeNull();
        plan.EstimatedCost.Monthly.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_install_verb_is_rejected_at_plan_time_and_never_executed()
    {
        var host = new SshHostDouble();
        var provisioner = Provisioner(host);
        host.ClearRecordings();

        var act = () => provisioner.PlanAsync(PalworldNativeRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["install:2:verb"] = "curl-pipe-bash",
            ["install:2:path"] = "https://example.invalid/install.sh",
        }));

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*curl-pipe-bash*")
            .WithMessage("*steamcmd, ensure-dir*");

        // The point of rejecting at plan time rather than at execution time: nothing was reachable yet.
        host.Transport.ReceivedCalls().Should().BeEmpty();
        host.Session.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task An_install_entry_with_no_verb_is_rejected_at_plan_time()
    {
        var provisioner = Provisioner(new SshHostDouble());

        var act = () => provisioner.PlanAsync(PalworldNativeRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["install:2:path"] = "/opt/palworld/extra",
        }));

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*install:2:verb*");
    }

    [Fact]
    public void An_instance_id_that_would_escape_the_marker_root_is_rejected_when_the_spec_is_built()
    {
        var act = () => SshProcessProvisioner.BuildSpec(PalworldNativeRequest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instanceId"] = "../../etc/cron.d/servyx",
        }));

        act.Should().Throw<ArgumentException>().WithMessage("*marker filename*");
    }

    [Fact]
    public async Task Every_install_step_is_an_argv_array_never_a_shell_string()
    {
        var (_, host) = await ProvisionAsync();

        var steamcmd = host.Commands.Single(c => c.Executable == "steamcmd");

        steamcmd.Arguments.Should().Equal(
            "+force_install_dir", "/opt/palworld", "+login", "anonymous", "+app_update", "2394010", "validate", "+quit");

        // The definition's ensure-dir verb, plus the marker root Servyx ensures itself.
        host.Commands.Where(c => c.Executable == "mkdir").Select(c => c.Arguments[^1])
            .Should().Equal(MarkerRoot, "/opt/palworld/Pal/Saved/Config/LinuxServer");

        // No command anywhere carries shell syntax Servyx built: no operators, no joined tokens.
        host.Commands.Should().OnlyContain(c => !c.Executable.Contains(' '));
        host.Commands.SelectMany(c => c.Arguments).Should().OnlyContain(a => !a.Contains("&&") && !a.Contains("|"));
    }

    [Fact]
    public async Task A_hostile_path_stays_one_inert_argv_element_and_is_never_concatenated()
    {
        var (_, host) = await ProvisionAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dataDir"] = Hostile,
            ["install:1:path"] = Hostile,
        });

        var ensureDir = host.Commands.Last(c => c.Executable == "mkdir");
        ensureDir.Arguments.Should().Equal("-p", "--", Hostile);
        ensureDir.Arguments.Should().ContainSingle(a => a == Hostile);

        var steamcmd = host.Commands.Single(c => c.Executable == "steamcmd");
        steamcmd.Arguments.Should().Equal(
            "+force_install_dir", Hostile, "+login", "anonymous", "+app_update", "2394010", "validate", "+quit");

        // Nothing merged it into a larger token: it is either the whole element or absent.
        host.Commands.SelectMany(c => c.Arguments)
            .Should().OnlyContain(a => a == Hostile || !a.Contains("rm -rf"));
        host.Commands.Should().OnlyContain(c => c.Executable == "mkdir" || c.Executable == "steamcmd");
    }

    [Fact]
    public async Task The_one_place_a_shell_string_is_unavoidable_quotes_the_hostile_argument_inert()
    {
        // SSH exec carries a single command-line string, so PosixArgv is where an argv array finally becomes
        // text. This asserts the whole pipeline end to end: the provisioner emits argv, and the only code that
        // turns argv into a line makes the metacharacters literal.
        var (_, host) = await ProvisionAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["install:1:path"] = Hostile,
        });

        var ensureDir = host.Commands.Last(c => c.Executable == "mkdir");
        var commandLine = PosixArgv.BuildCommandLine(ensureDir.Executable, ensureDir.Arguments);

        commandLine.Should().Be("'mkdir' '-p' '--' '; rm -rf /'");
    }

    [Fact]
    public async Task The_marker_is_written_before_any_install_verb_runs()
    {
        // A container is labelled by the same atomic call that creates it. A marker file is a separate write,
        // so the equivalent guarantee has to be bought by ordering: after the marker exists, every later
        // failure still leaves something on the host that a sweep can find.
        var (_, host) = await ProvisionAsync();

        host.Order.Should().Equal(
            "exec:mkdir",               // ensure the marker root
            $"write:{MarkerPath}",      // marker, before any install verb
            "exec:steamcmd",
            "exec:mkdir");

        host.Order.IndexOf($"write:{MarkerPath}").Should().BeLessThan(host.Order.IndexOf("exec:steamcmd"));
    }

    [Fact]
    public async Task The_marker_records_the_instance_job_connector_and_root_path()
    {
        var (resource, host) = await ProvisionAsync();

        var tags = ServyxProcessMarker.Deserialize(host.Files[MarkerPath]);

        tags.Should().NotBeNull();
        tags![ServyxProcessMarker.ManagedTag].Should().Be("true");
        tags[ServyxProcessMarker.InstanceIdTag].Should().Be("srv-0001");
        tags[ServyxProcessMarker.JobIdTag].Should().Be("job-42");
        tags[ServyxProcessMarker.ConnectorIdTag].Should().Be("ssh-palworld");
        tags[ServyxProcessMarker.RootPathTag].Should().Be("/opt/palworld");
        tags[ServyxProcessMarker.ProvisionerIdTag].Should().Be("ssh-process");
        tags[ServyxProcessMarker.ExecutableTag].Should().Be("./PalServer.sh");

        // The handle the executor records carries exactly the tags that reached the host.
        resource.Handle.ProviderResourceId.Should().Be(MarkerPath);
        resource.Handle.Tags.Should().BeEquivalentTo(tags);
        resource.ConnectorId.Should().Be("ssh-palworld");
    }

    [Fact]
    public async Task An_extra_tag_can_never_shadow_a_mandatory_servyx_tag()
    {
        var (_, host) = await ProvisionAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"tag:{ServyxProcessMarker.ManagedTag}"] = "false",
            [$"tag:{ServyxProcessMarker.InstanceIdTag}"] = "somebody-elses-server",
            ["tag:team"] = "ops",
        });

        var tags = ServyxProcessMarker.Deserialize(host.Files[MarkerPath])!;

        tags[ServyxProcessMarker.ManagedTag].Should().Be("true");
        tags[ServyxProcessMarker.InstanceIdTag].Should().Be("srv-0001");
        tags["team"].Should().Be("ops");
    }

    [Fact]
    public async Task A_failing_install_step_aborts_the_install_and_leaves_the_marker_for_the_sweep_to_find()
    {
        var host = new SshHostDouble
        {
            ExecHandler = command => command.Executable == "steamcmd"
                ? new CommandResult(8, string.Empty, "steamcmd: disk full", TimeSpan.Zero)
                : new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero),
        };

        var operation = Provisioner(host).CreateOperation(SshProcessProvisioner.BuildSpec(PalworldNativeRequest()));

        var act = () => operation.CreateAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*install-0-steamcmd*")
            .WithMessage("*disk full*");

        // The marker was already on the host when the install died, so the half-finished install is still
        // discoverable — the whole reason the marker is written first.
        host.Files.Should().ContainKey(MarkerPath);
        (await Provisioner(host).ReconcileAsync(new OrphanScope.ProviderWide("ssh-process")))
            .Select(h => h.ProviderResourceId).Should().Equal(MarkerPath);

        // The second install verb never ran: a failing step aborts rather than continuing.
        host.Commands.Where(c => c.Executable == "mkdir").Should().ContainSingle();
    }

    [Fact]
    public async Task RefreshAsync_reads_the_marker_back_for_a_known_instance()
    {
        var (resource, host) = await ProvisionAsync();
        host.ClearRecordings();

        var refreshed = await Provisioner(host).RefreshAsync(resource.Handle);

        refreshed.Should().NotBeNull();
        refreshed!.Handle.ProviderResourceId.Should().Be(MarkerPath);
        refreshed.Handle.ProvisionerId.Should().Be("ssh-process");
        refreshed.Handle.Tags.Should().BeEquivalentTo(resource.Handle.Tags);
        refreshed.ConnectorId.Should().Be("ssh-palworld");
        refreshed.Facts.PrivateAddress.Should().Be("palworld-host.internal");
        refreshed.Facts.Cost.Confidence.Should().Be(CostConfidence.Unknown);
    }

    [Fact]
    public async Task RefreshAsync_returns_null_when_the_marker_is_gone()
    {
        var (resource, host) = await ProvisionAsync();
        host.Files.Remove(MarkerPath);

        var refreshed = await Provisioner(host).RefreshAsync(resource.Handle);

        refreshed.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_returns_null_for_a_file_that_is_not_servyx_managed()
    {
        var (resource, host) = await ProvisionAsync();
        host.Files[MarkerPath] = "{\"something\":\"else\"}"u8.ToArray();

        var refreshed = await Provisioner(host).RefreshAsync(resource.Handle);

        refreshed.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_discovers_every_marker_under_the_marker_root()
    {
        var (_, host) = await ProvisionAsync();
        host.PutFile(
            $"{MarkerRoot}/srv-0002.servyx.json",
            ServyxProcessMarker.Serialize(ServyxProcessMarker.For("srv-0002", "job-43", "ssh-palworld").ToTags()));

        var handles = await Provisioner(host).ReconcileAsync(new OrphanScope.ProviderWide("ssh-process"));

        handles.Select(h => h.ProviderResourceId).Should().Equal(
            $"{MarkerRoot}/srv-0001.servyx.json",
            $"{MarkerRoot}/srv-0002.servyx.json");
        handles.Should().OnlyContain(h => h.ProvisionerId == "ssh-process" && h.Region == null);
        handles[1].Tags[ServyxProcessMarker.InstanceIdTag].Should().Be("srv-0002");
    }

    [Fact]
    public async Task ReconcileAsync_never_reports_a_file_it_could_not_confirm_is_servyx_managed()
    {
        // The filename suffix is the cheap filter; re-reading servyx.managed is this process's own guarantee.
        // A sweep acting on a false positive deletes someone else's install.
        var (_, host) = await ProvisionAsync();
        host.PutFile($"{MarkerRoot}/not-ours.servyx.json", "{\"servyx.managed\":\"false\"}"u8.ToArray());
        host.PutFile($"{MarkerRoot}/garbage.servyx.json", "not json at all"u8.ToArray());
        host.PutFile($"{MarkerRoot}/readme.txt", "ignore me"u8.ToArray());

        var handles = await Provisioner(host).ReconcileAsync(new OrphanScope.ProviderWide("ssh-process"));

        handles.Select(h => h.ProviderResourceId).Should().Equal($"{MarkerRoot}/srv-0001.servyx.json");
    }

    [Fact]
    public async Task ReconcileAsync_ignores_a_scope_that_belongs_to_another_provisioner()
    {
        var (_, host) = await ProvisionAsync();
        host.ClearRecordings();

        var handles = await Provisioner(host).ReconcileAsync(new OrphanScope.ProviderWide("docker-container"));

        handles.Should().BeEmpty();
        host.Connected.Should().BeEmpty("a sweep for someone else's provisioner must not even connect");
    }

    [Fact]
    public async Task ReconcileAsync_reports_no_orphans_on_a_host_that_has_never_been_installed_to()
    {
        var host = new SshHostDouble();

        var handles = await Provisioner(host).ReconcileAsync(new OrphanScope.ProviderWide("ssh-process"));

        handles.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_sweeps_the_marker_root_the_scope_names_rather_than_the_constructed_one()
    {
        // The defect this closes: the swept directory used to live only in constructor state, so a caller
        // holding an IProvisioner and an OrphanScope could not see what a sweep would cover.
        const string OtherRoot = "/srv/servyx/instances";

        var (_, host) = await ProvisionAsync();
        host.PutFile(
            $"{OtherRoot}/srv-0009.servyx.json",
            ServyxProcessMarker.Serialize(ServyxProcessMarker.For("srv-0009", "job-99", "ssh-palworld").ToTags()));

        var handles = await Provisioner(host).ReconcileAsync(new OrphanScope.MarkerDirectory("ssh-process", OtherRoot));

        handles.Select(h => h.ProviderResourceId).Should().Equal($"{OtherRoot}/srv-0009.servyx.json");
        handles[0].Tags[ServyxProcessMarker.InstanceIdTag].Should().Be("srv-0009");

        // The constructed root was genuinely not swept — its marker is still there and simply out of scope.
        host.Files.Should().ContainKey(MarkerPath);
    }

    [Fact]
    public async Task ReconcileAsync_falls_back_to_the_constructed_marker_root_when_the_scope_names_none()
    {
        var (_, host) = await ProvisionAsync();
        host.PutFile(
            "/srv/servyx/instances/srv-0009.servyx.json",
            ServyxProcessMarker.Serialize(ServyxProcessMarker.For("srv-0009", "job-99", "ssh-palworld").ToTags()));

        var handles = await Provisioner(host).ReconcileAsync(new OrphanScope.ProviderWide("ssh-process"));

        handles.Select(h => h.ProviderResourceId).Should().Equal(MarkerPath);
    }

    [Fact]
    public async Task A_scope_supplied_marker_root_is_normalised_the_same_way_a_constructed_one_is()
    {
        const string OtherRoot = "/srv/servyx/instances";

        var host = new SshHostDouble();
        host.PutFile(
            $"{OtherRoot}/srv-0009.servyx.json",
            ServyxProcessMarker.Serialize(ServyxProcessMarker.For("srv-0009", "job-99", "ssh-palworld").ToTags()));

        var handles = await Provisioner(host).ReconcileAsync(new OrphanScope.MarkerDirectory("ssh-process", OtherRoot + "/"));

        handles.Select(h => h.ProviderResourceId).Should().Equal($"{OtherRoot}/srv-0009.servyx.json");
    }

    [Theory]
    [InlineData("relative/instances")]
    [InlineData("/srv\\servyx")]
    [InlineData("C:/servyx/instances")]
    public async Task A_scope_supplied_marker_root_faces_the_same_validation_as_a_constructed_one(string markerRoot)
    {
        // A scope is not a route around the rule. The check runs before any connection is opened, so a
        // malformed root cannot get as far as listing a directory on the host.
        var host = new SshHostDouble();
        var provisioner = Provisioner(host);

        var act = () => provisioner.ReconcileAsync(new OrphanScope.MarkerDirectory("ssh-process", markerRoot));

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*absolute POSIX directory path*");
        host.Connected.Should().BeEmpty();
    }

    [Fact]
    public async Task A_scope_supplied_marker_root_never_changes_where_an_install_writes_its_marker()
    {
        // Reading a different directory is safe; writing to one is not. A request able to relocate its own
        // marker could place an install outside the directory a sweep covers.
        var (_, host) = await ProvisionAsync();

        host.Files.Should().ContainKey(MarkerPath);
        Provisioner(host).MarkerRoot.Should().Be(MarkerRoot);
    }

    [Fact]
    public async Task DestroyAsync_removes_the_marker_and_reports_whether_it_was_there()
    {
        var (resource, host) = await ProvisionAsync();
        var provisioner = Provisioner(host);

        var first = await provisioner.DestroyAsync(resource.Handle);
        var second = await provisioner.DestroyAsync(resource.Handle);

        first.Should().BeTrue();
        second.Should().BeFalse();
        host.Deleted.Should().Equal(MarkerPath);
        host.Files.Should().NotContainKey(MarkerPath);
    }

    [Fact]
    public async Task DestroyAsync_deliberately_leaves_the_data_directory_alone()
    {
        // Symmetric with the Docker provisioner's RemoveVolumes: false — destroying the Servyx handle to a
        // workload must never destroy a user's saves as a side effect.
        var (resource, host) = await ProvisionAsync();
        host.ClearRecordings();

        await Provisioner(host).DestroyAsync(resource.Handle);

        host.Deleted.Should().Equal(MarkerPath);
        host.Commands.Should().BeEmpty("no rm, no recursive delete, nothing but the marker");
    }

    [Fact]
    public async Task CompensateAsync_removes_the_marker_even_when_create_never_reported_success()
    {
        var host = new SshHostDouble();
        var provisioner = Provisioner(host);
        var operation = provisioner.CreateOperation(SshProcessProvisioner.BuildSpec(PalworldNativeRequest()));

        await operation.CreateAsync();
        host.ClearRecordings();

        await operation.CompensateAsync();

        host.Deleted.Should().Equal(MarkerPath);
        host.Files.Should().NotContainKey(MarkerPath);
    }

    [Fact]
    public void The_operations_tags_are_readable_before_create_so_the_executor_can_commit_them_first()
    {
        var host = new SshHostDouble();
        var operation = Provisioner(host).CreateOperation(SshProcessProvisioner.BuildSpec(PalworldNativeRequest()));

        operation.ProvisionerId.Should().Be("ssh-process");
        operation.Region.Should().BeNull();
        operation.Tags[ServyxProcessMarker.ManagedTag].Should().Be("true");
        operation.Tags[ServyxProcessMarker.InstanceIdTag].Should().Be("srv-0001");
        host.Connected.Should().BeEmpty("reading the tags must not open a connection");
    }
}
