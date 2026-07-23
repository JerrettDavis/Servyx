using FluentAssertions;
using Servyx.Domain.Connectors;
using Servyx.Infrastructure.Ssh;

namespace Servyx.Infrastructure.Ssh.Tests;

/// <summary>
/// Pool keying (<c>docs/connectors.md</c>, "Pooling"): the same descriptor must always yield the same
/// <see cref="ConnectorKey"/>, and rotating a credential URN must yield a different one — so a credential
/// rotation naturally evicts the old pooled connection rather than reusing a session authenticated under a
/// credential that just got revoked.
/// </summary>
public class SshConnectorKeyFactoryTests
{
    private static ConnectorDescriptor MakeDescriptor(
        string endpoint = "ssh:steam@10.0.0.4:22",
        IReadOnlyList<string>? credentialRefs = null,
        TrustPolicy? trust = null) =>
        new(
            ConnectorId: "conn-1",
            Kind: "ssh",
            DisplayName: "Prod box",
            TransportId: "ssh",
            Endpoint: endpoint,
            CredentialRefs: credentialRefs ?? ["secret://connector/conn-1/ssh/password"],
            Trust: trust ?? new TrustPolicy.RequirePinned(),
            Timeouts: TimeoutPolicy.Default,
            DeclaredChannels: ConnectorChannel.Exec | ConnectorChannel.FileRead | ConnectorChannel.FileWrite);

    [Fact]
    public void Same_descriptor_produces_the_same_key()
    {
        var a = SshConnectorKeyFactory.CreateKey(MakeDescriptor());
        var b = SshConnectorKeyFactory.CreateKey(MakeDescriptor());

        a.Should().Be(b);
    }

    [Fact]
    public void Rotated_credential_urn_produces_a_different_key()
    {
        var original = SshConnectorKeyFactory.CreateKey(MakeDescriptor(
            credentialRefs: ["secret://connector/conn-1/ssh/password"]));

        var rotated = SshConnectorKeyFactory.CreateKey(MakeDescriptor(
            credentialRefs: ["secret://connector/conn-1/ssh/password-v2"]));

        rotated.Should().NotBe(original);
        rotated.CredentialKey.Should().NotBe(original.CredentialKey);
        // Endpoint and kind are unaffected by a credential rotation.
        rotated.EndpointKey.Should().Be(original.EndpointKey);
        rotated.Kind.Should().Be(original.Kind);
    }

    [Fact]
    public void Different_endpoint_produces_a_different_key()
    {
        var a = SshConnectorKeyFactory.CreateKey(MakeDescriptor(endpoint: "ssh:steam@10.0.0.4:22"));
        var b = SshConnectorKeyFactory.CreateKey(MakeDescriptor(endpoint: "ssh:steam@10.0.0.5:22"));

        a.Should().NotBe(b);
    }

    [Fact]
    public void Different_trust_policy_produces_a_different_key()
    {
        var requirePinned = SshConnectorKeyFactory.CreateKey(MakeDescriptor(trust: new TrustPolicy.RequirePinned()));
        var trustOnFirstUse = SshConnectorKeyFactory.CreateKey(MakeDescriptor(trust: new TrustPolicy.TrustOnFirstUse()));

        requirePinned.Should().NotBe(trustOnFirstUse);
    }

    [Fact]
    public void Credential_key_never_contains_the_urn_text_itself()
    {
        const string urn = "secret://connector/conn-1/ssh/password";
        var key = SshConnectorKeyFactory.CreateKey(MakeDescriptor(credentialRefs: [urn]));

        key.CredentialKey.Should().NotContain(urn);
        key.CredentialKey.Should().NotContain("password");
    }

    [Fact]
    public void Reordering_credential_refs_produces_a_different_key()
    {
        var a = SshConnectorKeyFactory.CreateKey(MakeDescriptor(credentialRefs: [
            "secret://connector/conn-1/ssh/username",
            "secret://connector/conn-1/ssh/password",
        ]));
        var b = SshConnectorKeyFactory.CreateKey(MakeDescriptor(credentialRefs: [
            "secret://connector/conn-1/ssh/password",
            "secret://connector/conn-1/ssh/username",
        ]));

        a.Should().NotBe(b, "credential order is caller-stable, not something the key should normalize away");
    }
}
