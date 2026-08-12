using DotNet.Testcontainers.Containers;
using Renci.SshNet;
using Servyx.Domain.Connectors;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Connectors;
using Servyx.Infrastructure.Ssh.Tests.Integration;
using Xunit;

namespace Servyx.Infrastructure.Ssh.Tests;

/// <summary>
/// <see cref="SshHostKeyProbe"/> against a throwaway <c>linuxserver/openssh-server</c> Docker container —
/// reusing the same harness (<see cref="SshTestContainer"/>) as <c>SshIntegrationTests</c>. Tagged
/// <c>Category=Integration</c> and skips cleanly (not fails) when no Docker daemon is available, matching that
/// project's convention. Run explicitly with <c>dotnet test --filter "Category=Integration"</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SshHostKeyProbeTests : IAsyncLifetime
{
    private IContainer? _container;
    private string _host = string.Empty;
    private int _port;
    private const string Password = "servyx-test-password";
    private bool _dockerAvailable;
    private string? _unavailableReason;

    public async Task InitializeAsync()
    {
        try
        {
            _container = await SshTestContainer.StartAsync(Password, publicKeyLine: null);
            _host = _container.Hostname;
            _port = _container.GetMappedPublicPort(SshTestContainer.ContainerPort);
            _dockerAvailable = true;
        }
        catch (Exception ex)
        {
            _dockerAvailable = false;
            _unavailableReason = $"Docker is not available for integration tests: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private void SkipUnlessDockerAvailable() => Skip.IfNot(_dockerAvailable, _unavailableReason ?? "Docker unavailable");

    /// <summary>
    /// Opens a raw connection just to observe and capture the presented host key, trusting it
    /// unconditionally — an independent reference computation to compare the probe's result against, mirroring
    /// <c>SshIntegrationTests.ProbeRawHostKeyAsync</c>.
    /// </summary>
    private async Task<(string Algorithm, byte[] Blob)> ObserveRawHostKeyAsync()
    {
        var connectionInfo = new ConnectionInfo(_host, _port, SshTestContainer.Username, new PasswordAuthenticationMethod(SshTestContainer.Username, Password));
        string? algorithm = null;
        byte[]? blob = null;

        using var client = new SshClient(connectionInfo);
        client.HostKeyReceived += (_, e) =>
        {
            algorithm = e.HostKeyName;
            blob = e.HostKey;
            e.CanTrust = true;
        };

        await client.ConnectAsync(CancellationToken.None);
        client.Disconnect();

        return (algorithm!, blob!);
    }

    [SkippableFact]
    public async Task Reached_host_returns_fingerprint_matching_an_independently_computed_one()
    {
        SkipUnlessDockerAvailable();

        var (expectedAlgorithm, expectedBlob) = await ObserveRawHostKeyAsync();
        var expectedFingerprint = HostKeyFingerprint.ComputeSha256(expectedBlob);

        var result = await SshHostKeyProbe.ProbeAsync($"{_host}:{_port}");

        result.Status.Should().Be(SshHostKeyProbeStatus.Reached);
        result.Host.Should().Be(_host);
        result.Port.Should().Be(_port);
        result.Algorithm.Should().Be(expectedAlgorithm);
        result.Sha256Fingerprint.Should().Be(expectedFingerprint);
        result.FailureReason.Should().BeNull();
    }

    [SkippableFact]
    public async Task Probing_never_grants_trust_a_subsequent_connection_under_RequirePinned_still_sees_the_host_as_Unknown()
    {
        SkipUnlessDockerAvailable();

        var result = await SshHostKeyProbe.ProbeAsync($"{_host}:{_port}");
        result.Status.Should().Be(SshHostKeyProbeStatus.Reached);

        // The probe has no reference to any IHostKeyStore at all, so it cannot pin anything by construction.
        // Assert that structural guarantee via the real enforcement path (HostKeyVerifier + SshConnector),
        // the same choke point HostKeyGateTests exercises for HostKeyGate itself: a completely fresh store
        // must still report this host as Unknown, and a connection attempt under RequirePinned must still be
        // refused, after the probe already observed and fingerprinted its key.
        var hostKeyStore = new FileHostKeyStore(Path.Combine(Path.GetTempPath(), $"servyx-probe-test-hostkeys-{Guid.NewGuid():N}.json"));
        var verifier = new HostKeyVerifier(hostKeyStore);

        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(SecretUrn.Create("connector", "probe-test-conn", "ssh", "password"), System.Text.Encoding.UTF8.GetBytes(Password), "test");

        var descriptor = new ConnectorDescriptor(
            ConnectorId: "probe-test-conn",
            Kind: "ssh",
            DisplayName: "probe no-trust test",
            TransportId: "ssh",
            Endpoint: $"ssh:{SshTestContainer.Username}@{_host}:{_port}",
            CredentialRefs: ["secret://connector/probe-test-conn/ssh/password"],
            Trust: new TrustPolicy.RequirePinned(),
            Timeouts: TimeoutPolicy.Default with { Connect = TimeSpan.FromSeconds(20) },
            DeclaredChannels: ConnectorChannel.Exec);

        var connector = new SshConnector(descriptor, secretStore, verifier);

        var act = () => connector.OpenAsync();

        var thrown = await act.Should().ThrowAsync<HostKeyRejectedException>();
        thrown.Which.Verdict.Should().Be(HostKeyVerdict.Unknown);

        (await hostKeyStore.FindAsync(_host, _port)).Should().BeNull("the probe must never pin or otherwise persist trust as a side effect");
    }

    [Fact]
    public async Task Unreachable_host_returns_a_clear_failure_result_instead_of_throwing()
    {
        // Does not depend on Docker: connecting to a closed local port is refused immediately, exercising a
        // genuine "couldn't reach the host" connectivity failure without needing the container harness.
        var result = await SshHostKeyProbe.ProbeAsync("127.0.0.1:1", TimeSpan.FromSeconds(5));

        result.Status.Should().Be(SshHostKeyProbeStatus.Unreachable);
        result.Algorithm.Should().BeNull();
        result.Sha256Fingerprint.Should().BeNull();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }
}
