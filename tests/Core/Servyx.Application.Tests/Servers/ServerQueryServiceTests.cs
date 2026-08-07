using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Discovery;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Application.Tests.Servers;

public class ServerQueryServiceTests
{
    private static readonly AdoptionCriteria Criteria = new(
        GameId: "palworld",
        GameName: "Palworld Dedicated Server",
        ImageRepository: "thijsvanloef/palworld-server-docker",
        RequiredMountContainerPath: "/palworld");

    private static readonly SettingConstraints NoConstraints = new(
        MinLength: null, MaxLength: null, Min: null, Max: null, Step: null,
        Values: null, Pattern: null, TrueValue: null, FalseValue: null);

    private static SettingBinding EnvWrite(string key) => new SettingBinding.ByKey("env", BindingDirection.Write, Sensitive: false, key);

    /// <summary>
    /// A minimal stand-in for what <c>GameDefinitionYamlParser</c> would produce from
    /// <c>definitions/palworld-docker.yaml</c>'s <c>settings</c> block — just the rows this file's tests
    /// actually exercise (see <see cref="ServerQueryServiceCharacterizationTests"/> for the full 10-row
    /// mirror). Deliberately includes the schema-key/env-key divergence on the two secrets
    /// ("admin-password" vs. "ADMIN_PASSWORD", "server-password" vs. "SERVER_PASSWORD") for fidelity.
    /// </summary>
    private static readonly IReadOnlyList<SettingGroup> SettingGroups =
    [
        new("Identity",
        [
            new("SERVER_NAME", "Server name", "Identity", SettingType.String, false, null, null, false, null, NoConstraints, [EnvWrite("SERVER_NAME")]),
        ]),
        new("Gameplay",
        [
            new("PLAYERS", "Max players", "Gameplay", SettingType.Int, false, null, null, false, null, NoConstraints, [EnvWrite("PLAYERS")]),
        ]),
        new("Security",
        [
            new("admin-password", "Admin / RCON password", "Security", SettingType.Secret, true, null, null, false, null, NoConstraints, [EnvWrite("ADMIN_PASSWORD")]),
            new("server-password", "Join password", "Security", SettingType.Secret, false, null, null, false, null, NoConstraints, [EnvWrite("SERVER_PASSWORD")]),
        ]),
    ];

