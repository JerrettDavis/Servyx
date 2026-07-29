using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Process.Provisioning;

namespace Servyx.Infrastructure.Process.Tests.Provisioning;

/// <summary>
/// Tests for the opt-in
/// <see cref="ProcessProvisioningServiceCollectionExtensions.AddServyxProcessProvisioning"/> registration.
/// </summary>
/// <remarks>
/// The registration delegate is invoked directly against a stand-in composition root rather than through a
/// built container, mirroring <c>SshProvisioningRegistrationTests</c>: driving the real delegate is what
/// matters, and the container that would call it adds nothing to the assertion.
/// </remarks>
public class LocalProcessProvisioningRegistrationTests
{
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

    private static object Resolve(IServiceProvider root, string? markerRoot = null)
    {
        var services = new ServiceCollection();
        services.AddServyxProcessProvisioning(machineId: "test-machine", credentialUrn: null, transportOptions: null, markerRoot: markerRoot);

        var descriptor = services.Single(d => d.ServiceType == typeof(LocalProcessProvisioner));
        descriptor.ImplementationFactory.Should().NotBeNull("the registration must build the provisioner itself");

        return descriptor.ImplementationFactory!(root);
    }

    [Fact]
    public void The_registration_selects_the_local_transport_by_id_not_by_position()
    {
        // A composition root that also registers Docker and SSH transports must not hand this provisioner one
        // of them just because it happens to be registered first.
        var local = Transport("local");
        var root = new TransportRegistry(Transport("docker"), Transport("ssh"), local);

        var provisioner = Resolve(root);

        provisioner.Should().BeOfType<LocalProcessProvisioner>()
            .Which.ProvisionerId.Should().Be("local-process");
        _ = local.Received().TransportId;
    }

    [Fact]
    public void The_registration_fails_loudly_when_no_local_transport_is_registered()
    {
        var root = new TransportRegistry(Transport("docker"), Transport("ssh"));

        var act = () => Resolve(root);

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddServyxLocalProcess()*");
    }

    [Fact]
    public void The_registration_also_publishes_the_provisioner_under_the_domain_abstraction()
    {
        var services = new ServiceCollection();
        services.AddServyxProcessProvisioning();

        services.Select(d => d.ServiceType).Should().Contain([typeof(LocalProcessProvisioner), typeof(IProvisioner)]);
    }

    [Fact]
    public void The_transport_registration_publishes_a_local_transport_under_the_domain_abstraction()
    {
        var services = new ServiceCollection();
        services.AddServyxLocalProcess();

        var descriptor = services.Single(d => d.ServiceType == typeof(ITransport));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationFactory!(new TransportRegistry())
            .Should().BeOfType<LocalProcessTransport>()
            .Which.TransportId.Should().Be("local");
    }

    [Fact]
    public void The_configured_marker_root_is_what_the_provisioner_sweeps()
    {
        using var temp = new TempDirectory("registration");
        var root = new TransportRegistry(Transport("local"));

        var defaulted = (LocalProcessProvisioner)Resolve(root);
        var configured = (LocalProcessProvisioner)Resolve(root, temp.At("instances") + Path.DirectorySeparatorChar);

        defaulted.MarkerRoot.Should().Be(LocalProcessProvisioner.DefaultMarkerRoot);
        configured.MarkerRoot.Should().Be(temp.At("instances"));
    }

    [Fact]
    public void The_default_marker_root_is_an_absolute_path_this_machine_can_actually_use()
    {
        // The SSH adapter can hard-code "/var/lib/servyx/instances" because its target is POSIX by
        // definition. A local adapter cannot: on Windows that string is drive-relative, not absolute.
        Path.IsPathFullyQualified(LocalProcessProvisioner.DefaultMarkerRoot).Should().BeTrue();
    }

    [Fact]
    public void A_marker_root_that_is_not_fully_qualified_is_rejected()
    {
        var root = new TransportRegistry(Transport("local"));

        var act = () => Resolve(root, "relative/instances");

        act.Should().Throw<ArgumentException>().WithMessage("*fully-qualified*");
    }
}
