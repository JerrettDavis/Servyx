using Docker.DotNet;
using Docker.DotNet.Models;
using NSubstitute;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Provisioning;

namespace Servyx.Infrastructure.Docker.Tests.Provisioning;

/// <summary>
/// The proof of the "remote Docker needs no new provisioner code" claim: <em>a Docker Engine reached over
/// <c>tcp://</c> is planned against, created on, refreshed from, and connected to by exactly the same code
/// as the local <c>npipe://</c> engine — the only thing that differs is the endpoint string.</em>
/// </summary>
/// <remarks>
/// <para>
/// Every test here is the <c>tcp://</c> twin of an existing <c>npipe://</c> test in
/// <see cref="DockerContainerProvisionerTests"/> or <see cref="ProvisionedTargetHandoffTests"/>, and where
/// possible runs <em>both</em> endpoints through the same helper and asserts the results equal. That is a
/// stronger statement than "the remote case also works": it says the two cases are indistinguishable to
/// every layer below the endpoint string. If a special case for <c>npipe://</c>/<c>unix://</c> is ever
/// introduced, the equality assertions below break rather than the remote-only ones.
/// </para>
/// <para>
/// The engine is a substituted <see cref="IDockerClient"/>, as everywhere else in this project — no live
/// daemon, local or remote, is involved. That is not a weakening of the claim: the claim under test is
/// about which code path runs and what descriptor comes out of it, not about socket behaviour, and the one
/// place where the real endpoint plumbing does matter is pinned separately by
/// <see cref="The_real_client_factory_reaches_a_remote_tcp_endpoint_with_no_tls_and_anonymous_credentials"/>.
/// </para>
/// </remarks>
public class RemoteDockerEndpointTests
{
    /// <summary>The local endpoint the existing provisioning tests use, kept verbatim for comparison.</summary>
    private const string LocalEndpoint = "npipe://./pipe/dockerDesktopLinuxEngine";

    /// <summary>A remote daemon reached over TCP — the only value that differs from <see cref="LocalEndpoint"/>.</summary>
    private const string RemoteEndpoint = "tcp://docker-host.internal:2375";

    private static IDockerEnvironment Environment()
    {
        var environment = Substitute.For<IDockerEnvironment>();
        environment.GetEnvironmentVariable(Arg.Any<string>()).Returns((string?)null);
        environment.IsWindows.Returns(true);
        return environment;
    }

