using NSubstitute;
using Servyx.Domain.Connectors;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Provisioning;

namespace Servyx.Infrastructure.Ssh.Tests.Provisioning;

/// <summary>
/// The SSH twin of the Docker <c>ProvisionedTargetHandoffTests</c>, and the proof of the same architectural
/// claim for the process shape: <em>a provisioner's job is finished when it hands back a
/// <see cref="TargetDescriptor"/>; from that point the existing transport machinery takes over unchanged.</em>
/// </summary>
/// <remarks>
/// <para>
/// Every assertion below is made against the exact <see cref="TargetDescriptor"/> instance the provisioner
/// produced — never a copy, a rebuilt value, or an adapted one. If this file ever needs a mapping step
/// between <see cref="ProvisionedResource.Target"/> and <see cref="SshTransport"/>, the claim is false and
/// the mapping step is the evidence.
/// </para>
/// <para>
/// <strong>How this stays offline.</strong> The Docker twin can drive a real <c>DockerTransport</c> because
/// that transport takes an <c>IDockerClientFactory</c> seam. <see cref="SshTransport"/> has no equivalent
/// seam — it constructs an <see cref="SshConnector"/> (and thus a real SSH.NET client) internally — so a
/// fully-successful connect cannot be simulated. What <em>can</em> be driven, deterministically and with no
/// socket, is everything up to the first network byte: <see cref="SshConnector.OpenAsync"/> parses the
/// descriptor's endpoint and resolves its credentials <em>before</em> constructing any client, so a
/// descriptor missing a credential fails in credential resolution rather than in a connect. These tests use
/// that to prove the real transport reads the real descriptor, and say plainly where the seam runs out.
/// </para>
/// </remarks>
public class ProvisionedSshTargetHandoffTests
{
    private static SshTransport RealTransport() =>
        new(Substitute.For<ISecretStore>(), Substitute.For<IHostKeyVerifier>());

    [Fact]
    public async Task The_provisioned_target_names_the_transport_that_already_exists()
    {
        var (resource, _) = await SshProcessProvisionerTests.ProvisionAsync();

        resource.Target.TransportId.Should().Be("ssh");
        resource.Target.TransportId.Should().Be(RealTransport().TransportId);
    }

    [Fact]
    public async Task A_registry_of_transports_resolves_the_provisioned_target_by_its_transport_id()
    {
        var (resource, _) = await SshProcessProvisionerTests.ProvisionAsync();

        var other = Substitute.For<ITransport>();
        other.TransportId.Returns("docker");
        ITransport[] registered = [other, RealTransport()];

        var resolved = registered.Single(t => string.Equals(t.TransportId, resource.Target.TransportId, StringComparison.Ordinal));

        resolved.Should().BeOfType<SshTransport>();
        resolved.Capabilities.Should().HaveFlag(TransportCapabilities.ExecuteCommand);
        resolved.Capabilities.Should().HaveFlag(TransportCapabilities.FileWrite);
    }

    [Fact]
    public async Task The_provisioned_targets_endpoint_is_exactly_what_the_ssh_endpoint_parser_reads()
    {
        var (resource, _) = await SshProcessProvisionerTests.ProvisionAsync();

        // SshEndpoint.Parse is literally the first thing SshConnector.OpenAsync does with the descriptor's
        // endpoint, so agreeing with it is agreeing with the transport.
        var (endpoint, username) = SshEndpoint.Parse(resource.Target.Endpoint);

        endpoint.Host.Should().Be("palworld-host.internal");
        endpoint.Port.Should().Be(22);
        username.Should().Be("steam");
    }

    [Fact]
    public async Task The_real_ssh_transport_consumes_the_provisioned_target_with_no_translation()
    {
        var (resource, _) = await SshProcessProvisionerTests.ProvisionAsync();

        // The descriptor is passed straight through — no adapter, no copy, no field fix-up. The probe gets as
        // far as credential resolution, which is past endpoint parsing and connector-descriptor construction,
        // and stops there because a unit test supplies no credentials — not because anything about the
        // descriptor was unusable.
        var health = await RealTransport().ProbeAsync(resource.Target);

        health.Reachable.Should().BeFalse();
        health.Detail.Should().Contain("neither a 'password' nor a 'private-key'");
        health.Detail.Should().NotContain("does not contain a host");
    }

