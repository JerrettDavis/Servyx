using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;

namespace Servyx.Definitions.Tests.RoundTrip;

/// <summary>
/// Parses the real, shipped <c>definitions/minecraft-itzg.yaml</c> — the second real game definition
/// authored to prove milestone M6's own acceptance criterion (<c>docs/roadmap.md</c>): that adding a second
/// game requires no C# changes outside format adapters and the RCON dialect. This is the acceptance bar for
/// that claim: the real definition must parse with zero <see cref="ValidationSeverity.Error"/> issues.
///
/// Also covers the audit pass that brought this definition's schema usage up to date with capabilities
/// that landed after it was first authored: an explicit player-list parser, <c>stopGracePeriodSeconds</c>,
/// explicit <c>continueOnError</c> on every stop stage, the resume half of backup quiesce/resume, and an
/// explicit config-surface-authority decision for <c>server.properties</c>.
/// </summary>
public class MinecraftDefinitionRoundTripTests
{
    private static DefinitionParseResult Parse()
    {
        var repoRoot = RepoRootLocator.Find();
        var path = Path.Combine(repoRoot.FullName, "definitions", "minecraft-itzg.yaml");
        return new GameDefinitionYamlParser().Parse(File.ReadAllText(path));
    }

    [Fact]
    public void RealDefinition_ParsesWithNoErrors()
    {
        var result = Parse();

        result.Report.Issues.Where(i => i.Severity == ValidationSeverity.Error).Should().BeEmpty();
        result.Report.IsValid.Should().BeTrue();
        result.Definition.Should().NotBeNull();
    }

    /// <summary>
    /// Unlike <c>definitions/palworld-docker.yaml</c>, this definition produces zero Warnings: every
    /// <c>capabilities.network[].var</c> reference (<c>SERVER_PORT</c>, <c>RCON_PORT</c>) has a matching
    /// settings-catalogue entry, there is no <c>backup.adopt</c> entry (this image ships no cron-based
    /// backup rotation of its own), and there is no <c>signature</c> block.
    /// </summary>
    [Fact]
    public void RealDefinition_ProducesNoWarnings()
    {
        var result = Parse();

        result.Report.Issues.Where(i => i.Severity == ValidationSeverity.Warning).Should().BeEmpty();
    }

    [Fact]
    public void Metadata_MatchesTheFile()
    {
        var definition = Parse().Definition!;

        definition.ApiVersion.Should().Be("servyx.dev/v1");
        definition.Metadata.Id.Should().Be("minecraft-itzg");
        definition.Metadata.Name.Should().Be("Minecraft Server (itzg)");
        definition.Metadata.Tags.Should().Equal("survival", "java", "sandbox");
    }

    [Fact]
    public void Deployment_DeclaresThePropertiesSurface_AsDerivedFromEnv()
    {
        var deployment = Parse().Definition!.Deployments.Single(d => d.Id == "docker-itzg");

        deployment.Detect!.ImageRepo.Should().Be("itzg/minecraft-server");
        deployment.Detect.RequiredMounts.Should().ContainSingle().Which.ContainerPath.Should().Be("/data");
        deployment.DataDir.Should().Be("/data");

        deployment.Surfaces.Should().HaveCount(3);

        var env = deployment.Surfaces.Single(s => s.Id == "env");
        env.Format.Should().Be(SurfaceFormat.Dotenv);
        env.Role.Should().Be(SurfaceRole.Authoritative);

        // The finding this test exists to pin: server.properties is genuinely a DIFFERENT format from
        // Palworld's dotenv/ini surfaces — flat key=value text, but with dotted keys (rcon.password) and no
        // quoting convention — which is exactly why SurfaceFormat.Properties and PropertiesConfigAdapter
        // exist. See the M6 second-game report for the full finding.
        //
        // Also pins the audit's config-surface-authority decision (delta #5): 'env' stays authoritative and
        // 'properties' stays 'derived' — Servyx never writes this surface directly, so
        // OVERRIDE_SERVER_PROPERTIES defaulting to true (the image regenerating it from env on every start)
        // cannot discard any Servyx-originated edit.
        var properties = deployment.Surfaces.Single(s => s.Id == "properties");
        properties.Format.Should().Be(SurfaceFormat.Properties);
        properties.Role.Should().Be(SurfaceRole.Derived);
        properties.DerivedFrom.Should().Equal("env");
        properties.Regeneration.Should().NotBeNull();
        properties.Regeneration!.Kind.Should().Be(RegenerationKind.ContainerRestart);
    }

