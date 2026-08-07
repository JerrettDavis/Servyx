using System.Collections.Concurrent;
using Servyx.Domain.Discovery;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Remote.Tests;

/// <summary>
/// A STRICTLY READ-ONLY smoke suite that drives the real <c>ssh+docker</c> transport
/// (<see cref="SshDockerTransport"/>, <see cref="SshDockerServerDiscovery"/>,
/// <see cref="SshDockerLogStream"/>, <see cref="SshDockerMetricsSource"/>) against a LIVE, PRODUCTION
/// game server that real people are playing on right now.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read the safety contract before editing anything in this file.</b> The only docker verbs this suite
/// is permitted to issue are <c>version</c>, <c>container ls</c>, <c>container inspect</c>, <c>logs</c>,
/// and <c>stats</c> — exactly the read-only half of <see cref="DockerCli"/>.
/// <see cref="Every_command_this_suite_issues_is_read_only"/> asserts that mechanically, from a recording
/// decorator that sits between the write guard and the wire, so adding a mutating call here fails a test
/// rather than mutating production.
/// </para>
/// <para>
/// <b>Why constructing a mutating command is nonetheless safe.</b>
/// <see cref="Stopping_the_container_is_refused_before_any_io"/> builds a real
/// <see cref="DockerCli.Stop(string, int)"/> spec purely to prove it is refused.
/// <see cref="WriteGuardedExecutionTarget.ExecuteAsync"/> calls its intent check and only then
/// <c>return _inner.ExecuteAsync(...)</c> — the throw is synchronous and happens before the inner target is
/// touched at all, so the spec never becomes an argv, never reaches the SSH exec channel, and never leaves
/// this machine. That claim is verified twice over here: the refusal itself, and the fact that the
/// recording decorator (which sits <em>inside</em> the guard) never sees the spec.
/// </para>
/// <para>
/// <b>Every connection this suite opens is write-guarded.</b> The fixture wraps the transport in
/// <see cref="WriteGuardedTransport"/> with its DEFAULT resolver — <c>ReadOnlyWriteModeResolver</c>, i.e.
/// every target is <see cref="WriteMode.ReadOnly"/> — so the refusal above is not a special case set up for
/// one test; it is the posture the whole suite runs under. A mutating spec smuggled into any test here is
/// refused by the same guard.
/// </para>
/// <para>
/// <b>Gating.</b> Four independent layers, all of which must be satisfied simultaneously:
/// (1) the .csproj's <c>VSTestTestCaseFilter</c> of <c>Category!=Integration</c>, so a bare
/// <c>dotnet test</c> runs zero tests here; (2) <c>[Trait("Category", "Integration")]</c> on this class;
/// (3) <c>[SkippableFact]</c> plus <c>Skip.IfNot</c> on <c>SERVYX_REMOTE_E2E=1</c>; (4) every coordinate
/// read from the environment by <see cref="RemoteTestEnvironment"/>, with a missing one producing a SKIP
/// rather than a failure. This project is also absent from <c>Servyx.sln</c> and from
/// <c>.github/workflows/ci.yml</c>'s run list, for the same reason
/// <c>tests/Servyx.E2E.Tests</c> is.
/// </para>
/// <para>
/// <b>Operator setup.</b> The private key commonly lives inside WSL, where a Windows-hosted test runner
/// cannot read it. Produce a Windows-readable copy first, point
/// <c>SERVYX_REMOTE_KEY_PATH</c> at it, and delete it when the run is done:
/// </para>
/// <code>
/// wsl cp ~/.ssh/&lt;your-key&gt; /mnt/c/Users/&lt;you&gt;/AppData/Local/Temp/&lt;unique-name&gt;
///
/// $env:SERVYX_REMOTE_E2E        = "1"
/// $env:SERVYX_REMOTE_ENDPOINT   = "ssh:&lt;user&gt;@&lt;host&gt;:22"
/// $env:SERVYX_REMOTE_KEY_PATH   = "C:\Users\&lt;you&gt;\AppData\Local\Temp\&lt;unique-name&gt;"
/// $env:SERVYX_REMOTE_CONTAINER  = "&lt;container-name&gt;"
/// $env:SERVYX_REMOTE_FINGERPRINT = "SHA256:&lt;base64&gt;"   # wsl ssh-keygen -F &lt;host&gt; -l
/// dotnet test tests\Servyx.Remote.Tests --filter "Category=Integration"
///
/// Remove-Item $env:SERVYX_REMOTE_KEY_PATH
/// </code>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RemoteReadOnlySmokeTests : IClassFixture<RemoteSessionFixture>
{
    /// <summary>
    /// The image repository the Palworld deployment profile adopts on, tag and digest ignored. This is a
    /// game-definition constant, not a production coordinate — it identifies which workload shape Servyx
    /// is looking for, and reveals nothing about where that workload runs.
    /// </summary>
    private const string ImageRepository = "thijsvanloef/palworld-server-docker";

    /// <summary>The container-side mount destination the same profile requires.</summary>
    private const string RequiredMountContainerPath = "/palworld";

    /// <summary>The host-side bind-mount source that backs it.</summary>
    private const string ExpectedMountHostPath = "/opt/palworld/data";

    private const int RconPort = 25575;
    private const int GamePort = 8211;
    private const int QueryPort = 27015;

    private readonly RemoteSessionFixture _fixture;

    /// <summary>Creates the test class over the shared, write-guarded remote session.</summary>
    public RemoteReadOnlySmokeTests(RemoteSessionFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    private static void SkipUnlessConfigured() =>
        Skip.IfNot(RemoteTestEnvironment.MissingReason is null, RemoteTestEnvironment.MissingReason ?? string.Empty);

    private static CancellationTokenSource NewTimeout() => new(TimeSpan.FromMinutes(2));

    // ---- 1. Reachability -------------------------------------------------------------------------------

    /// <summary>
    /// <c>docker version</c> over the real SSH exec channel: the daemon answers, and Servyx folds the
    /// engine's own version into the health detail rather than reporting a bare "reachable".
    /// </summary>
    [SkippableFact]
    public async Task Probe_reaches_the_remote_docker_daemon()
    {
        SkipUnlessConfigured();
        using var cts = NewTimeout();

        var transport = await _fixture.GetTransportAsync(cts.Token);
        var health = await transport.ProbeAsync(_fixture.Descriptor, cts.Token);

        health.Reachable.Should().BeTrue(health.Detail);
        health.Latency.Should().NotBeNull();

        // Asserted by shape, not by a pinned literal: the point is that Servyx surfaces the DAEMON's
        // version (SshDockerTransport.DescribeHealthy parses Server.Version out of `docker version`'s
        // JSON), and a production host is free to be upgraded between runs.
        health.Detail.Should().MatchRegex(@"Server version \d+\.\d+");
    }

    // ---- 2. Discovery ----------------------------------------------------------------------------------

    /// <summary>Real <c>container ls</c> + <c>container inspect</c> adopt the live Palworld container.</summary>
    [SkippableFact]
    public async Task Discovery_finds_the_palworld_container()
    {
        SkipUnlessConfigured();
        using var cts = NewTimeout();

        var server = await DiscoverAsync(cts.Token);

        server.Name.Should().Be(RemoteTestEnvironment.Current!.Container);
        DockerInspectJson.StripTagAndDigest(server.Image).Should().Be(ImageRepository);
    }

    // ---- 3. THE key assertion --------------------------------------------------------------------------

    /// <summary>
    /// RCON's 25575/tcp is EXPOSED inside the container but NOT PUBLISHED to any host port, and Servyx says
    /// so by reporting <see cref="DiscoveredPort.HostPort"/> as <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// This is the assertion the whole suite exists for. Docker reports an exposed-but-unpublished port as
    /// a JSON <c>null</c> value under <c>NetworkSettings.Ports</c>, which
    /// <see cref="DockerInspectJson"/> maps to a single <see cref="DiscoveredPort"/> with a null host port.
    /// A caller that could not tell that apart from "published somewhere" would try to open a TCP socket to
    /// a port nothing is listening on — the whole reason Servyx must reach RCON through the container's
    /// control channel rather than a direct host socket.
    /// </remarks>
    [SkippableFact]
    public async Task Inspect_reports_rcon_25575_as_exposed_but_not_published()
    {
        SkipUnlessConfigured();
        using var cts = NewTimeout();

        var server = await InspectAsync(cts.Token);

        var rcon = server.Ports.Should()
            .ContainSingle(p => p.ContainerPort == RconPort && p.Protocol == "tcp")
            .Subject;

        rcon.HostPort.Should().BeNull(
            "25575/tcp is exposed inside the container but never published to a host port, so it is " +
            "unreachable by a direct TCP connection from anywhere off the box");
    }

    // ---- 4. The published ports ------------------------------------------------------------------------

    /// <summary>
    /// The game (8211/udp) and query (27015/udp) ports ARE published, so neither is mistaken for the
    /// exposed-only RCON port.
    /// </summary>
    /// <remarks>
    /// Each is asserted as "at least one binding, and every binding has a host port" rather than as a
    /// single entry: a published port routinely carries one binding for the IPv4 wildcard and another for
    /// the IPv6 wildcard, and <see cref="DockerInspectJson"/> deliberately surfaces every binding rather
    /// than collapsing them.
    /// </remarks>
    [SkippableFact]
    public async Task Inspect_reports_the_published_game_and_query_ports()
    {
        SkipUnlessConfigured();
        using var cts = NewTimeout();

        var server = await InspectAsync(cts.Token);

        foreach (var port in (int[])[GamePort, QueryPort])
        {
            var bindings = server.Ports.Where(p => p.ContainerPort == port && p.Protocol == "udp").ToList();

            bindings.Should().NotBeEmpty($"{port}/udp is a published port on this deployment");
            bindings.Should().OnlyContain(
                p => p.HostPort != null,
                $"every binding of the published {port}/udp must carry a host port");
        }
    }

    // ---- 5. The bind mount -----------------------------------------------------------------------------

    /// <summary>The save-data bind mount is reported with both its host source and container destination.</summary>
    [SkippableFact]
    public async Task Inspect_reports_the_expected_bind_mount()
    {
        SkipUnlessConfigured();
        using var cts = NewTimeout();

        var server = await InspectAsync(cts.Token);

        server.Mounts.Should().Contain(
            m => m.Source == ExpectedMountHostPath && m.Destination == RequiredMountContainerPath);
    }

    // ---- 6. Health -------------------------------------------------------------------------------------

    /// <summary>
    /// Whatever the container's healthcheck currently says, Servyx SURFACES it rather than swallowing it or
    /// substituting a guess.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test deliberately does not assert the server is broken.</b> On this deployment the
    /// healthcheck is a known FALSE NEGATIVE — it probes an internal REST API that answers HTTP 401, so
    /// docker reports <c>unhealthy</c> while the game server is perfectly playable. Asserting
    /// <c>unhealthy</c> as the expected value would enshrine a bug in the workload's healthcheck as a
    /// Servyx requirement, and asserting <c>healthy</c> would fail for a reason that has nothing to do with
    /// Servyx.
    /// </para>
    /// <para>
    /// So what is asserted is the contract Servyx actually owns: the container is <em>running</em>, and the
    /// health status is reported as one of docker's own health values rather than the <c>"none"</c>
    /// <see cref="DockerInspectJson"/> substitutes when a container declares no healthcheck at all. That
    /// distinction is the whole point — "the healthcheck says unhealthy" and "there is no healthcheck" must
    /// never look the same to an operator.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Health_status_is_surfaced_even_though_it_is_a_false_negative()
    {
        SkipUnlessConfigured();
        using var cts = NewTimeout();

        var server = await InspectAsync(cts.Token);

        server.State.Should().Be("running");
        server.HealthStatus.Should().BeOneOf(
            "healthy", "unhealthy", "starting");
    }

    // ---- 7. Logs ---------------------------------------------------------------------------------------

    /// <summary>A real <c>docker logs --tail</c> returns lines.</summary>
    /// <remarks>
    /// Nothing is asserted about the CONTENT of those lines. This is a live server whose console output
    /// changes minute to minute, and pinning any of it would be asserting on the game, not on Servyx.
    /// </remarks>
    [SkippableFact]
    public async Task Log_tail_returns_recent_lines()
    {
        SkipUnlessConfigured();
        using var cts = NewTimeout();

        var target = await _fixture.GetTargetAsync(cts.Token);
        var logStream = new SshDockerLogStream(target);

        var lines = new List<ConsoleLine>();
        await foreach (var line in logStream.FollowAsync(
                           RemoteTestEnvironment.Current!.Container, new ConsoleTailOptions(50), cts.Token))
        {
            lines.Add(line);
        }

        lines.Should().NotBeEmpty();
    }

    // ---- 8. Metrics ------------------------------------------------------------------------------------

    /// <summary>A real <c>docker stats --no-stream</c> snapshot yields CPU and memory readings.</summary>
    [SkippableFact]
    public async Task Metrics_report_cpu_and_memory()
    {
        SkipUnlessConfigured();
        using var cts = NewTimeout();

        var target = await _fixture.GetTargetAsync(cts.Token);
        var metrics = new SshDockerMetricsSource(target, pollInterval: TimeSpan.FromMilliseconds(250));

        ResourceSample? sample = null;
        await foreach (var candidate in metrics.StreamAsync(RemoteTestEnvironment.Current!.Container, cts.Token))
        {
            sample = candidate;
            break;
        }

        sample.Should().NotBeNull();

        // Bounded rather than pinned: a live server's load is whatever it is. CPU percent is
        // daemon-computed and can exceed 100 on a multi-core host, so only the floor is meaningful.
        sample!.CpuPercent.Should().BeGreaterThanOrEqualTo(0);
        sample.MemoryBytes.Should().BePositive("a running container always has resident memory");
    }

    // ---- 9. The refusal --------------------------------------------------------------------------------

    /// <summary>
    /// Asking the guarded target to stop the live container is refused synchronously, and the container is
    /// still running afterwards.
    /// </summary>
    /// <remarks>
    /// The two assertions are doing different jobs. The first proves the guard refuses; the second proves
    /// the refusal was HARMLESS — that nothing partial reached production on the way to being refused. The
    /// recording check in between is the third and strongest: the recorder sits between the guard and the
    /// SSH exec channel, so a spec it never saw is a spec that never became an argv.
    /// </remarks>
    [SkippableFact]
    public async Task Stopping_the_container_is_refused_before_any_io()
    {
        SkipUnlessConfigured();
        using var cts = NewTimeout();

        var container = RemoteTestEnvironment.Current!.Container;
        var target = await _fixture.GetTargetAsync(cts.Token);

        target.Should().BeOfType<WriteGuardedExecutionTarget>(
            "every session this fixture hands out comes out of WriteGuardedTransport");

        await Assert.ThrowsAsync<WritesDisabledException>(
            () => target.ExecuteAsync(DockerCli.Stop(container, 30), cts.Token));

        _fixture.Recorded.Should().NotContain(
            spec => spec.Arguments.Count > 0 && spec.Arguments[0] == "stop",
            "the refusal happens before WriteGuardedExecutionTarget touches its inner target, so the " +
            "recorder that sits inside the guard must never have seen the stop spec");

        // And the proof that the refusal cost production nothing: the container is still up.
        var server = await InspectAsync(cts.Token);
        server.State.Should().Be("running");
    }

    // ---- 10. The whole-suite audit ---------------------------------------------------------------------

    /// <summary>
    /// Every <see cref="CommandSpec"/> this suite has actually put on the wire is declared
    /// <see cref="CommandIntent.ReadOnly"/> and uses only a permitted docker verb.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixture wraps every session in <see cref="RecordingExecutionTarget"/>, placed INSIDE
    /// <see cref="WriteGuardedTransport"/>'s guard, so the recorder observes exactly what the guard let
    /// through and nothing else. That placement is what makes both halves of this test meaningful at once:
    /// what was recorded is what production saw, and what was refused was never recorded.
    /// </para>
    /// <para>
    /// This test drives the full read-only surface itself before asserting, so its verdict can never be
    /// vacuous no matter which order xunit runs the class in — it is not relying on other tests having
    /// already populated the recorder.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Every_command_this_suite_issues_is_read_only()
    {
        SkipUnlessConfigured();
        using var cts = NewTimeout();

        var container = RemoteTestEnvironment.Current!.Container;
        var target = await _fixture.GetTargetAsync(cts.Token);

        // Exercise every read surface through the recorder, so the audit below has real traffic to audit.
        await new SshDockerServerDiscovery(target).DiscoverAsync(ImageRepository, RequiredMountContainerPath, cts.Token);
        await target.ExecuteAsync(DockerCli.Inspect(container), cts.Token);
        await target.ExecuteAsync(DockerCli.Logs(container, 10), cts.Token);
        await target.ExecuteAsync(DockerCli.Stats(container), cts.Token);
        await target.ExecuteAsync(DockerCli.Version(), cts.Token);

        var recorded = _fixture.Recorded.ToArray();

        recorded.Should().NotBeEmpty();
        recorded.Should().OnlyContain(
            spec => spec.Intent == CommandIntent.ReadOnly,
            "a spec that reached the wire without declaring ReadOnly means the write guard's contract was bypassed");

        recorded.Should().OnlyContain(
            spec => spec.Executable == "docker",
            "this suite has no business running anything but the docker CLI on a production host");

        // The verb allow-list, asserted positively. `container` covers `container ls` and
        // `container inspect`; nothing else — notably not exec, start, stop, restart, rm, pull, or cp —
        // may ever appear here.
        recorded.Should().OnlyContain(
            spec => spec.Arguments.Count > 0
                    && (spec.Arguments[0] == "version"
                        || spec.Arguments[0] == "container"
                        || spec.Arguments[0] == "logs"
                        || spec.Arguments[0] == "stats"),
            "only version / container ls / container inspect / logs / stats are permitted against production");
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    /// <summary>Runs real discovery over the live daemon and returns the single adopted container.</summary>
    private async Task<DiscoveredServer> DiscoverAsync(CancellationToken ct)
    {
        var target = await _fixture.GetTargetAsync(ct);
        var results = await new SshDockerServerDiscovery(target)
            .DiscoverAsync(ImageRepository, RequiredMountContainerPath, ct);

        return results.Should().ContainSingle().Subject;
    }

    /// <summary>
    /// Inspects the configured container directly (rather than via discovery), so the port/mount/health
    /// assertions read the container the operator named, not whichever one happened to match the profile.
    /// </summary>
    private async Task<DiscoveredServer> InspectAsync(CancellationToken ct)
    {
        var target = await _fixture.GetTargetAsync(ct);
        var result = await target.ExecuteAsync(
            DockerCli.Inspect(RemoteTestEnvironment.Current!.Container), ct);

        result.Succeeded.Should().BeTrue(result.StandardError);
        return DockerInspectJson.ParseInspect(result.StandardOutput);
    }
}

/// <summary>
/// Owns the one write-guarded, recorded SSH session the whole remote suite shares, plus the record of every
/// command that session actually carried.
/// </summary>
/// <remarks>
/// <para>
/// The stack it builds, outermost first, is:
/// <see cref="WriteGuardedTransport"/> → <see cref="RecordingTransport"/> →
/// <see cref="SshDockerTransport"/> → <see cref="SshTransport"/> → the real socket. Order matters: the
/// guard is outermost so nothing can reach the recorder — let alone the wire — without passing the intent
/// check, and the recorder is immediately inside it so what it records is precisely what production saw.
/// </para>
/// <para>
/// <see cref="WriteGuardedTransport"/> is constructed with no resolver, which selects
/// <c>ReadOnlyWriteModeResolver</c>: every target is <see cref="WriteMode.ReadOnly"/>, so every
/// <see cref="CommandIntent.Mutating"/> spec — including one a future edit forgets to declare — is refused.
/// </para>
/// <para>
/// Connecting is deferred to first use rather than done in <see cref="InitializeAsync"/>, so a genuine
/// connection failure surfaces inside the test that needed the connection (with its own message and
/// timeout) instead of as an opaque fixture-construction error across the whole class.
/// </para>
/// </remarks>
public sealed class RemoteSessionFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITransport? _transport;
    private IExecutionTarget? _target;

    /// <summary>Every <see cref="CommandSpec"/> that made it past the write guard, in arrival order.</summary>
    public ConcurrentQueue<CommandSpec> Recorded { get; } = new();

    /// <summary>The live target's descriptor. Only valid when <see cref="RemoteTestEnvironment.MissingReason"/> is null.</summary>
    public TargetDescriptor Descriptor => RemoteTestEnvironment.Current!.BuildDescriptor();

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_target is not null)
        {
            await _target.DisposeAsync();
            _target = null;
        }

        _gate.Dispose();
    }

    /// <summary>Builds (once) the guarded, recording transport stack. No I/O happens here.</summary>
    public async Task<ITransport> GetTransportAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return _transport ??= await BuildTransportAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Opens (once) the shared session and returns the guarded execution target.</summary>
    public async Task<IExecutionTarget> GetTargetAsync(CancellationToken ct = default)
    {
        var transport = await GetTransportAsync(ct);

        await _gate.WaitAsync(ct);
        try
        {
            return _target ??= await transport.ConnectAsync(Descriptor, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ITransport> BuildTransportAsync(CancellationToken ct)
    {
        var environment = RemoteTestEnvironment.Current
            ?? throw new InvalidOperationException(RemoteTestEnvironment.MissingReason);

        var secretStore = await environment.CreateSecretStoreAsync(ct);
        var sshTransport = new SshTransport(secretStore, RemoteTestEnvironment.CreateHostKeyVerifier());
        var dockerTransport = new SshDockerTransport(sshTransport);
        var recording = new RecordingTransport(dockerTransport, Recorded);

        // No IWriteModeResolver argument: that selects ReadOnlyWriteModeResolver, under which nothing
        // mutating can ever pass. This is the only place the suite's write posture is decided.
        return new WriteGuardedTransport(recording);
    }
}

/// <summary>
/// An <see cref="ITransport"/> decorator whose sessions are wrapped in <see cref="RecordingExecutionTarget"/>.
/// </summary>
/// <remarks>
/// <see cref="ProbeAsync"/> delegates unchanged rather than recording: <see cref="SshDockerTransport.ProbeAsync"/>
/// opens and disposes its own private session internally, so there is no seam here to observe it through.
/// What it runs is <see cref="DockerCli.Version"/>, which is <see cref="CommandIntent.ReadOnly"/> by
/// construction and cannot be anything else without editing <see cref="SshDockerTransport"/> itself.
/// </remarks>
internal sealed class RecordingTransport : ITransport
{
    private readonly ITransport _inner;
    private readonly ConcurrentQueue<CommandSpec> _sink;

    /// <summary>Creates a recording decorator over <paramref name="inner"/>, writing specs to <paramref name="sink"/>.</summary>
    public RecordingTransport(ITransport inner, ConcurrentQueue<CommandSpec> sink)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(sink);

        _inner = inner;
        _sink = sink;
    }

    /// <inheritdoc />
    public string TransportId => _inner.TransportId;

    /// <inheritdoc />
    public TransportCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    public Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default) =>
        _inner.ProbeAsync(target, ct);

    /// <inheritdoc />
    public async Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default) =>
        new RecordingExecutionTarget(await _inner.ConnectAsync(target, ct).ConfigureAwait(false), _sink);
}

