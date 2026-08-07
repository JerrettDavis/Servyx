using System.Text;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Renci.SshNet;
using Servyx.Domain.Connectors;
using Servyx.Domain.Discovery;
using Servyx.Domain.Observability;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Connectors;
using Servyx.Infrastructure.Ssh.Docker;
using Xunit;

namespace Servyx.Infrastructure.Ssh.Tests.Integration;

/// <summary>
/// End-to-end integration tests proving the "ssh+docker" transport (<see cref="SshDockerTransport"/>,
/// <see cref="SshDockerServerDiscovery"/>, <see cref="SshDockerLogStream"/>, <see cref="SshDockerMetricsSource"/>)
/// against a REAL SSH connection, REAL command dispatch, and REAL JSON parsing — without any Docker daemon
/// on the far side and without ever contacting a production host.
/// </summary>
/// <remarks>
/// <para>
/// <b>Technique:</b> start a throwaway <c>linuxserver/openssh-server</c> Testcontainer (same image and
/// helper as <see cref="SshIntegrationTests"/> — see <see cref="SshTestContainer"/>), plant a STUB
/// <c>/usr/local/bin/docker</c> shell script that dispatches on argv and echoes canned fixture JSON captured
/// from a real production Palworld container (secrets scrubbed, public IP rewritten to
/// <c>203.0.113.10</c> — see <c>TestData/</c>), then drive the real transport, discovery, log stream, and
/// metrics source against it exactly as production code would. Every "docker" invocation in these tests is
/// this shell script running inside the throwaway container; nothing here ever touches a real Docker Engine
/// or a real remote host.
/// </para>
/// <para>
/// <b>Stub planting:</b> fixture files and the stub script are written into the container via
/// <see cref="IContainer.CopyAsync(byte[], string, uint, uint, UnixFileModes, CancellationToken)"/> — this
/// Testcontainers version (4.13.0) exposes a byte-array overload directly, so no <c>ExecAsync</c>
/// base64-through-exec workaround is needed. <c>CopyAsync</c> is tar-based (equivalent to <c>docker cp</c>)
/// and does NOT create missing parent directories (confirmed against a live container before writing this
/// file — <c>docker cp</c> onto a non-existent directory fails outright), so <c>/opt/fixtures</c> is created
/// with <c>mkdir -p</c> via <see cref="IContainer.ExecAsync"/> first; <c>/usr/local/bin</c> already exists
/// in the base image.
/// </para>
/// <para>
/// <b>PATH finding:</b> a non-interactive SSH exec session on <c>linuxserver/openssh-server</c> was verified
/// live (before writing this file, via a real keypair and a real container) to already carry
/// <c>PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin</c> for the exec'd non-login shell —
/// sshd does not reset PATH to some narrower compiled-in default here, and the exec'd shell does not source
/// any rc file that could have narrowed it. So <c>/usr/local/bin/docker</c> is on PATH with no extra
/// handling required; this remark exists so a future reader does not have to re-derive that.
/// </para>
/// <para>
/// <b>Exit code ground truth:</b> also verified live rather than assumed. A stub present but not marked
/// executable (default <c>CopyAsync</c> file mode, root-owned) yields exit 126 ("Permission denied") for a
/// non-root SSH user, matching <see cref="SshDockerTransport.ProbeAsync"/>'s exit-126 branch exactly. A
/// missing stub yields exit 127 ("command not found"), matching the exit-127 branch. Both are exercised
/// below using the real observed codes, not assumed ones.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SshDockerIntegrationTests : IAsyncLifetime
{
    private const string Password = "servyx-test-password";
    private const string ContainerName = "palworld-server";
    private const string ImageRepository = "thijsvanloef/palworld-server-docker";
    private const string RequiredMountPath = "/palworld";
    private const string PrivateKeyUrn = "secret://connector/it-ssh-docker/ssh/private-key";
    private const string FixturesDirectory = "/opt/fixtures";
    private const string InvocationLog = FixturesDirectory + "/invocations.log";
    private const string StubPath = "/usr/local/bin/docker";

    private static readonly string[] FixtureFiles =
    [
        "docker-version.json",
        "palworld-container-ls.jsonl",
        "palworld-inspect.json",
        "palworld-logs.txt",
        "palworld-stats.json",
    ];

    // rwxr-xr-x: the stub must be runnable by the non-root SSH user (other-execute) as well as root (who
    // planted it via CopyAsync).
    private static readonly UnixFileModes ExecutableStubMode =
        UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute |
        UnixFileModes.GroupRead | UnixFileModes.GroupExecute |
        UnixFileModes.OtherRead | UnixFileModes.OtherExecute;

    // rw-r--r-- (CopyAsync's own default): deliberately NOT executable, for the permission-denied scenario.
    private static readonly UnixFileModes NonExecutableStubMode =
        UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.GroupRead | UnixFileModes.OtherRead;

    private IContainer? _container;
    private string _host = string.Empty;
    private int _port;
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

            // /usr/local/bin already exists in the base image; /opt/fixtures does not, and CopyAsync will
            // not create it, so it (and a world-writable, pre-existing invocation log) is prepared up front.
            await _container.ExecAsync(["mkdir", "-p", FixturesDirectory]);
            await _container.ExecAsync(["sh", "-c", $"touch {InvocationLog} && chmod 666 {InvocationLog} && chmod 777 {FixturesDirectory}"]);

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

    // ---- Stub + fixture planting ---------------------------------------------------------------------------

    /// <summary>Copies the real captured fixtures into the container, un-parsed, exactly as bytes on disk.</summary>
    private async Task PlantFixturesAsync()
    {
        foreach (var name in FixtureFiles)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "TestData", name));
            await _container!.CopyAsync(bytes, $"{FixturesDirectory}/{name}");
        }
    }

    /// <summary>
    /// Plants fixtures plus the stub <c>docker</c> script at <see cref="StubPath"/>, either executable or
    /// not, per <paramref name="executable"/>.
    /// </summary>
    private async Task PlantDockerStubAsync(bool executable)
    {
        await PlantFixturesAsync();

        var script = BuildStubScript();
        var bytes = Encoding.UTF8.GetBytes(script);
        await _container!.CopyAsync(bytes, StubPath, fileMode: executable ? ExecutableStubMode : NonExecutableStubMode);
    }

    /// <summary>
    /// Builds the stub's POSIX shell script text with explicit LF line endings (joined with "\n" rather than
    /// relying on the source file's own line-ending convention, which on Windows would otherwise smuggle CRLF
    /// into the shebang line and break interpreter resolution). Every invocation is logged in full — one
    /// line per argv element, with its index — to <see cref="InvocationLog"/> BEFORE dispatch, which is what
    /// makes both the quoting-fidelity assertion and the write-guard "never reached the stub" assertion
    /// possible from the same mechanism.
    /// </summary>
    private static string BuildStubScript()
    {
        string[] lines =
        [
            "#!/bin/sh",
            $"LOG={InvocationLog}",
            "{",
            "  echo '--- invocation start ---'",
            "  i=0",
            "  for a in \"$@\"; do",
            "    i=$((i+1))",
            "    echo \"arg[$i]: $a\"",
            "  done",
            "  echo '--- invocation end ---'",
            "} >> \"$LOG\"",
            "",
            "case \"$1\" in",
            "  version)",
            $"    cat {FixturesDirectory}/docker-version.json",
            "    exit 0",
            "    ;;",
            "  container)",
            "    case \"$2\" in",
            "      ls)",
            $"        cat {FixturesDirectory}/palworld-container-ls.jsonl",
            "        exit 0",
            "        ;;",
            "      inspect)",
            $"        cat {FixturesDirectory}/palworld-inspect.json",
            "        exit 0",
            "        ;;",
            "    esac",
            "    ;;",
            "  logs)",
            $"    cat {FixturesDirectory}/palworld-logs.txt",
            "    exit 0",
            "    ;;",
            "  stats)",
            $"    cat {FixturesDirectory}/palworld-stats.json",
            "    exit 0",
            "    ;;",
            "esac",
            "",
            "echo \"stub: unrecognized docker invocation: $*\" >&2",
            "exit 1",
            "",
        ];

        return string.Join("\n", lines);
    }

    /// <summary>Reads the stub's invocation log via a genuinely read-only, guard-permitted raw exec.</summary>
    private static async Task<string> ReadInvocationLogAsync(IExecutionTarget target)
    {
        var result = await target.ExecuteAsync(
            new CommandSpec("cat", [InvocationLog], Intent: CommandIntent.ReadOnly));
        return result.Succeeded ? result.StandardOutput : string.Empty;
    }

    // ---- Transport / credential construction ----------------------------------------------------------------

    /// <summary>
    /// Opens a raw connection just to observe and capture the presented host key, trusting it
    /// unconditionally — mirrors <c>SshIntegrationTests.ProbeRawHostKeyAsync</c> exactly (out-of-band
    /// fingerprint capture standing in for a human's TOFU verification, ahead of an explicit
    /// <see cref="IHostKeyStore.PinAsync"/> call).
    /// </summary>
    private async Task<(string Algorithm, byte[] Blob)> ProbeRawHostKeyAsync()
    {
        var connectionInfo = new ConnectionInfo(
            _host, _port, SshTestContainer.Username, new PasswordAuthenticationMethod(SshTestContainer.Username, Password));
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

    private async Task<IHostKeyVerifier> PinHostKeyAsync()
    {
        var (algorithm, blob) = await ProbeRawHostKeyAsync();
        var hostKeyStore = new FileHostKeyStore(Path.Combine(Path.GetTempPath(), $"servyx-it-dockerhostkeys-{Guid.NewGuid():N}.json"));
        await hostKeyStore.PinAsync(
            new HostKeyRecord(_host, _port, algorithm, HostKeyFingerprint.ComputeSha256(blob), blob, DateTimeOffset.UtcNow, "test"),
            "test");
        return new HostKeyVerifier(hostKeyStore);
    }

    private async Task<InMemorySecretStore> MakeSecretStoreAsync()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(
            SecretUrn.Create("connector", "it-ssh-docker", "ssh", "private-key"),
            Encoding.ASCII.GetBytes(_keyPair!.PrivateKeyPem),
            "test");
        return store;
    }

    private TargetDescriptor MakeDescriptor() => new(
        TransportId: "ssh+docker",
        Endpoint: $"{SshTestContainer.Username}@{_host}:{_port}",
        CredentialUrn: PrivateKeyUrn,
        DockerContext: null,
        Options: new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Builds a real <see cref="SshDockerTransport"/> wired to a pinned, real SSH connection.</summary>
    private async Task<(SshDockerTransport Transport, TargetDescriptor Descriptor)> CreateTransportAsync()
    {
        var verifier = await PinHostKeyAsync();
        var secretStore = await MakeSecretStoreAsync();
        var sshTransport = new SshTransport(secretStore, verifier);
        var dockerTransport = new SshDockerTransport(sshTransport);
        return (dockerTransport, MakeDescriptor());
    }

    // ---- Tests ------------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Probe_reports_healthy_against_the_stub_docker()
    {
        SkipUnlessDockerAvailable();
        await PlantDockerStubAsync(executable: true);

        var (transport, descriptor) = await CreateTransportAsync();

        var health = await transport.ProbeAsync(descriptor);

        health.Reachable.Should().BeTrue();
        // "Server": { "Version": "29.7.0" } in TestData/docker-version.json.
        health.Detail.Should().Contain("29.7.0");
    }

    [SkippableFact]
    public async Task Probe_reports_docker_missing_when_the_stub_is_absent()
    {
        SkipUnlessDockerAvailable();
        // No stub planted: /usr/local/bin/docker does not exist on this fresh container.

        var (transport, descriptor) = await CreateTransportAsync();

        var health = await transport.ProbeAsync(descriptor);

        health.Reachable.Should().BeFalse();
        health.Detail.Should().Contain("not found");
    }

    [SkippableFact]
    public async Task Probe_reports_permission_denied_when_the_stub_is_not_executable()
    {
        SkipUnlessDockerAvailable();
        await PlantDockerStubAsync(executable: false);

        var (transport, descriptor) = await CreateTransportAsync();

        var health = await transport.ProbeAsync(descriptor);

        // Observed live against this exact image/scenario before writing this test: a root-owned, non-executable
        // file yields exit 126 ("Permission denied") for the non-root SSH user, not 127.
        health.Reachable.Should().BeFalse();
        health.Detail.Should().Contain("permission denied");
        health.Detail.Should().Contain("docker");
        health.Detail.Should().NotContain("not found");
    }

    [SkippableFact]
    public async Task Discovery_finds_the_palworld_container_over_real_ssh()
    {
        SkipUnlessDockerAvailable();
        await PlantDockerStubAsync(executable: true);

        var (transport, descriptor) = await CreateTransportAsync();
        await using var target = await transport.ConnectAsync(descriptor);
        var discovery = new SshDockerServerDiscovery(target);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        var server = results.Should().ContainSingle().Subject;
        server.Name.Should().Be(ContainerName);
        server.Image.Should().Be("thijsvanloef/palworld-server-docker:latest");
        server.HealthStatus.Should().Be("unhealthy");
        server.Mounts.Should().Contain(m => m.Source == "/opt/palworld/data" && m.Destination == RequiredMountPath);
    }

    [SkippableFact]
    public async Task Discovery_surfaces_rcon_25575_as_exposed_but_not_published()
    {
        SkipUnlessDockerAvailable();
        await PlantDockerStubAsync(executable: true);

        var (transport, descriptor) = await CreateTransportAsync();
        await using var target = await transport.ConnectAsync(descriptor);
        var discovery = new SshDockerServerDiscovery(target);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        var server = results.Should().ContainSingle().Subject;
        var rcon = server.Ports.Should().ContainSingle(p => p.ContainerPort == 25575 && p.Protocol == "tcp").Subject;
        rcon.HostPort.Should().BeNull("25575/tcp is exposed inside the container but never published to a host port");

        // And the genuinely published game ports are NOT mistaken for the same thing.
        server.Ports.Should().Contain(p => p.ContainerPort == 8211 && p.Protocol == "udp" && p.HostPort == 8211);
        server.Ports.Should().Contain(p => p.ContainerPort == 27015 && p.Protocol == "udp" && p.HostPort == 27015);
    }

    [SkippableFact]
    public async Task Go_template_format_arguments_survive_shell_quoting()
    {
        SkipUnlessDockerAvailable();
        await PlantDockerStubAsync(executable: true);

        var (transport, descriptor) = await CreateTransportAsync();
        await using var target = await transport.ConnectAsync(descriptor);

        // DockerCli.Version()'s argv is ["version", "--format", "{{json .}}"] — the third element contains
        // an internal space, braces, and a dot. If PosixArgv's single-quote escaping (or the SSH exec
        // channel itself) mangled or word-split it, the stub would see something other than this exact
        // three-token argv, and/or `docker version --format {{json .}}` would fail to produce JSON.
        var result = await target.ExecuteAsync(DockerCli.Version());
        result.Succeeded.Should().BeTrue();

        var log = await ReadInvocationLogAsync(target);
        log.Should().Contain("arg[1]: version");
        log.Should().Contain("arg[2]: --format");
        log.Should().Contain("arg[3]: {{json .}}", "the Go template argument must arrive byte-exact — braces, dot, and internal space intact");
    }

    [SkippableFact]
    public async Task Log_stream_returns_lines_over_real_ssh()
    {
        SkipUnlessDockerAvailable();
        await PlantDockerStubAsync(executable: true);

        var (transport, descriptor) = await CreateTransportAsync();
        await using var target = await transport.ConnectAsync(descriptor);
        var logStream = new SshDockerLogStream(target);

        var lines = new List<ConsoleLine>();
        await foreach (var line in logStream.FollowAsync(ContainerName, new ConsoleTailOptions(200)))
        {
            lines.Add(line);
        }

        lines.Should().NotBeEmpty();
        lines[0].Text.Should().Be("[2026-08-05 13:15:33] [LOG] REST accessed endpoint /v1/api/players OK (x6)");
        lines[0].Stream.Should().Be(OutputStream.StdOut);
    }

    [SkippableFact]
    public async Task Metrics_report_cpu_and_memory_over_real_ssh()
    {
        SkipUnlessDockerAvailable();
        await PlantDockerStubAsync(executable: true);

        var (transport, descriptor) = await CreateTransportAsync();
        await using var target = await transport.ConnectAsync(descriptor);
        var metrics = new SshDockerMetricsSource(target, pollInterval: TimeSpan.FromMilliseconds(50));

        ResourceSample? sample = null;
        await foreach (var candidate in metrics.StreamAsync(ContainerName))
        {
            sample = candidate;
            break;
        }

        sample.Should().NotBeNull();
        sample!.CpuPercent.Should().BeApproximately(138.51, 0.01);
        // "2.141GiB" as reported by TestData/palworld-stats.json's MemUsage field.
        sample.MemoryBytes.Should().Be((long)(2.141 * 1024 * 1024 * 1024));
    }

    [SkippableFact]
    public async Task Mutating_docker_command_is_refused_before_reaching_the_stub()
    {
        SkipUnlessDockerAvailable();
        await PlantDockerStubAsync(executable: true);

        var (innerTransport, descriptor) = await CreateTransportAsync();
        var guarded = new WriteGuardedTransport(innerTransport); // default resolver: every target read-only.
        await using var target = await guarded.ConnectAsync(descriptor);

        var act = () => target.ExecuteAsync(DockerCli.Stop(ContainerName, 30));

        await act.Should().ThrowAsync<WritesDisabledException>();

        // The proof that this was refused BEFORE reaching the stub, not after: the stub logs every
        // invocation it receives, in full, before doing anything else, so a "stop" invocation reaching it
        // would show up here regardless of what the stub's dispatch logic then did with it.
        var log = await ReadInvocationLogAsync(target);
        log.Should().NotContain("arg[1]: stop");
    }
}