    /// <summary>
    /// Audit delta #2: Docker's own stop grace period defaults to 10s, far short of what a large world's
    /// flush-and-stop sequence needs. Pins both the declared value and that it clears the
    /// 'lifecycle.stop' ladder's stage-timeout sum (30s + 30s + 120s = 180s) — the exact invariant
    /// ResolveDeferredChecks enforces as a hard Error when violated.
    /// </summary>
    [Fact]
    public void Deployment_DeclaresAGracePeriod_AtOrAboveTheStopLadderTotal()
    {
        var deployment = Parse().Definition!.Deployments.Single(d => d.Id == "docker-itzg");
        var lifecycle = Parse().Definition!.Lifecycle;

        deployment.StopGracePeriod.Should().Be(TimeSpan.FromSeconds(200));

        var ladderTotal = lifecycle.Stop.Stages
            .OfType<StopStage.Rcon>()
            .Select(s => s.Timeout)
            .Concat(lifecycle.Stop.Stages.OfType<StopStage.Signal>().Select(s => s.Timeout))
            .Aggregate(TimeSpan.Zero, (total, timeout) => total + timeout);

        ladderTotal.Should().Be(TimeSpan.FromSeconds(180));
        deployment.StopGracePeriod.Should().BeGreaterThanOrEqualTo(ladderTotal);
    }

    [Fact]
    public void Control_RconChannel_HasEightCommands_WithCorrectReadOnlyClassification()
    {
        var rcon = Parse().Definition!.Control.Channels.Single(c => c.Id == "rcon");

        rcon.Commands.Should().HaveCount(8);
        var expectedReadOnly = new Dictionary<string, bool>
        {
            ["list"] = true,
            ["save-all"] = false,
            ["save-off"] = false,
            ["save-on"] = false,
            ["stop"] = false,
            ["say"] = false,
            ["kick"] = false,
            ["ban"] = false,
        };

        foreach (var (commandId, readOnly) in expectedReadOnly)
        {
            rcon.Commands.Should().ContainKey(commandId);
            rcon.Commands[commandId].ReadOnly.Should().Be(readOnly, $"command '{commandId}' readOnly classification must match the definition");
        }

        // Audit delta: 'save-all' is rendered as the flush variant so both the stop ladder and the backup
        // quiesce sequence force a synchronous write rather than merely queuing one.
        rcon.Commands["save-all"].Template.Should().Be("save-all flush");
    }

    /// <summary>
    /// Audit delta #1: an explicit 'control.players' block using the 'summary-line' parser shape (verified
    /// against GameDefinitionYamlParser.Control.cs), covering the 'list' command's reply. The pattern is
    /// deliberately permissive — a mis-parse degrades to PlayerListFidelity.Unknown by design rather than
    /// breaking readiness or the stop ladder — because the exact reply text (particularly the zero-player
    /// case) could not be verified against a captured fixture.
    /// </summary>
    [Fact]
    public void Control_PlayersConfig_UsesSummaryLineParser_ForRconList()
    {
        var players = Parse().Definition!.Control.Players;

        players.Should().NotBeNull();
        players!.Preferred.Should().Equal("rcon.list");
        players.PollInterval.Should().Be(TimeSpan.FromSeconds(30));
        players.Parsers.Should().ContainKey("rcon.list");

        var parser = players.Parsers["rcon.list"].Should().BeOfType<PlayerParserSpec.SummaryLine>().Subject;
        parser.Pattern.Source.Should().Be(
            @"There are (?<count>\d+) of a max(?: of)? (?<max>\d+) players online:?(?<names>.*)");
        parser.NameSeparator.Should().Be(", ");
    }

    [Fact]
    public void Lifecycle_HasNoHealthSignalBlock_BecauseTheImageShipsNoHealthcheckToDistrust()
    {
        var lifecycle = Parse().Definition!.Lifecycle;

        lifecycle.HealthSignal.Should().BeNull();
    }

    /// <summary>
    /// Audit delta #2/#3: the full stop ladder, its ordering, per-stage timeouts, and explicit
    /// 'continueOnError' — save-all flush -> stop -> SIGTERM -> kill, raised from the original 30/30/30 to
    /// 30/30/120 so the SIGTERM stage's budget matches the STOP_DURATION setting the entrypoint itself
    /// honors.
    /// </summary>
    [Fact]
    public void Lifecycle_StopLadder_IsFourStagesWithExplicitContinueOnErrorAndRaisedTimeouts()
    {
        var stages = Parse().Definition!.Lifecycle.Stop.Stages;

        stages.Should().HaveCount(4);

        var saveAll = stages[0].Should().BeOfType<StopStage.Rcon>().Subject;
        saveAll.CommandId.Should().Be("save-all");
        saveAll.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        saveAll.ContinueOnError.Should().BeTrue();

        var stop = stages[1].Should().BeOfType<StopStage.Rcon>().Subject;
        stop.CommandId.Should().Be("stop");
        stop.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        stop.ContinueOnError.Should().BeTrue();

        var sigterm = stages[2].Should().BeOfType<StopStage.Signal>().Subject;
        sigterm.SignalName.Should().Be("SIGTERM");
        sigterm.Timeout.Should().Be(TimeSpan.FromSeconds(120));
        sigterm.ContinueOnError.Should().BeFalse();

        stages[3].Should().BeOfType<StopStage.Kill>();
    }

