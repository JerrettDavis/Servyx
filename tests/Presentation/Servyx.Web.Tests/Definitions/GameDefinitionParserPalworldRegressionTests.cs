using Servyx.Definitions;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;
using Servyx.Infrastructure.Rcon;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Definitions;

/// <summary>
/// Retargets the former <c>PalworldDefinitionLoaderTests</c>' regression intents at the data-driven
/// <see cref="GameDefinitionYamlParser"/>/catalog path, for the composition-root swap that replaced the
/// original hardcoded loader as the production source of adoption criteria, RCON commands, and lifecycle
/// data. That original loader — <c>PalworldDefinitionLoader.cs</c> — and its own test file,
/// <c>PalworldDefinitionLoaderTests.cs</c>, have since been deleted outright: this suite (together with
/// <c>LiveDashboardDataServiceCatalogGamesTests</c> and the catalog-backed
/// <c>LiveDashboardDataServiceCharacterizationTests</c>) is now the sole regression coverage for everything
/// that loader used to pin, ported to the new parser's shape.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why every assertion is not a line-for-line port.</strong> The new parser's API shape is
/// different enough from the old hand-rolled loader that several of the original assertions do not translate
/// 1:1 — see each test's own remarks below for what changed and why. Nothing is silently dropped: a genuinely
/// unportable assertion gets its own test here documenting the new, deliberately different behavior instead.
/// </para>
/// <para>
/// Every fixture here is the real, shipped <c>definitions/palworld-docker.yaml</c> with at most one targeted
/// <see cref="string.Replace(string,string)"/> mutation, per the same convention
/// <c>Servyx.Definitions.Tests.Support.DefinitionYamlFixture</c> documents: everything except the one
/// deliberately-changed piece is exactly what ships, rather than a bespoke miniature document that could
/// silently drift from the schema's real requirements.
/// </para>
/// </remarks>
public class GameDefinitionParserPalworldRegressionTests
{
    private static readonly Lazy<string> RealYamlLazy = new(() =>
    {
        var repoRoot = RepoRootLocator.Find();
        var path = Path.Combine(repoRoot.FullName, "definitions", "palworld-docker.yaml");
        return File.ReadAllText(path);
    });

    private static string RealYaml => RealYamlLazy.Value;

    private static string Mutate(string find, string replace)
    {
        var yaml = RealYaml;
        yaml.Should().Contain(find, "the fixture mutation target must actually exist in the real definition text");
        return yaml.Replace(find, replace, StringComparison.Ordinal);
    }

    private static GameDefinition ParseValid(string yaml)
    {
        var result = new GameDefinitionYamlParser().Parse(yaml);
        result.Definition.Should().NotBeNull(
            because: $"the mutation under test should not affect document validity; issues: "
                + string.Join("; ", result.Report.Issues.Select(i => i.Message)));
        return result.Definition!;
    }

    /// <summary>
    /// Swaps the real definition's two <c>deployments</c> entries so the process-kind ("native-steamcmd")
    /// profile is declared first and the docker-kind profile second — the reverse of the shipped order.
    /// </summary>
    private static string WithDeploymentOrderSwapped()
    {
        const string dockerMarker = "  - id: docker-thijsvanloef";
        const string nativeMarker = "  - id: native-steamcmd";
        const string lifecycleMarker = "\nlifecycle:";

        var yaml = RealYaml;
        var dockerIndex = yaml.IndexOf(dockerMarker, StringComparison.Ordinal);
        var nativeIndex = yaml.IndexOf(nativeMarker, StringComparison.Ordinal);
        var lifecycleIndex = yaml.IndexOf(lifecycleMarker, StringComparison.Ordinal);

        dockerIndex.Should().BeGreaterThan(-1);
        nativeIndex.Should().BeGreaterThan(dockerIndex);
        lifecycleIndex.Should().BeGreaterThan(nativeIndex);

        var before = yaml[..dockerIndex];
        var dockerBlock = yaml[dockerIndex..nativeIndex];
        var nativeBlock = yaml[nativeIndex..lifecycleIndex];
        var after = yaml[lifecycleIndex..];

        return before + nativeBlock + dockerBlock + after;
    }

