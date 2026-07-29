using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Provisioning;

namespace Servyx.Infrastructure.Ssh.Tests.Provisioning;

/// <summary>
/// Tests for the opt-in <see cref="SshProvisioningServiceCollectionExtensions.AddServyxSshProvisioning"/>
/// registration.
/// </summary>
/// <remarks>
/// The registration delegate is invoked directly against a stand-in composition root rather than through a
/// built container, because this test project references only
/// <c>Microsoft.Extensions.DependencyInjection.Abstractions</c> (transitively) — enough for
/// <see cref="ServiceCollection"/> and <see cref="ServiceDescriptor"/>, not for a provider implementation.
/// Driving the real delegate is what matters; the container that would call it adds nothing to the assertion.
/// </remarks>
public class SshProvisioningRegistrationTests
{
    private const string Endpoint = "steam@palworld-host.internal:22";

    private sealed class TransportRegistry(params ITransport[] transports) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IEnumerable<ITransport>) ? transports : null;
    }

    private static ITransport Transport(string transportId)
    {
        var transport = Substitute.For<ITransport>();
        transport.TransportId.Returns(transportId);
        return transport;
    }

    private static object Resolve(IServiceProvider root, string endpoint, string? markerRoot = null)
    {
        var services = new ServiceCollection();
        services.AddServyxSshProvisioning(endpoint, credentialUrn: null, transportOptions: null, markerRoot: markerRoot);

        var descriptor = services.Single(d => d.ServiceType == typeof(SshProcessProvisioner));
        descriptor.ImplementationFactory.Should().NotBeNull("the registration must build the provisioner itself");

        return descriptor.ImplementationFactory!(root);
    }

    [Fact]
    public void The_registration_selects_the_ssh_transport_by_id_not_by_position()
    {
        // A composition root that also registers a Docker transport must not hand this provisioner a Docker
        // connection just because it happens to be registered first.
        var ssh = Transport("ssh");
        var root = new TransportRegistry(Transport("docker"), ssh, Transport("local"));

        var provisioner = Resolve(root, Endpoint);

        provisioner.Should().BeOfType<SshProcessProvisioner>()
            .Which.ProvisionerId.Should().Be("ssh-process");
        _ = ssh.Received().TransportId;
    }

    [Fact]
    public void The_registration_fails_loudly_when_no_ssh_transport_is_registered()
    {
        var root = new TransportRegistry(Transport("docker"));

        var act = () => Resolve(root, Endpoint);

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddServyxSsh()*");
    }

    [Fact]
    public void The_registration_also_publishes_the_provisioner_under_the_domain_abstraction()
    {
        var services = new ServiceCollection();
        services.AddServyxSshProvisioning(Endpoint);

        services.Select(d => d.ServiceType).Should().Contain([typeof(SshProcessProvisioner), typeof(IProvisioner)]);
    }

    [Fact]
    public void The_configured_marker_root_is_what_the_provisioner_sweeps()
    {
        var root = new TransportRegistry(Transport("ssh"));

        var defaulted = (SshProcessProvisioner)Resolve(root, Endpoint);
        var configured = (SshProcessProvisioner)Resolve(root, Endpoint, "/srv/servyx/instances/");

        defaulted.MarkerRoot.Should().Be(SshProcessProvisioner.DefaultMarkerRoot);
        configured.MarkerRoot.Should().Be("/srv/servyx/instances");
    }

    [Fact]
    public void A_marker_root_that_is_not_an_absolute_posix_path_is_rejected()
    {
        var root = new TransportRegistry(Transport("ssh"));

        var act = () => Resolve(root, Endpoint, @"C:\servyx\instances");

        act.Should().Throw<ArgumentException>().WithMessage("*absolute POSIX*");
    }
}
