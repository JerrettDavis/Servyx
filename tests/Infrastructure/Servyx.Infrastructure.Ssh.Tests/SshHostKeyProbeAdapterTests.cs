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
    [InlineData("ssh paladmin@10.0.0.4")]
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

    /// <summary>
    /// The registration bug this rejection exists for. "ssh paladmin@host" — a space where the colon after
    /// "ssh" belongs — used to parse cleanly: the prefix went unstripped, so everything before the '@' became
    /// the username "ssh paladmin". Nothing downstream noticed, because the host-key probe never authenticates,
    /// so registration reported success and the failure surfaced only much later as an unexplained empty
    /// adoption list. Refusing it at the parse is what turns it back into a form error the operator can fix.
    /// </summary>
    [Fact]
    public async Task A_missing_colon_after_ssh_is_refused_and_the_reason_names_the_username_it_parsed_to()
    {
        IHostKeyProbe probe = new SshHostKeyProbeAdapter(TimeSpan.FromSeconds(2));

        var observation = await probe.ObserveAsync("ssh paladmin@10.0.0.4:22");

        observation.Status.Should().Be(HostKeyObservationStatus.InvalidEndpoint);
        observation.FailureReason.Should().Contain("ssh paladmin").And.Contain("ssh:user@host:port");
    }

    /// <summary>A username is legal without the "ssh:" prefix and must keep parsing.</summary>
    [Fact]
    public async Task A_well_formed_endpoint_gets_far_enough_to_report_a_connectivity_failure()
    {
        IHostKeyProbe probe = new SshHostKeyProbeAdapter(TimeSpan.FromSeconds(5));

        var observation = await probe.ObserveAsync("ssh:paladmin@127.0.0.1:1");

        observation.Status.Should().Be(HostKeyObservationStatus.Unreachable);
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
