using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Composition;
using Servyx.Web.Tests.Fakes;
using ServerDetail = Servyx.Application.Servers.ServerDetail;
using ServerSummary = Servyx.Application.Servers.ServerSummary;
using IServerQueryService = Servyx.Application.Servers.IServerQueryService;
using ServerHealthStatus = Servyx.Application.Servers.ServerHealthStatus;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// <see cref="LiveDashboardDataService.GetServerSavesWithStatusAsync"/>: live, definition-driven save-file
/// inspection, and the three-way <see cref="SavesAvailability"/> honesty it must uphold — especially that a
/// read failure never collapses into the same <see cref="SavesAvailability.Listed"/>-with-null-save shape a
/// genuinely empty world root reports.
/// </summary>
public class LiveDashboardDataServiceSavesTests
{
    private const string ServerId = "palworld-server";
    private const string DataRoot = "/palworld";

    private static readonly TargetDescriptor Target =
        new("docker", "npipe://./pipe/docker_engine", null, null, new Dictionary<string, string>());

    private static SavesLayout PalworldLayout(string? worldIdPattern = "^[0-9A-F]{32}$", string? worldRoot = null, string? playerDir = "Players") =>
        new(
            WorldRoot: worldRoot ?? "${DATA_DIR}/Pal/Saved/SaveGames/0",
            WorldIdPattern: worldIdPattern,
            LevelFile: "Level.sav",
            MetaFile: "LevelMeta.sav",
            PlayerDir: playerDir);

    private static IServerQueryService QueryFor(ServerDetail? detail)
    {
        var query = Substitute.For<IServerQueryService>();
        query.GetServerDetailAsync(ServerId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(detail));
        return query;
    }

    private static ServerDetail AdoptedDetail() => new(
        new ServerSummary(
            ServerId, "Palworld Server", "palworld", ServerState.Running, ServerHealthStatus.Healthy,
            null, null, "localhost", []),
        "thijsvanloef/palworld-server-docker:latest",
        "/srv/palworld",
        DataRoot,
        null, null, null, null, []);

    private static async Task<LiveDashboardDataService> BuildAsync(
        SavesLayout? saves, bool adopted = true, ITransport? transport = null, bool loadDefinition = true)
    {
        var catalog = loadDefinition ? await SavesFakes.CatalogFor(SavesFakes.MinimalDefinition(saves)) : null;
        return new LiveDashboardDataService(
            QueryFor(adopted ? AdoptedDetail() : null),
            NullLogger<LiveDashboardDataService>.Instance,
            Target,
            backupDashboard: null,
            catalog: catalog,
            transport: transport);
    }

    // -- NotConfigured ---------------------------------------------------------------------------------------

    [Fact]
    public async Task NoDefinitionLoaded_ReportsNotConfigured()
    {
        var sut = await BuildAsync(saves: null, transport: new FakeSavesTransport { Target = new InMemoryExecutionTarget() }, loadDefinition: false);

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Availability.Should().Be(SavesAvailability.NotConfigured);
        result.Save.Should().BeNull();
        result.FailureDetail.Should().BeNull();
    }

    [Fact]
    public async Task DefinitionWithNoSavesBlock_ReportsNotConfigured()
    {
        var sut = await BuildAsync(saves: null, transport: new FakeSavesTransport { Target = new InMemoryExecutionTarget() });

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Availability.Should().Be(SavesAvailability.NotConfigured);
        result.Save.Should().BeNull();
    }

    [Fact]
    public async Task NoTransportWired_ReportsNotConfigured()
    {
        var sut = await BuildAsync(PalworldLayout(), transport: null);

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Availability.Should().Be(SavesAvailability.NotConfigured);
        result.Save.Should().BeNull();
    }

    // -- Real-data path ----------------------------------------------------------------------------------------

