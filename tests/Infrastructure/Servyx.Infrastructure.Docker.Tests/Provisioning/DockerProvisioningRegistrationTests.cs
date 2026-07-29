using Docker.DotNet;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Infrastructure.Docker.Provisioning;

namespace Servyx.Infrastructure.Docker.Tests.Provisioning;

/// <summary>
/// Pins the one invariant that makes a provisioned Docker resource's ledger entry trustworthy:
/// <em>the daemon the provisioner actually talks to and the daemon its <c>TargetDescriptor</c> names are
/// always the same daemon.</em>
/// </summary>
/// <remarks>
/// <para>
/// The failure this guards against is silent and data-corrupting rather than loud. If the client is built
/// from one endpoint (say, the OS default) while descriptors are stamped with another (say, a caller-supplied
/// <c>tcp://</c> host), the container is created locally, the ledger records it as living on the remote host,
/// and the transport is later pointed at a machine that has never heard of it. Nothing throws; the record is
/// simply wrong forever.
/// </para>
/// <para>
/// These tests drive the real <see cref="DockerProvisioningServiceCollectionExtensions.AddServyxDockerProvisioning"/>
/// registration delegate against a stand-in composition root that reproduces
/// <see cref="ServiceCollectionExtensions.AddServyxDocker"/>'s own <see cref="IDockerClient"/> wiring verbatim
/// (including its <c>DOCKER_HOST</c>-then-OS-default resolution). That is what makes the assertions meaningful:
/// the divergence, if reintroduced, happens between two registrations, so a test that constructed the
/// provisioner directly could never see it.
/// </para>
/// </remarks>
public class DockerProvisioningRegistrationTests
{
    private const string RemoteEndpoint = "tcp://remote-daemon.internal:2375";

    /// <summary>
    /// A minimal <see cref="IServiceProvider"/> standing in for a composition root that has called
    /// <c>AddServyxDocker()</c>: it offers a substituted <see cref="IDockerEnvironment"/> and
    /// <see cref="IDockerClientFactory"/>, and — importantly — builds its <see cref="IDockerClient"/> exactly
    /// the way <see cref="ServiceCollectionExtensions.AddServyxDocker"/> does, so asking it for a client has
    /// the same endpoint-resolution consequences as the real container.
    /// </summary>
    private sealed class DockerCompositionRoot : IServiceProvider
    {
        private readonly Lazy<IDockerClient> _client;

        internal DockerCompositionRoot(string? dockerHost, bool isWindows = true)
        {
            var environment = Substitute.For<IDockerEnvironment>();
            environment.GetEnvironmentVariable(Arg.Any<string>()).Returns((string?)null);
            environment.GetEnvironmentVariable("DOCKER_HOST").Returns(dockerHost);
            environment.IsWindows.Returns(isWindows);
            Environment = environment;

            var client = Substitute.For<IDockerClient>();
            var factory = Substitute.For<IDockerClientFactory>();
            factory.Create(Arg.Any<Uri>()).Returns(client);
            Factory = factory;

            // Verbatim copy of AddServyxDocker()'s IDockerClient registration.
            _client = new Lazy<IDockerClient>(() => Factory.Create(DockerEndpointResolver.Resolve(explicitEndpoint: null, Environment)));
        }

        internal IDockerEnvironment Environment { get; }

        internal IDockerClientFactory Factory { get; }

        /// <summary>Every endpoint a Docker client was actually constructed for, in call order.</summary>
        internal IReadOnlyList<Uri> ClientEndpoints => Factory.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IDockerClientFactory.Create))
            .Select(call => (Uri)call.GetArguments()[0]!)
            .ToList();

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IDockerEnvironment))
            {
                return Environment;
            }

            if (serviceType == typeof(IDockerClientFactory))
            {
                return Factory;
            }

            return serviceType == typeof(IDockerClient) ? _client.Value : null;
        }
    }

    /// <summary>Runs the real registration and materialises the provisioner it registers.</summary>
    private static DockerContainerProvisioner Register(DockerCompositionRoot root, string? endpoint)
    {
        var services = new ServiceCollection();
        services.AddServyxDockerProvisioning(endpoint);

        var descriptor = services.Single(d => d.ServiceType == typeof(DockerContainerProvisioner));
        descriptor.ImplementationFactory.Should().NotBeNull("the registration must build the provisioner itself");

        return (DockerContainerProvisioner)descriptor.ImplementationFactory!(root);
    }

    [Theory]
    [InlineData("tcp://remote-daemon.internal:2375")]
    [InlineData("tcp://10.0.0.5:2376")]
    [InlineData("https://remote-daemon.internal:2376")]
    [InlineData("npipe://./pipe/dockerDesktopLinuxEngine")]
    public void The_registered_provisioner_talks_to_exactly_the_daemon_its_descriptors_name(string endpoint)
    {
        var root = new DockerCompositionRoot(dockerHost: null);

        var provisioner = Register(root, endpoint);
        var target = provisioner.BuildTargetDescriptor("container-1", "palworld-server", "/palworld");

        target.Endpoint.Should().NotBeNullOrWhiteSpace();
        root.ClientEndpoints.Should().NotBeEmpty("registering the provisioner must construct the client it will use")
            .And.AllSatisfy(uri => uri.Should().Be(new Uri(target.Endpoint)));
    }

    [Fact]
    public void A_DOCKER_HOST_pointing_elsewhere_cannot_redirect_the_registered_provisioner()
    {
        // The original bug in one line: the caller asked for a specific remote daemon, and an ambient
        // DOCKER_HOST silently sent the create call somewhere else while the descriptor kept claiming the
        // remote one.
        var root = new DockerCompositionRoot(dockerHost: "tcp://someone-elses-daemon.internal:2375");

        var provisioner = Register(root, RemoteEndpoint);
        var target = provisioner.BuildTargetDescriptor("container-1", "palworld-server", "/palworld");

        target.Endpoint.Should().Be(RemoteEndpoint);
        root.ClientEndpoints.Should().Equal(new Uri(RemoteEndpoint));
    }

    [Fact]
    public void With_no_explicit_endpoint_both_the_client_and_the_descriptor_follow_DOCKER_HOST()
    {
        const string dockerHost = "tcp://from-docker-host.internal:2375";
        var root = new DockerCompositionRoot(dockerHost);

        var provisioner = Register(root, endpoint: null);
        var target = provisioner.BuildTargetDescriptor("container-1", "palworld-server", "/palworld");

        // Not merely "they agree once resolved": the descriptor names the concretely resolved daemon, so a
        // later change to DOCKER_HOST cannot retroactively re-point a resource Servyx has already recorded.
        target.Endpoint.Should().Be(dockerHost);
        root.ClientEndpoints.Should().Equal(new Uri(dockerHost));
    }

    [Fact]
    public void With_neither_an_endpoint_nor_DOCKER_HOST_the_descriptor_names_the_OS_default_it_connected_to()
    {
        var root = new DockerCompositionRoot(dockerHost: null, isWindows: true);

        var provisioner = Register(root, endpoint: null);
        var target = provisioner.BuildTargetDescriptor("container-1", "palworld-server", "/palworld");

        target.Endpoint.Should().Be(DockerEndpointResolver.DefaultWindowsEndpoint);
        root.ClientEndpoints.Should().Equal(new Uri(DockerEndpointResolver.DefaultWindowsEndpoint));
    }
}
