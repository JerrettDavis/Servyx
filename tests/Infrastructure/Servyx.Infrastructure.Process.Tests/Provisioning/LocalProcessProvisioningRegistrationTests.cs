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
    public void The_transport_registration_publishes_a_write_guarded_local_transport_under_the_domain_abstraction()
    {
        // This assertion used to require the registration to produce a bare LocalProcessTransport, which is
        // what pinned the last write-guard exemption in place. What it was really worth asserting — that
        // AddServyxLocalProcess() publishes exactly one singleton ITransport, built by the registration
        // itself, and that the thing it publishes is the *local* transport — is asserted here unchanged. The
        // only difference is that the local transport now arrives wrapped, so the assertion follows it
        // through WriteGuardedTransport.Inner instead of stopping at the outermost type. The transport id is
        // still checked on the resolved service, because that is the value AddServyxProcessProvisioning()
        // selects on: a guard that failed to delegate TransportId would break that lookup, and this catches it.
        var services = new ServiceCollection();
        services.AddServyxLocalProcess();

        var descriptor = services.Single(d => d.ServiceType == typeof(ITransport));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);

        using var root = services.BuildServiceProvider();
        var resolved = descriptor.ImplementationFactory!(root);

        resolved.Should().BeOfType<WriteGuardedTransport>()
            .Which.Inner.Should().BeOfType<LocalProcessTransport>();
        ((ITransport)resolved).TransportId.Should().Be("local");
    }

    [Fact]
    public void The_transport_registration_publishes_the_bare_local_transport_under_no_service_type_at_all()
    {
        // The other half of the guarantee the assertion above used to undercut: wrapping is worth nothing if
        // the inner transport is separately resolvable, because a caller wanting an unguarded session would
        // simply ask for that instead.
        var services = new ServiceCollection();
        services.AddServyxLocalProcess();

        services.Should().NotContain(d => d.ServiceType == typeof(LocalProcessTransport));
    }

    [Fact]
    public async Task The_registered_transport_hands_out_read_only_sessions_when_no_grant_was_registered()
    {
        // AddServyxLocalProcess() on its own is the M1 shape: a host that configured nothing gets a process
        // that cannot write anywhere. Before the guard, ExecutionTargetWriteMode.Resolve answered null here
        // and every mutation was permitted.
        using var temp = new TempDirectory("registration-guard");
        var services = new ServiceCollection();
        services.AddServyxLocalProcess();

        using var provider = services.BuildServiceProvider();
        var target = new TargetDescriptor(
            "local",
            "local://test-machine",
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["rootPath"] = temp.Root });

        await using var session = await provider.GetRequiredService<ITransport>().ConnectAsync(target);

        ExecutionTargetWriteMode.Resolve(session).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void The_provisioning_registration_grants_writes_to_exactly_the_machine_endpoint_it_was_given()
    {
        var services = new ServiceCollection();
        services.AddServyxLocalProcess();
        services.AddServyxProcessProvisioning(machineId: "test-machine");

        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IWriteModeResolver>();

        TargetDescriptor At(string endpoint) => new(
            "local",
            endpoint,
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal));

        resolver.Resolve(At("local://test-machine")).Should().Be(WriteMode.Enabled);
        resolver.Resolve(At("local://some-other-machine")).Should().Be(WriteMode.ReadOnly);
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