    [Fact]
    public async Task RealWorld_ProducesExpectedSaveInfo()
    {
        const string worldId = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var target = new InMemoryExecutionTarget()
            .AddDirectory("Pal/Saved/SaveGames/0")
            .AddDirectory($"Pal/Saved/SaveGames/0/{worldId}")
            .AddFile($"Pal/Saved/SaveGames/0/{worldId}/Level.sav", 123_456)
            .AddFile($"Pal/Saved/SaveGames/0/{worldId}/LevelMeta.sav", 4_096)
            .AddDirectory($"Pal/Saved/SaveGames/0/{worldId}/Players")
            .AddFile($"Pal/Saved/SaveGames/0/{worldId}/Players/76561198000000002.sav", 4_096)
            .AddFile($"Pal/Saved/SaveGames/0/{worldId}/Players/76561198000000001.sav", 2_048);

        var sut = await BuildAsync(PalworldLayout(), transport: new FakeSavesTransport { Target = target });

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Availability.Should().Be(SavesAvailability.Listed);
        result.FailureDetail.Should().BeNull();
        result.Save.Should().NotBeNull();
        result.Save!.WorldId.Should().Be(worldId);
        result.Save.LevelFileName.Should().Be("Level.sav");
        result.Save.LevelFileSizeBytes.Should().Be(123_456);
        result.Save.LevelMetaFileName.Should().Be("LevelMeta.sav");
        result.Save.LevelMetaFileSizeBytes.Should().Be(4_096);
        result.Save.PlayerFiles.Should().HaveCount(2);
        result.Save.PlayerFiles.Select(f => f.FileName).Should().Equal(
            "76561198000000001.sav", "76561198000000002.sav");
        result.Save.PlayerFiles.Single(f => f.FileName == "76561198000000001.sav").SizeBytes.Should().Be(2_048);
    }

    // -- worldIdPattern filtering ------------------------------------------------------------------------------

    [Fact]
    public async Task WorldIdPattern_ExcludesADirectoryThatDoesNotMatch()
    {
        const string validId = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var target = new InMemoryExecutionTarget()
            .AddDirectory("Pal/Saved/SaveGames/0")
            .AddDirectory($"Pal/Saved/SaveGames/0/{validId}")
            .AddFile($"Pal/Saved/SaveGames/0/{validId}/Level.sav", 10)
            .AddFile($"Pal/Saved/SaveGames/0/{validId}/LevelMeta.sav", 20)
            // Not 32 hex characters — must be excluded by worldIdPattern, never chosen or read.
            .AddDirectory("Pal/Saved/SaveGames/0/not-a-world-id")
            .AddFile("Pal/Saved/SaveGames/0/not-a-world-id/Level.sav", 999_999);

        var sut = await BuildAsync(PalworldLayout(), transport: new FakeSavesTransport { Target = target });

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Availability.Should().Be(SavesAvailability.Listed);
        result.Save.Should().NotBeNull();
        result.Save!.WorldId.Should().Be(validId);
        result.Save.LevelFileSizeBytes.Should().Be(10, "the non-matching directory's Level.sav must never be read");
    }

    [Fact]
    public async Task MultipleMatchingWorlds_PicksTheMostRecentlyModified()
    {
        var older = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var newer = DateTimeOffset.Parse("2026-02-01T00:00:00Z");
        const string olderId = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        const string newerId = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";

        var target = new InMemoryExecutionTarget()
            .AddDirectory("Pal/Saved/SaveGames/0")
            .AddDirectory($"Pal/Saved/SaveGames/0/{olderId}", older)
            .AddFile($"Pal/Saved/SaveGames/0/{olderId}/Level.sav", 1)
            .AddFile($"Pal/Saved/SaveGames/0/{olderId}/LevelMeta.sav", 1)
            .AddDirectory($"Pal/Saved/SaveGames/0/{newerId}", newer)
            .AddFile($"Pal/Saved/SaveGames/0/{newerId}/Level.sav", 2)
            .AddFile($"Pal/Saved/SaveGames/0/{newerId}/LevelMeta.sav", 2);

        var sut = await BuildAsync(PalworldLayout(), transport: new FakeSavesTransport { Target = target });

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Save.Should().NotBeNull();
        result.Save!.WorldId.Should().Be(newerId, "the most-recently-modified world directory is the deliberate, deterministic pick");
    }