/// <summary>
/// An <see cref="IExecutionTarget"/> decorator that records every <see cref="CommandSpec"/> it is asked to
/// run before delegating, and refuses outright to perform any file mutation.
/// </summary>
/// <remarks>
/// <para>
/// Recording happens BEFORE delegation, so a spec that threw somewhere downstream is still recorded — the
/// audit answers "what did this suite ask production to do", which is the question that matters, not "what
/// succeeded".
/// </para>
/// <para>
/// <see cref="WriteFileAsync"/> and <see cref="DeleteAsync"/> throw <see cref="NotSupportedException"/>
/// rather than delegating. The write guard outside already refuses them, so this is belt-and-braces: it
/// means that even if this decorator were ever used ungurded by mistake, it still cannot write to a
/// production host.
/// </para>
/// </remarks>
internal sealed class RecordingExecutionTarget : IExecutionTarget
{
    private readonly IExecutionTarget _inner;
    private readonly ConcurrentQueue<CommandSpec> _sink;

    /// <summary>Creates a recording decorator over <paramref name="inner"/>.</summary>
    public RecordingExecutionTarget(IExecutionTarget inner, ConcurrentQueue<CommandSpec> sink)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(sink);

        _inner = inner;
        _sink = sink;
    }

    /// <inheritdoc />
    public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default)
    {
        _sink.Enqueue(spec);
        return _inner.ExecuteAsync(spec, ct);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default)
    {
        _sink.Enqueue(spec);
        return _inner.ExecuteStreamingAsync(spec, ct);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) => _inner.ExistsAsync(path, ct);

    /// <inheritdoc />
    public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default) => _inner.StatAsync(path, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) =>
        _inner.ListDirectoryAsync(path, ct);

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default) => _inner.OpenReadAsync(path, ct);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. This suite never writes to production.</exception>
    public Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default) =>
        throw new NotSupportedException("The remote read-only suite must never write a file on a production host.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. This suite never deletes anything on production.</exception>
    public Task DeleteAsync(TargetPath path, CancellationToken ct = default) =>
        throw new NotSupportedException("The remote read-only suite must never delete a file on a production host.");

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
