using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Domain.Connectors;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Aws.Provisioning;
using Servyx.Infrastructure.Azure.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Provisioning;
using Servyx.Infrastructure.Process.Provisioning;
using Servyx.Infrastructure.Ssh.Provisioning;
using Servyx.Web.Services;
using Servyx.Composition;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Composes the provisioner block the way <c>Program.cs</c> does, on both sides of the gate.
/// </summary>
/// <remarks>
/// <para>
/// The failure this file is really about is the one a previous increment already found once: calling
/// <c>AddServyxSsh()</c> registers a second <see cref="ITransport"/>, and <see cref="ITransport"/> is injected
/// <em>singly</em> by <c>ServyxBackupContextSource</c>, so it silently pointed Docker's backups at an SSH host.
/// Every registration added here is checked against that shape — no transport, no client, no resolver
/// overwritten, nothing shared between two provisioners.
/// </para>
/// <para>
/// The Docker transport and provisioner are stood in for by substitutes: the real ones need a daemon.
/// Everything below them is the real registration path.
/// </para>
/// </remarks>
public class ProvisionerRegistrationTests
{
    private const string DockerTransportId = "docker";
    private const string DockerProvisionerId = "docker-container";

    /// <summary>
    /// The container as it stands before the provisioner block runs: the cross-cutting services
    /// <c>AddServyxSecrets()</c> supplies, plus stand-ins for <c>AddServyxDocker()</c> and
    /// <c>AddServyxDockerProvisioning()</c>.
    /// </summary>
    private static ServiceCollection BaseServices(out ITransport dockerTransport)
    {
        var transport = Substitute.For<ITransport>();
        transport.TransportId.Returns(DockerTransportId);
        dockerTransport = transport;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecretStore>(new RecordingSecretStore());
        services.AddSingleton(Substitute.For<IHostKeyVerifier>());
        services.AddSingleton(transport);
        services.AddSingleton<IProvisioner>(new FakeProvisioner(
            DockerProvisionerId,
            ProvisioningCapabilities.Create | ProvisioningCapabilities.Destroy,
            Plan()));

        return services;
    }