    // -- Degradation: genuinely empty, never confused with a failure --------------------------------------------

    [Fact]
    public async Task WorldRootDoesNotExist_ReportsListedWithNoSave_NotFailed()
    {
        var sut = await BuildAsync(PalworldLayout(), transport: new FakeSavesTransport { Target = new InMemoryExecutionTarget() });

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Availability.Should().Be(SavesAvailability.Listed, "a missing world root on an adopted server is a genuine 'no saves yet', not a failure");
        result.Save.Should().BeNull();
        result.FailureDetail.Should().BeNull();
    }

    [Fact]
    public async Task WorldRootExistsButHoldsNoMatchingWorld_ReportsListedWithNoSave()
    {
        var target = new InMemoryExecutionTarget()
            .AddDirectory("Pal/Saved/SaveGames/0")
            .AddDirectory("Pal/Saved/SaveGames/0/not-a-world-id");

        var sut = await BuildAsync(PalworldLayout(), transport: new FakeSavesTransport { Target = target });

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Availability.Should().Be(SavesAvailability.Listed);
        result.Save.Should().BeNull();
    }

    // -- Degradation: unreachable, never confused with empty -----------------------------------------------------

    [Fact]
    public async Task TransportConnectFailure_ReportsFailed_NotEmpty()
    {
        var transport = new FakeSavesTransport { ConnectThrows = new IOException("daemon unreachable") };
        var sut = await BuildAsync(PalworldLayout(), transport: transport);

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Availability.Should().Be(SavesAvailability.Failed);
        result.Save.Should().BeNull();
        result.FailureDetail.Should().Contain("daemon unreachable");
    }

    [Fact]
    public async Task ServerNotAdopted_ReportsFailed()
    {
        var sut = await BuildAsync(PalworldLayout(), adopted: false, transport: new FakeSavesTransport { Target = new InMemoryExecutionTarget() });

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Availability.Should().Be(SavesAvailability.Failed);
        result.Save.Should().BeNull();
    }

    // -- Containment: a definition-declared path may never escape the data root -----------------------------------

