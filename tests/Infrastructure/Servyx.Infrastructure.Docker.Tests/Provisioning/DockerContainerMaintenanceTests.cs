using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Docker.Provisioning;

namespace Servyx.Infrastructure.Docker.Tests.Provisioning;

/// <summary>
/// Unit tests for <see cref="DockerContainerProvisioner"/>'s <see cref="IMaintainer"/> half — update
/// planning and drift detection. Same house pattern as
/// <see cref="DockerContainerProvisionerTests"/>: the Docker Engine is an NSubstitute-substituted
/// <see cref="IDockerClient"/>, so no live daemon is involved and every engine call the code under test
/// makes is countable.
/// </summary>
/// <remarks>
/// The negative these tests exist for is the same one <c>PlanAsync_issues_no_docker_call_at_all</c> pins
/// for creation, restated for a strictly harder case: creation planning is pure computation with no call to
/// audit, whereas update planning <em>must</em> read the live container, so "changes nothing" here is a
/// claim about which calls it makes rather than about making none.
/// </remarks>
public class DockerContainerMaintenanceTests
{
    private const string Endpoint = "npipe://./pipe/dockerDesktopLinuxEngine";
    private const string CurrentImage = "thijsvanloef/palworld-server-docker:latest";
    private const string ContainerId = "container-1";

