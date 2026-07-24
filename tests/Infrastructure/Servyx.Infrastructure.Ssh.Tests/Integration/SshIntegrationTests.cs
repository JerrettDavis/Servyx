using DotNet.Testcontainers.Containers;
using Renci.SshNet;
using Servyx.Domain.Connectors;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Connectors;
using Xunit;

namespace Servyx.Infrastructure.Ssh.Tests.Integration;

/// <summary>
/// End-to-end integration tests against a throwaway <c>linuxserver/openssh-server</c> Docker container.
/// Tagged <c>Category=Integration</c> and built to skip cleanly (not fail) when no Docker daemon is
/// available, so <c>dotnet test Servyx.sln</c> stays green and daemon-free by default. Run explicitly with
/// <c>dotnet test --filter "Category=Integration"</c> when Docker is available.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SshIntegrationTests : IAsyncLifetime
{
    private IContainer? _container;
    private string _host = string.Empty;
    private int _port;
    private const string Password = "servyx-test-password";
    private SshRsaKeyPair? _keyPair;
    private bool _dockerAvailable;
    private string? _unavailableReason;

    public async Task InitializeAsync()
    {
        try
        {
            _keyPair = SshRsaKeyPair.Generate();
            _container = await SshTestContainer.StartAsync(Password, _keyPair.AuthorizedKeyLine);
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

        _keyPair?.Dispose();
    }

    private void SkipUnlessDockerAvailable() => Skip.IfNot(_dockerAvailable, _unavailableReason ?? "Docker unavailable");

    // ---- Descriptor / connector construction helpers ----------------------------------------------------

    private ConnectorDescriptor MakeDescriptor(IReadOnlyList<string> credentialRefs, TrustPolicy trust, ConnectorChannel channels) =>
        new(
            ConnectorId: "it-conn",
            Kind: "ssh",
            DisplayName: "integration test box",
            TransportId: "ssh",
            Endpoint: $"ssh:{SshTestContainer.Username}@{_host}:{_port}",
            CredentialRefs: credentialRefs,
            Trust: trust,
            Timeouts: TimeoutPolicy.Default with { Connect = TimeSpan.FromSeconds(20) },
            DeclaredChannels: channels);

    private static async Task<InMemorySecretStore> MakeSecretStoreWithPasswordAsync(string password)
    {
        var store = new InMemorySecretStore();
        var urn = SecretUrn.Create("connector", "it-conn", "ssh", "password");
        await store.SetAsync(urn, System.Text.Encoding.UTF8.GetBytes(password), "test");
        return store;
    }

    private static async Task<InMemorySecretStore> MakeSecretStoreWithPrivateKeyAsync(SshRsaKeyPair keyPair)
    {
        var store = new InMemorySecretStore();
        var urn = SecretUrn.Create("connector", "it-conn", "ssh", "private-key");
        await store.SetAsync(urn, System.Text.Encoding.ASCII.GetBytes(keyPair.PrivateKeyPem), "test");
        return store;
    }

    private static FileHostKeyStore MakeHostKeyStore() =>
        new(Path.Combine(Path.GetTempPath(), $"servyx-it-hostkeys-{Guid.NewGuid():N}.json"));

    /// <summary>
    /// Resolves a <see cref="TargetPath"/> for a file under the remote container's <c>/tmp</c>. Rooted at
    /// <c>/</c> (rather than at <c>/tmp</c> itself) so the resulting <see cref="TargetPath.Value"/> retains
    /// the <c>tmp/</c> segment — <see cref="SftpFileChannel"/>/<see cref="ShellFileChannel"/> both treat a
    /// <see cref="TargetPath"/> as relative to the SFTP/shell session's own root (<c>/</c>), not to some
    /// separately-configured connector root, so the root passed to <see cref="SandboxedPathResolver"/> here
    /// must be <c>/</c> for the remote path to come out as <c>/tmp/...</c> rather than just <c>/...</c>.
    /// </summary>
    private static TargetPath ResolveRemoteTmpPath(string fileName) =>
        new SandboxedPathResolver("/").Resolve("tmp/" + fileName);

    /// <summary>
    /// Opens a raw connection just to observe and capture the presented host key, trusting it
    /// unconditionally — simulating the out-of-band fingerprint verification a human performs during TOFU,
    /// before a separate, explicit <see cref="IHostKeyStore.PinAsync"/> call.
    /// </summary>
    private async Task<(string Algorithm, byte[] Blob)> ProbeRawHostKeyAsync(int? portOverride = null)
    {
        var connectionInfo = new ConnectionInfo(_host, portOverride ?? _port, SshTestContainer.Username, new PasswordAuthenticationMethod(SshTestContainer.Username, Password));
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

    // ---- Tests --------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Connect_with_password_auth_succeeds_and_reports_working_channels()
    {
        SkipUnlessDockerAvailable();

        var (algorithm, blob) = await ProbeRawHostKeyAsync();
        var hostKeyStore = MakeHostKeyStore();
        await hostKeyStore.PinAsync(new HostKeyRecord(_host, _port, algorithm, HostKeyFingerprint.ComputeSha256(blob), blob, DateTimeOffset.UtcNow, "test"), "test");
        var verifier = new HostKeyVerifier(hostKeyStore);

        var secretStore = await MakeSecretStoreWithPasswordAsync(Password);
        var descriptor = MakeDescriptor(
            ["secret://connector/it-conn/ssh/password"],
            new TrustPolicy.RequirePinned(),
            ConnectorChannel.Exec | ConnectorChannel.FileRead | ConnectorChannel.FileWrite | ConnectorChannel.DirectoryList);

        var connector = new SshConnector(descriptor, secretStore, verifier);
        await using var session = await connector.OpenAsync();

        session.AvailableChannels.Should().HaveFlag(ConnectorChannel.Exec);
        session.AvailableChannels.Should().HaveFlag(ConnectorChannel.FileRead);
        session.AvailableChannels.Should().HaveFlag(ConnectorChannel.FileWrite);
    }

    [SkippableFact]
    public async Task Connect_with_generated_keypair_succeeds()
    {
        SkipUnlessDockerAvailable();

        var (algorithm, blob) = await ProbeRawHostKeyAsync();
        var hostKeyStore = MakeHostKeyStore();
        await hostKeyStore.PinAsync(new HostKeyRecord(_host, _port, algorithm, HostKeyFingerprint.ComputeSha256(blob), blob, DateTimeOffset.UtcNow, "test"), "test");
        var verifier = new HostKeyVerifier(hostKeyStore);

        var secretStore = await MakeSecretStoreWithPrivateKeyAsync(_keyPair!);
        var descriptor = MakeDescriptor(
            ["secret://connector/it-conn/ssh/private-key"],
            new TrustPolicy.RequirePinned(),
            ConnectorChannel.Exec);

        var connector = new SshConnector(descriptor, secretStore, verifier);
        await using var session = await connector.OpenAsync();

        session.AvailableChannels.Should().HaveFlag(ConnectorChannel.Exec);

        var result = await session.ExecutionTarget.ExecuteAsync(new CommandSpec("whoami", []));
        result.Succeeded.Should().BeTrue();
        result.StandardOutput.Trim().Should().Be(SshTestContainer.Username);
    }

    [SkippableFact]
    public async Task Host_key_pinning_is_reused_across_two_separate_connections()
    {
        SkipUnlessDockerAvailable();

        var (algorithm, blob) = await ProbeRawHostKeyAsync();
        var hostKeyStore = MakeHostKeyStore();
        await hostKeyStore.PinAsync(new HostKeyRecord(_host, _port, algorithm, HostKeyFingerprint.ComputeSha256(blob), blob, DateTimeOffset.UtcNow, "test"), "test");
        var verifier = new HostKeyVerifier(hostKeyStore);
        var secretStore = await MakeSecretStoreWithPasswordAsync(Password);
        var descriptor = MakeDescriptor(["secret://connector/it-conn/ssh/password"], new TrustPolicy.RequirePinned(), ConnectorChannel.Exec);

        // First connection.
        var connectorA = new SshConnector(descriptor, secretStore, verifier);
        await using (var sessionA = await connectorA.OpenAsync())
        {
            (await sessionA.ExecutionTarget.ExecuteAsync(new CommandSpec("true", []))).Succeeded.Should().BeTrue();
        }

        // Second, independent connection against the same pinned record: must succeed without re-prompting
        // (there is no prompt in this model — RequirePinned either finds the pinned record and trusts it, or
        // refuses; it must not treat the second connection as "unknown" just because it's a new session).
        var connectorB = new SshConnector(descriptor, secretStore, verifier);
        await using var sessionB = await connectorB.OpenAsync();
        (await sessionB.ExecutionTarget.ExecuteAsync(new CommandSpec("true", []))).Succeeded.Should().BeTrue();
    }

    [SkippableFact]
    public async Task Changed_host_key_is_refused()
    {
        SkipUnlessDockerAvailable();

        // A fixed host port is required here specifically so a second, differently-keyed container can
        // occupy the exact same host:port the first one was pinned under — that's what makes the verifier
        // see "same host:port, different key" (Changed) rather than "different host:port" (a new Unknown).
        const int fixedPort = 22422;

        await using var containerA = await SshTestContainer.StartAsync(Password, publicKeyLine: null, fixedHostPort: fixedPort);
        var (algorithmA, blobA) = await ProbeRawHostKeyAsync(fixedPort);

        var hostKeyStore = MakeHostKeyStore();
        await hostKeyStore.PinAsync(new HostKeyRecord("localhost", fixedPort, algorithmA, HostKeyFingerprint.ComputeSha256(blobA), blobA, DateTimeOffset.UtcNow, "test"), "test");
        var verifier = new HostKeyVerifier(hostKeyStore);

        await containerA.DisposeAsync();

        await using var containerB = await SshTestContainer.StartAsync(Password, publicKeyLine: null, fixedHostPort: fixedPort);

        var secretStore = await MakeSecretStoreWithPasswordAsync(Password);
        var descriptor = new ConnectorDescriptor(
            ConnectorId: "it-conn-changed",
            Kind: "ssh",
            DisplayName: "changed host key test",
            TransportId: "ssh",
            Endpoint: $"ssh:{SshTestContainer.Username}@localhost:{fixedPort}",
            CredentialRefs: ["secret://connector/it-conn/ssh/password"],
            Trust: new TrustPolicy.RequirePinned(),
            Timeouts: TimeoutPolicy.Default with { Connect = TimeSpan.FromSeconds(20) },
            DeclaredChannels: ConnectorChannel.Exec);

        var connector = new SshConnector(descriptor, secretStore, verifier);

        var act = () => connector.OpenAsync();

        var thrown = await act.Should().ThrowAsync<HostKeyRejectedException>();
        thrown.Which.Verdict.Should().Be(HostKeyVerdict.Changed);
    }

    [SkippableFact]
    public async Task Read_a_file_returns_content_written_via_exec()
    {
        SkipUnlessDockerAvailable();

        await using var target = await OpenTrustedSessionAsync(ConnectorChannel.Exec | ConnectorChannel.FileRead | ConnectorChannel.FileWrite);

        await target.ExecutionTarget.ExecuteAsync(new CommandSpec("sh", ["-c", "printf 'hello from servyx' > /tmp/servyx-read-test.txt"]));

        var path = ResolveRemoteTmpPath("servyx-read-test.txt");

        await using var stream = await target.ExecutionTarget.OpenReadAsync(path);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();

        content.Should().Be("hello from servyx");
    }

    [SkippableFact]
    public async Task Atomic_write_then_read_back_round_trips_and_uses_the_posix_rename_extension()
    {
        SkipUnlessDockerAvailable();

        var logger = new TestLogger();
        await using var target = await OpenTrustedSessionAsync(ConnectorChannel.FileRead | ConnectorChannel.FileWrite, logger);

        var path = ResolveRemoteTmpPath("servyx-atomic-write-test.txt");

        var content = "line one\nline two\n"u8.ToArray();
        var receipt = await target.ExecutionTarget.WriteFileAsync(path, new MemoryStream(content), new FileWriteOptions(null));

        receipt.PreImageSha256.Should().BeNull("the file did not exist before this write");
        receipt.PostImageSha256.Should().NotBeNullOrEmpty();

        await using var stream = await target.ExecutionTarget.OpenReadAsync(path);
        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).Should().Be("line one\nline two\n");

        logger.Entries.Should().NotContain(
            e => e.Message.Contains("non-atomic"),
            "the container's OpenSSH sftp-server is expected to support posix-rename@openssh.com");
    }

    [SkippableFact]
    public async Task Write_is_refused_when_the_preimage_hash_does_not_match()
    {
        SkipUnlessDockerAvailable();

        await using var target = await OpenTrustedSessionAsync(ConnectorChannel.Exec | ConnectorChannel.FileRead | ConnectorChannel.FileWrite);

        await target.ExecutionTarget.ExecuteAsync(new CommandSpec("sh", ["-c", "printf 'original content' > /tmp/servyx-drift-test.txt"]));

        var path = ResolveRemoteTmpPath("servyx-drift-test.txt");

        var act = () => target.ExecutionTarget.WriteFileAsync(
            path,
            new MemoryStream("new content"u8.ToArray()),
            new FileWriteOptions("0000000000000000000000000000000000000000000000000000000000000000"));

        await act.Should().ThrowAsync<TargetDriftException>();

        // The original content must be untouched: the write must have been refused before any I/O to the target.
        await using var stream = await target.ExecutionTarget.OpenReadAsync(path);
        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).Should().Be("original content");
    }

    [SkippableFact]
    public async Task Exec_captures_stdout_and_exit_code()
    {
        SkipUnlessDockerAvailable();

        await using var target = await OpenTrustedSessionAsync(ConnectorChannel.Exec);

        var result = await target.ExecutionTarget.ExecuteAsync(new CommandSpec("echo", ["hello", "world"]));

        result.Succeeded.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.StandardOutput.Trim().Should().Be("hello world");
    }

    [SkippableFact]
    public async Task Exec_with_a_hostile_argument_is_inert()
    {
        SkipUnlessDockerAvailable();

        await using var target = await OpenTrustedSessionAsync(ConnectorChannel.Exec | ConnectorChannel.FileRead);

        const string hostile = "; touch /tmp/servyx-pwned; echo injected";

        // If quoting failed, this would execute `touch /tmp/servyx-pwned` as a second command and echo
        // "hello  injected" instead of the literal hostile string.
        var result = await target.ExecutionTarget.ExecuteAsync(new CommandSpec("echo", ["hello", hostile]));

        result.Succeeded.Should().BeTrue();
        result.StandardOutput.Trim().Should().Be($"hello {hostile}");

        var markerCheck = await target.ExecutionTarget.ExecuteAsync(new CommandSpec("test", ["-e", "/tmp/servyx-pwned"]));
        markerCheck.Succeeded.Should().BeFalse("the hostile argument must never have been interpreted as a second command");
    }

    [SkippableFact]
    public async Task Exec_only_fallback_ShellFileChannel_writes_and_reads_a_file_over_the_exec_channel()
    {
        SkipUnlessDockerAvailable();

        var (algorithm, blob) = await ProbeRawHostKeyAsync();
        var hostKeyStore = MakeHostKeyStore();
        await hostKeyStore.PinAsync(new HostKeyRecord(_host, _port, algorithm, HostKeyFingerprint.ComputeSha256(blob), blob, DateTimeOffset.UtcNow, "test"), "test");

        var connectionInfo = new ConnectionInfo(_host, _port, SshTestContainer.Username, new PasswordAuthenticationMethod(SshTestContainer.Username, Password));
        using var sshClient = new SshClient(connectionInfo);
        sshClient.HostKeyReceived += (_, e) => e.CanTrust = true; // Already pinned above; this is just the live handshake.
        await sshClient.ConnectAsync(CancellationToken.None);

        await using var shellFileChannel = new ShellFileChannel(sshClient, ownsClient: false, TimeSpan.FromSeconds(10));

        var path = ResolveRemoteTmpPath("servyx-shell-fallback-test.txt");

        var content = "content written via cat > path\n"u8.ToArray();
        var receipt = await shellFileChannel.WriteFileAsync(path, new MemoryStream(content), new FileWriteOptions(null));
        receipt.PostImageSha256.Should().NotBeNullOrEmpty();

        (await shellFileChannel.ExistsAsync(path)).Should().BeTrue();

        await using var readBack = await shellFileChannel.OpenReadAsync(path);
        using var reader = new StreamReader(readBack);
        (await reader.ReadToEndAsync()).Should().Be("content written via cat > path\n");

        var stat = await shellFileChannel.StatAsync(path);
        stat.Exists.Should().BeTrue();
        stat.SizeBytes.Should().Be(content.LongLength);
    }

    private async Task<IConnectorSession> OpenTrustedSessionAsync(ConnectorChannel channels, TestLogger? logger = null)
    {
        var (algorithm, blob) = await ProbeRawHostKeyAsync();
        var hostKeyStore = MakeHostKeyStore();
        await hostKeyStore.PinAsync(new HostKeyRecord(_host, _port, algorithm, HostKeyFingerprint.ComputeSha256(blob), blob, DateTimeOffset.UtcNow, "test"), "test");
        var verifier = new HostKeyVerifier(hostKeyStore);
        var secretStore = await MakeSecretStoreWithPasswordAsync(Password);
        var descriptor = MakeDescriptor(["secret://connector/it-conn/ssh/password"], new TrustPolicy.RequirePinned(), channels);

        var loggerFactory = logger is null
            ? null
            : new ForwardingLoggerFactory(logger);

        var connector = new SshConnector(descriptor, secretStore, verifier, loggerFactory);
        return await connector.OpenAsync();
    }

    /// <summary>Routes every category's logger to the same <see cref="TestLogger"/> instance, for simple assertion on what was logged.</summary>
    private sealed class ForwardingLoggerFactory(TestLogger logger) : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider)
        {
        }

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }
}