    /// <summary>
    /// Ports <c>PalworldDefinitionLoaderTests.Parse_ResolvesTheDockerProfile_WhenItIsReorderedToNotBeFirst</c>:
    /// the docker deployment profile must resolve by its own <c>kind</c>/<c>id</c>, never by list position.
    /// </summary>
    /// <remarks>
    /// The new model makes the underlying bug class largely structural: every real consumer (the round-trip
    /// suite, <c>Program.cs</c>, <c>LiveDashboardDataService</c>) selects by <c>Kind</c>/<c>Id</c> via LINQ,
    /// never by index — but this test still exercises the parser itself against a genuinely reordered
    /// document, rather than assume that from the shape of the model alone.
    /// </remarks>
    [Fact]
    public void DockerDeploymentProfile_IsSelectedByKind_RegardlessOfListPosition()
    {
        var definition = ParseValid(WithDeploymentOrderSwapped());

        definition.Deployments.Should().HaveCount(2);
        definition.Deployments[0].Kind.Should().Be(DeploymentKind.Process, "the native profile was moved first");

        var docker = definition.Deployments.Single(d => d.Kind == DeploymentKind.Docker);
        docker.Id.Should().Be("docker-thijsvanloef");
        docker.Detect.Should().NotBeNull();
        docker.Detect!.ImageRepo.Should().Be("thijsvanloef/palworld-server-docker");
        docker.Detect.RequiredMounts.Should().ContainSingle().Which.ContainerPath.Should().Be("/palworld");
        docker.Image.Should().NotBeNull();
        docker.Image!.Default.Should().Be("thijsvanloef/palworld-server-docker:latest");
    }

    /// <summary>
    /// Swaps the real definition's <c>rcon</c> and <c>rest</c> control channels so <c>rest</c> is declared
    /// first — the reverse of the shipped order — leaving <c>query</c> in place after both.
    /// </summary>
    private static string WithRconAndRestChannelOrderSwapped()
    {
        const string rconMarker = "    - id: rcon";
        const string restMarker = "    - id: rest";
        const string queryMarker = "    - id: query";

        var yaml = RealYaml;
        var rconIndex = yaml.IndexOf(rconMarker, StringComparison.Ordinal);
        var restIndex = yaml.IndexOf(restMarker, StringComparison.Ordinal);
        var queryIndex = yaml.IndexOf(queryMarker, StringComparison.Ordinal);

        rconIndex.Should().BeGreaterThan(-1);
        restIndex.Should().BeGreaterThan(rconIndex);
        queryIndex.Should().BeGreaterThan(restIndex);

        var before = yaml[..rconIndex];
        var rconBlock = yaml[rconIndex..restIndex];
        var restBlock = yaml[restIndex..queryIndex];
        var after = yaml[queryIndex..];

        return before + restBlock + rconBlock + after;
    }

    /// <summary>
    /// Ports <c>PalworldDefinitionLoaderTests.ParseRconCommands_SelectsTheRconChannelByIdNotByPosition</c>
    /// and <c>...CarriesTheReadOnlyClassificationVerbatim</c>: the <c>rcon</c> channel's command catalogue
    /// must resolve correctly by channel <c>id</c> even when it is not the first declared channel.
    /// </summary>
    [Fact]
    public void RconChannel_CommandCatalogue_IsSelectedById_RegardlessOfListPosition()
    {
        var definition = ParseValid(WithRconAndRestChannelOrderSwapped());

        definition.Control.Channels[0].Id.Should().Be("rest", "the rest channel was moved first");

        var rcon = definition.Control.Channels.Single(c => c.Id == "rcon");
        rcon.Commands.Should().HaveCount(8);

        var expectedReadOnly = new Dictionary<string, bool>
        {
            ["info"] = true,
            ["players"] = true,
            ["save"] = false,
            ["broadcast"] = false,
            ["kick"] = false,
            ["ban"] = false,
            ["shutdown"] = false,
            ["doexit"] = false,
        };

        foreach (var (commandId, readOnly) in expectedReadOnly)
        {
            rcon.Commands.Should().ContainKey(commandId);
            rcon.Commands[commandId].ReadOnly.Should().Be(readOnly, $"command '{commandId}' readOnly classification must match the definition");
        }

        rcon.Commands["save"].Template.Should().Be("Save");
    }

