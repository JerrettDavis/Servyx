using Servyx.Domain.Hosts;

namespace Servyx.Infrastructure.Ssh.Tests;

/// <summary>
/// Unit tests for <see cref="SshHostKeyProbeAdapter"/> — the <see cref="IHostKeyProbe"/> seam an
/// Application-layer use case observes host keys through. The reached-a-host path needs a real SSH server and
/// is covered by <see cref="SshHostKeyProbeTests"/>; what is asserted here is everything the adapter itself
/// adds on top of <see cref="SshHostKeyProbe"/>, none of which needs a container.
/// </summary>
public class SshHostKeyProbeAdapterTests
{
    /// <summary>
    /// <see cref="SshEndpoint.Parse"/> throws for a malformed endpoint, and a malformed address typed into a
    /// registration form is an ordinary mistake, not a crash — so it has to arrive as a result.
    /// </summary>
    [Theory]
    [InlineData("host:not-a-port")]
    [InlineData("host:99999")]
    [InlineData("[::1")]
    [InlineData("user@")]
    public async Task An_unparseable_endpoint_is_reported_as_InvalidEndpoint_rather_than_throwing(string endpoint)
    {
        IHostKeyProbe probe = new SshHostKeyProbeAdapter(TimeSpan.FromSeconds(2));

        var observation = await probe.ObserveAsync(endpoint);

        observation.Status.Should().Be(HostKeyObservationStatus.InvalidEndpoint);
        observation.Sha256Fingerprint.Should().BeNull();
        observation.PublicKeyBlob.Should().BeNull();
        observation.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_unreachable_host_is_reported_as_Unreachable_rather_than_throwing()
    {
        // Port 1 on loopback refuses immediately: a genuine connectivity failure without needing a container.
        IHostKeyProbe probe = new SshHostKeyProbeAdapter(TimeSpan.FromSeconds(5));

        var observation = await probe.ObserveAsync("127.0.0.1:1");

        observation.Status.Should().Be(HostKeyObservationStatus.Unreachable);
        observation.Host.Should().Be("127.0.0.1");
        observation.Port.Should().Be(1);
        observation.Algorithm.Should().BeNull();
        observation.Sha256Fingerprint.Should().BeNull();
        observation.PublicKeyBlob.Should().BeNull();
        observation.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_non_positive_timeout_is_refused_at_construction()
    {
        var act = () => new SshHostKeyProbeAdapter(TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_blank_endpoint_is_a_caller_bug_and_throws()
    {
        IHostKeyProbe probe = new SshHostKeyProbeAdapter();

        var act = () => probe.ObserveAsync("   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
