using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Discovery;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Application.Tests.Servers;

/// <summary>
/// Phase 0 characterization tests for the hardcoded Palworld constants in
/// <see cref="ServerQueryService"/> and <see cref="AdoptionCriteria"/>, taken ahead of the data-driven
/// game-definition refactor. These pin CURRENT observable behavior exactly — including behavior that
/// looks like a bug — so the refactor has a regression net. Anything flagged
/// <c>// CHARACTERIZATION:</c> is a known quirk being pinned on purpose, not endorsed as correct.
/// </summary>
/// <remarks>
/// <strong>Post data-driven-settings update:</strong> <see cref="ServerQueryService"/> now sources its
/// settings rows from an injected <c>IReadOnlyList&lt;SettingGroup&gt;</c> (see its <c>settingGroups</c>
/// constructor parameter) rather than the old hardcoded <c>KnownSettings</c> allowlist.
/// <see cref="PalworldSettingGroups"/> below is a hand-built stand-in for what
/// <c>GameDefinitionYamlParser</c> would produce from <c>definitions/palworld-docker.yaml</c>'s
/// <c>settings</c> block — mirroring that file's groups, items, types, and env-surface <c>bindings</c>
/// exactly (including the schema-key/env-key divergence on the two secrets) — kept as a hand-built
/// literal rather than a real parse, consistent with this file's existing style of hand-building
/// <see cref="DiscoveredServer"/>/<see cref="DiscoveredMount"/> fixtures instead of exercising real
/// infrastructure. <see cref="CreateService"/> passes it by default so every existing test keeps
/// exercising the real <c>BuildSettings</c> code path unchanged.
/// </remarks>
/// <remarks>
/// <strong>Post data-driven-lifecycle update:</strong> <see cref="ServerQueryService"/> now sources a
/// discovered server's unhealthy explanation from an injected <c>LifecycleDefinition?</c> (see its
/// <c>lifecycle</c> constructor parameter) rather than a hardcoded Palworld constant.
/// <see cref="PalworldLifecycleDefinition"/> below is a hand-built stand-in for
/// <c>definitions/palworld-docker.yaml</c>'s <c>lifecycle.healthSignal</c> block, carrying the exact same
/// explanation text. <see cref="CreateService"/> passes it by default, same as
/// <see cref="PalworldSettingGroups"/>, so every existing test keeps observing Palworld's explanation
/// unchanged; only a test that explicitly supplies a different <c>lifecycle</c> — see
/// <see cref="LifecycleWithNoHealthSignal"/> — observes anything else.
/// </remarks>
public class ServerQueryServiceCharacterizationTests
{
    /// <summary>
    /// The exact adoption criteria the now-removed <c>AdoptionCriteria.PalworldDefault</c> hardcoded default
    /// used to carry, kept here as a test fixture literal rather than product code — see
    /// <see cref="AdoptionCriteriaFactory.TryDerive"/> for the code path that now derives the equivalent
    /// criteria from a loaded <c>definitions/palworld-docker.yaml</c> definition instead.
    /// </summary>
    private static readonly AdoptionCriteria PalworldCriteria = new(
        GameId: "palworld",
        GameName: "Palworld Dedicated Server",
        ImageRepository: "thijsvanloef/palworld-server-docker",
        RequiredMountContainerPath: "/palworld");

    private static readonly AdoptionCriteria Criteria = PalworldCriteria;

    private static readonly SettingConstraints NoConstraints = new(
        MinLength: null, MaxLength: null, Min: null, Max: null, Step: null,
        Values: null, Pattern: null, TrueValue: null, FalseValue: null);

    private static SettingBinding EnvWrite(string key) => new SettingBinding.ByKey("env", BindingDirection.Write, Sensitive: false, key);

