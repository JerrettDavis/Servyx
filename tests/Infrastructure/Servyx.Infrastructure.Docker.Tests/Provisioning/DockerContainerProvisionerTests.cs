using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using NSubstitute;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Docker.Provisioning;

namespace Servyx.Infrastructure.Docker.Tests.Provisioning;

/// <summary>
/// Unit tests for <see cref="DockerContainerProvisioner"/>. Follows the house pattern: the Docker Engine
/// is an NSubstitute-substituted <see cref="IDockerClient"/>, so no live daemon is involved anywhere.
/// </summary>
public class DockerContainerProvisionerTests
{
    private const string Endpoint = "npipe://./pipe/dockerDesktopLinuxEngine";

    private static (IDockerClient Client, IContainerOperations Containers) SubstituteClient()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);
        client.ClearReceivedCalls();
        containers.ClearReceivedCalls();
        return (client, containers);
    }

    /// <summary>A realistic request modelled on the <c>docker-thijsvanloef</c> profile of <c>definitions/palworld-docker.yaml</c>.</summary>
    internal static ProvisioningRequest PalworldRequest(IReadOnlyDictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["image"] = "thijsvanloef/palworld-server-docker:latest",
            ["containerName"] = "palworld-server",
            ["instanceId"] = "srv-0001",
            ["jobId"] = "job-42",
            ["connectorId"] = "docker-local",
            ["rootPath"] = "/palworld",
            ["restartPolicy"] = "unless-stopped",
            // capabilities.network — published: true for game/query, false for rcon/rest.
            ["port:8211/udp"] = "8211",
            ["port:27015/udp"] = "27015",
            ["port:25575/tcp"] = "",
            ["port:8212/tcp"] = "",
            // capabilities.filesystem — ${DATA_DIR} with access: rw.
            ["volume:/palworld"] = @"C:\srv\palworld|rw",
            ["env:SERVER_NAME"] = "Servyx Test Server",
            ["env:PORT"] = "8211",
        };

        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                parameters[pair.Key] = pair.Value;
            }
        }

        return new ProvisioningRequest("palworld", "docker-thijsvanloef", ConnectorId: null, parameters);
    }

    [Fact]
    public void ProvisionerId_is_docker_container()
    {
        var (client, _) = SubstituteClient();

        new DockerContainerProvisioner(client).ProvisionerId.Should().Be("docker-container");
    }

    [Fact]
    public void Capabilities_advertise_create_destroy_firewall_and_tag_query()
    {
        var (client, _) = SubstituteClient();

        var capabilities = new DockerContainerProvisioner(client).Capabilities;

        capabilities.Should().HaveFlag(ProvisioningCapabilities.Create);
        capabilities.Should().HaveFlag(ProvisioningCapabilities.Destroy);
        capabilities.Should().HaveFlag(ProvisioningCapabilities.FirewallRules);
        capabilities.Should().HaveFlag(ProvisioningCapabilities.TagQuery);
    }

    [Fact]
    public void Capabilities_do_not_claim_cost_estimation_because_a_local_container_has_no_provider_price()
    {
        var (client, _) = SubstituteClient();

        new DockerContainerProvisioner(client).Capabilities
            .Should().NotHaveFlag(ProvisioningCapabilities.EstimatesCost);
    }

    [Fact]
    public async Task PlanAsync_issues_no_docker_call_at_all_so_planning_cannot_mutate_anything()
    {
        var (client, containers) = SubstituteClient();
        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var plan = await provisioner.PlanAsync(PalworldRequest());

        plan.Should().NotBeNull();
        client.ReceivedCalls().Should().BeEmpty();
        containers.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_describes_the_container_ports_mounts_and_labels_without_fabricating_a_cost()
    {
        var (client, _) = SubstituteClient();
        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var plan = await provisioner.PlanAsync(PalworldRequest());

        plan.Stages.Select(s => s.StageId).Should().Equal("create-container", "publish-ports", "start-container");
        plan.Stages.Should().OnlyContain(s => s.ProvisionerId == "docker-container");
        plan.Stages[0].Description.Should().Contain("thijsvanloef/palworld-server-docker:latest").And.Contain("palworld-server");
        plan.Stages[1].Description.Should().Contain("8211->8211/udp").And.Contain("27015->27015/udp");
        plan.Stages[1].Description.Should().NotContain("25575");
        plan.EstimatedCost.Confidence.Should().Be(CostConfidence.Unknown);
        plan.EstimatedCost.Hourly.Should().BeNull();
        plan.EstimatedCost.Monthly.Should().BeNull();
    }

    [Fact]
    public async Task PlanAsync_is_deterministic_so_a_plan_hash_can_detect_input_drift()
    {
        var (client, _) = SubstituteClient();
        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var first = await provisioner.PlanAsync(PalworldRequest());
        var second = await provisioner.PlanAsync(PalworldRequest());
        var different = await provisioner.PlanAsync(PalworldRequest(new Dictionary<string, string> { ["env:SERVER_NAME"] = "Different" }));

        second.PlanHash.Should().Be(first.PlanHash);
        different.PlanHash.Should().NotBe(first.PlanHash);
    }

    [Fact]
    public async Task Creating_a_container_through_the_public_path_always_applies_the_three_mandatory_servyx_labels()
    {
        var (client, containers) = SubstituteClient();
        CreateContainerParameters? captured = null;
        containers
            .CreateContainerAsync(Arg.Do<CreateContainerParameters>(p => captured = p), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CreateContainerResponse { ID = "container-1" }));

        var provisioner = new DockerContainerProvisioner(client, Endpoint);
        var spec = DockerContainerProvisioner.BuildSpec(PalworldRequest());

        await provisioner.CreateOperation(spec).CreateAsync();

        captured.Should().NotBeNull();
        captured!.Labels.Should().ContainKey(ServyxResourceTags.ManagedLabel).WhoseValue.Should().Be("true");
        captured.Labels.Should().ContainKey(ServyxResourceTags.InstanceIdLabel).WhoseValue.Should().Be("srv-0001");
        captured.Labels.Should().ContainKey(ServyxResourceTags.JobIdLabel).WhoseValue.Should().Be("job-42");
    }

    [Fact]
    public async Task An_extra_label_can_never_shadow_a_mandatory_servyx_label()
    {
        var (client, containers) = SubstituteClient();
        CreateContainerParameters? captured = null;
        containers
            .CreateContainerAsync(Arg.Do<CreateContainerParameters>(p => captured = p), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CreateContainerResponse { ID = "container-1" }));

        var provisioner = new DockerContainerProvisioner(client, Endpoint);
        var spec = DockerContainerProvisioner.BuildSpec(PalworldRequest(new Dictionary<string, string>
        {
            [$"label:{ServyxResourceTags.ManagedLabel}"] = "false",
            [$"label:{ServyxResourceTags.InstanceIdLabel}"] = "spoofed",
            [$"label:{ServyxResourceTags.JobIdLabel}"] = "spoofed",
        }));

        await provisioner.CreateOperation(spec).CreateAsync();

        captured!.Labels[ServyxResourceTags.ManagedLabel].Should().Be("true");
        captured.Labels[ServyxResourceTags.InstanceIdLabel].Should().Be("srv-0001");
        captured.Labels[ServyxResourceTags.JobIdLabel].Should().Be("job-42");
    }

    [Fact]
    public void A_resource_tag_set_cannot_be_constructed_without_an_instance_id_and_a_job_id()
    {
        var withoutInstanceId = () => ServyxResourceTags.For("  ", "job-42", "docker-local");
        var withoutJobId = () => ServyxResourceTags.For("srv-0001", "", "docker-local");

        withoutInstanceId.Should().Throw<ArgumentException>();
        withoutJobId.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Building_a_spec_without_an_instance_id_or_job_id_is_rejected_rather_than_defaulted()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["image"] = "img",
            ["containerName"] = "name",
            ["connectorId"] = "docker-local",
        };

        var act = () => DockerContainerProvisioner.BuildSpec(new ProvisioningRequest("g", "p", null, parameters));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task RefreshAsync_returns_null_when_the_container_is_gone()
    {
        var (client, containers) = SubstituteClient();
        containers
            .InspectContainerAsync("missing", Arg.Any<CancellationToken>())
            .Returns<Task<ContainerInspectResponse>>(_ => throw new DockerContainerNotFoundException(HttpStatusCode.NotFound, "no such container"));

        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var refreshed = await provisioner.RefreshAsync(
            new ResourceHandle("docker-container", "missing", null, new Dictionary<string, string>()));

        refreshed.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_returns_null_when_the_engine_reports_404_without_a_typed_exception()
    {
        var (client, containers) = SubstituteClient();
        containers
            .InspectContainerAsync("missing", Arg.Any<CancellationToken>())
            .Returns<Task<ContainerInspectResponse>>(_ => throw new DockerApiException(HttpStatusCode.NotFound, "{}"));

        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var refreshed = await provisioner.RefreshAsync(
            new ResourceHandle("docker-container", "missing", null, new Dictionary<string, string>()));

        refreshed.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_rebuilds_the_resource_from_the_live_container()
    {
        var (client, containers) = SubstituteClient();
        containers
            .InspectContainerAsync("container-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(InspectResponse("container-1", "/palworld-server")));

        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var refreshed = await provisioner.RefreshAsync(
            new ResourceHandle("docker-container", "container-1", null, new Dictionary<string, string>()));

        refreshed.Should().NotBeNull();
        refreshed!.ConnectorId.Should().Be("docker-local");
        refreshed.RequireTarget().Options["containerId"].Should().Be("container-1");
        refreshed.RequireTarget().Options["containerName"].Should().Be("palworld-server");
        refreshed.RequireTarget().Options["rootPath"].Should().Be("/palworld");
        refreshed.Facts.Cost.Confidence.Should().Be(CostConfidence.Unknown);
        refreshed.Facts.PrivateAddress.Should().Be("172.18.0.2");
    }

    [Fact]
    public async Task ReconcileAsync_asks_the_engine_for_servyx_managed_containers_only()
    {
        var (client, containers) = SubstituteClient();
        ContainersListParameters? captured = null;
        containers
            .ListContainersAsync(Arg.Do<ContainersListParameters>(p => captured = p), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<ContainerListResponse>>([]));

        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        await provisioner.ReconcileAsync(new OrphanScope.ProviderWide("docker-container"));

        captured.Should().NotBeNull();
        captured!.All.Should().Be(true);
        captured.Filters.Should().ContainKey("label");
        captured.Filters["label"].Should().ContainKey("servyx.managed=true");
    }

    [Fact]
    public async Task ReconcileAsync_returns_only_labelled_containers_even_if_the_engine_returns_others()
    {
        var (client, containers) = SubstituteClient();
        containers
            .ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<ContainerListResponse>>(
            [
                new ContainerListResponse
                {
                    ID = "managed-1",
                    Labels = new Dictionary<string, string>
                    {
                        ["servyx.managed"] = "true",
                        ["servyx.instance-id"] = "srv-0001",
                        ["servyx.job-id"] = "job-42",
                    },
                },
                new ContainerListResponse
                {
                    ID = "someone-elses",
                    Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = "unrelated" },
                },
                new ContainerListResponse { ID = "unlabelled", Labels = null },
                new ContainerListResponse
                {
                    ID = "opted-out",
                    Labels = new Dictionary<string, string> { ["servyx.managed"] = "false" },
                },
            ]));

        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var handles = await provisioner.ReconcileAsync(new OrphanScope.ProviderWide("docker-container"));

        handles.Select(h => h.ProviderResourceId).Should().Equal("managed-1");
        handles[0].ProvisionerId.Should().Be("docker-container");
        handles[0].Tags["servyx.instance-id"].Should().Be("srv-0001");
    }

    [Fact]
    public async Task ReconcileAsync_ignores_a_scope_belonging_to_a_different_provisioner()
    {
        var (client, containers) = SubstituteClient();

        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var handles = await provisioner.ReconcileAsync(new OrphanScope.ProviderWide("hetzner", "nbg1"));

        handles.Should().BeEmpty();
        await containers.DidNotReceive().ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_declines_a_search_space_it_cannot_serve_rather_than_widening_it()
    {
        // The engine's inventory is this provisioner's only search space. Asked to sweep a marker directory
        // it reports nothing, exactly as it does for another provisioner's scope. Quietly reinterpreting the
        // request as "list every managed container" would hand the caller more containers than it asked to
        // sweep — and a sweep's output is a delete list.
        var (client, containers) = SubstituteClient();

        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var handles = await provisioner.ReconcileAsync(
            new OrphanScope.MarkerDirectory("docker-container", "/var/lib/servyx/instances"));

        handles.Should().BeEmpty();
        await containers.DidNotReceive().ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_still_sweeps_the_whole_engine_for_a_provider_wide_scope_with_a_region()
    {
        // Docker's local engine is not region-scoped, so a region on the scope changes nothing here. This
        // pins that the region is ignored rather than silently narrowing the sweep.
        var (client, containers) = SubstituteClient();
        containers
            .ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<ContainerListResponse>>(
            [
                new ContainerListResponse
                {
                    ID = "managed-1",
                    Labels = new Dictionary<string, string> { ["servyx.managed"] = "true" },
                },
            ]));

        var provisioner = new DockerContainerProvisioner(client, Endpoint);

        var handles = await provisioner.ReconcileAsync(new OrphanScope.ProviderWide("docker-container", "nbg1"));

        handles.Select(h => h.ProviderResourceId).Should().Equal("managed-1");
        handles[0].Region.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_start_removes_the_partial_container_and_surfaces_the_failure()
    {
        var (client, containers) = SubstituteClient();
        containers
            .CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CreateContainerResponse { ID = "container-1" }));
        containers
            .StartContainerAsync("container-1", Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new DockerApiException(HttpStatusCode.Conflict, "port already allocated"));

        var provisioner = new DockerContainerProvisioner(client, Endpoint);
        var operation = provisioner.CreateOperation(DockerContainerProvisioner.BuildSpec(PalworldRequest()));

        var act = async () => await operation.CreateAsync();
        await act.Should().ThrowAsync<DockerApiException>();

        await operation.CompensateAsync();

        await containers.Received(1).RemoveContainerAsync("container-1", Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>());
    }

    /// <summary>An inspect response shaped like a container this provisioner created.</summary>
    internal static ContainerInspectResponse InspectResponse(string id, string name) => new()
    {
        ID = id,
        Name = name,
        Created = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc),
        Config = new Config
        {
            Labels = new Dictionary<string, string>
            {
                ["servyx.managed"] = "true",
                ["servyx.instance-id"] = "srv-0001",
                ["servyx.job-id"] = "job-42",
                ["servyx.connector-id"] = "docker-local",
                ["servyx.root-path"] = "/palworld",
            },
        },
        NetworkSettings = new NetworkSettings { IPAddress = "172.18.0.2" },
        State = new ContainerState { Status = "running", Running = true },
    };
}