    private static DiscoveredServer BuildDiscoveredServer(
        string id = "container-1",
        string name = "palworld-server",
        string state = "running",
        string health = "unhealthy",
        IReadOnlyDictionary<string, string>? env = null) => new(
        ServerId: id,
        Name: name,
        Image: "thijsvanloef/palworld-server-docker:latest",
        ImageDigest: "sha256:abc",
        State: state,
        HealthStatus: health,
        CreatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        StartedAt: new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero),
        Ports: [new DiscoveredPort(8211, 8211, "udp"), new DiscoveredPort(null, 25575, "tcp")],
        Mounts: [new DiscoveredMount("/srv/palworld/data", "/palworld", true)],
        NetworkName: "palworld_default",
        ContainerIp: "172.19.0.2",
        MemoryLimitBytes: 8_000_000_000,
        CpuLimit: 4.0,
        RestartPolicy: "unless-stopped",
        ComposeLabels: new Dictionary<string, string>(),
        EnvironmentVariables: env ?? new Dictionary<string, string>());

    /// <summary>
    /// Mirrors <c>definitions/palworld-docker.yaml</c>'s <c>lifecycle.healthSignal</c> block — a real,
    /// successfully-loaded Palworld definition's own explanation for its known-unreliable HEALTHCHECK, not a
    /// hardcoded constant in <see cref="ServerQueryService"/> itself. <see cref="CreateService"/> passes it
    /// by default so <see cref="GetAdoptedServersAsync_reports_Running_state_and_Unhealthy_health_as_distinct_signals"/>
    /// keeps observing the same explanation text unchanged.
    /// </summary>
    private static readonly LifecycleDefinition PalworldLifecycleDefinition = new(
        Ready: [],
        Stop: new StopPlan([]),
        CrashDetection: [],
        HealthSignal: new HealthSignalDefinition(
            HealthSignalTrust.Ignore,
            "The container's own HEALTHCHECK calls http://localhost:8212/v1/api/info without admin " +
            "credentials and receives 401 Unauthorized on every probe. The Palworld server itself is " +
            "healthy — /v1/api/players returns OK on the same polling cycle. Servyx derives readiness " +
            "from its own authenticated detectors, never from this signal."));

    private static ServerQueryService CreateService(
        IServerDiscovery? discovery = null,
        IMetricsSource? metrics = null,
        ILogStream? logs = null,
        ITransport? transport = null,
        AdoptionCriteria? criteria = null,
        ILogger<ServerQueryService>? logger = null,
        IReadOnlyList<SettingGroup>? settingGroups = null,
        LifecycleDefinition? lifecycle = null) => new(
        discovery ?? Substitute.For<IServerDiscovery>(),
        metrics ?? Substitute.For<IMetricsSource>(),
        logs ?? Substitute.For<ILogStream>(),
        transport ?? Substitute.For<ITransport>(),
        criteria ?? Criteria,
        logger ?? NullLogger<ServerQueryService>.Instance,
        settingGroups ?? SettingGroups,
        lifecycle ?? PalworldLifecycleDefinition);

    [Fact]
    public async Task GetAdoptedServersAsync_maps_discovered_containers_to_summaries()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Criteria.ImageRepository, Criteria.RequiredMountContainerPath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer()]));

        var sut = CreateService(discovery: discovery);

        var result = await sut.GetAdoptedServersAsync();

        result.Should().ContainSingle();
        var summary = result[0];
        summary.Id.Should().Be("container-1");
        summary.Name.Should().Be("palworld-server");
        summary.Game.Should().Be("Palworld Dedicated Server");
        summary.State.Should().Be(ServerState.Running);
    }

    [Fact]
    public async Task GetAdoptedServersAsync_reports_Running_state_and_Unhealthy_health_as_distinct_signals()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer(state: "running", health: "unhealthy")]));

        var sut = CreateService(discovery: discovery);

        var summary = (await sut.GetAdoptedServersAsync()).Single();

        summary.State.Should().Be(ServerState.Running);
        summary.Health.Should().Be(ServerHealthStatus.Unhealthy);
        summary.HealthDetail.Should().Contain("401 Unauthorized");
    }

    [Fact]
    public async Task GetAdoptedServersAsync_returns_empty_list_when_discovery_throws()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<DiscoveredServer>>>(_ => throw new InvalidOperationException("daemon unreachable"));

        var sut = CreateService(discovery: discovery);

        var result = await sut.GetAdoptedServersAsync();

        result.Should().BeEmpty();
    }

    /// <summary>
    /// The bug this regression-guards: a discovery failure and a genuinely empty adopted-server list must
    /// not collapse into the same, indistinguishable result. <see cref="ServerQueryService.GetAdoptedServersAsync"/>
    /// is allowed to flatten both to an empty list (existing, unchanged contract — see the test above), but
    /// <see cref="ServerQueryService.GetAdoptedServersWithStatusAsync"/> must not.
    /// </summary>
    [Fact]
    public async Task GetAdoptedServersWithStatusAsync_ReportsDiscoveryFailed_WhenDiscoveryThrows_InsteadOfLookingEmpty()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<DiscoveredServer>>>(_ => throw new InvalidOperationException("stale cached session"));

        var sut = CreateService(discovery: discovery);

        var result = await sut.GetAdoptedServersWithStatusAsync();

        result.Servers.Should().BeEmpty();
        result.DiscoveryFailed.Should().BeTrue();
        result.FailureDetail.Should().Be("stale cached session");
    }

    /// <summary>Twin of the test above: a genuine "zero servers adopted" result must still report success, not failure.</summary>
    [Fact]
    public async Task GetAdoptedServersWithStatusAsync_ReportsDiscoverySucceeded_WhenDiscoveryReturnsGenuinelyEmpty()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([]));

        var sut = CreateService(discovery: discovery);

        var result = await sut.GetAdoptedServersWithStatusAsync();

        result.Servers.Should().BeEmpty();
        result.DiscoveryFailed.Should().BeFalse();
        result.FailureDetail.Should().BeNull();
    }

    [Fact]
    public async Task GetServerDetailAsync_returns_null_when_no_container_matches()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([]));

        var sut = CreateService(discovery: discovery);

        var detail = await sut.GetServerDetailAsync("does-not-exist");

        detail.Should().BeNull();
    }

    [Fact]
    public async Task GetServerDetailAsync_returns_null_when_discovery_throws()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<DiscoveredServer>>>(_ => throw new InvalidOperationException("daemon unreachable"));

        var sut = CreateService(discovery: discovery);

        var detail = await sut.GetServerDetailAsync("container-1");

        detail.Should().BeNull();
    }

    [Fact]
    public async Task GetServerDetailAsync_maps_non_secret_settings_from_container_environment()
    {
        var env = new Dictionary<string, string>
        {
            ["SERVER_NAME"] = "Palygondwanaland",
            ["PLAYERS"] = "32",
        };

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer(env: env)]));

        var sut = CreateService(discovery: discovery);

        var detail = await sut.GetServerDetailAsync("container-1");

        detail.Should().NotBeNull();
        detail!.Settings.Single(s => s.Key == "SERVER_NAME").Authoritative.Should().Be("Palygondwanaland");
        detail.Settings.Single(s => s.Key == "PLAYERS").Authoritative.Should().Be("32");
        detail.MountHostPath.Should().Be("/srv/palworld/data");
    }

    /// <summary>
    /// The non-negotiable secret guarantee from the project brief: feed a discovery result whose
    /// environment contains a real <c>ADMIN_PASSWORD</c> value and assert the literal secret string
    /// appears nowhere in the mapped view model — not in the masked field, not in any other field, and
    /// not in the record's own <see cref="object.ToString"/> representation (which a careless log call
    /// elsewhere in the app could easily trigger).
    /// </summary>
    [Fact]
    public async Task GetServerDetailAsync_never_exposes_the_real_secret_value_anywhere_in_the_mapped_model()
    {
        const string realSecret = "supersecret123";
        var env = new Dictionary<string, string>
        {
            ["ADMIN_PASSWORD"] = realSecret,
            ["SERVER_PASSWORD"] = "alsosecret456",
            ["SERVER_NAME"] = "Palygondwanaland",
        };

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer(env: env)]));

        var sut = CreateService(discovery: discovery);

        var detail = await sut.GetServerDetailAsync("container-1");

        detail.Should().NotBeNull();

        var adminRow = detail!.Settings.Single(s => s.Key == "admin-password");
        adminRow.IsSecret.Should().BeTrue();
        adminRow.Authoritative.Should().Be("********");

        var serverPasswordRow = detail.Settings.Single(s => s.Key == "server-password");
        serverPasswordRow.Authoritative.Should().Be("********");

        // Brute-force sweep: the real secret must not appear anywhere in the mapped model, including
        // its auto-generated ToString() (what a naive structured-logging call would emit).
        detail.ToString().Should().NotContain(realSecret);
        detail.ToString().Should().NotContain("alsosecret456");
        foreach (var row in detail.Settings)
        {
            row.ToString().Should().NotContain(realSecret);
            row.ToString().Should().NotContain("alsosecret456");
            row.Authoritative.Should().NotBe(realSecret);
            row.Authoritative.Should().NotBe("alsosecret456");
        }
    }

    [Fact]
    public async Task GetConnectionStateAsync_reports_unreachable_and_names_the_endpoint_when_probe_reports_unreachable()
    {
        var transport = Substitute.For<ITransport>();
        var target = new TargetDescriptor("docker", "npipe://./pipe/dockerDesktopLinuxEngine", null, "desktop-linux", new Dictionary<string, string>());
        transport.ProbeAsync(target, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TargetHealth(false, null, "Docker engine unreachable: no such pipe")));

        var sut = CreateService(transport: transport);

        var state = await sut.GetConnectionStateAsync(target);

        state.Reachable.Should().BeFalse();
        state.Endpoint.Should().Be("npipe://./pipe/dockerDesktopLinuxEngine");
        state.Detail.Should().Contain("unreachable");
    }

    [Fact]
    public async Task GetConnectionStateAsync_degrades_instead_of_throwing_when_the_transport_itself_throws()
    {
        var transport = Substitute.For<ITransport>();
        var target = new TargetDescriptor("docker", "npipe://./pipe/dockerDesktopLinuxEngine", null, null, new Dictionary<string, string>());
        transport.ProbeAsync(target, Arg.Any<CancellationToken>())
            .Returns<Task<TargetHealth>>(_ => throw new TransportUnavailableException("socket reset"));

        var sut = CreateService(transport: transport);

        var state = await sut.GetConnectionStateAsync(target);

        state.Reachable.Should().BeFalse();
        state.Endpoint.Should().Be("npipe://./pipe/dockerDesktopLinuxEngine");
        state.Detail.Should().Contain("socket reset");
    }

    [Fact]
    public async Task GetConnectionStateAsync_reports_reachable_when_probe_succeeds()
    {
        var transport = Substitute.For<ITransport>();
        var target = new TargetDescriptor("docker", "npipe://./pipe/dockerDesktopLinuxEngine", null, null, new Dictionary<string, string>());
        transport.ProbeAsync(target, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TargetHealth(true, TimeSpan.FromMilliseconds(5), "Docker 27.0.0")));

        var sut = CreateService(transport: transport);

        var state = await sut.GetConnectionStateAsync(target);

        state.Reachable.Should().BeTrue();
        state.Detail.Should().Be("Docker 27.0.0");
    }

    [Fact]
    public async Task GetMetricsSampleAsync_returns_a_single_sample_from_the_stream()
    {
        var metrics = Substitute.For<IMetricsSource>();
        var expected = new ResourceSample(DateTimeOffset.UtcNow, 12.5, 512_000_000, 100, 200);
        metrics.StreamAsync("container-1", Arg.Any<CancellationToken>()).Returns(SingleSample(expected));

        var sut = CreateService(metrics: metrics);

        var sample = await sut.GetMetricsSampleAsync("container-1");

        sample.Should().Be(expected);
    }

    [Fact]
    public async Task GetMetricsSampleAsync_returns_null_instead_of_throwing_when_the_stream_fails()
    {
        var metrics = Substitute.For<IMetricsSource>();
        metrics.StreamAsync("container-1", Arg.Any<CancellationToken>()).Returns(ThrowingSampleStream());

        var sut = CreateService(metrics: metrics);

        var sample = await sut.GetMetricsSampleAsync("container-1");

        sample.Should().BeNull();
    }

    /// <summary>
    /// Regression guard for the observability fix: a swallowed metrics failure must never be silent — an
    /// operator needs a Warning-level log entry (with the causing exception attached) to distinguish "no
    /// metrics available" from "the query failed", even though the degraded <see langword="null"/> return
    /// is intentionally preserved.
    /// </summary>
    [Fact]
    public async Task GetMetricsSampleAsync_LogsWarning_WithTheException_WhenTheStreamFails()
    {
        var metrics = Substitute.For<IMetricsSource>();
        metrics.StreamAsync("container-1", Arg.Any<CancellationToken>()).Returns(ThrowingSampleStream());
        var logger = new RecordingLogger<ServerQueryService>();

        var sut = CreateService(metrics: metrics, logger: logger);

        await sut.GetMetricsSampleAsync("container-1");

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning
            && e.Exception != null && e.Exception.Message == "simulated stats failure"
            && e.Message.Contains("container-1"));
    }

    /// <summary>
    /// Regression guard: a genuine caller cancellation (the caller's own <c>ct</c> being cancelled, as
    /// opposed to <see cref="GetMetricsSampleAsync"/>'s own internal <c>cts.Cancel()</c> after the first
    /// sample) must propagate as <see cref="OperationCanceledException"/> — consistent with every other
    /// method on this class — rather than falling through to the generic catch, which would both log a
    /// spurious Warning and swallow the cancellation into a misleading <see langword="null"/> return.
    /// </summary>
    [Fact]
    public async Task GetMetricsSampleAsync_PropagatesCancellation_WithNoWarningLogged_WhenTheCallerCancels()
    {
        var metrics = Substitute.For<IMetricsSource>();
        metrics.StreamAsync("container-1", Arg.Any<CancellationToken>())
            .Returns(callInfo => NeverEndingStream(callInfo.ArgAt<CancellationToken>(1)));
        var logger = new RecordingLogger<ServerQueryService>();

        var sut = CreateService(metrics: metrics, logger: logger);

        using var callerCts = new CancellationTokenSource();
        var task = sut.GetMetricsSampleAsync("container-1", callerCts.Token);
        await callerCts.CancelAsync();

        var act = async () => await task;

        await act.Should().ThrowAsync<OperationCanceledException>();
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task FollowLogsAsync_yields_lines_from_the_underlying_stream()
    {
        var logs = Substitute.For<ILogStream>();
        var line = new ConsoleLine(0, "hello", DateTimeOffset.UtcNow, OutputStream.StdOut);
        logs.FollowAsync("container-1", Arg.Any<ConsoleTailOptions>(), Arg.Any<CancellationToken>())
            .Returns(SingleLine(line));

        var sut = CreateService(logs: logs);

        var received = new List<ConsoleLine>();
        await foreach (var l in sut.FollowLogsAsync("container-1", 100))
        {
            received.Add(l);
        }

        received.Should().ContainSingle().Which.Should().Be(line);
    }

    [Fact]
    public async Task FollowLogsAsync_ends_quietly_instead_of_throwing_when_the_stream_fails()
    {
        var logs = Substitute.For<ILogStream>();
        logs.FollowAsync("container-1", Arg.Any<ConsoleTailOptions>(), Arg.Any<CancellationToken>())
            .Returns(ThrowingLogStream());

        var sut = CreateService(logs: logs);

        var received = new List<ConsoleLine>();
        var act = async () =>
        {
            await foreach (var l in sut.FollowLogsAsync("container-1", 100))
            {
                received.Add(l);
            }
        };

        await act.Should().NotThrowAsync();
        received.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRecentLogsAsync_returns_lines_from_the_underlying_store()
    {
        var logs = Substitute.For<ILogStream>();
        var lines = new List<ConsoleLine> { new(0, "line one", DateTimeOffset.UtcNow, OutputStream.StdOut) };
        logs.ReadAsync("container-1", 0, 50, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ConsoleLine>>(lines));

        var sut = CreateService(logs: logs);

        var result = await sut.ReadRecentLogsAsync("container-1", 50);

        result.Should().BeEquivalentTo(lines);
    }

    [Fact]
    public async Task ReadRecentLogsAsync_returns_empty_list_instead_of_throwing_when_the_store_fails()
    {
        var logs = Substitute.For<ILogStream>();
        logs.ReadAsync("container-1", 0, 50, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ConsoleLine>>>(_ => throw new InvalidOperationException("container not found"));

        var sut = CreateService(logs: logs);

        var result = await sut.ReadRecentLogsAsync("container-1", 50);

        result.Should().BeEmpty();
    }

    /// <summary>Regression guard: see <see cref="GetMetricsSampleAsync_LogsWarning_WithTheException_WhenTheStreamFails"/>.</summary>
    [Fact]
    public async Task ReadRecentLogsAsync_LogsWarning_WithTheException_WhenTheStoreFails()
    {
        var logs = Substitute.For<ILogStream>();
        logs.ReadAsync("container-1", 0, 50, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ConsoleLine>>>(_ => throw new InvalidOperationException("container not found"));
        var logger = new RecordingLogger<ServerQueryService>();

        var sut = CreateService(logs: logs, logger: logger);

        await sut.ReadRecentLogsAsync("container-1", 50);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning
            && e.Exception != null && e.Exception.Message == "container not found"
            && e.Message.Contains("container-1"));
    }

    /// <summary>Regression guard: see <see cref="GetMetricsSampleAsync_LogsWarning_WithTheException_WhenTheStreamFails"/>.</summary>
    [Fact]
    public async Task GetAdoptedServersAsync_LogsWarning_WithTheException_WhenDiscoveryThrows()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<DiscoveredServer>>>(_ => throw new InvalidOperationException("daemon unreachable"));
        var logger = new RecordingLogger<ServerQueryService>();

        var sut = CreateService(discovery: discovery, logger: logger);

        await sut.GetAdoptedServersAsync();

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning
            && e.Exception != null && e.Exception.Message == "daemon unreachable");
    }

    /// <summary>Minimal <see cref="ILogger{T}"/> test double that records every log entry, so assertions can
    /// verify level, exception, and formatted message without NSubstitute's generic-method verification
    /// pitfalls against <see cref="ILogger.Log{TState}"/>.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId, exception, formatter(state, exception)));
    }

    private static async IAsyncEnumerable<ResourceSample> SingleSample(ResourceSample sample)
    {
        await Task.Yield();
        yield return sample;
    }

    private static async IAsyncEnumerable<ResourceSample> ThrowingSampleStream()
    {
        await Task.Yield();
        throw new InvalidOperationException("simulated stats failure");
#pragma warning disable CS0162 // Unreachable code detected: required so the compiler treats this as an iterator method.
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>
    /// A stream that never produces a sample and never completes on its own — it only ever ends via the
    /// <paramref name="ct"/> passed in by the caller being cancelled (mirroring a real long-lived stats
    /// connection), so tests can distinguish genuine caller cancellation from
    /// <see cref="ServerQueryService.GetMetricsSampleAsync"/>'s own internal single-shot cancellation.
    /// </summary>
    private static async IAsyncEnumerable<ResourceSample> NeverEndingStream([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
#pragma warning disable CS0162 // Unreachable code detected: required so the compiler treats this as an iterator method.
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ConsoleLine> SingleLine(ConsoleLine line)
    {
        await Task.Yield();
        yield return line;
    }

    private static async IAsyncEnumerable<ConsoleLine> ThrowingLogStream([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        throw new InvalidOperationException("simulated log stream failure");
#pragma warning disable CS0162 // Unreachable code detected: required so the compiler treats this as an iterator method.
        yield break;
#pragma warning restore CS0162
    }
}