    [Fact]
    public void Settings_IncludeGameSpecificKeys_DistinctFromPalworlds()
    {
        var settings = Parse().Definition!.Settings.SelectMany(g => g.Items).ToList();

        settings.Select(s => s.Key).Should().Contain(["EULA", "MOTD", "TYPE", "VERSION", "MEMORY", "rcon-password", "STOP_DURATION"]);
        settings.Select(s => s.Key).Should().NotContain(["SERVER_NAME", "admin-password", "DAY_TIME_SPEEDRATE"]);

        var eula = settings.Single(s => s.Key == "EULA");
        eula.Required.Should().BeTrue();
        eula.Default.Should().BeNull();

        // Audit delta #6: bound from a declared secret, not the image's own random default. No literal
        // 'default' — the parser rejects one on a secret item.
        var rconPassword = settings.Single(s => s.Key == "rcon-password");
        rconPassword.Type.Should().Be(SettingType.Secret);
        rconPassword.IsSecret.Should().BeTrue();
        rconPassword.Default.Should().BeNull();
        rconPassword.Required.Should().BeTrue();

        // Audit delta: STOP_DURATION raised from the image's 60s default to 120s, matching the SIGTERM
        // stop-stage timeout above.
        var stopDuration = settings.Single(s => s.Key == "STOP_DURATION");
        stopDuration.Type.Should().Be(SettingType.Int);
        stopDuration.Default.Should().Be("120");
    }

    /// <summary>
    /// Audit delta #4: the resume half of backup quiesce/resume, now supported by the parser
    /// (GameDefinitionYamlParser.Backup.cs). Expresses the full canonical Minecraft sequence:
    /// save-off -> save-all flush -> capture -> save-on.
    /// </summary>
    [Fact]
    public void Backup_QuiesceAndResume_FollowTheFullCanonicalSaveOffFlushSaveOnSequence()
    {
        var backup = Parse().Definition!.Backup;

        backup.Quiesce.Should().HaveCount(2);
        var saveOff = backup.Quiesce[0].Should().BeOfType<QuiesceStep.Control>().Subject;
        saveOff.Channel.Should().Be("rcon");
        saveOff.CommandId.Should().Be("save-off");
        saveOff.Timeout.Should().Be(TimeSpan.FromSeconds(30));

        var saveAllFlush = backup.Quiesce[1].Should().BeOfType<QuiesceStep.Control>().Subject;
        saveAllFlush.Channel.Should().Be("rcon");
        saveAllFlush.CommandId.Should().Be("save-all");
        saveAllFlush.Timeout.Should().Be(TimeSpan.FromSeconds(30));

        backup.Resume.Should().HaveCount(1);
        var saveOn = backup.Resume[0].Should().BeOfType<QuiesceStep.Control>().Subject;
        saveOn.Channel.Should().Be("rcon");
        saveOn.CommandId.Should().Be("save-on");
        saveOn.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Audit delta #8: backup includes cover both nether/end world dirs, and excludes now cover the image's
    /// download cache, library jars, the server jar itself, and the live session.lock advisory file — none
    /// of which is world data.
    /// </summary>
    [Fact]
    public void Backup_IncludesNetherAndEnd_AndExcludesNonWorldArtifacts()
    {
        var backup = Parse().Definition!.Backup;

        backup.Include.Should().Contain([
            "${DATA_DIR}/world/**",
            "${DATA_DIR}/world_nether/**",
            "${DATA_DIR}/world_the_end/**",
        ]);

        backup.Exclude.Should().Contain([
            "${DATA_DIR}/logs/**",
            "${DATA_DIR}/crash-reports/**",
            "${DATA_DIR}/cache/**",
            "${DATA_DIR}/libraries/**",
            "${DATA_DIR}/*.jar",
            "${DATA_DIR}/**/session.lock",
        ]);
    }

    [Fact]
    public void Mods_SupportedIsTrue_UnlikePalworld()
    {
        Parse().Definition!.Mods.Supported.Should().BeTrue();
    }
}