    /// <summary>Mirrors <c>definitions/palworld-docker.yaml</c>'s <c>settings</c> block — see the class remarks.</summary>
    private static readonly IReadOnlyList<SettingGroup> PalworldSettingGroups =
    [
        new("Identity",
        [
            new("SERVER_NAME", "Server name", "Identity", SettingType.String, false, null, null, false, null, NoConstraints, [EnvWrite("SERVER_NAME")]),
            new("SERVER_DESCRIPTION", "Description", "Identity", SettingType.Text, false, null, null, false, null, NoConstraints, [EnvWrite("SERVER_DESCRIPTION")]),
        ]),
        new("Networking",
        [
            new("PORT", "Game port", "Networking", SettingType.Port, false, "8211", null, true, null, NoConstraints, [EnvWrite("PORT")]),
            new("RCON_PORT", "RCON port", "Networking", SettingType.Port, false, "25575", null, false, false, NoConstraints, [EnvWrite("RCON_PORT")]),
        ]),
        new("Gameplay",
        [
            new("PLAYERS", "Max players", "Gameplay", SettingType.Int, false, null, null, false, null, NoConstraints, [EnvWrite("PLAYERS")]),
            new("DIFFICULTY", "Difficulty", "Gameplay", SettingType.Enum, false, null, null, false, null, NoConstraints, [EnvWrite("DIFFICULTY")]),
            new("DAY_TIME_SPEEDRATE", "Day time speed", "Gameplay", SettingType.Float, false, null, "F6", false, null, NoConstraints, [EnvWrite("DAY_TIME_SPEEDRATE")]),
            new("ENABLE_PLAYER_TO_PLAYER_DAMAGE", "Enable PvP", "Gameplay", SettingType.Bool, false, null, null, false, null, NoConstraints, [EnvWrite("ENABLE_PLAYER_TO_PLAYER_DAMAGE")]),
        ]),
        new("Security",
        [
            // CORRECTED: schema key "admin-password" diverges from its env-surface binding key
            // "ADMIN_PASSWORD" — see Characterization_AdminPasswordSettingKey_IsTheDefinitionSchemaKeyNotTheEnvKey.
            new("admin-password", "Admin / RCON password", "Security", SettingType.Secret, true, null, null, false, null, NoConstraints, [EnvWrite("ADMIN_PASSWORD")]),
            new("server-password", "Join password", "Security", SettingType.Secret, false, null, null, false, null, NoConstraints, [EnvWrite("SERVER_PASSWORD")]),
        ]),
    ];

    /// <summary>
    /// Mirrors <c>definitions/palworld-docker.yaml</c>'s <c>lifecycle.healthSignal</c> block — see the class
    /// remarks. <see cref="CreateService"/> passes it by default so every existing test keeps observing
    /// Palworld's exact unhealthy explanation unless it explicitly overrides <c>lifecycle</c>.
    /// </summary>
    private static readonly LifecycleDefinition PalworldLifecycleDefinition = new(
        Ready: [],
        Stop: new StopPlan([]),
        CrashDetection: [],
        HealthSignal: new HealthSignalDefinition(HealthSignalTrust.Ignore, ExpectedUnhealthyExplanation));

    /// <summary>
    /// A definition that declares a <c>lifecycle</c> block but no <c>healthSignal</c> — what a real,
    /// successfully-loaded definition for a non-Palworld game looks like. Passed explicitly by
    /// <see cref="Corrected_UnhealthyExplanation_ForANonPalworldDefinition_IsGenericNotPalworldSpecific"/> so
    /// that test does not fall back to <see cref="PalworldLifecycleDefinition"/>.
    /// </summary>
    private static readonly LifecycleDefinition LifecycleWithNoHealthSignal = new(
        Ready: [],
        Stop: new StopPlan([]),
        CrashDetection: []);

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

