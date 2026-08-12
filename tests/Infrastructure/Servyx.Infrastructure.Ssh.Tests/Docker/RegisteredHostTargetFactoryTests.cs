using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>Unit tests for <see cref="RegisteredHostTargetFactory"/>.</summary>
public class RegisteredHostTargetFactoryTests
{
    private static Host NewHost(
        string endpoint = "fsn1-node-7.example.com:22",
        string? credentialUrn = null,
        string trustPolicy = "trustOnFirstUse",
        string? pinnedFingerprints = null) => new()
    {
        Id = HostId.New(),
        Name = "fsn1-node-7",
        ConnectorId = "ssh:fsn1-node-7",
        Endpoint = endpoint,
        CredentialUrn = credentialUrn,
        TrustPolicy = trustPolicy,
        PinnedFingerprints = pinnedFingerprints,
        Enabled = true,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Uses_the_ssh_docker_transport_id_and_the_hosts_endpoint()
    {
        var target = RegisteredHostTargetFactory.Build(NewHost());

        target.TransportId.Should().Be(SshDockerWiringOptions.TransportIdValue);
        target.Endpoint.Should().Be("fsn1-node-7.example.com:22");
    }

    [Fact]
    public void Declares_the_same_channel_set_a_configured_host_declares()
    {
        var target = RegisteredHostTargetFactory.Build(NewHost());

        target.Options.Should().ContainKey("declaredChannels")
            .WhoseValue.Should().Be(SshDockerWiringOptions.DeclaredChannels);
    }

    [Fact]
    public void Does_not_carry_a_containerName_option()
    {
        var target = RegisteredHostTargetFactory.Build(NewHost());

        target.Options.Should().NotContainKey("containerName",
            "a registered Host row names a machine, not a container — discovery lists every container the " +
            "connected host runs rather than filtering by name");
    }

    [Fact]
    public void Carries_the_trust_policy_and_pinned_fingerprints_when_present()
    {
        var target = RegisteredHostTargetFactory.Build(
            NewHost(trustPolicy: "requirePinned", pinnedFingerprints: "SHA256:abc123"));

        target.Options["trustPolicy"].Should().Be("requirePinned");
        target.Options["pinnedFingerprints"].Should().Be("SHA256:abc123");
    }

    [Fact]
    public void Omits_pinned_fingerprints_when_the_host_has_none()
    {
        var target = RegisteredHostTargetFactory.Build(NewHost(pinnedFingerprints: null));

        target.Options.Should().NotContainKey("pinnedFingerprints");
    }

    [Fact]
    public void Carries_the_credential_urn_trimmed_when_present()
    {
        var target = RegisteredHostTargetFactory.Build(NewHost(credentialUrn: "  secret://hosts/fsn1-node-7/ssh-key  "));

        target.CredentialUrn.Should().Be("secret://hosts/fsn1-node-7/ssh-key");
    }

    [Fact]
    public void Credential_urn_is_null_when_the_host_has_none()
    {
        var target = RegisteredHostTargetFactory.Build(NewHost(credentialUrn: null));

        target.CredentialUrn.Should().BeNull();
    }
}