    private static (IDockerClient Client, IContainerOperations Containers) SubstituteClient()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);
        client.ClearReceivedCalls();
        containers.ClearReceivedCalls();
        return (client, containers);
    }

    /// <summary>Runs the real provisioner against a substituted engine at <paramref name="endpoint"/> and returns what it handed back.</summary>
    private static async Task<(ProvisionedResource Resource, CreateContainerParameters Created)> ProvisionAsync(string endpoint)
    {
        var (client, containers) = SubstituteClient();

        CreateContainerParameters? captured = null;
        containers
            .CreateContainerAsync(Arg.Do<CreateContainerParameters>(p => captured = p), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CreateContainerResponse { ID = "container-1" }));
        containers
            .StartContainerAsync("container-1", Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        containers
            .InspectContainerAsync("container-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerContainerProvisionerTests.InspectResponse("container-1", "/palworld-server")));

        var provisioner = new DockerContainerProvisioner(client, endpoint);
        var spec = DockerContainerProvisioner.BuildSpec(DockerContainerProvisionerTests.PalworldRequest());

        var resource = await provisioner.CreateOperation(spec).CreateAsync();

        captured.Should().NotBeNull();
        return (resource, captured!);
    }

    /// <summary>A transport wired to a substituted engine that answers a version probe.</summary>
    private static (DockerTransport Transport, IDockerClientFactory Factory) Transport()
    {
        var client = Substitute.For<IDockerClient>();
        var system = Substitute.For<ISystemOperations>();
        client.System.Returns(system);
        system.GetVersionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new VersionResponse
        {
            Version = "27.0.0",
            APIVersion = "1.46",
            Os = "linux",
            Arch = "amd64",
            KernelVersion = "5.15.0",
        }));

        var factory = Substitute.For<IDockerClientFactory>();
        factory.Create(Arg.Any<Uri>()).Returns(client);

        return (new DockerTransport(factory, Environment()), factory);
    }

    [Fact]
    public async Task PlanAsync_against_a_remote_tcp_endpoint_issues_no_docker_call_either()
    {
        var (client, containers) = SubstituteClient();
        var provisioner = new DockerContainerProvisioner(client, RemoteEndpoint);

        var plan = await provisioner.PlanAsync(DockerContainerProvisionerTests.PalworldRequest());

        plan.Should().NotBeNull();
        client.ReceivedCalls().Should().BeEmpty();
        containers.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_remote_plan_is_identical_to_the_local_one_because_the_endpoint_is_not_an_input_to_planning()
    {
        var (localClient, _) = SubstituteClient();
        var (remoteClient, _) = SubstituteClient();

        var local = await new DockerContainerProvisioner(localClient, LocalEndpoint)
            .PlanAsync(DockerContainerProvisionerTests.PalworldRequest());
        var remote = await new DockerContainerProvisioner(remoteClient, RemoteEndpoint)
            .PlanAsync(DockerContainerProvisionerTests.PalworldRequest());

        remote.PlanHash.Should().Be(local.PlanHash);
        remote.PlanId.Should().Be(local.PlanId);
        remote.Stages.Select(s => s.StageId).Should().Equal(local.Stages.Select(s => s.StageId));
        remote.Stages.Select(s => s.Description).Should().Equal(local.Stages.Select(s => s.Description));
        remote.EstimatedCost.Should().Be(local.EstimatedCost);
    }

    [Fact]
    public async Task A_remote_plan_still_describes_the_container_ports_and_labels_the_same_way()
    {
        var (client, _) = SubstituteClient();
        var provisioner = new DockerContainerProvisioner(client, RemoteEndpoint);

        var plan = await provisioner.PlanAsync(DockerContainerProvisionerTests.PalworldRequest());

        plan.Stages.Select(s => s.StageId).Should().Equal("create-container", "publish-ports", "start-container");
        plan.Stages[0].Description.Should().Contain("thijsvanloef/palworld-server-docker:latest").And.Contain("palworld-server");
        plan.Stages[1].Description.Should().Contain("8211->8211/udp").And.Contain("27015->27015/udp");
        plan.EstimatedCost.Confidence.Should().Be(CostConfidence.Unknown);
    }

    [Fact]
    public async Task Creating_against_a_remote_endpoint_sends_the_engine_exactly_the_same_create_parameters()
    {
        var (_, local) = await ProvisionAsync(LocalEndpoint);
        var (_, remote) = await ProvisionAsync(RemoteEndpoint);

        remote.Image.Should().Be(local.Image);
        remote.Name.Should().Be(local.Name);
        remote.Labels.Should().BeEquivalentTo(local.Labels);
        remote.Env.Should().BeEquivalentTo(local.Env);
        remote.ExposedPorts.Keys.Should().BeEquivalentTo(local.ExposedPorts.Keys);
        remote.HostConfig.Binds.Should().BeEquivalentTo(local.HostConfig.Binds);
        remote.HostConfig.PortBindings.Keys.Should().BeEquivalentTo(local.HostConfig.PortBindings.Keys);
    }

    [Fact]
    public async Task Creating_against_a_remote_endpoint_still_applies_the_three_mandatory_servyx_labels()
    {
        var (_, created) = await ProvisionAsync(RemoteEndpoint);

        created.Labels.Should().ContainKey(ServyxResourceTags.ManagedLabel).WhoseValue.Should().Be("true");
        created.Labels.Should().ContainKey(ServyxResourceTags.InstanceIdLabel).WhoseValue.Should().Be("srv-0001");
        created.Labels.Should().ContainKey(ServyxResourceTags.JobIdLabel).WhoseValue.Should().Be("job-42");
    }

    [Fact]
    public async Task The_provisioned_target_carries_the_remote_endpoint_verbatim()
    {
        var (resource, _) = await ProvisionAsync(RemoteEndpoint);

        resource.Target.TransportId.Should().Be("docker");
        resource.Target.Endpoint.Should().Be(RemoteEndpoint);
    }

    [Fact]
    public async Task Only_the_endpoint_differs_between_a_locally_and_a_remotely_provisioned_resource()
    {
        // The claim, stated as an equality rather than as a pair of passing tests: everything the
        // provisioner hands back is byte-for-byte the same except the one field that is supposed to differ.
        var (local, _) = await ProvisionAsync(LocalEndpoint);
        var (remote, _) = await ProvisionAsync(RemoteEndpoint);

        remote.Target.Endpoint.Should().Be(RemoteEndpoint).And.NotBe(local.Target.Endpoint);

        remote.ConnectorId.Should().Be(local.ConnectorId);
        remote.Handle.ProvisionerId.Should().Be(local.Handle.ProvisionerId);
        remote.Handle.ProviderResourceId.Should().Be(local.Handle.ProviderResourceId);
        remote.Handle.Region.Should().Be(local.Handle.Region);
        remote.Handle.Tags.Should().BeEquivalentTo(local.Handle.Tags);
        remote.Target.TransportId.Should().Be(local.Target.TransportId);
        remote.Target.CredentialUrn.Should().Be(local.Target.CredentialUrn);
        remote.Target.DockerContext.Should().Be(local.Target.DockerContext);
        remote.Target.Options.Should().BeEquivalentTo(local.Target.Options);
        remote.Facts.PrivateAddress.Should().Be(local.Facts.PrivateAddress);
        remote.Facts.PublicAddress.Should().Be(local.Facts.PublicAddress);
        remote.Facts.Cost.Should().Be(local.Facts.Cost);
    }

    [Fact]
    public async Task The_endpoint_resolver_resolves_the_remote_target_with_no_scheme_special_casing()
    {
        var (resource, _) = await ProvisionAsync(RemoteEndpoint);
        var environment = Environment();

        var resolved = DockerEndpointResolver.Resolve(resource.Target, environment);

        resolved.Should().Be(new Uri(RemoteEndpoint));
        resolved.Should().Be(DockerEndpointResolver.Resolve(RemoteEndpoint, environment));
        resolved.Scheme.Should().Be("tcp");
    }

    [Fact]
    public async Task The_existing_transport_probes_the_remote_target_with_no_translation()
    {
        var (resource, _) = await ProvisionAsync(RemoteEndpoint);
        var (transport, factory) = Transport();

        // The descriptor is passed straight through — no adapter, no copy, no field fix-up.
        var health = await transport.ProbeAsync(resource.Target);

        health.Reachable.Should().BeTrue();
        health.Detail.Should().Contain("27.0.0");
        factory.Received(1).Create(new Uri(RemoteEndpoint));
    }

    [Fact]
    public async Task The_existing_transport_connects_to_the_remote_target_with_no_translation()
    {
        var (resource, _) = await ProvisionAsync(RemoteEndpoint);
        var (transport, factory) = Transport();

        await using var session = await transport.ConnectAsync(resource.Target);

        session.Should().NotBeNull();
        factory.Received(1).Create(new Uri(RemoteEndpoint));
    }

    [Fact]
    public async Task The_transports_own_option_conventions_read_the_remote_target_unaided()
    {
        var (resource, _) = await ProvisionAsync(RemoteEndpoint);

        DockerTransport.ResolveContainerRef(resource.Target).Should().Be("container-1");
        DockerTransport.ResolveContainerRootPath(resource.Target).Should().Be("/palworld");
    }

    [Fact]
    public async Task Refreshing_a_remotely_provisioned_resource_rebuilds_the_same_remote_descriptor()
    {
        var (resource, _) = await ProvisionAsync(RemoteEndpoint);

        var (client, containers) = SubstituteClient();
        containers
            .InspectContainerAsync("container-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerContainerProvisionerTests.InspectResponse("container-1", "/palworld-server")));

        var refreshed = await new DockerContainerProvisioner(client, RemoteEndpoint).RefreshAsync(resource.Handle);

        refreshed.Should().NotBeNull();
        refreshed!.Target.Endpoint.Should().Be(RemoteEndpoint);
        refreshed.Target.TransportId.Should().Be(resource.Target.TransportId);
        refreshed.Target.Options.Should().BeEquivalentTo(resource.Target.Options);
    }

    [Fact]
    public async Task Reconciling_a_remote_engine_asks_it_for_servyx_managed_containers_the_same_way()
    {
        var (client, containers) = SubstituteClient();
        ContainersListParameters? captured = null;
        containers
            .ListContainersAsync(Arg.Do<ContainersListParameters>(p => captured = p), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<ContainerListResponse>>([]));

        await new DockerContainerProvisioner(client, RemoteEndpoint).ReconcileAsync(new OrphanScope.ProviderWide("docker-container"));

        captured.Should().NotBeNull();
        captured!.All.Should().Be(true);
        captured.Filters["label"].Should().ContainKey("servyx.managed=true");
    }

    [Theory]
    [InlineData("tcp://docker-host.internal:2375")]
    [InlineData("tcp://10.0.0.5:2376")]
    [InlineData("http://docker-host.internal:2375")]
    [InlineData("https://docker-host.internal:2376")]
    public async Task Every_remote_scheme_the_resolver_supports_survives_the_full_provision_to_transport_handoff(string endpoint)
    {
        var (resource, _) = await ProvisionAsync(endpoint);
        var (transport, factory) = Transport();

        var health = await transport.ProbeAsync(resource.Target);

        resource.Target.Endpoint.Should().Be(endpoint);
        health.Reachable.Should().BeTrue();
        factory.Received(1).Create(new Uri(endpoint));
    }

    /// <summary>
    /// Pins the one place where "remote works unchanged" stops being the whole story. The provisioner and
    /// transport genuinely need no new code — but the real
    /// <see cref="DockerClientFactory"/> builds every client with
    /// <see cref="AnonymousCredentials"/>, so a remote daemon is reached over plaintext HTTP with no client
    /// certificate, whatever the endpoint's port suggests. Note that even <c>2376</c> — the conventional
    /// TLS port — and an explicit <c>https://</c> scheme produce non-TLS credentials, because nothing in
    /// this assembly ever populates <see cref="DockerClientConfiguration.Credentials"/>. This test asserts
    /// today's behaviour, not the desired one; see the TLS gap analysis.
    /// </summary>
    [Theory]
    [InlineData("tcp://docker-host.internal:2375")]
    [InlineData("tcp://docker-host.internal:2376")]
    [InlineData("https://docker-host.internal:2376")]
    public void The_real_client_factory_reaches_a_remote_tcp_endpoint_with_no_tls_and_anonymous_credentials(string endpoint)
    {
        using var client = new DockerClientFactory().Create(new Uri(endpoint));

        client.Configuration.Credentials.Should().BeOfType<AnonymousCredentials>();
        client.Configuration.Credentials.IsTlsCredentials().Should().BeFalse();
    }
}