    private static ProvisioningPlan Plan() => new(
        PlanId: "docker-container:servyx-preview:abc123def456",
        PlanHash: "abc123def456abc123def456abc123def456abc123def456abc123def456abcd",
        Stages: [new("create-container", DockerProvisionerId, "Create container 'servyx-preview'.")],
        EstimatedCost: CostEstimate.Unknown("Local Docker containers are not billed by a provider."),
        ExpiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static ServiceProvider Compose(IConfiguration configuration, out ITransport dockerTransport)
    {
        var services = BaseServices(out dockerTransport);

        var gate = ProvisioningGate.FromConfiguration(configuration);
        services.AddSingleton(gate);

        // Exactly the two lines Program.cs runs inside the gate.
        var options = ProvisionerWiringOptions.FromConfiguration(configuration, gate);
        services.AddSingleton(options);
        services.AddServyxConfiguredProvisioners(options);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static IReadOnlyList<string> ProvisionerIds(IServiceProvider provider) =>
        [.. provider.GetServices<IProvisioner>().Select(p => p.ProvisionerId).Order(StringComparer.Ordinal)];

    [Fact]
    public void With_the_gate_closed_nothing_is_registered_however_much_is_configured()
    {
        // Every provisioner switched on and fully credentialed, and the gate left at its default.
        var settings = ProvisionerWiringTests.AllEnabled();
        settings.Remove("Servyx:Provisioning:Enabled");

        using var provider = Compose(ProvisionerWiringTests.Config(settings), out var dockerTransport);

        // The composition is what it was before this block existed: one provisioner, one transport.
        ProvisionerIds(provider).Should().Equal(DockerProvisionerId);
        provider.GetServices<IMaintainer>().Should().BeEmpty();
        provider.GetServices<ITransport>().Should().ContainSingle().Which.Should().BeSameAs(dockerTransport);
        provider.GetRequiredService<ProvisionerWiringOptions>().Should().BeSameAs(ProvisionerWiringOptions.None);
    }

    [Fact]
    public void With_the_gate_open_and_nothing_else_configured_only_todays_composition_exists()
    {
        using var provider = Compose(
            ProvisionerWiringTests.Config(ProvisionerWiringTests.GateOpen()),
            out var dockerTransport);

        ProvisionerIds(provider).Should().Equal(DockerProvisionerId);
        provider.GetServices<IMaintainer>().Should().BeEmpty();
        provider.GetServices<ITransport>().Should().ContainSingle().Which.Should().BeSameAs(dockerTransport);
    }

    [Theory]
    [MemberData(nameof(ProvisionerWiringTests.EveryProvisioner), MemberType = typeof(ProvisionerWiringTests))]
    public void Enabling_one_provisioner_registers_that_one_and_no_other(string provisionerKey, string expectedId)
    {
        using var provider = Compose(
            ProvisionerWiringTests.Config(
                ProvisionerWiringTests.GateOpen(),
                ProvisionerWiringTests.MinimalSettings(provisionerKey)),
            out _);

        ProvisionerIds(provider).Should().BeEquivalentTo([DockerProvisionerId, expectedId]);
    }

    [Theory]
    [MemberData(nameof(ProvisionerWiringTests.EveryProvisioner), MemberType = typeof(ProvisionerWiringTests))]
    public void Every_registered_provisioner_is_a_singleton_and_publishes_its_maintenance_half(
        string provisionerKey,
        string expectedId)
    {
        using var provider = Compose(
            ProvisionerWiringTests.Config(
                ProvisionerWiringTests.GateOpen(),
                ProvisionerWiringTests.MinimalSettings(provisionerKey)),
            out _);

        var first = provider.GetServices<IProvisioner>().Single(p => p.ProvisionerId == expectedId);
        var second = provider.GetServices<IProvisioner>().Single(p => p.ProvisionerId == expectedId);
        first.Should().BeSameAs(second, "a provisioner holding an HTTP client or a transport must not be rebuilt per resolve");

        // The maintenance half rides on the same instance, exactly as the adapters' own registration
        // extensions publish it — never a second object with a second client behind it.
        var maintainers = provider.GetServices<IMaintainer>();
        maintainers.Should().ContainSingle().Which.Should().BeSameAs(first);
    }

    [Fact]
    public void Enabling_every_provisioner_registers_every_id_exactly_once()
    {
        using var provider = Compose(ProvisionerWiringTests.Config(ProvisionerWiringTests.AllEnabled()), out _);

        ProvisionerIds(provider).Should().BeEquivalentTo(
        [
            DockerProvisionerId,
            SshProcessProvisioner.Id,
            LocalProcessProvisioner.Id,
            DigitalOceanDropletProvisioner.Id,
            AzureVirtualMachineProvisioner.Id,
            AwsEc2Provisioner.Id,
            AwsLightsailProvisioner.Id,
        ]);
        ProvisionerIds(provider).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void No_provisioner_registration_adds_a_second_transport()
    {
        // The regression this whole file exists for. AddServyxSsh() and AddServyxLocalProcess() each
        // register an ITransport; ServyxBackupContextSource injects ITransport singly, so a second one
        // resolves there and points Docker's backups at another machine. Neither is called.
        using var provider = Compose(ProvisionerWiringTests.Config(ProvisionerWiringTests.AllEnabled()), out var dockerTransport);

        provider.GetServices<ITransport>().Should().ContainSingle();
        provider.GetRequiredService<ITransport>().Should().BeSameAs(dockerTransport);
        provider.GetRequiredService<ITransport>().TransportId.Should().Be(DockerTransportId);
    }

    [Fact]
    public void No_provisioner_registration_adds_a_write_grant_a_backup_transport_could_pick_up()
    {
        // AddServyxSshProvisioning() registers its endpoint-scoped WriteModeGrant into the container. Here
        // the same grant is handed straight to the provisioner's own transport instead, because the SSH
        // *backup* block builds its guard over GetServices<WriteModeGrant>() — a registered grant would make
        // backups writable at that endpoint without the operator ever setting Servyx:Servers:<name>:WriteMode.
        using var provider = Compose(ProvisionerWiringTests.Config(ProvisionerWiringTests.AllEnabled()), out _);

        provider.GetServices<WriteModeGrant>().Should().BeEmpty();
        provider.GetService<IWriteModeResolver>().Should().BeNull();
    }

    [Fact]
    public void No_provisioner_registration_publishes_a_shared_http_client_or_connector_pool()
    {
        // Each cloud adapter owns a private HttpClient constructed in its own factory. Publishing one would
        // be the same shape of defect as the transport above: a singly-injected dependency that the last
        // registration wins. AddServyxSsh()'s IConnectorPool — whose factory throws pending a connector
        // registry — is likewise never brought in.
        using var provider = Compose(ProvisionerWiringTests.Config(ProvisionerWiringTests.AllEnabled()), out _);

        provider.GetService<HttpClient>().Should().BeNull();
        provider.GetServices<HttpClient>().Should().BeEmpty();
        provider.GetService<IConnectorPool>().Should().BeNull();
    }

    [Fact]
    public void The_two_aws_provisioners_are_separate_objects_despite_sharing_a_configuration_shape()
    {
        // EC2 and Lightsail read the same region and the same key-pair URNs. They must still be two
        // instances with two clients and two signers — not one adapter answering for both ids.
        using var provider = Compose(ProvisionerWiringTests.Config(ProvisionerWiringTests.AllEnabled()), out _);

        var ec2 = provider.GetRequiredService<AwsEc2Provisioner>();
        var lightsail = provider.GetRequiredService<AwsLightsailProvisioner>();

        ((object)ec2).Should().NotBeSameAs(lightsail);
        ec2.ProvisionerId.Should().Be(AwsEc2Provisioner.Id);
        lightsail.ProvisionerId.Should().Be(AwsLightsailProvisioner.Id);
        ec2.Region.Should().Be("us-east-1");
        lightsail.Region.Should().Be("us-east-1");
    }

    [Fact]
    public void The_concrete_types_resolve_too_so_a_host_can_reach_the_adapter_it_registered()
    {
        using var provider = Compose(ProvisionerWiringTests.Config(ProvisionerWiringTests.AllEnabled()), out _);

        provider.GetRequiredService<SshProcessProvisioner>().ProvisionerId.Should().Be(SshProcessProvisioner.Id);
        provider.GetRequiredService<LocalProcessProvisioner>().ProvisionerId.Should().Be(LocalProcessProvisioner.Id);
        provider.GetRequiredService<DigitalOceanDropletProvisioner>().ProvisionerId.Should().Be(DigitalOceanDropletProvisioner.Id);
        provider.GetRequiredService<AzureVirtualMachineProvisioner>().ProvisionerId.Should().Be(AzureVirtualMachineProvisioner.Id);
    }

    [Fact]
    public void A_provisioner_enabled_without_its_credentials_never_reaches_the_container()
    {
        // The refusal happens while reading configuration, so there is no window in which a half-built
        // provisioner exists — the process does not start at all.
        var settings = ProvisionerWiringTests.MinimalSettings(ProvisionerWiringOptions.DigitalOceanKey);
        settings.Remove($"{ProvisionerWiringOptions.SectionKey}:{ProvisionerWiringOptions.DigitalOceanKey}:ApiTokenUrn");

        var configuration = ProvisionerWiringTests.Config(ProvisionerWiringTests.GateOpen(), settings);

        var act = () =>
        {
            using var provider = Compose(configuration, out _);
        };

        act.Should().Throw<InvalidOperationException>().WithMessage("*ApiTokenUrn*");
    }

    [Fact]
    public void The_registration_is_a_no_op_for_the_empty_options()
    {
        var services = BaseServices(out _);
        var before = services.Count;

        services.AddServyxConfiguredProvisioners(ProvisionerWiringOptions.None);

        // Byte-for-byte, at the only level a service collection can be compared: not one descriptor added.
        services.Count.Should().Be(before);
    }
}
