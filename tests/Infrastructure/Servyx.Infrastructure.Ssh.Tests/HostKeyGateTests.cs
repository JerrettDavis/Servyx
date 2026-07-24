using NSubstitute;
using Servyx.Domain.Connectors;
using Servyx.Infrastructure.Ssh;

namespace Servyx.Infrastructure.Ssh.Tests;

/// <summary>
/// Host-key refusal (<c>docs/connectors.md</c>, "Host key trust"): an <see cref="HostKeyVerdict.Unknown"/>
/// or <see cref="HostKeyVerdict.Changed"/> verdict must prevent any privileged follow-on action —
/// concretely, the callback that would let <see cref="SshConnector"/> proceed to open exec/file channels —
/// from ever running. <see cref="HostKeyGate.EnforceAsync"/> is the exact choke point
/// <see cref="SshConnector"/> wires onto SSH.NET's <c>HostKeyReceived</c> event, so testing it directly
/// here is testing the real refusal path, not a stand-in for it.
/// </summary>
public class HostKeyGateTests
{
    private static readonly byte[] SomeKeyBlob = [1, 2, 3, 4];

    [Fact]
    public async Task Trusted_verdict_invokes_the_onTrusted_spy()
    {
        var verifier = Substitute.For<IHostKeyVerifier>();
        verifier.VerifyAsync("host", 22, "ssh-ed25519", SomeKeyBlob, Arg.Any<TrustPolicy>(), Arg.Any<CancellationToken>())
            .Returns(HostKeyVerdict.Trusted);

        var spyInvoked = false;

        var verdict = await HostKeyGate.EnforceAsync(
            verifier, "host", 22, "ssh-ed25519", SomeKeyBlob, new TrustPolicy.RequirePinned(),
            onTrusted: () => spyInvoked = true);

        verdict.Should().Be(HostKeyVerdict.Trusted);
        spyInvoked.Should().BeTrue();
    }

    [Theory]
    [InlineData(HostKeyVerdict.Unknown)]
    [InlineData(HostKeyVerdict.Changed)]
    [InlineData(HostKeyVerdict.Revoked)]
    public async Task Non_trusted_verdict_never_invokes_the_onTrusted_spy(HostKeyVerdict rejectedVerdict)
    {
        var verifier = Substitute.For<IHostKeyVerifier>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<TrustPolicy>(), Arg.Any<CancellationToken>())
            .Returns(rejectedVerdict);

        var spyInvoked = false;

        var verdict = await HostKeyGate.EnforceAsync(
            verifier, "host", 22, "ssh-ed25519", SomeKeyBlob, new TrustPolicy.RequirePinned(),
            onTrusted: () => spyInvoked = true);

        verdict.Should().Be(rejectedVerdict);
        spyInvoked.Should().BeFalse("no command execution or file access must ever be reachable for a non-Trusted host key verdict");
    }

    [Fact]
    public async Task Passes_through_the_exact_host_port_algorithm_and_blob_to_the_verifier()
    {
        var verifier = Substitute.For<IHostKeyVerifier>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<TrustPolicy>(), Arg.Any<CancellationToken>())
            .Returns(HostKeyVerdict.Trusted);
        var policy = new TrustPolicy.RequirePinned();

        await HostKeyGate.EnforceAsync(verifier, "10.0.0.4", 2222, "rsa-sha2-256", SomeKeyBlob, policy, onTrusted: () => { });

        await verifier.Received(1).VerifyAsync("10.0.0.4", 2222, "rsa-sha2-256", SomeKeyBlob, policy, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void HostKeyRejectedException_carries_the_host_port_and_verdict()
    {
        var ex = new HostKeyRejectedException("10.0.0.4", 22, HostKeyVerdict.Changed);

        ex.Host.Should().Be("10.0.0.4");
        ex.Port.Should().Be(22);
        ex.Verdict.Should().Be(HostKeyVerdict.Changed);
        ex.Message.Should().Contain("Changed");
    }
}
