using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Backups;
using Servyx.Web.Services;
using Servyx.Composition;
using Servyx.Web.Tests.Documentation;
using GameDefinition = Servyx.Domain.Definitions.Model.GameDefinition;
using DefinitionQuiesceStep = Servyx.Domain.Definitions.Model.QuiesceStep;

namespace Servyx.Web.Tests.Definitions;

/// <summary>
/// Runtime parity between <c>definitions/palworld-docker.yaml</c>'s <c>backup</c> block and the operative
/// capture set <c>ServyxBackupContextSource</c> actually builds from it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This suite used to pin known divergences.</strong> Before this fix, the declared block parsed
/// cleanly but had no operative reader at all: the real capture set came from hand-maintained C# constants
/// (<c>BackupWiringOptions.DefaultInclude</c>, <c>RconWiringOptions.QuiesceCommandId</c>/
/// <c>QuiesceTimeout</c>) kept in sync with the YAML by hand. Three concrete gaps followed from that —
/// <c>${COMPOSE_DIR}/.env</c> and <c>${COMPOSE_DIR}/compose.yaml</c> were declared captured and were not,
/// and <c>Pal/Saved/Logs/**</c> was declared excluded and was not — and this class pinned each one as a
/// <c>KnownDivergence</c> with a named reason and a concrete instruction for closing it.
/// </para>
/// <para>
/// <strong>All three are closed.</strong> <c>ServyxBackupContextSource.GetAsync</c> now reads
/// <c>backup.include</c>/<c>backup.exclude</c>/<c>backup.adopt</c>/<c>backup.quiesce</c> off the real, typed
/// <c>GameDefinition</c> — parsed below through the real <see cref="GameDefinitionYamlParser"/>, not a
/// second, ad-hoc reading of the YAML — so this suite exercises the actual code path <c>DockerBackupProvider</c>
/// runs against, rather than re-deriving an expectation from the file a second time. There is no divergence
/// list left to maintain for <c>backup</c>.
/// </para>
/// <para>
/// <strong><c>saves</c> is now closed too.</strong> <c>LiveDashboardDataService.GetServerSavesWithStatusAsync</c>
/// reads the parsed <c>SavesLayout</c> off the same real, typed <c>GameDefinition</c> — see the final
/// section below, which used to pin this as the one remaining unclosed gap and now just asserts the block's
/// declared shape, the one fact that section still has anything to say about.
/// </para>
/// </remarks>
public class DefinitionCaptureSetParityTests
{
    private const string Container = "palworld-server";
    private const string ComposeDirectory = "/srv/palworld-compose";

    private static GameDefinition LoadRealDefinition()
    {
        var repoRoot = RepoRootLocator.Find();
        var path = Path.Combine(repoRoot.FullName, "definitions", "palworld-docker.yaml");
        var yaml = File.ReadAllText(path);

        var result = new GameDefinitionYamlParser().Parse(yaml);
        result.Definition.Should().NotBeNull("the shipped definition must parse for this suite to say anything meaningful about it");

        return result.Definition!;
    }

    private static IServerQueryService Query()
    {
        var query = Substitute.For<IServerQueryService>();
        query.GetServerDetailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ServerDetail?>(new ServerDetail(
                new ServerSummary(
                    Container,
                    Container,
                    "palworld",
                    ServerState.Running,
                    ServerHealthStatus.Healthy,
                    null,
                    null,
                    "localhost",
                    []),
                "thijsvanloef/palworld-server-docker:latest",
                "/srv/palworld",
                "/palworld",
                null,
                null,
                null,
                null,
                [])));

