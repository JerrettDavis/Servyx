using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Backups;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;

namespace Servyx.Definitions.Tests.RoundTrip;

/// <summary>
/// Parses the real, shipped <c>definitions/factorio-factoriotools.yaml</c> — the fourth real game definition,
/// and the first to use the brand-new <c>deployments[].files[]</c> seeding feature, a <c>format: json</c>
/// config surface addressed by RFC 6901 JSON pointers, and a stop ladder with no RCON quit-shaped stage at
/// all. This is the acceptance bar this definition must clear: the real file must parse with zero
/// <see cref="ValidationSeverity.Error"/> issues, and every block this test asserts on must match the YAML's
/// own declared shape exactly.
/// </summary>
public class FactorioDefinitionRoundTripTests
{
    private static DefinitionParseResult Parse()
    {
        var repoRoot = RepoRootLocator.Find();
        var path = Path.Combine(repoRoot.FullName, "definitions", "factorio-factoriotools.yaml");
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

    [Fact]
    public void Metadata_MatchesTheFile()
    {
        var definition = Parse().Definition!;

        definition.ApiVersion.Should().Be("servyx.dev/v1");
        definition.Metadata.Id.Should().Be("factorio-factoriotools");
        definition.Metadata.Name.Should().Be("Factorio Dedicated Server (factoriotools)");
        definition.Metadata.Version.Should().Be("1.0.0");
        definition.Metadata.License.Should().Be("MIT");
        definition.Metadata.Tags.Should().Equal("survival", "factory", "steam");
    }

    [Fact]
    public void Capabilities_DeclareGameAndRconPorts_WithNoEgressDeclared()
    {
        var capabilities = Parse().Definition!.Capabilities;

        capabilities.Network.Should().HaveCount(2);

        var game = capabilities.Network.Single(p => p.Purpose == "game");
        game.Port.Should().BeOfType<PortRef.Literal>().Which.Port.Should().Be(34197);
        game.Protocol.Should().Be(NetworkProtocol.Udp);
        game.Var.Should().Be("PORT");
        game.Published.Should().BeTrue();

        var rcon = capabilities.Network.Single(p => p.Purpose == "rcon");
        rcon.Port.Should().BeOfType<PortRef.Literal>().Which.Port.Should().Be(27015);
        rcon.Protocol.Should().Be(NetworkProtocol.Tcp);
        rcon.Var.Should().Be("RCON_PORT");
        rcon.Published.Should().BeFalse();

        // Mod-download egress hosts are not part of this definition's verified fact set (same position as
        // ark-asa-pok.yaml), so 'egress' is empty rather than guessed at.
        capabilities.Egress.Should().BeEmpty();

        // Both 'var' references (PORT, RCON_PORT) resolve against a declared settings-catalogue entry, so
        // this definition produces zero Warnings.
        Parse().Report.Issues.Where(i => i.Severity == ValidationSeverity.Warning).Should().BeEmpty();
    }

    [Fact]
    public void Deployment_DeclaresThreeAuthoritativeSurfaces_IncludingTheNewJsonFormatSurface()
    {
        var deployment = Parse().Definition!.Deployments.Single(d => d.Id == "docker-factoriotools");

        deployment.Detect!.ImageRepo.Should().Be("factoriotools/factorio");
        deployment.Detect.RequiredMounts.Should().ContainSingle().Which.ContainerPath.Should().Be("/factorio");
        deployment.Image!.Default.Should().Be("factoriotools/factorio:stable");
        deployment.DataDir.Should().Be("/factorio");
        deployment.StopTimeout.Should().Be(TimeSpan.FromSeconds(285));

        deployment.Surfaces.Should().HaveCount(3);
        deployment.Surfaces.Select(s => s.Id).Should().Equal("env", "compose", "server-settings");

        var env = deployment.Surfaces.Single(s => s.Id == "env");
        env.Format.Should().Be(SurfaceFormat.Dotenv);
        env.Role.Should().Be(SurfaceRole.Authoritative);

        var compose = deployment.Surfaces.Single(s => s.Id == "compose");
        compose.Format.Should().Be(SurfaceFormat.Yaml);
        compose.ManagedSubtree.Should().Be("services.factorio");

        // The image's own entrypoint never regenerates this file (unlike Palworld's PalWorldSettings.ini or
        // Minecraft's server.properties, both 'derived'), so it is modeled 'authoritative' with no
        // 'regeneration' block, and is the first surface in this project to use 'format: json'.
        var serverSettings = deployment.Surfaces.Single(s => s.Id == "server-settings");
        serverSettings.Format.Should().Be(SurfaceFormat.Json);
        serverSettings.Role.Should().Be(SurfaceRole.Authoritative);
        serverSettings.Regeneration.Should().BeNull();

        deployment.Ignored.Should().HaveCount(2);
        deployment.Ignored.Select(i => i.Path).Should().Equal(
            "${DATA_DIR}/config/map-gen-settings.json",
            "${DATA_DIR}/config/map-settings.json");
    }

    /// <summary>
    /// Pins the single least obvious fact in this definition: factoriotools/factorio accepts NO environment
    /// variable for its RCON password, and only generates one itself into 'config/rconpw' when that file is
    /// absent. 'files[]' seeds it first so the image's own generator never runs, and the same secret key is
    /// what 'control.channels[0].passwordRef' authenticates with (see
    /// <see cref="Control_RconChannel_PasswordRefMatchesTheSeededFilesSecret"/>).
    /// </summary>
    [Fact]
    public void Deployment_SeedsTheRconPasswordFile_BeforeTheImagesOwnGeneratorCanRun()
    {
        var deployment = Parse().Definition!.Deployments.Single(d => d.Id == "docker-factoriotools");

        deployment.Files.Should().ContainSingle();
        var file = deployment.Files[0];

        file.Path.Should().Be("${DATA_DIR}/config/rconpw");
        file.Mode.Should().Be("0600");
        file.CreateOnly.Should().BeTrue();
        file.ContentFrom.Should().Be("secret:rcon-password");
        file.Content.Should().BeNull();
    }

    /// <summary>
    /// Pins the load-bearing arithmetic: the stop ladder's stage timeouts sum to 270s (30 + 240), and the
    /// declared grace period (300s) is at or above that total, which is what keeps
    /// <c>ResolveDeferredChecks</c> in <c>GameDefinitionYamlParser.Semantics.cs</c> from rejecting the file.
    /// Docker's own stop-timeout default is a mere 10 seconds — nowhere near enough to survive a SIGTERM-
    /// triggered save alone.
    /// </summary>
    [Fact]
    public void Deployment_StopGracePeriod_CoversTheFullStopLadderWithHeadroom()
    {
        var deployment = Parse().Definition!.Deployments.Single(d => d.Id == "docker-factoriotools");
        var stages = Parse().Definition!.Lifecycle.Stop.Stages;

        deployment.StopGracePeriod.Should().Be(TimeSpan.FromSeconds(300));

        var ladderTotal = stages.Aggregate(TimeSpan.Zero, (total, stage) => total + stage switch
        {
            StopStage.Rcon rcon => rcon.Timeout,
            StopStage.Signal signal => signal.Timeout,
            _ => TimeSpan.Zero,
        });

        ladderTotal.Should().Be(TimeSpan.FromSeconds(270));
        deployment.StopGracePeriod!.Value.Should().BeGreaterThanOrEqualTo(ladderTotal);
    }

    /// <summary>
    /// The readiness log line is UNVERIFIED (unlike ark-asa-pok.yaml's confirmed line), so per this
    /// definition's brief the control probe is PRIMARY and the log-regex is a secondary fallback — the
    /// opposite ordering from palworld-docker.yaml and minecraft-itzg.yaml, where a confirmed log line comes
    /// first and an authenticated probe is the fallback. The probe's 'expect' pattern is '.*' — matching any
    /// response, including an empty one — specifically so readiness never depends on the unverified exact
    /// reply text of '/players online'.
    /// </summary>
    [Fact]
    public void Lifecycle_Ready_PutsTheControlProbeFirst_AheadOfTheUnverifiedLogLine()
    {
        var ready = Parse().Definition!.Lifecycle.Ready;

        ready.Should().HaveCount(2);

        var probe = ready[0].Should().BeOfType<ReadinessProbeDefinition.ControlProbe>().Subject;
        probe.Channel.Should().Be("rcon");
        probe.Command.Should().Be("players");
        probe.Expect.Should().Be(".*");
        probe.Interval.Should().Be(TimeSpan.FromSeconds(10));
        probe.Timeout.Should().Be(TimeSpan.FromMinutes(20));

        var logRegex = ready[1].Should().BeOfType<ReadinessProbeDefinition.LogRegex>().Subject;
        logRegex.Pattern.Should().Be(@"Hosting game at IP ADDR:\(0\.0\.0\.0:\d+\)");
        logRegex.Timeout.Should().Be(TimeSpan.FromMinutes(20));
    }

    /// <summary>
    /// Pins the quit-less ladder: exactly two escalating stages (a best-effort RCON save, then SIGTERM) plus
    /// the mandatory terminal kill — NOT three RCON-then-signal-then-kill stages the way
    /// palworld-docker.yaml (Shutdown + DoExit) and ark-asa-pok.yaml (SaveWorld + DoExit) both use. There is
    /// deliberately no second RCON stage here because Factorio's RCON dialect has no quit/stop/exit command
    /// to put there (see <see cref="Control_RconChannel_HasNoQuitShapedCommand"/>).
    /// </summary>
    [Fact]
    public void Lifecycle_StopLadder_IsSaveThenSigtermThenKill_WithNoRconQuitStage()
    {
        var stages = Parse().Definition!.Lifecycle.Stop.Stages;

        stages.Should().HaveCount(3);

        var save = stages[0].Should().BeOfType<StopStage.Rcon>().Subject;
        save.CommandId.Should().Be("save");
        save.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        save.ContinueOnError.Should().BeTrue();

        var sigterm = stages[1].Should().BeOfType<StopStage.Signal>().Subject;
        sigterm.SignalName.Should().Be("SIGTERM");
        sigterm.Timeout.Should().Be(TimeSpan.FromSeconds(240));
        sigterm.ContinueOnError.Should().BeFalse();

        stages[2].Should().BeOfType<StopStage.Kill>();
    }

    [Fact]
    public void Lifecycle_CrashDetectionDeclared_AndNoHealthSignalBlock()
    {
        var lifecycle = Parse().Definition!.Lifecycle;

        lifecycle.CrashDetection.Should().ContainSingle();
        lifecycle.CrashDetection[0].Pattern.Should().Be(@"Segmentation fault|Aborted \(core dumped\)|std::exception");
        lifecycle.CrashDetection[0].Action.Should().Be("mark-crashed");

        lifecycle.HealthSignal.Should().BeNull();
    }

    [Fact]
    public void Control_RconChannel_PasswordRefMatchesTheSeededFilesSecret()
    {
        var rcon = Parse().Definition!.Control.Channels.Single(c => c.Id == "rcon");
        var deployment = Parse().Definition!.Deployments.Single(d => d.Id == "docker-factoriotools");

        rcon.Protocol.Should().Be("source-rcon");
        rcon.Port.Should().BeOfType<PortRef.SettingRef>().Which.Key.Should().Be("RCON_PORT");
        rcon.PasswordRef.Should().Be(new SecretRef("secret", "rcon-password"));
        rcon.PasswordRef!.Key.Should().Be(deployment.Files[0].ContentFrom!.Split(':')[1]);

        rcon.Reachability.Should().HaveCount(2);
        rcon.Reachability[0].Should().BeOfType<ReachabilityStrategy.DirectTcp>();
        rcon.Reachability[1].Should().BeOfType<ReachabilityStrategy.DockerExecNetwork>();
    }

    [Fact]
    public void Control_RconChannel_HasFiveCommands_WithCorrectReadOnlyClassification()
    {
        var rcon = Parse().Definition!.Control.Channels.Single(c => c.Id == "rcon");

        rcon.Commands.Should().HaveCount(5);
        var expected = new Dictionary<string, (string Template, bool ReadOnly)>
        {
            ["players"] = ("/players online", true),
            ["save"] = ("/server-save", false),
            ["broadcast"] = ("/shout {message}", false),
            ["kick"] = ("/kick {player} {reason}", false),
            ["ban"] = ("/ban {player} {reason}", false),
        };

        foreach (var (commandId, (template, readOnly)) in expected)
        {
            rcon.Commands.Should().ContainKey(commandId);
            rcon.Commands[commandId].Template.Should().Be(template);
            rcon.Commands[commandId].ReadOnly.Should().Be(readOnly);
        }
    }

    /// <summary>Verified, not an oversight: Factorio's Source-RCON dialect exposes no quit/stop/exit-shaped command at all.</summary>
    [Fact]
    public void Control_RconChannel_HasNoQuitShapedCommand()
    {
        var rcon = Parse().Definition!.Control.Channels.Single(c => c.Id == "rcon");

        rcon.Commands.Keys.Should().NotContain(id =>
            id.Contains("quit", StringComparison.OrdinalIgnoreCase)
            || id.Contains("exit", StringComparison.OrdinalIgnoreCase)
            || id.Contains("stop", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Pins the deliberately permissive shape of the UNVERIFIED '/players online' parser: a required 'name'
    /// group tolerating (but discarding) a trailing parenthesized annotation, and broad ignore patterns for an
    /// empty-server sentinel and blank lines — the same permissive-over-precise reasoning
    /// ark-asa-pok.yaml's own UNVERIFIED 'rcon.players' parser uses.
    /// </summary>
    [Fact]
    public void Control_PlayersParser_IsPermissiveLinesKind_MatchingTheUnverifiedPlayersOnlineComment()
    {
        var players = Parse().Definition!.Control.Players!;

        players.Preferred.Should().Equal("rcon.players");
        players.PollInterval.Should().Be(TimeSpan.FromSeconds(30));

        players.Parsers.Should().ContainKey("rcon.players");
        var parser = players.Parsers["rcon.players"].Should().BeOfType<PlayerParserSpec.Lines>().Subject;

        parser.HeaderPattern.Should().BeNull();
        parser.EntryPattern.HasGroup(PlayerParserGroups.Name).Should().BeTrue();
        parser.IgnorePatterns.Should().HaveCount(3);
    }

    [Fact]
    public void Settings_IncludeGameSpecificKeys_GroupedIntoIdentityNetworkingGameplayModsSecurity()
    {
        var groups = Parse().Definition!.Settings;

        groups.Select(g => g.Name).Should().Equal("Identity", "Networking", "Gameplay", "Mods", "Security");

        var settings = groups.SelectMany(g => g.Items).ToList();
        settings.Select(s => s.Key).Should().BeEquivalentTo(
            "name", "description", "PORT", "RCON_PORT", "PRESET", "BIND", "GENERATE_NEW_SAVE",
            "LOAD_LATEST_SAVE", "SAVE_NAME", "DLC_SPACE_AGE", "max_players", "visibility_public",
            "visibility_lan", "require_user_verification", "autosave_interval", "autosave_slots",
            "allow_commands", "auto_pause", "UPDATE_MODS_ON_START", "factorio-username", "factorio-token",
            "game-password", "rcon-password");

        var maxPlayers = settings.Single(s => s.Key == "max_players");
        maxPlayers.Type.Should().Be(SettingType.Int);
        maxPlayers.Default.Should().Be("0");
        maxPlayers.Constraints.Min.Should().Be(0);

        var visibilityPublic = settings.Single(s => s.Key == "visibility_public");
        var pointerBinding = visibilityPublic.Bindings.Should().ContainSingle()
            .Which.Should().BeOfType<SettingBinding.ByPointer>().Subject;
        pointerBinding.SurfaceId.Should().Be("server-settings");
        pointerBinding.Pointer.Should().Be("/visibility/public");

        var visibilityLan = settings.Single(s => s.Key == "visibility_lan");
        visibilityLan.Bindings.Should().ContainSingle()
            .Which.Should().BeOfType<SettingBinding.ByPointer>()
            .Which.Pointer.Should().Be("/visibility/lan");

        // Secrets must never carry a literal default — the parser rejects that outright.
        foreach (var secretKey in new[] { "factorio-username", "factorio-token", "game-password", "rcon-password" })
        {
            var secret = settings.Single(s => s.Key == secretKey);
            secret.Type.Should().Be(SettingType.Secret);
            secret.IsSecret.Should().BeTrue();
            secret.Default.Should().BeNull();
        }

        var rconPassword = settings.Single(s => s.Key == "rcon-password");
        rconPassword.Required.Should().BeTrue();
        // Never bound to a config surface: only consumed via 'secret:' references (files[].contentFrom and
        // control.channels[0].passwordRef) — see the deployment's 'files[]' assertions above.
        rconPassword.Bindings.Should().BeEmpty();
    }

    [Fact]
    public void Backup_IncludesSavesAndJsonConfig_ExcludesTruncatedAutosaves_QuiescesWithServerSaveOnly()
    {
        var backup = Parse().Definition!.Backup;

        backup.Include.Should().Equal(
            "${DATA_DIR}/saves/**",
            "${DATA_DIR}/config/server-settings.json",
            "${DATA_DIR}/config/server-adminlist.json",
            "${DATA_DIR}/config/server-whitelist.json",
            "${DATA_DIR}/config/server-banlist.json",
            "${DATA_DIR}/mods/**",
            "${COMPOSE_DIR}/.env",
            "${COMPOSE_DIR}/compose.yaml");

        backup.Exclude.Should().Equal("${DATA_DIR}/saves/*.tmp.zip");

        backup.Quiesce.Should().ContainSingle();
        var quiesce = backup.Quiesce[0].Should().BeOfType<QuiesceStep.Control>().Subject;
        quiesce.Channel.Should().Be("rcon");
        quiesce.CommandId.Should().Be("save");
        quiesce.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        backup.Resume.Should().BeEmpty();

        backup.Adopt.Should().BeEmpty();
        backup.DefaultRetention.Should().Be(new RetentionPolicy(KeepHourly: 6, KeepDaily: 7, KeepWeekly: 4));
    }

    /// <summary>
    /// A Factorio save is a single '.zip' archive with no split between "the level" and "the level's
    /// metadata" the way Palworld (Level.sav/LevelMeta.sav) or Minecraft (level.dat) draw one — so, like
    /// ark-asa-pok.yaml's own single-file '*_WP.ark' saves, 'levelFile' and 'metaFile' point at the same glob.
    /// </summary>
    [Fact]
    public void Saves_UseTheSameGlobForLevelAndMeta_BecauseAFactorioSaveIsASingleArchive()
    {
        var saves = Parse().Definition!.Saves;

        saves.Should().NotBeNull();
        saves!.WorldRoot.Should().Be("${DATA_DIR}/saves");
        saves.WorldIdPattern.Should().BeNull();
        saves.LevelFile.Should().Be("*.zip");
        saves.MetaFile.Should().Be("*.zip");
        saves.PlayerDir.Should().BeNull();
    }

    [Fact]
    public void Mods_SupportedIsTrue_ViaTheImagesOwnModPortalDownload()
    {
        Parse().Definition!.Mods.Supported.Should().BeTrue();
    }
}