    [Fact]
    public async Task The_username_in_the_provisioned_endpoint_is_what_the_transport_authenticates_as()
    {
        // Same probe against an endpoint with no user@ part: the transport now complains about the username
        // instead, which is only possible if it read the username out of the endpoint the provisioner stamped.
        var (withUser, _) = await SshProcessProvisionerTests.ProvisionAsync(endpoint: "steam@palworld-host.internal:22");
        var (withoutUser, _) = await SshProcessProvisionerTests.ProvisionAsync(endpoint: "palworld-host.internal:22");

        var withUserHealth = await RealTransport().ProbeAsync(withUser.Target);
        var withoutUserHealth = await RealTransport().ProbeAsync(withoutUser.Target);

        withUserHealth.Detail.Should().Contain("neither a 'password' nor a 'private-key'");
        withoutUserHealth.Detail.Should().Contain("has no username");
    }

    [Fact]
    public async Task The_transports_own_option_conventions_read_the_provisioned_target_unaided()
    {
        var host = new SshHostDouble();
        var provisioner = new SshProcessProvisioner(
            host.Transport,
            "steam@palworld-host.internal:22",
            credentialUrn: "secret://connector/ssh-palworld/ssh/password",
            transportOptions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["trustPolicy"] = "trustOnFirstUse",
                ["declaredChannels"] = "Exec,FileRead,FileWrite",
            });

        var resource = await provisioner
            .CreateOperation(SshProcessProvisioner.BuildSpec(SshProcessProvisionerTests.PalworldNativeRequest()))
            .CreateAsync();

        // These are exactly the keys SshTransport.BuildConnectorDescriptor already reads; the provisioner
        // invents no option of its own beyond the rootPath the layers above it consume.
        resource.Target.CredentialUrn.Should().Be("secret://connector/ssh-palworld/ssh/password");
        resource.Target.Options["trustPolicy"].Should().Be("trustOnFirstUse");
        resource.Target.Options["declaredChannels"].Should().Be("Exec,FileRead,FileWrite");
        resource.Target.Options["rootPath"].Should().Be("/opt/palworld");
        resource.Target.DockerContext.Should().BeNull();
    }

    [Fact]
    public async Task The_provisioner_connects_with_exactly_the_descriptor_it_hands_back()
    {
        // The invariant the Docker provisioning registration bug violated, stated structurally rather than by
        // comparison: there is only one descriptor, so the host installed on and the host recorded in the
        // ledger cannot be different hosts.
        var (resource, host) = await SshProcessProvisionerTests.ProvisionAsync();

        host.Connected.Should().ContainSingle().Which.Should().BeSameAs(resource.Target);
    }

    [Fact]
    public async Task A_refreshed_target_is_identical_to_the_one_handed_over_at_creation()
    {
        var (resource, host) = await SshProcessProvisionerTests.ProvisionAsync();

        var refreshed = await SshProcessProvisionerTests.Provisioner(host).RefreshAsync(resource.Handle);

        refreshed.Should().NotBeNull();

        // Compared field by field rather than with record equality: TargetDescriptor's Options is an
        // IReadOnlyDictionary, which the compiler-generated record Equals compares by reference — the same
        // pre-existing defect the Docker handoff tests pin.
        refreshed!.Target.TransportId.Should().Be(resource.Target.TransportId);
        refreshed.Target.Endpoint.Should().Be(resource.Target.Endpoint);
        refreshed.Target.CredentialUrn.Should().Be(resource.Target.CredentialUrn);
        refreshed.Target.DockerContext.Should().Be(resource.Target.DockerContext);
        refreshed.Target.Options.Should().BeEquivalentTo(resource.Target.Options);
    }
}