        return query;
    }

    private static ITransport FakeTransport()
    {
        var transport = Substitute.For<ITransport>();

        // Stands in for the Docker Engine transport, so it must declare the container-scoped file access
        // ServyxBackupContextSource requires before it will open a container-rooted session — an unflagged
        // transport is refused outright (ContainerScopedFilesNotSupportedException).
        transport.Capabilities.Returns(
            TransportCapabilities.FileRead | TransportCapabilities.DirectoryList |
            TransportCapabilities.ContainerApi | TransportCapabilities.ContainerScopedFiles);
        transport.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Substitute.For<IExecutionTarget>()));

        return transport;
    }

    /// <summary>
    /// Builds the real <see cref="DockerBackupContext"/> <c>ServyxBackupContextSource</c> would hand
    /// <c>DockerBackupProvider</c> for this server, off the real parsed definition.
    /// </summary>
    private static async Task<DockerBackupContext> GetRealContextAsync(bool withComposeDirectory = true)
    {
        var definition = LoadRealDefinition();
        var options = new BackupWiringOptions(composeDirectory: withComposeDirectory ? ComposeDirectory : null);

        await using var source = new ServyxBackupContextSource(
            Query(),
            FakeTransport(),
            options,
            rcon: null,
            definition,
            composeTransport: withComposeDirectory ? FakeTransport() : null);

        return await source.GetAsync(Container);
    }

    // -- backup.include / backup.exclude -------------------------------------------------------------------

    [Fact]
    public async Task The_data_rooted_include_globs_are_captured_by_the_container_rooted_source()
    {
        var context = await GetRealContextAsync();

        var data = context.Sources.Should().ContainSingle(s => s.Id == BackupWiringOptions.DataSourceId).Subject;

        data.Include.Should().BeEquivalentTo(
        [
            "Pal/Saved/SaveGames/**",
            "Pal/Saved/Config/LinuxServer/PalWorldSettings.ini",
        ], "these are definitions/palworld-docker.yaml's 'backup.include' entries relative to ${DATA_DIR}");
    }

    [Fact]
    public async Task The_compose_rooted_include_globs_are_captured_by_a_second_host_rooted_source_when_configured()
    {
        var context = await GetRealContextAsync();

        var compose = context.Sources.Should().ContainSingle(s => s.Id == BackupWiringOptions.ComposeSourceId).Subject;

        compose.Root.Should().Be(ComposeDirectory);
        compose.Include.Should().BeEquivalentTo(
            [".env", "compose.yaml"],
            "these are definitions/palworld-docker.yaml's 'backup.include' entries relative to ${COMPOSE_DIR} "
            + "— the gap this suite used to pin as unclosed under KnownBackupIncludeDivergences");
    }

    [Fact]
    public async Task Without_a_configured_compose_directory_the_host_rooted_paths_stay_uncaptured()
    {
        // Servyx cannot discover an arbitrary host directory from inside a container — that is the honest,
        // structural limit of the fix, not a bug. See BackupWiringOptions.ComposeDirectory's remarks.
        var context = await GetRealContextAsync(withComposeDirectory: false);

        context.Sources.Should().ContainSingle().Which.Id.Should().Be(BackupWiringOptions.DataSourceId);
    }

    [Fact]
    public async Task The_declared_exclude_globs_are_all_honoured_by_the_data_rooted_source()
    {
        var context = await GetRealContextAsync();

        var data = context.Sources.Should().ContainSingle(s => s.Id == BackupWiringOptions.DataSourceId).Subject;

        data.Exclude.Should().Contain(
            "Pal/Saved/Logs/**",
            "definitions/palworld-docker.yaml declares this excluded, and the include glob 'Pal/Saved/**' "
            + "would otherwise glob-match it — the gap this suite used to pin as unclosed under "
            + "KnownBackupExcludeDivergences");

        data.Exclude.Should().Contain(
            "backups/**",
            "the image's own cron directory: declared excluded in 'backup.exclude' AND independently derived "
            + "from 'backup.adopt[0].path' by ServyxBackupContextSource — both routes must agree");
    }

    // -- backup.adopt ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_declared_adopt_source_is_what_the_context_reports_as_foreign()
    {
        var context = await GetRealContextAsync();

        var foreign = context.Foreign.Should().ContainSingle().Subject;

        foreign.AdapterId.Should().Be("palworld-docker-cron", "definitions/palworld-docker.yaml's 'backup.adopt[0].adapter'");
        foreign.Directory.Should().Be("backups", "'backup.adopt[0].path' is '${DATA_DIR}/backups'");
        foreign.Pattern.Should().Be("*.tar.gz", "'backup.adopt[0].pattern'");
        foreign.RestoreSourceId.Should().Be(
            BackupWiringOptions.DataSourceId,
            "the cron archives' entries are relative to the same ${DATA_DIR} root the data source reads, so "
            + "they stay restorable rather than merely listable");
    }

    // -- backup.quiesce ----------------------------------------------------------------------------------------

    [Fact]
    public void Declared_quiesce_command_and_timeout_match_the_operative_RconWiringOptions_fallback()
    {
        var definition = LoadRealDefinition();

        var step = definition.Backup.Quiesce.Should().ContainSingle().Subject
            .Should().BeOfType<DefinitionQuiesceStep.Control>().Subject;

        step.Channel.Should().Be("rcon");

        // RconWiringOptions.QuiesceCommandId/QuiesceTimeout are ServyxBackupContextSource's built-in
        // fallback for when no definition is loaded at all (see its GetAsync). Keeping them equal to the
        // shipped definition's own declared values means a process that somehow starts with zero definitions
        // loaded still quiesces exactly the way one with this definition loaded would — a deliberate
        // guard-rail against the two drifting apart silently, not a claim that the fallback is derived from
        // this file.
        step.CommandId.Should().Be(RconWiringOptions.QuiesceCommandId);
        step.Timeout.Should().Be(RconWiringOptions.QuiesceTimeout);
    }

    [Fact]
    public async Task A_real_backup_context_carries_the_declared_quiesce_command_when_a_channel_exists()
    {
        var definition = LoadRealDefinition();
        var options = new BackupWiringOptions();

        var client = new Servyx.Infrastructure.Rcon.SourceRconClient();
        var secrets = new Fakes.RecordingSecretStore();
        var catalog = new Servyx.Infrastructure.Rcon.RconCommandCatalog(
        [
            new Servyx.Infrastructure.Rcon.RconCommand("info", "Info", ReadOnly: true),
            new Servyx.Infrastructure.Rcon.RconCommand("save", "Save", ReadOnly: false),
        ]);

        var passwordUrn = Servyx.Domain.Secrets.SecretUrn.Create("server", Container, "rcon", "password");
        var rcon = new ServyxRconChannels(
            new RconWiringOptions([new RconChannel(Container, new Servyx.Domain.Rcon.RconEndpoint("127.0.0.1", 25575), passwordUrn)]),
            catalog,
            client,
            secrets,
            new WritableServers([Container]),
            chainFactory: channel => new Servyx.Infrastructure.Rcon.RconReachabilityChain(
            [
                new Fakes.AlwaysAvailableRconReachability(
                    endpoint => new Servyx.Infrastructure.Rcon.RconSession(client, endpoint, catalog, secrets, channel.PasswordUrn)),
            ]));

        await using var source = new ServyxBackupContextSource(Query(), FakeTransport(), options, rcon, definition);

        var context = await source.GetAsync(Container);

        context.Quiesce.Should().NotBeNull();
        context.Quiesce!.CommandId.Should().Be(RconWiringOptions.QuiesceCommandId);
        context.Quiesce.Timeout.Should().Be(RconWiringOptions.QuiesceTimeout);
    }

    // -- saves ---------------------------------------------------------------------------------------------
    //
    // This section used to pin SavesBlockDivergence: a KnownDivergence recording that nothing parsed or
    // consumed the declared 'saves' block, plus a test asserting LiveDashboardDataService.GetServerSavesAsync
    // returned null unconditionally. Both are gone now that GetServerSavesWithStatusAsync reads SavesLayout
    // for real (see its remarks) — per this suite's own instructions for closing that gap. What is left is
    // the one fact this section still has something to say about: the block's declared shape.

    private static Dictionary<object, object> AsMap(object value) => (Dictionary<object, object>)value;

    private static Dictionary<object, object> ParseRoot(string yaml) =>
        new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(yaml);

    [Fact]
    public void Saves_block_is_declared_with_its_documented_shape()
    {
        var repoRoot = RepoRootLocator.Find();
        var yaml = File.ReadAllText(Path.Combine(repoRoot.FullName, "definitions", "palworld-docker.yaml"));
        var root = ParseRoot(yaml);

        root.Should().ContainKey("saves",
            "if this key is ever removed, LiveDashboardDataService.GetServerSavesWithStatusAsync degrades to "
            + "SavesAvailability.NotConfigured for every server — update that expectation, not just this test");

        var saves = AsMap(root["saves"]);

        saves.Keys.Cast<string>().Should().BeEquivalentTo(
            ["worldRoot", "worldIdPattern", "levelFile", "metaFile", "playerDir"],
            "this is the exact shape GameDefinitionYamlParser.ParseSaves and LiveDashboardDataService's real "
            + "saves reader both operate on");
    }
}
