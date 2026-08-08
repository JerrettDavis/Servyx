using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;
using Servyx.Domain.Definitions;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Composition;
using Servyx.Web.Tests.Definitions.Support;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Phase 0 characterization tests for hardcoded Palworld constants in <see cref="LiveDashboardDataService"/>
/// ahead of the data-driven game-definition refactor. These pin CURRENT observable behavior exactly, not
/// aspirational behavior — anything flagged <c>// CHARACTERIZATION:</c> is a known quirk being pinned on
/// purpose.
/// </summary>
/// <remarks>
/// <para>
/// Constructs <see cref="LiveDashboardDataService"/> via a real <see cref="GameDefinitionCatalog"/> — the same
/// data-driven path <c>Program.cs</c>'s composition root uses — rather than the retired
/// <c>PalworldDefinitionInfo</c>/<c>PalworldDefinitionLoader</c> path these tests originally pinned. See
/// <c>LiveDashboardDataServiceCatalogGamesTests</c> for the test that first proved the two paths produce
/// byte-identical output for today's bundled definition, which is what makes this substitution safe: every
/// literal asserted below is unchanged from what the legacy path used to pin.
/// </para>
/// <para>
/// <c>LoadRealCatalogAsync</c> copies the real, unmodified <c>definitions/palworld-docker.yaml</c> text into
/// an isolated temp directory rather than rooting the provider at the repository's own <c>definitions/</c>
/// directory directly — see the identical note on <see cref="LiveDashboardDataServiceCatalogGamesTests"/>,
/// whose <c>LoadRealCatalogAsync</c> this mirrors. Necessary once M6's second real game definition
/// (<c>definitions/minecraft-itzg.yaml</c>) started living in that directory too: this suite's pin is
/// specifically "exactly one card from exactly Palworld's real content", which a directory listing that now
/// holds two files can no longer produce. No assertion below changed.
/// </para>
/// </remarks>
public class LiveDashboardDataServiceCharacterizationTests
{
    private static readonly TargetDescriptor Target =
        new("docker", "npipe://./pipe/docker_engine", null, null, new Dictionary<string, string>());

    private static LiveDashboardDataService CreateService(
        IServerQueryService query,
        GameDefinitionCatalog? catalog = null) => new(
        query,
        NullLogger<LiveDashboardDataService>.Instance,
        Target,
        backupDashboard: null,
        catalog: catalog);

    private static async Task<GameDefinitionCatalog> LoadRealCatalogAsync()
    {
        var repoRoot = RepoRootLocator.Find();
        var yaml = await File.ReadAllTextAsync(Path.Combine(repoRoot.FullName, "definitions", "palworld-docker.yaml"));

        using var dir = new TempDefinitionsDirectory();
        dir.WriteFlat("palworld-docker.yaml", yaml);

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);
        await catalog.RefreshAsync();