    [Fact]
    public async Task WorldRootEscapingTheDataRoot_IsRefused_ReportsFailed()
    {
        var sut = await BuildAsync(
            PalworldLayout(worldRoot: "${DATA_DIR}/../../outside-the-sandbox"),
            transport: new FakeSavesTransport { Target = new InMemoryExecutionTarget() });

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Availability.Should().Be(SavesAvailability.Failed, "a worldRoot that lexically escapes the data root must be refused, never silently ignored as 'empty'");
        result.Save.Should().BeNull();
        result.FailureDetail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PlayerDirEscapingTheDataRoot_IsRefused_ReportsFailed()
    {
        const string worldId = "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE";
        var target = new InMemoryExecutionTarget()
            .AddDirectory("Pal/Saved/SaveGames/0")
            .AddDirectory($"Pal/Saved/SaveGames/0/{worldId}")
            .AddFile($"Pal/Saved/SaveGames/0/{worldId}/Level.sav", 1)
            .AddFile($"Pal/Saved/SaveGames/0/{worldId}/LevelMeta.sav", 1);

        var sut = await BuildAsync(
            // worldRelative is 5 segments deep (Pal/Saved/SaveGames/0/{worldId}); enough ".." segments to
            // pop past all of them, and past the data root itself, is what actually escapes it — anything
            // shallower just lands on a different (still-contained) directory.
            PalworldLayout(playerDir: "../../../../../../outside-the-sandbox"),
            transport: new FakeSavesTransport { Target = target });

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Availability.Should().Be(SavesAvailability.Failed, "a playerDir that lexically escapes the data root must be refused, never silently treated as 'no players'");
        result.Save.Should().BeNull();
    }

    // -- A definition-authored regex can never hang the read ---------------------------------------------------

    [Fact]
    public async Task PatternRequiringBacktracking_FailsFastInsteadOfHanging()
    {
        // Contains a backreference, which RegexOptions.NonBacktracking rejects at compile time — and, since
        // CompileWorldIdPattern deliberately has no Compiled-engine fallback (see its remarks: the fallback
        // used to exist but was deleted because it could cost up to one full MatchTimeout per directory,
        // uncapped by SavesReadTimeout), this is unreachable via the real catalog (GameDefinitionYamlParser's
        // own ValidateSafeRegex refuses to load a definition declaring it) and, when hit directly the way
        // this test hits it, throws synchronously and immediately rather than being attempted per directory.
        const string adversarialPattern = @"^(a+)+\1$";
        var adversarialName = new string('a', 30) + "!";

        var target = new InMemoryExecutionTarget()
            .AddDirectory("Pal/Saved/SaveGames/0")
            .AddDirectory($"Pal/Saved/SaveGames/0/{adversarialName}")
            .AddFile($"Pal/Saved/SaveGames/0/{adversarialName}/Level.sav", 1)
            .AddFile($"Pal/Saved/SaveGames/0/{adversarialName}/LevelMeta.sav", 1);

        var sut = await BuildAsync(
            PalworldLayout(worldIdPattern: adversarialPattern),
            transport: new FakeSavesTransport { Target = target });

        var stopwatch = Stopwatch.StartNew();
        var result = await sut.GetServerSavesWithStatusAsync(ServerId);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(2), "compilation fails synchronously, before any per-directory work — this must be near-instant, not merely 'bounded'");

        // The regex never compiles, so ReadSavesAsync throws before it ever lists a candidate — caught by
        // the outer handler and reported as Failed, never silently treated as "no saves".
        result.Availability.Should().Be(SavesAvailability.Failed);
        result.Save.Should().BeNull();
    }

    // -- Transport gating: only a transport declaring ContainerScopedFiles is ever read through ------------------

    [Fact]
    public async Task SshDockerTransport_IsRefused_NeverOpensASessionOrReadsHostPaths()
    {
        // No Target configured at all: if GetServerSavesWithStatusAsync ever called ConnectAsync despite the
        // missing capability, this fake would throw (Target is null), which would surface as Failed rather
        // than UnsupportedTransport — the assertion on Availability below is what actually proves the gate
        // fired before any connection attempt, and LastDescriptor staying null proves it independently.
        var transport = new FakeSavesTransport
        {
            TransportId = "ssh+docker",
            Capabilities = TransportCapabilities.FileRead | TransportCapabilities.DirectoryList,
        };
        var sut = await BuildAsync(PalworldLayout(), transport: transport);

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Availability.Should().Be(
            SavesAvailability.UnsupportedTransport,
            "ssh+docker resolves file paths against the SSH host's filesystem, not the container's — reading " +
            "through it could display host files as container save data, so nothing may be attempted");
        result.Save.Should().BeNull();
        result.FailureDetail.Should().NotBeNullOrEmpty();
        transport.LastDescriptor.Should().BeNull("no session may be opened once the transport is refused");
    }

    [Fact]
    public async Task LocalProcessTransport_IsAlsoRefused_NotJustSshDocker()
    {
        // Any transport lacking ContainerScopedFiles is refused, not an ssh+docker-specific special case — a
        // non-Docker deployment (e.g. the "process" kind) has no container for containerName/rootPath to
        // address in the first place.
        var transport = new FakeSavesTransport
        {
            TransportId = "local",
            Capabilities = TransportCapabilities.FileRead | TransportCapabilities.DirectoryList,
        };
        var sut = await BuildAsync(PalworldLayout(), transport: transport);

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Availability.Should().Be(SavesAvailability.UnsupportedTransport);
        transport.LastDescriptor.Should().BeNull();
    }

