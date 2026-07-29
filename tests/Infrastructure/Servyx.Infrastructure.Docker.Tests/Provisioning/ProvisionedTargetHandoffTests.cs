using Docker.DotNet;
using Docker.DotNet.Models;
using NSubstitute;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Provisioning;

namespace Servyx.Infrastructure.Docker.Tests.Provisioning;

/// <summary>
/// The proof of the provisioning subsystem's central architectural claim: <em>a provisioner's job is
/// finished when it hands back a <see cref="TargetDescriptor"/>; from that point the existing transport
/// machinery takes over unchanged.</em>
/// </summary>
/// <remarks>
/// Every assertion below is made against the exact <see cref="TargetDescriptor"/> instance the
/// provisioner produced — never a copy, a rebuilt value, or an adapted one. If this file ever needs a
/// mapping step between <see cref="ProvisionedResource.Target"/> and <see cref="DockerTransport"/>, the
/// claim is false and the mapping step is the evidence.
/// </remarks>
public class ProvisionedTargetHandoffTests
{
    private const string Endpoint = "npipe://./pipe/dockerDesktopLinuxEngine";

    private static IDockerEnvironment Environment()
    {
        var environment = Substitute.For<IDockerEnvironment>();
        environment.GetEnvironmentVariable(Arg.Any<string>()).Returns((string?)null);
        environment.IsWindows.Returns(true);
        return environment;
    }

    /// <summary>Runs the real provisioner against a substituted engine and returns what it handed back.</summary>
    private static async Task<ProvisionedResource> ProvisionAsync()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);

        containers
            .CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CreateContainerResponse { ID = "container-1" }));
        containers
            .StartContainerAsync("container-1", Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        containers
            .InspectContainerAsync("container-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerContainerProvisionerTests.InspectResponse("container-1", "/palworld-server")));

        var provisioner = new DockerContainerProvisioner(client, Endpoint);
        var spec = DockerContainerProvisioner.BuildSpec(DockerContainerProvisionerTests.PalworldRequest());

        return await provisioner.CreateOperation(spec).CreateAsync();
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
    public async Task The_provisioned_target_names_the_transport_that_already_exists()
    {
        var resource = await ProvisionAsync();
        var (transport, _) = Transport();

        resource.RequireTarget().TransportId.Should().Be("docker");
        resource.RequireTarget().TransportId.Should().Be(transport.TransportId);
    }

    [Fact]
    public async Task The_provisioned_targets_endpoint_is_exactly_what_the_endpoint_resolver_resolves()
    {
        var resource = await ProvisionAsync();
        var environment = Environment();

        var resolvedFromDescriptor = DockerEndpointResolver.Resolve(resource.RequireTarget(), environment);

        resolvedFromDescriptor.Should().Be(DockerEndpointResolver.Resolve(Endpoint, environment));
        resolvedFromDescriptor.Should().Be(new Uri(Endpoint));
    }

    [Fact]
    public async Task The_provisioned_target_is_probed_by_the_existing_transport_with_no_translation()
    {
        var resource = await ProvisionAsync();
        var (transport, factory) = Transport();

        // The descriptor is passed straight through — no adapter, no copy, no field fix-up.
        var health = await transport.ProbeAsync(resource.RequireTarget());

        health.Reachable.Should().BeTrue();
        health.Detail.Should().Contain("27.0.0");
        factory.Received(1).Create(new Uri(Endpoint));
    }

    [Fact]
    public async Task The_provisioned_target_is_connected_by_the_existing_transport_with_no_translation()
    {
        var resource = await ProvisionAsync();
        var (transport, factory) = Transport();

        await using var session = await transport.ConnectAsync(resource.RequireTarget());

        session.Should().NotBeNull();
        factory.Received(1).Create(new Uri(Endpoint));
    }

    [Fact]
    public async Task The_transports_own_option_conventions_read_the_provisioned_target_unaided()
    {
        var resource = await ProvisionAsync();

        DockerTransport.ResolveContainerRef(resource.RequireTarget()).Should().Be("container-1");
        DockerTransport.ResolveContainerRootPath(resource.RequireTarget()).Should().Be("/palworld");
    }

    [Fact]
    public async Task A_refreshed_target_is_identical_to_the_one_handed_over_at_creation()
    {
        var resource = await ProvisionAsync();

        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);
        containers
            .InspectContainerAsync("container-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerContainerProvisionerTests.InspectResponse("container-1", "/palworld-server")));

        var refreshed = await new DockerContainerProvisioner(client, Endpoint).RefreshAsync(resource.Handle);

        refreshed.Should().NotBeNull();

        // Compared field-by-field rather than with record equality: TargetDescriptor's Options is an
        // IReadOnlyDictionary, and the compiler-generated record Equals compares it by reference, so two
        // descriptors with identical values are NOT Equal. See this test class's remarks and the task
        // report — the type's own doc comment claims value equality, which it does not have.
        refreshed!.RequireTarget().TransportId.Should().Be(resource.RequireTarget().TransportId);
        refreshed.RequireTarget().Endpoint.Should().Be(resource.RequireTarget().Endpoint);
        refreshed.RequireTarget().CredentialUrn.Should().Be(resource.RequireTarget().CredentialUrn);
        refreshed.RequireTarget().DockerContext.Should().Be(resource.RequireTarget().DockerContext);
        refreshed.RequireTarget().Options.Should().BeEquivalentTo(resource.RequireTarget().Options);
    }

    [Fact]
    public async Task Two_descriptors_with_identical_values_are_not_record_equal_because_options_compares_by_reference()
    {
        // Documents a real defect found while proving the handoff: TargetDescriptor's XML doc says
        // "two descriptors with equal values are considered the same target", but its Options dictionary
        // is compared by reference by the generated record equality. Nothing in this task depends on the
        // claim, and fixing it is out of scope — but callers that dedupe or pool on descriptor equality
        // would be wrong today, so it is pinned here rather than left as folklore.
        var resource = await ProvisionAsync();

        var copy = new TargetDescriptor(
            resource.RequireTarget().TransportId,
            resource.RequireTarget().Endpoint,
            resource.RequireTarget().CredentialUrn,
            resource.RequireTarget().DockerContext,
            new Dictionary<string, string>(resource.RequireTarget().Options, StringComparer.Ordinal));

        copy.Should().NotBe(resource.RequireTarget());
        copy.Options.Should().BeEquivalentTo(resource.RequireTarget().Options);
    }
}