    /// <summary>
    /// Ports (and documents the deliberate change from)
    /// <c>PalworldDefinitionLoaderTests.ParseRconCommands_ReadsAMissingReadOnlyFlagAsMutating</c>.
    /// </summary>
    /// <remarks>
    /// <strong>This assertion could not be preserved as-is.</strong> The old hand-rolled loader treated a
    /// missing <c>readOnly</c> flag as "default to mutating (false)" and parsed the rest of the document
    /// anyway. <see cref="GameDefinitionYamlParser"/>'s schema requires <c>readOnly</c> on every command —
    /// see <c>GameDefinitionYamlParser.Control.cs</c>'s use of <c>RequireBool</c>, which reports a
    /// <see cref="ValidationSeverity.Error"/> and fails the whole document (<c>Definition</c> is
    /// <see langword="null"/>) when the key is absent, rather than silently defaulting it. This is a
    /// deliberate, strictly safer replacement for the old silent-default behavior — an incomplete command
    /// classification now blocks the whole definition from loading at all, instead of loading with an
    /// implicit "not read-only" guess for one command — so this test pins the new behavior rather than the
    /// old one. It is unreachable for the real bundled definition today: every command there declares
    /// <c>readOnly</c> explicitly (see <see cref="RconChannel_CommandCatalogue_IsSelectedById_RegardlessOfListPosition"/>),
    /// so this is not an observable behavior change for the shipped app.
    /// </remarks>
    [Fact]
    public void MissingReadOnlyClassification_FailsTheWholeDocument_RatherThanDefaultingSilently()
    {
        var yaml = Mutate(
            "info:      { template: \"Info\",                       readOnly: true }",
            "info:      { template: \"Info\" }");

        var result = new GameDefinitionYamlParser().Parse(yaml);

        result.Definition.Should().BeNull("a command missing its required 'readOnly' classification must invalidate the whole document");
        result.Report.Issues.Should().Contain(
            i => i.Severity == ValidationSeverity.Error && i.Message.Contains("declares no 'readOnly'", StringComparison.Ordinal));
    }

    /// <summary>
    /// Ports <c>PalworldDefinitionLoaderTests.An_unknown_stage_kind_is_reported_not_silently_dropped</c>.
    /// </summary>
    /// <remarks>
    /// The old loader reported this by throwing <see cref="InvalidOperationException"/>; the new parser
    /// never throws for a content problem (see <see cref="GameDefinitionYamlParser"/>'s class remarks) and
    /// instead reports it through <see cref="DefinitionParseResult.Report"/> — the assertion form differs,
    /// but the intent (an unrecognized stage <c>kind</c> is loudly reported, never silently dropped from the
    /// ladder) is preserved exactly.
    /// </remarks>
    [Fact]
    public void UnknownLifecycleStopStageKind_IsReportedAsAnError_NotSilentlyDropped()
    {
        var yaml = Mutate(
            "{ kind: control, channel: rcon, command: shutdown, args: { seconds: 30, message: \"Server shutting down\" }, timeout: 45s }",
            "{ kind: teleport-away, channel: rcon, command: shutdown, args: { seconds: 30, message: \"Server shutting down\" }, timeout: 45s }");

        var result = new GameDefinitionYamlParser().Parse(yaml);

        result.Definition.Should().BeNull("an unrecognized 'lifecycle.stop' stage kind must invalidate the document, not be dropped");
        result.Report.Issues.Should().Contain(
            i => i.Severity == ValidationSeverity.Error && i.Message.Contains("teleport-away", StringComparison.Ordinal));
    }

    /// <summary>
    /// Ports <c>PalworldDefinitionLoaderTests.Every_stop_stage_command_exists_in_the_rcon_catalogue</c>
    /// against the real, unmodified definition.
    /// </summary>
    [Fact]
    public void EveryLifecycleStopStageCommand_ExistsInTheRconChannelsCommandCatalogue()
    {
        var definition = ParseValid(RealYaml);

        var rconCommandIds = definition.Control.Channels
            .Single(c => c.Id == "rcon").Commands.Keys
            .ToHashSet(StringComparer.Ordinal);

        var referencedCommandIds = definition.Lifecycle.Stop.Stages
            .OfType<StopStage.Rcon>()
            .Select(s => s.CommandId);

        referencedCommandIds.Should().OnlyContain(id => rconCommandIds.Contains(id));
    }

    /// <summary>
    /// Ports <c>PalworldDefinitionLoaderTests.The_shutdown_stage_renders_without_throwing</c> — THE
    /// regression test for the latent bug where <c>lifecycle.stop</c>'s <c>shutdown</c> stage supplies args
    /// that must render cleanly against the <c>rcon</c> channel's own command template. Exercises the exact
    /// pipeline <c>Program.cs</c>'s RCON wiring now composes: the parsed <see cref="ControlPlane"/>'s
    /// commands become an <see cref="RconCommandCatalog"/>, and the parsed <see cref="LifecycleDefinition"/>'s
    /// stop stage renders against it.
    /// </summary>
    [Fact]
    public void TheShutdownStopStage_RendersAgainstTheParsedRconCatalogue_WithoutThrowing()
    {
        var definition = ParseValid(RealYaml);

        var rconChannel = definition.Control.Channels.Single(c => c.Id == "rcon");
        var catalogue = new RconCommandCatalog(
            rconChannel.Commands.Select(kv => new RconCommand(kv.Key, kv.Value.Template, kv.Value.ReadOnly)).ToList());

        var shutdownStage = definition.Lifecycle.Stop.Stages
            .OfType<StopStage.Rcon>()
            .Should().ContainSingle(s => s.CommandId == "shutdown")
            .Which;

        var act = () => catalogue.Render(shutdownStage.CommandId, shutdownStage.Args);

        act.Should().NotThrow();
        act().Should().Be("Shutdown 30 \"Server shutting down\"");
    }