    private static (IDockerClient Client, IContainerOperations Containers) SubstituteClient()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);
        client.ClearReceivedCalls();
        containers.ClearReceivedCalls();
        return (client, containers);
    }

    /// <summary>
    /// The label set this provisioner stamps on a container created from <paramref name="image"/> for the
    /// Palworld request — i.e. what the ledger's <see cref="ResourceHandle.Tags"/> holds for it.
    /// </summary>
    private static IReadOnlyDictionary<string, string> StampedLabels(string image = CurrentImage) =>
        ServyxResourceTags.For("srv-0001", "job-42", "docker-local").ToLabels(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ServyxResourceTags.RootPathLabel] = "/palworld",
                [ServyxResourceTags.ImageLabel] = image,
            });

    /// <summary>The ports the Palworld request asks for, as the engine reports them on a live container.</summary>
    private static readonly (string Key, string? HostPort)[] MatchingPorts =
    [
        ("8211/udp", "8211"),
        ("27015/udp", "27015"),
        ("25575/tcp", null),
        ("8212/tcp", null),
    ];

    /// <summary>The mount the Palworld request asks for, as the engine reports it on a live container.</summary>
    private static readonly (string Host, string Container, bool ReadWrite)[] MatchingMounts =
    [
        (@"C:\srv\palworld", "/palworld", true),
    ];

    /// <summary>
    /// An inspect response for a live container. Defaults describe a container that exactly matches
    /// <see cref="DockerContainerProvisionerTests.PalworldRequest"/>, so any single override is the only
    /// difference a plan can find.
    /// </summary>
    private static ContainerInspectResponse LiveContainer(
        string image = CurrentImage,
        IReadOnlyDictionary<string, string>? labels = null,
        IReadOnlyList<(string Key, string? HostPort)>? ports = null,
        IReadOnlyList<(string Host, string Container, bool ReadWrite)>? mounts = null)
    {
        var exposed = new Dictionary<string, EmptyStruct>(StringComparer.Ordinal);
        var bindings = new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal);

        foreach (var (key, hostPort) in ports ?? MatchingPorts)
        {
            exposed[key] = default;
            if (hostPort is not null)
            {
                bindings[key] = [new PortBinding { HostPort = hostPort }];
            }
        }

        return new ContainerInspectResponse
        {
            ID = ContainerId,
            Name = "/palworld-server",
            Created = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc),
            Config = new Config
            {
                Image = image,
                Labels = new Dictionary<string, string>(labels ?? StampedLabels(image), StringComparer.Ordinal),
                ExposedPorts = exposed,
            },
            HostConfig = new HostConfig { PortBindings = bindings },
            Mounts = [.. (mounts ?? MatchingMounts).Select(m => new MountPoint
            {
                Type = "bind",
                Source = m.Host,
                Destination = m.Container,
                RW = m.ReadWrite,
            })],
            NetworkSettings = new NetworkSettings { IPAddress = "172.18.0.2" },
            State = new ContainerState { Status = "running", Running = true },
        };
    }

    private static DockerContainerProvisioner ProvisionerOver(
        IContainerOperations containers,
        IDockerClient client,
        ContainerInspectResponse? inspect)
    {
        if (inspect is null)
        {
            containers
                .InspectContainerAsync(ContainerId, Arg.Any<CancellationToken>())
                .Returns<Task<ContainerInspectResponse>>(_ => throw new DockerContainerNotFoundException(HttpStatusCode.NotFound, "no such container"));
        }
        else
        {
            containers
                .InspectContainerAsync(ContainerId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(inspect));
        }

        return new DockerContainerProvisioner(client, Endpoint);
    }

    private static ResourceHandle Handle(IReadOnlyDictionary<string, string>? tags = null) =>
        new("docker-container", ContainerId, null, tags ?? StampedLabels());

    // ── Capabilities ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_declare_recreate_to_update_and_never_claim_update_in_place()
    {
        // The engine has no call that edits a live container's image, port bindings, or labels. Claiming the
        // in-place bit would tell a caller it can avoid the downtime and the new container id that every
        // update here actually costs.
        var (client, _) = SubstituteClient();

        var capabilities = new DockerContainerProvisioner(client).Capabilities;

        capabilities.Should().HaveFlag(ProvisioningCapabilities.RecreateToUpdate);
        capabilities.Should().HaveFlag(ProvisioningCapabilities.DetectDrift);
        capabilities.Should().NotHaveFlag(ProvisioningCapabilities.UpdateInPlace);
    }

    [Fact]
    public void The_provisioner_is_reachable_as_a_maintainer_naming_the_same_provisioner_id()
    {
        var (client, _) = SubstituteClient();

        IMaintainer maintainer = new DockerContainerProvisioner(client);

        maintainer.ProvisionerId.Should().Be("docker-container");
    }

    // ── Update planning issues no mutating call ──────────────────────────────────────────────────

    [Fact]
    public async Task PlanUpdateAsync_issues_exactly_one_inspect_and_no_mutating_docker_call()
    {
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer(image: "thijsvanloef/palworld-server-docker:v1.2.3"));

        var plan = await provisioner.PlanUpdateAsync(Handle(), DockerContainerProvisionerTests.PalworldRequest());

        plan.Should().NotBeNull();

        // Planning reads. It does not create, start, stop, restart, or remove anything — and the assertion
        // is on the whole call log, so a mutating call added later cannot slip past an enumerated list.
        containers.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Should().AllBe(nameof(IContainerOperations.InspectContainerAsync));
        containers.ReceivedCalls().Should().HaveCount(1);

        await containers.DidNotReceive().CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>());
        await containers.DidNotReceive().StartContainerAsync(Arg.Any<string>(), Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>());
        await containers.DidNotReceive().StopContainerAsync(Arg.Any<string>(), Arg.Any<ContainerStopParameters>(), Arg.Any<CancellationToken>());
        await containers.DidNotReceive().RestartContainerAsync(Arg.Any<string>(), Arg.Any<ContainerRestartParameters>(), Arg.Any<CancellationToken>());
        await containers.DidNotReceive().RemoveContainerAsync(Arg.Any<string>(), Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlanUpdateAsync_returns_null_for_a_container_the_engine_no_longer_knows_about()
    {
        // Not "nothing needs to change": there is nothing to update, and inventing a create-from-scratch
        // plan would quietly turn an update preview into a provisioning one.
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, inspect: null);

        var plan = await provisioner.PlanUpdateAsync(Handle(), DockerContainerProvisionerTests.PalworldRequest());

        plan.Should().BeNull();
    }

    // ── The recreate plan ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_changed_image_tag_produces_a_recreate_plan_whose_stages_name_the_old_and_new_image()
    {
        const string oldImage = "thijsvanloef/palworld-server-docker:v1.2.3";
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer(image: oldImage));

        var plan = await provisioner.PlanUpdateAsync(Handle(StampedLabels(oldImage)), DockerContainerProvisionerTests.PalworldRequest());

        plan.Should().NotBeNull();
        plan!.Strategy.Should().Be(UpdateStrategy.Recreate);
        plan.ProvisionerId.Should().Be("docker-container");

        plan.Changes.Should().ContainSingle(c => c.Aspect == "image")
            .Which.Should().BeEquivalentTo(new { Current = oldImage, Desired = CurrentImage, RequiresRecreate = true });

        plan.Stages.Select(s => s.StageId).Should().Equal(
            "stop-container", "remove-container", "create-container", "reattach-volumes", "publish-ports", "start-container");
        plan.Stages.Should().OnlyContain(s => s.ProvisionerId == "docker-container");

        var create = plan.Stages.Single(s => s.StageId == "create-container");
        create.Description.Should().Contain(CurrentImage, "the plan must name the image it would create from")
            .And.Contain(oldImage, "the plan must name the image being replaced");
    }

    [Fact]
    public async Task A_changed_published_port_also_forces_a_recreate_because_bindings_are_fixed_at_create_time()
    {
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer(ports:
        [
            ("8211/udp", "9999"),
            ("27015/udp", "27015"),
            ("25575/tcp", null),
            ("8212/tcp", null),
        ]));

        var plan = await provisioner.PlanUpdateAsync(Handle(), DockerContainerProvisionerTests.PalworldRequest());

        plan!.Strategy.Should().Be(UpdateStrategy.Recreate);
        var change = plan.Changes.Should().ContainSingle(c => c.Aspect == "ports").Which;
        change.RequiresRecreate.Should().BeTrue();
        change.Current.Should().Contain("8211/udp->9999");
        change.Desired.Should().Contain("8211/udp->8211");
    }

    [Fact]
    public async Task A_changed_servyx_label_forces_a_recreate_and_is_named_by_its_key()
    {
        var drifted = new Dictionary<string, string>(StampedLabels(), StringComparer.Ordinal)
        {
            [ServyxResourceTags.JobIdLabel] = "job-oldest",
        };

        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer(labels: drifted));

        var plan = await provisioner.PlanUpdateAsync(Handle(), DockerContainerProvisionerTests.PalworldRequest());

        plan!.Strategy.Should().Be(UpdateStrategy.Recreate);
        plan.Changes.Should().ContainSingle(c => c.Aspect == $"label {ServyxResourceTags.JobIdLabel}")
            .Which.Should().BeEquivalentTo(new { Current = "job-oldest", Desired = "job-42", RequiresRecreate = true });
    }

    [Fact]
    public async Task A_container_already_in_the_desired_state_yields_a_plan_that_would_do_nothing()
    {
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer());

        var plan = await provisioner.PlanUpdateAsync(Handle(), DockerContainerProvisionerTests.PalworldRequest());

        plan!.Strategy.Should().Be(UpdateStrategy.NoChangeRequired);
        plan.Changes.Should().BeEmpty();
        plan.Stages.Should().BeEmpty();
        plan.DataImpact.Should().Be(DataImpact.Preserved, "nothing would run, so nothing can happen to the data");
    }

    // ── Data impact ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_plan_states_a_data_impact_and_carries_every_live_mount_across_the_recreate()
    {
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer(image: "thijsvanloef/palworld-server-docker:v1.2.3"));

        var plan = await provisioner.PlanUpdateAsync(Handle(), DockerContainerProvisionerTests.PalworldRequest());

        // Explicitly asserted, and asserted to a defined value — the zero value is not constructible.
        Enum.IsDefined(plan!.DataImpact).Should().BeTrue();
        plan.DataImpact.Should().Be(DataImpact.Preserved);

        // The volume is reattached rather than dropped, and the removal stage says why the data survives it.
        var reattach = plan.Stages.Single(s => s.StageId == "reattach-volumes");
        reattach.Description.Should().Contain(@"C:\srv\palworld").And.Contain("/palworld").And.Contain("rw");
        reattach.Description.Should().Contain("carried over unchanged");
        reattach.Description.Should().Contain(nameof(DataImpact.Preserved));

        plan.Stages.Single(s => s.StageId == "remove-container").Description
            .Should().Contain("RemoveVolumes").And.Contain("Volumes are never removed");
    }

    [Fact]
    public async Task A_recreate_of_a_container_holding_its_state_in_the_writable_layer_is_reported_at_risk()
    {
        // No mounts means everything the workload wrote is in the writable layer, and removing the container
        // removes that layer. Preserved here would be a lie produced by "the adapter deletes nothing".
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer(image: "old:1", mounts: []));

        var plan = await provisioner.PlanUpdateAsync(Handle(), DockerContainerProvisionerTests.PalworldRequest());

        plan!.DataImpact.Should().Be(DataImpact.AtRisk);
        plan.Stages.Single(s => s.StageId == "reattach-volumes").Description
            .Should().Contain("writable layer being discarded");
    }

    [Fact]
    public async Task A_recreate_that_would_drop_a_live_mount_is_reported_at_risk_and_names_the_dropped_mount()
    {
        // The bytes stay on the host, but the replacement comes up without them. That is not preservation.
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer(
            image: "old:1",
            mounts:
            [
                (@"C:\srv\palworld", "/palworld", true),
                (@"C:\srv\palworld-backups", "/backups", true),
            ]));

        var plan = await provisioner.PlanUpdateAsync(Handle(), DockerContainerProvisionerTests.PalworldRequest());

        plan!.DataImpact.Should().Be(DataImpact.AtRisk);
        plan.Stages.Single(s => s.StageId == "reattach-volumes").Description
            .Should().Contain("/backups", "the operator has to be told which mount is being left behind");
    }

    [Fact]
    public async Task A_recreate_that_remaps_a_live_mount_to_a_different_host_path_is_reported_at_risk()
    {
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer(
            image: "old:1",
            mounts: [(@"C:\srv\somewhere-else", "/palworld", true)]));

        var plan = await provisioner.PlanUpdateAsync(Handle(), DockerContainerProvisionerTests.PalworldRequest());

        plan!.DataImpact.Should().Be(DataImpact.AtRisk);
    }

    [Fact]
    public async Task No_plan_this_adapter_can_produce_ever_claims_it_would_destroy_data()
    {
        // Not a stylistic assertion: the removal stage never sets RemoveVolumes, so Destroyed would
        // overstate what the adapter does. It stays reachable in the enum for an adapter that genuinely
        // deletes a volume one day.
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer(image: "old:1", mounts: []));

        var plan = await provisioner.PlanUpdateAsync(Handle(), DockerContainerProvisionerTests.PalworldRequest());

        plan!.DataImpact.Should().NotBe(DataImpact.Destroyed);
    }

    [Fact]
    public async Task An_update_plan_hash_is_deterministic_and_moves_when_the_live_state_moves()
    {
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer(image: "old:1"));

        var first = await provisioner.PlanUpdateAsync(Handle(), DockerContainerProvisionerTests.PalworldRequest());
        var second = await provisioner.PlanUpdateAsync(Handle(), DockerContainerProvisionerTests.PalworldRequest());

        var (otherClient, otherContainers) = SubstituteClient();
        var moved = ProvisionerOver(otherContainers, otherClient, LiveContainer(image: "old:2"));
        var third = await moved.PlanUpdateAsync(Handle(), DockerContainerProvisionerTests.PalworldRequest());

        second!.PlanHash.Should().Be(first!.PlanHash);
        third!.PlanHash.Should().NotBe(first.PlanHash);
    }

    // ── Drift detection ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DetectDriftAsync_reports_a_match_for_an_untouched_container()
    {
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer());

        var drift = await provisioner.DetectDriftAsync(Handle());

        drift.Matches.Should().BeTrue();
        drift.Divergences.Should().BeEmpty();
        drift.Summary.Should().Contain("matches the resource Servyx provisioned");
    }

    [Fact]
    public async Task DetectDriftAsync_names_the_image_divergence_for_a_container_recreated_from_a_different_tag()
    {
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer(
            image: "nginx:1.27",
            labels: StampedLabels("nginx:1.25")));

        var drift = await provisioner.DetectDriftAsync(Handle(StampedLabels("nginx:1.25")));

        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle()
            .Which.Description.Should().Be("image: expected nginx:1.25, found nginx:1.27");
    }

    [Fact]
    public async Task DetectDriftAsync_names_a_relabelled_container_by_the_label_key_that_changed()
    {
        var live = new Dictionary<string, string>(StampedLabels(), StringComparer.Ordinal)
        {
            [ServyxResourceTags.InstanceIdLabel] = "srv-9999",
        };

        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer(labels: live));

        var drift = await provisioner.DetectDriftAsync(Handle());

        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle()
            .Which.Description.Should().Be($"label {ServyxResourceTags.InstanceIdLabel}: expected srv-0001, found srv-9999");
    }

    [Fact]
    public async Task DetectDriftAsync_reports_a_vanished_container_as_drift_rather_than_as_no_answer()
    {
        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, inspect: null);

        var drift = await provisioner.DetectDriftAsync(Handle());

        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle().Which.Aspect.Should().Be("existence");
    }

    [Fact]
    public async Task DetectDriftAsync_will_not_claim_a_match_it_cannot_prove()
    {
        // A handle recorded before servyx.image existed holds no expectation for the image. Reporting
        // "matches" would be asserting agreement with a record that says nothing.
        var withoutImage = new Dictionary<string, string>(StampedLabels(), StringComparer.Ordinal);
        withoutImage.Remove(ServyxResourceTags.ImageLabel);

        var (client, containers) = SubstituteClient();
        var provisioner = ProvisionerOver(containers, client, LiveContainer(labels: withoutImage));

        var drift = await provisioner.DetectDriftAsync(Handle(withoutImage));

        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle()
            .Which.Description.Should().Be($"image: Servyx recorded no expected value, found {CurrentImage}");
    }

    [Fact]
    public async Task DetectDriftAsync_refuses_another_provisioners_handle_without_touching_the_engine()
    {
        var (client, containers) = SubstituteClient();
        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var drift = await provisioner.DetectDriftAsync(
            new ResourceHandle("hetzner", "srv-77", "nbg1", new Dictionary<string, string>()));

        // Reported as a divergence, not as a match: "this is not my resource" is not evidence it is intact.
        drift.Matches.Should().BeFalse();
        drift.Divergences.Should().ContainSingle().Which.Aspect.Should().Be("provisioner");
        containers.ReceivedCalls().Should().BeEmpty();
    }

    // ── Wiring stays behind the same gate as creation ────────────────────────────────────────────

    [Fact]
    public void The_opt_in_provisioning_registration_publishes_the_maintainer_alongside_the_provisioner()
    {
        var services = new ServiceCollection();

        services.AddServyxDockerProvisioning(Endpoint);

        services.Should().Contain(d => d.ServiceType == typeof(IProvisioner));
        services.Should().Contain(d => d.ServiceType == typeof(IMaintainer));
    }

    [Fact]
    public void A_composition_root_that_never_opts_in_has_no_maintainer_at_all()
    {
        // Servyx:Provisioning:Enabled=false means Program.cs never calls AddServyxDockerProvisioning(), so
        // maintenance is behind exactly the same flag as creation — there is no object in the process that
        // can even be asked to plan an update, let alone apply one.
        var services = new ServiceCollection();

        services.AddServyxDocker();

        services.Should().NotContain(d => d.ServiceType == typeof(IMaintainer));
        services.Should().NotContain(d => d.ServiceType == typeof(IProvisioner));
        services.Should().NotContain(d => d.ServiceType == typeof(DockerContainerProvisioner));
    }
}