    // -- Truncation is surfaced, never silently rendered as a complete list --------------------------------------

    [Fact]
    public async Task MorePlayerFilesThanTheCapAllows_ReportsTruncation()
    {
        const string worldId = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";
        var target = new InMemoryExecutionTarget()
            .AddDirectory("Pal/Saved/SaveGames/0")
            .AddDirectory($"Pal/Saved/SaveGames/0/{worldId}")
            .AddFile($"Pal/Saved/SaveGames/0/{worldId}/Level.sav", 1)
            .AddFile($"Pal/Saved/SaveGames/0/{worldId}/LevelMeta.sav", 1)
            .AddDirectory($"Pal/Saved/SaveGames/0/{worldId}/Players");

        // One more than LiveDashboardDataService.MaxPlayerFilesListed (500) — the exact count doesn't matter
        // beyond "more than the cap", so this exercises the boundary without hardcoding the private constant.
        for (var i = 0; i < 501; i++)
        {
            target.AddFile($"Pal/Saved/SaveGames/0/{worldId}/Players/{i:D6}.sav", 1);
        }

        var sut = await BuildAsync(PalworldLayout(), transport: new FakeSavesTransport { Target = target });

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Save.Should().NotBeNull();
        result.Save!.PlayerFilesTruncated.Should().BeTrue();
        result.Save.PlayerFiles.Should().HaveCount(500, "the cap bounds what is returned even though more files exist");
    }

    [Fact]
    public async Task MoreMatchingWorldDirectoriesThanTheCapAllows_ReportsTruncation()
    {
        var target = new InMemoryExecutionTarget().AddDirectory("Pal/Saved/SaveGames/0");

        // One more than LiveDashboardDataService.MaxWorldDirectoriesScanned (200) — every directory matches
        // the pattern and has no Level.sav/LevelMeta.sav, since only WorldCandidatesTruncated is under test.
        for (var i = 0; i < 201; i++)
        {
            target.AddDirectory($"Pal/Saved/SaveGames/0/{i:X32}");
        }

        var sut = await BuildAsync(PalworldLayout(), transport: new FakeSavesTransport { Target = target });

        var result = await sut.GetServerSavesWithStatusAsync(ServerId);

        result.Save.Should().NotBeNull();
        result.Save!.WorldCandidatesTruncated.Should().BeTrue(
            "more than 200 world directories matched the pattern, so the most-recently-modified pick was only decided among the first 200 considered");
    }

    [Fact]
    public async Task FewerPlayerFilesThanTheCap_ReportsNoTruncation()
    {
        var result = await (await BuildAsync(
            PalworldLayout(),
            transport: new FakeSavesTransport
            {
                Target = new InMemoryExecutionTarget()
                    .AddDirectory("Pal/Saved/SaveGames/0")
                    .AddDirectory("Pal/Saved/SaveGames/0/11111111111111111111111111111111")
                    .AddFile("Pal/Saved/SaveGames/0/11111111111111111111111111111111/Level.sav", 1)
                    .AddFile("Pal/Saved/SaveGames/0/11111111111111111111111111111111/LevelMeta.sav", 1)
                    .AddDirectory("Pal/Saved/SaveGames/0/11111111111111111111111111111111/Players")
                    .AddFile("Pal/Saved/SaveGames/0/11111111111111111111111111111111/Players/only.sav", 1),
            })).GetServerSavesWithStatusAsync(ServerId);

        result.Save.Should().NotBeNull();
        result.Save!.PlayerFilesTruncated.Should().BeFalse();
        result.Save.WorldCandidatesTruncated.Should().BeFalse();
    }
}