    // -- rcon-less definitions ------------------------------------------------------------------------------

    /// <summary>
    /// Removes the real definition's entire <c>rcon</c> control channel, plus every reference to it that
    /// <see cref="GameDefinitionYamlParser"/> actually cross-validates: the <c>lifecycle.ready</c>
    /// control-probe fallback that targets <c>channel: rcon</c>, the <c>rcon.players</c> entry in
    /// <c>control.players.preferred</c> (and its now-pointless parser), and the two <c>lifecycle.stop</c>
    /// control stages that target <c>channel: rcon</c> (<c>shutdown</c>, <c>doexit</c>).
    /// </summary>
    /// <remarks>
    /// <strong>Removing only the channel block is not enough.</strong> A first attempt at this fixture
    /// removed nothing but the <c>rcon</c> channel entry, on the assumption that would be sufficient — it is
    /// not: <c>ResolveDeferredChecks</c> (see <c>GameDefinitionYamlParser.Semantics.cs</c>) reports an Error
    /// for every <c>lifecycle.ready</c> control-probe and <c>lifecycle.stop</c> control stage whose
    /// <c>channel</c> resolves to no declared entry, and <c>ValidatePreferredEntry</c> (see
    /// <c>GameDefinitionYamlParser.Control.cs</c>) does the same for <c>control.players.preferred</c>
    /// entries. All three fire for the real definition's unmodified <c>rcon</c>-referencing content, so the
    /// document fails validation (<c>Definition</c> is <see langword="null"/>) unless those references are
    /// removed alongside the channel itself. <c>backup.quiesce</c>'s own <c>channel: rcon</c> reference is
    /// deliberately left untouched — since <see cref="GameDefinitionYamlParser"/> now cross-validates that
    /// reference too, leaving it in place here means this fixture alone no longer parses cleanly; see
    /// <see cref="BackupQuiesceReferencingTheRemovedRconChannel_IsNowAnError"/>, which pins exactly that, and
    /// <see cref="WithRconChannelAndEveryReferenceIncludingQuiesceRemoved"/>, the variant used by tests that
    /// need a still-valid document.
    /// </remarks>
    private static string WithRconChannelAndItsReferencesRemoved()
    {
        var yaml = RealYaml;

        const string rconStart = "    - id: rcon";
        const string restStart = "    - id: rest";
        var rconIndex = yaml.IndexOf(rconStart, StringComparison.Ordinal);
        var restIndex = yaml.IndexOf(restStart, StringComparison.Ordinal);
        rconIndex.Should().BeGreaterThan(-1);
        restIndex.Should().BeGreaterThan(rconIndex);
        yaml = yaml[..rconIndex] + yaml[restIndex..];

        yaml = MutateContained(
            yaml,
            "    - kind: control-probe            # fallback when upstream changes the log line\n"
                + "      channel: rcon\n"
                + "      command: info\n"
                + "      expect: 'Welcome to Pal Server'\n"
                + "      interval: 15s\n"
                + "      timeout: 12m\n",
            string.Empty);

        yaml = MutateContained(
            yaml,
            "    - { kind: control, channel: rcon, command: shutdown, args: { seconds: 30, message: \"Server shutting down\" }, timeout: 45s }\n"
                + "    - { kind: control, channel: rcon, command: doexit, timeout: 15s }\n",
            string.Empty);

        yaml = MutateContained(yaml, "preferred: [rest.players, rcon.players, query]", "preferred: [rest.players, query]");

        yaml = MutateContained(
            yaml,
            "    parsers:\n      rcon.players: { kind: csv-with-header, columns: [name, playerUid, steamId] }\n",
            string.Empty);

        return yaml;
    }

    private static string MutateContained(string yaml, string find, string replace)
    {
        yaml.Should().Contain(find, "the fixture mutation target must actually exist in the text being mutated");
        return yaml.Replace(find, replace, StringComparison.Ordinal);
    }