        return catalog;
    }

    private static Servyx.Application.Servers.ServerSummary BuildAppSummary(
        IReadOnlyList<ServerPort>? ports = null,
        ServerHealthStatus health = ServerHealthStatus.Healthy,
        string? healthDetail = null) => new(
        Id: "container-1",
        Name: "palworld-server",
        Game: "Palworld Dedicated Server",
        State: ServerState.Running,
        Health: health,
        HealthDetail: healthDetail,
        StartedAt: new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero),
        Host: "docker",
        Ports: ports ?? []);

    // -- PurposeFor(int containerPort) ---------------------------------------------------------------------

    /// <summary>
    /// Pins <c>LiveDashboardDataService.PurposeFor</c>'s exact mapping for every recognized Palworld port
    /// and the "other" fallback for two unmapped ports, exercised indirectly through
    /// <see cref="LiveDashboardDataService.GetServersAsync"/> mapping <c>PortBinding.Purpose</c> since the
    /// method itself is private.
    /// </summary>
    [Theory]
    [InlineData(8211, "game")]
    [InlineData(27015, "query")]
    [InlineData(25575, "rcon")]
    [InlineData(8212, "rest")]
    [InlineData(80, "other")]
    [InlineData(65535, "other")]
    public async Task Characterization_PurposeFor_MapsContainerPortToExpectedPurpose(int containerPort, string expectedPurpose)
    {
        var query = Substitute.For<IServerQueryService>();
        var ports = new List<ServerPort> { new(HostPort: containerPort, ContainerPort: containerPort, Protocol: "tcp") };
        query.GetAdoptedServersWithStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Servyx.Application.Servers.ServerListResult.Ok([BuildAppSummary(ports)])));

        var sut = CreateService(query);

        var servers = await sut.GetServersAsync();

        servers.Should().ContainSingle();
        var binding = servers[0].Ports.Should().ContainSingle().Which;
        binding.Port.Should().Be(containerPort);
        binding.Purpose.Should().Be(expectedPurpose);
    }

    // -- MapHealth(ServerHealthStatus) / DefaultHealthTooltip -----------------------------------------------

    /// <summary>
    /// Pins every arm of <c>LiveDashboardDataService.MapHealth</c> exhaustively — <see cref="ServerHealthStatus"/>
    /// has exactly three values, so the two named arms plus the wildcard default (which only Unknown can
    /// reach) is the full switch. Exercised indirectly through <see cref="LiveDashboardDataService.GetServersAsync"/>
    /// mapping <c>ServerSummary.Health</c>, since the method itself is private.
    /// </summary>
    [Theory]
    [InlineData(ServerHealthStatus.Healthy, ContainerHealth.Healthy)]
    [InlineData(ServerHealthStatus.Unhealthy, ContainerHealth.Unhealthy)]
    [InlineData(ServerHealthStatus.Unknown, ContainerHealth.Unknown)]
    public async Task Characterization_MapHealth_MapsEveryServerHealthStatusArmExhaustively(
        ServerHealthStatus applicationHealth, ContainerHealth expected)
    {
        var query = Substitute.For<IServerQueryService>();
        query.GetAdoptedServersWithStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Servyx.Application.Servers.ServerListResult.Ok(
                [BuildAppSummary(health: applicationHealth)])));

        var sut = CreateService(query);

        var servers = await sut.GetServersAsync();

        servers.Should().ContainSingle().Which.Health.Should().Be(expected, $"{applicationHealth} should map to {expected}");
    }

    /// <summary>
    /// Pins every arm of <c>LiveDashboardDataService.DefaultHealthTooltip</c> exhaustively, verbatim.
    /// Exercised with <c>HealthDetail: null</c> so <c>MapSummary</c>'s <c>s.HealthDetail ?? DefaultHealthTooltip(health)</c>
    /// falls through to the default tooltip rather than an upstream-supplied detail string (a non-null
    /// <c>HealthDetail</c> — e.g. the Palworld unhealthy explanation — always wins over this fallback; that
    /// precedence is pinned separately by the Application-layer characterization tests).
    /// </summary>
    [Theory]
    [InlineData(ServerHealthStatus.Healthy, "Reported healthy by the container's own HEALTHCHECK.")]
    [InlineData(ServerHealthStatus.Unhealthy, "Reported unhealthy by the container's own HEALTHCHECK.")]
    [InlineData(ServerHealthStatus.Unknown, "Health status not reported by the container.")]
    public async Task Characterization_DefaultHealthTooltip_MapsEveryContainerHealthArmExhaustively(
        ServerHealthStatus applicationHealth, string expectedTooltip)
    {
        var query = Substitute.For<IServerQueryService>();
        query.GetAdoptedServersWithStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Servyx.Application.Servers.ServerListResult.Ok(
                [BuildAppSummary(health: applicationHealth, healthDetail: null)])));

        var sut = CreateService(query);

        var servers = await sut.GetServersAsync();

        servers.Should().ContainSingle().Which.HealthTooltip.Should().Be(expectedTooltip);
    }

    // -- GetGamesAsync --------------------------------------------------------------------------------------

    /// <summary>
    /// Pins the full <see cref="GameCardSummary"/> shape produced from the real, bundled
    /// <c>definitions/palworld-docker.yaml</c> — including the hardcoded deployment-profile kind "docker"
    /// that <see cref="LiveDashboardDataService.GetGamesAsync"/> fabricates rather than reading from the
    /// parsed definition. Every literal here is unchanged from what this test pinned when it constructed the
    /// service through the retired <c>PalworldDefinitionInfo</c> path instead — see the class remarks.
    /// </summary>
    [Fact]
    public async Task Characterization_GetGamesAsync_ReturnsExactlyOneCard_WithEveryFieldPinned_WhenDefinitionIsLoaded()
    {
        var catalog = await LoadRealCatalogAsync();
        catalog.Faults.Should().BeEmpty("definitions/palworld-docker.yaml must be present and parse for this pin to be meaningful");

        var sut = CreateService(Substitute.For<IServerQueryService>(), catalog);

        var games = await sut.GetGamesAsync();

        games.Should().HaveCount(1);
        var card = games[0];

        card.Id.Should().Be("palworld");
        card.Name.Should().Be("Palworld Dedicated Server");
        card.Version.Should().Be("1.0.0");
        card.Tags.Should().Equal("survival", "steam", "unreal");
        // CHARACTERIZATION: Trust is a hardcoded TrustTier.Builtin literal in GetGamesAsync, not derived
        // from a real trust evaluation — no IDefinitionTrustEvaluator runs in this milestone (see
        // FileSystemGameDefinitionProvider's remarks). Every card this method produces reports Builtin
        // regardless of the definition's actual provenance.
        card.Trust.Should().Be(TrustTier.Builtin);
        // CHARACTERIZATION: ModsSupported is likewise a hardcoded `false` literal, not read from the yaml's
        // own `mods: supported: false` block (yaml:293-294) — GetGamesAsync does not read the parsed
        // definition's Mods.Supported at all, so this literal only coincidentally matches today's yaml value.
        // Changing the yaml's `mods.supported` to `true` would NOT change this card's ModsSupported.
        card.ModsSupported.Should().BeFalse();

        card.DeploymentProfiles.Should().HaveCount(1);
        var profile = card.DeploymentProfiles[0];
        // Unlike the retired legacy path (which hardcoded the literal "docker-thijsvanloef"), the
        // catalog-backed path reads the deployment profile's own parsed Id — for today's bundled definition
        // the two are the same string, which is exactly what this assertion proves.
        profile.Id.Should().Be("docker-thijsvanloef");
        profile.Kind.Should().Be("docker");
        profile.Description.Should().Be(
            "thijsvanloef/palworld-server-docker:latest. Adopts an existing container whose image "
            + "repository matches 'thijsvanloef/palworld-server-docker'.");
    }

    /// <summary>Pins the null-catalog degraded path: an honest empty catalogue, not a fabricated card.</summary>
    [Fact]
    public async Task Characterization_GetGamesAsync_ReturnsEmptyList_WhenDefinitionIsNull()
    {
        var sut = CreateService(Substitute.For<IServerQueryService>(), catalog: null);

        var games = await sut.GetGamesAsync();

        games.Should().BeEmpty();
    }
}