    private static ServerQueryService CreateService(
        IServerDiscovery? discovery = null,
        IMetricsSource? metrics = null,
        ILogStream? logs = null,
        ITransport? transport = null,
        AdoptionCriteria? criteria = null,
        IReadOnlyList<SettingGroup>? settingGroups = null,
        LifecycleDefinition? lifecycle = null) => new(
        discovery ?? Substitute.For<IServerDiscovery>(),
        metrics ?? Substitute.For<IMetricsSource>(),
        logs ?? Substitute.For<ILogStream>(),
        transport ?? Substitute.For<ITransport>(),
        criteria ?? Criteria,
        NullLogger<ServerQueryService>.Instance,
        settingGroups ?? PalworldSettingGroups,
        lifecycle ?? PalworldLifecycleDefinition);

    // -- KnownSettings / BuildSettings ---------------------------------------------------------------------

    /// <summary>
    /// Pins the full settings table: exact row count, and for each row the exact Key, Label, Group,
    /// IsSecret flag, and declaration order. A future data-driven refactor must reproduce this table
    /// exactly (or deliberately change it with this test updated in lockstep).
    /// </summary>
    /// <remarks>
    /// CORRECTED (was CHARACTERIZATION): rows 8 and 9's <c>Key</c> were pinned as the old hardcoded
    /// allowlist's env-var names ("ADMIN_PASSWORD"/"SERVER_PASSWORD"), conflating them with the definition
    /// schema's actual setting keys ("admin-password"/"server-password" — same divergence as
    /// <see cref="Characterization_AdminPasswordSettingKey_IsTheDefinitionSchemaKeyNotTheEnvKey"/>).
    /// Corrected here in lockstep with that same deliberate fix; every other row, and every other column
    /// of these two rows, is unchanged.
    /// </remarks>
    [Fact]
    public async Task Characterization_KnownSettings_ProducesExactlyTenRowsInDeclarationOrder()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer()]));

        var sut = CreateService(discovery: discovery);

        var detail = await sut.GetServerDetailAsync("container-1");

        detail.Should().NotBeNull();
        var settings = detail!.Settings;

        settings.Should().HaveCount(10);

        var expected = new (string Key, string Label, string Group, bool IsSecret)[]
        {
            ("SERVER_NAME", "Server name", "Identity", false),
            ("SERVER_DESCRIPTION", "Description", "Identity", false),
            ("PORT", "Game port", "Networking", false),
            ("RCON_PORT", "RCON port", "Networking", false),
            ("PLAYERS", "Max players", "Gameplay", false),
            ("DIFFICULTY", "Difficulty", "Gameplay", false),
            ("DAY_TIME_SPEEDRATE", "Day time speed", "Gameplay", false),
            ("ENABLE_PLAYER_TO_PLAYER_DAMAGE", "Enable PvP", "Gameplay", false),
            ("admin-password", "Admin / RCON password", "Security", true),
            ("server-password", "Join password", "Security", true),
        };

        for (var i = 0; i < expected.Length; i++)
        {
            settings[i].Key.Should().Be(expected[i].Key, $"row {i} key");
            settings[i].Label.Should().Be(expected[i].Label, $"row {i} label");
            settings[i].Group.Should().Be(expected[i].Group, $"row {i} group");
            settings[i].IsSecret.Should().Be(expected[i].IsSecret, $"row {i} IsSecret");
        }
    }

    /// <summary>
    /// CORRECTED (was CHARACTERIZATION): the old hardcoded <c>KnownSettings</c> allowlist used the
    /// container ENV key ("ADMIN_PASSWORD") as the row's <c>Key</c>, conflating it with the definition
    /// schema's actual setting key <c>admin-password</c> (see <c>definitions/palworld-docker.yaml</c>
    /// lines 253-258, binding <c>env.ADMIN_PASSWORD</c>). Now that settings are sourced from the parsed
    /// game definition, <c>Key</c> is correctly the schema's <see cref="Servyx.Domain.Definitions.Model.SettingDescriptor.Key"/>
    /// ("admin-password"), and the environment lookup instead uses that same setting's env-surface
    /// binding key ("ADMIN_PASSWORD") internally — see <c>ServerQueryService.BuildSettings</c>'s remarks.
    /// This was a deliberate correction of a genuine divergence the old allowlist pinned as a known bug,
    /// not an accidental behavior change.
    /// </summary>
    [Fact]
    public async Task Characterization_AdminPasswordSettingKey_IsTheDefinitionSchemaKeyNotTheEnvKey()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer()]));

        var sut = CreateService(discovery: discovery);

        var detail = await sut.GetServerDetailAsync("container-1");

        detail!.Settings.Should().Contain(s => s.Key == "admin-password");
        // The container ENV key the old allowlist used in its place never appears as a row's Key.
        detail.Settings.Should().NotContain(s => s.Key == "ADMIN_PASSWORD");
    }

    /// <summary>
    /// Pins the exact secret mask literal — any change to this string is observable drift a refactor must
    /// not introduce silently.
    /// </summary>
    [Fact]
    public async Task Characterization_SecretValues_AreMaskedWithTheExactLiteral_EightAsterisks()
    {
        var env = new Dictionary<string, string>
        {
            ["ADMIN_PASSWORD"] = "realvalue123",
            ["SERVER_PASSWORD"] = "anotherrealvalue",
        };

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer(env: env)]));

        var sut = CreateService(discovery: discovery);

        var detail = await sut.GetServerDetailAsync("container-1");

        detail!.Settings.Single(s => s.Key == "admin-password").Authoritative.Should().Be("********");
        detail.Settings.Single(s => s.Key == "server-password").Authoritative.Should().Be("********");
    }

    /// <summary>Pins that an allowlisted key absent from the container environment yields a null Authoritative value, not an empty string or a fabricated default.</summary>
    [Fact]
    public async Task Characterization_MissingEnvironmentKeys_ProduceNullAuthoritativeValue()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer(env: new Dictionary<string, string>())]));

        var sut = CreateService(discovery: discovery);

        var detail = await sut.GetServerDetailAsync("container-1");

        detail!.Settings.Should().OnlyContain(s => s.Authoritative == null);
    }

    // -- PalworldUnhealthyExplanation ----------------------------------------------------------------------

    /// <summary>
    /// Mirrors <c>ServerQueryService.PalworldUnhealthyExplanation</c> verbatim. That member is
    /// <c>internal</c> with no <c>InternalsVisibleTo</c> exposing it to this test assembly, so the literal
    /// is duplicated here rather than referenced — the test still pins the exact production string via
    /// <see cref="Characterization_PalworldUnhealthyExplanation_IsPinnedVerbatim"/> asserting the real
    /// <see cref="Servyx.Application.Servers.ServerSummary.HealthDetail"/> output equals this literal.
    /// </summary>
    private const string ExpectedUnhealthyExplanation =
        "The container's own HEALTHCHECK calls http://localhost:8212/v1/api/info without admin " +
        "credentials and receives 401 Unauthorized on every probe. The Palworld server itself is " +
        "healthy — /v1/api/players returns OK on the same polling cycle. Servyx derives readiness " +
        "from its own authenticated detectors, never from this signal.";

    /// <summary>Pins the exact unhealthy-explanation string verbatim, as observed on a real mapped <see cref="Servyx.Application.Servers.ServerSummary"/>.</summary>
    [Fact]
    public async Task Characterization_PalworldUnhealthyExplanation_IsPinnedVerbatim()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer(health: "unhealthy")]));

        var sut = CreateService(discovery: discovery);

        var summary = (await sut.GetAdoptedServersAsync()).Single();

        summary.HealthDetail.Should().Be(ExpectedUnhealthyExplanation);
    }

    /// <summary>
    /// CORRECTED (was CHARACTERIZATION): this test previously pinned a known leak — the hardcoded Palworld
    /// unhealthy explanation was applied to ANY discovered server whose HealthStatus mapped to Unhealthy,
    /// regardless of the server's actual game, because the explanation lived as a single constant in
    /// <c>ServerQueryService</c> rather than coming from that server's own game definition. Now that the
    /// explanation is sourced from the discovered server's own <c>lifecycle.healthSignal</c> block (falling
    /// back to a generic, game-neutral explanation when a definition declares none — see
    /// <c>ServerQueryService.GenericUnhealthyExplanation</c>), a server whose definition declares no health
    /// signal must never show Palworld's text. <see cref="LifecycleWithNoHealthSignal"/> below stands in for
    /// exactly that: a real, successfully-loaded definition for a game that simply never documented its
    /// health check's trustworthiness.
    /// </summary>
    private const string ExpectedGenericUnhealthyExplanation =
        "The container's own health check is reporting unhealthy. This definition has not documented " +
        "whether that signal can be trusted, so Servyx is showing it as-is.";

    [Fact]
    public async Task Corrected_UnhealthyExplanation_ForANonPalworldDefinition_IsGenericNotPalworldSpecific()
    {
        var nonPalworldCriteria = new AdoptionCriteria(
            GameId: "not-palworld",
            GameName: "Some Other Game",
            ImageRepository: "someoneelse/other-game-server",
            RequiredMountContainerPath: "/data");

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer(health: "unhealthy")]));

        var sut = CreateService(discovery: discovery, criteria: nonPalworldCriteria, lifecycle: LifecycleWithNoHealthSignal);

        var summary = (await sut.GetAdoptedServersAsync()).Single();

        summary.Game.Should().Be("Some Other Game");
        summary.Health.Should().Be(ServerHealthStatus.Unhealthy);
        // Corrected: a non-Palworld definition that declares no health signal must never receive Palworld's
        // hardcoded explanation — it gets the generic, game-neutral one instead.
        summary.HealthDetail.Should().Be(ExpectedGenericUnhealthyExplanation);
        summary.HealthDetail.Should().NotBe(ExpectedUnhealthyExplanation);
    }

    /// <summary>Twin of the test above: a Healthy or Unknown server never carries the unhealthy explanation, regardless of game.</summary>
    [Theory]
    [InlineData("healthy", ServerHealthStatus.Healthy)]
    [InlineData("something-else", ServerHealthStatus.Unknown)]
    public async Task Characterization_HealthDetail_IsNull_ForNonUnhealthyStatuses(string dockerHealth, ServerHealthStatus expected)
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer(health: dockerHealth)]));

        var sut = CreateService(discovery: discovery);

        var summary = (await sut.GetAdoptedServersAsync()).Single();

        summary.Health.Should().Be(expected);
        summary.HealthDetail.Should().BeNull();
    }

    // -- AdoptionCriteria.PalworldDefault (removed; see PalworldCriteria fixture above) -------------------

    /// <summary>
    /// Pins every field of the Palworld adoption criteria exactly — formerly the hardcoded
    /// <c>AdoptionCriteria.PalworldDefault</c> default, now this file's own <see cref="PalworldCriteria"/>
    /// fixture, constructed with the identical values. <c>AdoptionCriteria.PalworldDefault</c> itself was
    /// removed once adoption criteria started being derived from loaded definitions — see
    /// <see cref="AdoptionCriteriaFactory"/> — so there is deliberately no more hardcoded default to pin in
    /// product code; only the values below remain, as this file's own fixture.
    /// </summary>
    [Fact]
    public void Characterization_AdoptionCriteria_PalworldDefault_HasExactFieldValues()
    {
        var criteria = PalworldCriteria;

        criteria.GameId.Should().Be("palworld");
        criteria.GameName.Should().Be("Palworld Dedicated Server");
        criteria.ImageRepository.Should().Be("thijsvanloef/palworld-server-docker");
        criteria.RequiredMountContainerPath.Should().Be("/palworld");
    }

    // -- MapState / MapHealth -------------------------------------------------------------------------------

    /// <summary>
    /// Pins every arm of <c>ServerQueryService.MapState</c> exhaustively, including the arms the reviewer
    /// called out by name ("paused" -&gt; Unknown, "created" -&gt; Stopped, "exited" -&gt; Stopped,
    /// "dead" -&gt; Crashed), plus the wildcard fallback for an unrecognized Docker state string. Exercised
    /// indirectly through <see cref="ServerQueryService.GetAdoptedServersAsync"/> since <c>MapState</c>
    /// itself is private; the input name is threaded into the theory row so a broken arm names the
    /// offending Docker state string in the failure message.
    /// </summary>
    [Theory]
    [InlineData("running", ServerState.Running)]
    [InlineData("restarting", ServerState.Starting)]
    [InlineData("removing", ServerState.Stopping)]
    [InlineData("paused", ServerState.Unknown)]
    [InlineData("created", ServerState.Stopped)]
    [InlineData("exited", ServerState.Stopped)]
    [InlineData("dead", ServerState.Crashed)]
    [InlineData("some-unrecognized-docker-state", ServerState.Unknown)]
    public async Task Characterization_MapState_MapsEveryDockerStateArmExhaustively(string dockerState, ServerState expected)
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer(state: dockerState, health: "healthy")]));

        var sut = CreateService(discovery: discovery);

        var summary = (await sut.GetAdoptedServersAsync()).Single();

        summary.State.Should().Be(expected, $"Docker state '{dockerState}' should map to {expected}");
    }

    /// <summary>
    /// Pins every arm of <c>ServerQueryService.MapHealth</c> exhaustively — the two recognized Docker
    /// health strings and the wildcard fallback for an unrecognized one. Exercised indirectly through
    /// <see cref="ServerQueryService.GetAdoptedServersAsync"/> since <c>MapHealth</c> itself is private.
    /// </summary>
    [Theory]
    [InlineData("healthy", ServerHealthStatus.Healthy)]
    [InlineData("unhealthy", ServerHealthStatus.Unhealthy)]
    [InlineData("some-unrecognized-docker-health", ServerHealthStatus.Unknown)]
    public async Task Characterization_MapHealth_MapsEveryDockerHealthArmExhaustively(string dockerHealth, ServerHealthStatus expected)
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer(state: "running", health: dockerHealth)]));

        var sut = CreateService(discovery: discovery);

        var summary = (await sut.GetAdoptedServersAsync()).Single();

        summary.Health.Should().Be(expected, $"Docker health '{dockerHealth}' should map to {expected}");
    }

    // -- BuildSettings: present-but-empty-string values ------------------------------------------------------

    /// <summary>
    /// Pins <c>BuildSettings</c>'s behavior for a secret-flagged key whose environment value is present but
    /// is the empty string: <c>authoritative = !present ? null : isSecret ? "********" : value</c> masks
    /// unconditionally once the key is present, regardless of whether the underlying value is empty — an
    /// empty secret still renders the mask, not an empty string and not null.
    /// </summary>
    [Fact]
    public async Task Characterization_BuildSettings_EmptyStringSecretValue_StillRendersTheMask()
    {
        var env = new Dictionary<string, string> { ["ADMIN_PASSWORD"] = "" };

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer(env: env)]));

        var sut = CreateService(discovery: discovery);

        var detail = await sut.GetServerDetailAsync("container-1");

        detail!.Settings.Single(s => s.Key == "admin-password").Authoritative.Should().Be("********");
    }

    /// <summary>
    /// Twin of the test above for a non-secret key: a present-but-empty environment value passes the empty
    /// string through verbatim as Authoritative — distinct from the null produced when the key is absent
    /// entirely (see <see cref="Characterization_MissingEnvironmentKeys_ProduceNullAuthoritativeValue"/>).
    /// </summary>
    [Fact]
    public async Task Characterization_BuildSettings_EmptyStringNonSecretValue_PassesThroughAsEmptyString()
    {
        var env = new Dictionary<string, string> { ["SERVER_NAME"] = "" };

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServer(env: env)]));

        var sut = CreateService(discovery: discovery);

        var detail = await sut.GetServerDetailAsync("container-1");

        var row = detail!.Settings.Single(s => s.Key == "SERVER_NAME");
        row.Authoritative.Should().Be("");
        row.Authoritative.Should().NotBeNull();
    }
}