    /// <summary>
    /// As <see cref="WithRconChannelAndItsReferencesRemoved"/>, but additionally clears the one
    /// <c>backup.quiesce</c> step, so nothing anywhere in the document references the removed <c>rcon</c>
    /// channel — including the reference <see cref="GameDefinitionYamlParser"/> now also cross-validates
    /// (see <c>GameDefinitionYamlParser.Semantics.cs</c>'s <c>PendingChannelCommandRefs</c> resolution).
    /// Needed as a separate fixture from <see cref="WithRconChannelAndItsReferencesRemoved"/> because
    /// <see cref="RconlessDocument_Parses_AndProgramsRconGuard_DegradesToEmptyCatalogue_WithoutThrowing"/>
    /// needs a document that still parses cleanly, while
    /// <see cref="BackupQuiesceReferencingTheRemovedRconChannel_IsNowAnError"/> needs the dangling
    /// <c>backup.quiesce</c> reference left in place to prove the new rule actually fires.
    /// </summary>
    private static string WithRconChannelAndEveryReferenceIncludingQuiesceRemoved()
    {
        return MutateContained(
            WithRconChannelAndItsReferencesRemoved(),
            "  quiesce:\n    - { kind: control, channel: rcon, command: save, timeout: 30s }\n",
            "  quiesce: []\n");
    }

    /// <summary>
    /// Asserts <c>Program.cs</c>'s RCON-wiring guard — reproduced here verbatim — degrades cleanly to
    /// <see cref="RconCommandCatalog.Empty"/> rather than throwing when the loaded definition has no
    /// <c>rcon</c> control channel. There is still no hardcoded fallback catalogue anywhere in this codebase.
    /// </summary>
    [Fact]
    public void RconlessDocument_Parses_AndProgramsRconGuard_DegradesToEmptyCatalogue_WithoutThrowing()
    {
        var yaml = WithRconChannelAndEveryReferenceIncludingQuiesceRemoved();
        var result = new GameDefinitionYamlParser().Parse(yaml);

        result.Definition.Should().NotBeNull(
            because: "removing 'rcon' together with every reference GameDefinitionYamlParser actually "
                + "cross-validates must leave a document that still parses cleanly; issues: "
                + string.Join("; ", result.Report.Issues.Select(i => i.Message)));

        var definition = result.Definition!;
        definition.Control.Channels.Should().NotContain(c => c.Id == "rcon");

        // The exact guard Program.cs's RCON-wiring block runs.
        var rconChannel = definition.Control.Channels.FirstOrDefault(c => c.Id == "rcon");
        List<RconCommand>? rconCommands = rconChannel is not null && rconChannel.Commands.Count > 0
            ? rconChannel.Commands.Select(kv => new RconCommand(kv.Key, kv.Value.Template, kv.Value.ReadOnly)).ToList()
            : null;

        var act = () => rconCommands is null ? RconCommandCatalog.Empty : new RconCommandCatalog(rconCommands);

        act.Should().NotThrow();
        act().Should().BeSameAs(RconCommandCatalog.Empty);
    }

    /// <summary>
    /// Previously named <c>RconlessDocument_LeavesBackupQuiesceSilentlyReferencingTheNowMissingRconChannel</c>
    /// and pinned a real, then-uncaught gap: unlike <c>control.players.preferred</c> and <c>lifecycle.stop</c>,
    /// <see cref="GameDefinitionYamlParser"/> never cross-validated <c>backup.quiesce[].channel</c>/
    /// <c>command</c> against <c>control.channels</c>, so a document with no <c>rcon</c> channel still
    /// parsed cleanly with <c>backup.quiesce</c> silently referencing a channel that no longer existed. That
    /// gap is now closed — see <c>GameDefinitionYamlParser.Semantics.cs</c>'s resolution of
    /// <c>PendingChannelCommandRefs</c>, populated by <c>ParseQuiesceStep</c> in
    /// <c>GameDefinitionYamlParser.Backup.cs</c> — so this test now pins the corrected behavior: the dangling
    /// reference is an <see cref="ValidationSeverity.Error"/> that invalidates the whole document, not a
    /// silent pass-through. This was a deliberate, reviewed behavior fix, not a characterization pin.
    /// </summary>
    [Fact]
    public void BackupQuiesceReferencingTheRemovedRconChannel_IsNowAnError()
    {
        var result = new GameDefinitionYamlParser().Parse(WithRconChannelAndItsReferencesRemoved());

        result.Definition.Should().BeNull(
            "'backup.quiesce' still references the removed 'rcon' channel, and GameDefinitionYamlParser now "
                + "cross-validates that reference the same way it already did for 'lifecycle.stop' and "
                + "'control.players.preferred'");
        result.Report.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Error
            && i.Message.Contains("'backup.quiesce'", StringComparison.Ordinal)
            && i.Message.Contains("channel 'rcon'", StringComparison.Ordinal));
    }
}
