using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Backups;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;

namespace Servyx.Definitions.Tests.RoundTrip;

/// <summary>
/// Parses the real, shipped <c>definitions/ark-asa-pok.yaml</c> — the third real game definition, and the
/// first whose control-channel reply formats are only partially documented (see the "UNVERIFIED" comments in
/// the YAML itself). This is the acceptance bar this definition must clear: the real file must parse with
/// zero <see cref="ValidationSeverity.Error"/> issues, and every block this test asserts on must match the
/// YAML's own declared shape exactly.
/// </summary>
public class ArkAscendedDefinitionRoundTripTests
{
    private static DefinitionParseResult Parse()
    {
        var repoRoot = RepoRootLocator.Find();
        var path = Path.Combine(repoRoot.FullName, "definitions", "ark-asa-pok.yaml");
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
        definition.Metadata.Id.Should().Be("ark-asa-pok");
        definition.Metadata.Name.Should().Be("ARK: Survival Ascended (acekorneya Docker)");
        definition.Metadata.Version.Should().Be("1.0.0");
        definition.Metadata.License.Should().Be("MIT");
        definition.Metadata.Tags.Should().Equal("survival", "steam", "unreal", "proton");
    }

    [Fact]
    public void Capabilities_DeclareGameAndRconPorts_ButNoSeparateQueryPort()
    {
        var capabilities = Parse().Definition!.Capabilities;

        capabilities.Network.Should().HaveCount(2);

        var game = capabilities.Network.Single(p => p.Purpose == "game");
        game.Port.Should().BeOfType<PortRef.Literal>().Which.Port.Should().Be(7777);
        game.Protocol.Should().Be(NetworkProtocol.Udp);
        game.Var.Should().Be("ASA_PORT");
        game.Published.Should().BeTrue();

        var rcon = capabilities.Network.Single(p => p.Purpose == "rcon");
        rcon.Port.Should().BeOfType<PortRef.Literal>().Which.Port.Should().Be(27020);
        rcon.Protocol.Should().Be(NetworkProtocol.Tcp);
        rcon.Var.Should().Be("RCON_PORT");
        rcon.Published.Should().BeFalse();

        // Unlike Palworld (a2s query on a third port), ARK ASA exposes no separate query protocol.
        capabilities.Network.Should().NotContain(p => p.Purpose == "query");

        // Both 'var' references (ASA_PORT, RCON_PORT) have a matching settings-catalogue entry, and the
        // image self-manages its own downloads with undocumented egress destinations (see the YAML's own
        // comment) — so this definition, like minecraft-itzg.yaml, produces zero Warnings.
        Parse().Report.Issues.Where(i => i.Severity == ValidationSeverity.Warning).Should().BeEmpty();
    }

    [Fact]
    public void Deployment_DeclaresFourAuthoritativeSurfaces_UnlikePalworldsDerivedIni()
    {
        var deployment = Parse().Definition!.Deployments.Single(d => d.Id == "docker-acekorneya");

        deployment.Detect!.ImageRepo.Should().Be("acekorneya/asa_server");
        deployment.Detect.RequiredMounts.Should().ContainSingle()
            .Which.ContainerPath.Should().Be("/home/pok/arkserver/ShooterGame/Saved");
        deployment.Image!.Default.Should().Be("acekorneya/asa_server:2_1_latest");
        deployment.DataDir.Should().Be("/home/pok/arkserver/ShooterGame/Saved");
        deployment.StopTimeout.Should().Be(TimeSpan.FromSeconds(220));

        deployment.Surfaces.Should().HaveCount(4);
        deployment.Surfaces.Select(s => s.Id).Should().Equal("env", "compose", "gameusersettings", "gameini");

        var env = deployment.Surfaces.Single(s => s.Id == "env");
        env.Format.Should().Be(SurfaceFormat.Dotenv);
        env.Role.Should().Be(SurfaceRole.Authoritative);

        var compose = deployment.Surfaces.Single(s => s.Id == "compose");
        compose.Format.Should().Be(SurfaceFormat.Yaml);
        compose.ManagedSubtree.Should().Be("services.ark-asa");

        // The finding these two surfaces exist to pin: GameUserSettings.ini and Game.ini PERSIST across
        // restarts and are hand-editable, unlike Palworld's PalWorldSettings.ini (regenerated wholesale from
        // .env on every boot, and therefore modeled 'derived'). Both are modeled 'authoritative' here — never
        // 'derived' — because nothing regenerates either file out from under a Servyx write.
        var gameUserSettings = deployment.Surfaces.Single(s => s.Id == "gameusersettings");
        gameUserSettings.Format.Should().Be(SurfaceFormat.Ini);
        gameUserSettings.Role.Should().Be(SurfaceRole.Authoritative);
        gameUserSettings.Regeneration.Should().BeNull();

        var gameIni = deployment.Surfaces.Single(s => s.Id == "gameini");
        gameIni.Format.Should().Be(SurfaceFormat.Ini);
        gameIni.Role.Should().Be(SurfaceRole.Authoritative);
        gameIni.Regeneration.Should().BeNull();
    }

    /// <summary>
    /// Pins the load-bearing arithmetic: the stop ladder's stage timeouts sum to 210s (60 + 30 + 120), and
    /// the declared grace period (240s) is at or above that total, which is what keeps
    /// <c>ResolveDeferredChecks</c> in <c>GameDefinitionYamlParser.Semantics.cs</c> from rejecting the file.
    /// Docker's own stop-timeout default is a mere 10 seconds — nowhere near enough to survive
    /// <c>SaveWorld</c> alone.
    /// </summary>
    [Fact]
    public void Deployment_StopGracePeriod_CoversTheFullStopLadderWithHeadroom()
    {
        var deployment = Parse().Definition!.Deployments.Single(d => d.Id == "docker-acekorneya");
        var stages = Parse().Definition!.Lifecycle.Stop.Stages;

        deployment.StopGracePeriod.Should().Be(TimeSpan.FromSeconds(240));

        var ladderTotal = stages.Aggregate(TimeSpan.Zero, (total, stage) => total + stage switch
        {
            StopStage.Rcon rcon => rcon.Timeout,
            StopStage.Signal signal => signal.Timeout,
            _ => TimeSpan.Zero,
        });

        ladderTotal.Should().Be(TimeSpan.FromSeconds(210));
        deployment.StopGracePeriod!.Value.Should().BeGreaterThanOrEqualTo(ladderTotal);
    }

    /// <summary>
    /// Unlike palworld-docker.yaml (control-probe on 'Info') and minecraft-itzg.yaml (control-probe on
    /// 'list'), this definition declares no control-probe fallback: the exact reply text of every RCON
    /// command it could probe with is unverified (see the player-parser comment below), and a false-negative
    /// readiness match would be far more dangerous than the player-list parser's worst case.
    /// </summary>
    [Fact]
    public void Lifecycle_Ready_IsLogRegexOnly_WithNoControlProbeFallback()
    {
        var ready = Parse().Definition!.Lifecycle.Ready;

        ready.Should().ContainSingle();
        var probe = ready[0].Should().BeOfType<ReadinessProbeDefinition.LogRegex>().Subject;
        probe.Pattern.Should().Be("Server has completed startup and is now advertising for join");
        probe.Timeout.Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Lifecycle_StopLadder_IsSaveThenDoExitThenSigtermThenKill()
    {
        var stages = Parse().Definition!.Lifecycle.Stop.Stages;

        stages.Should().HaveCount(4);

        var save = stages[0].Should().BeOfType<StopStage.Rcon>().Subject;
        save.CommandId.Should().Be("save");
        save.Timeout.Should().Be(TimeSpan.FromSeconds(60));
        save.ContinueOnError.Should().BeTrue();

        var doExit = stages[1].Should().BeOfType<StopStage.Rcon>().Subject;
        doExit.CommandId.Should().Be("doexit");
        doExit.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        doExit.ContinueOnError.Should().BeTrue();

        var sigterm = stages[2].Should().BeOfType<StopStage.Signal>().Subject;
        sigterm.SignalName.Should().Be("SIGTERM");
        sigterm.Timeout.Should().Be(TimeSpan.FromSeconds(120));
        sigterm.ContinueOnError.Should().BeFalse();

        stages[3].Should().BeOfType<StopStage.Kill>();
    }

    [Fact]
    public void Lifecycle_CrashDetectionDeclared_AndNoHealthSignalBlock()
    {
        var lifecycle = Parse().Definition!.Lifecycle;

        lifecycle.CrashDetection.Should().ContainSingle();
        lifecycle.CrashDetection[0].Pattern.Should().Be("Fatal error|Assertion failed");
        lifecycle.CrashDetection[0].Action.Should().Be("mark-crashed");

        // No documented HEALTHCHECK behavior to distrust for this image, unlike Palworld's.
        lifecycle.HealthSignal.Should().BeNull();
    }

    [Fact]
    public void Control_RconChannel_HasSevenCommands_WithNoAdmincheatPrefix_AndCorrectReadOnlyClassification()
    {
        var rcon = Parse().Definition!.Control.Channels.Single(c => c.Id == "rcon");

        rcon.Protocol.Should().Be("source-rcon");
        rcon.Port.Should().BeOfType<PortRef.SettingRef>().Which.Key.Should().Be("RCON_PORT");
        rcon.PasswordRef.Should().Be(new SecretRef("secret", "admin-password"));
        rcon.EnabledWhen.Should().Be(new EnabledWhenPredicate("env", "RCON_ENABLED", "true"));

        rcon.Reachability.Should().HaveCount(2);
        rcon.Reachability[0].Should().BeOfType<ReachabilityStrategy.DirectTcp>();
        rcon.Reachability[1].Should().BeOfType<ReachabilityStrategy.DockerExecNetwork>();

        rcon.Commands.Should().HaveCount(7);
        var expected = new Dictionary<string, (string Template, bool ReadOnly)>
        {
            ["players"] = ("ListPlayers", true),
            ["save"] = ("SaveWorld", false),
            ["broadcast"] = ("Broadcast {message}", false),
            ["chat"] = ("ServerChat {message}", false),
            ["kick"] = ("KickPlayer {steamId}", false),
            ["ban"] = ("BanPlayer {steamId}", false),
            ["doexit"] = ("DoExit", false),
        };

        foreach (var (commandId, (template, readOnly)) in expected)
        {
            rcon.Commands.Should().ContainKey(commandId);
            rcon.Commands[commandId].Template.Should().Be(template, $"command '{commandId}' must use ARK's no-admincheat-prefix RCON dialect");
            rcon.Commands[commandId].ReadOnly.Should().Be(readOnly, $"command '{commandId}' readOnly classification must match the definition");
        }
    }

    /// <summary>
    /// Pins the deliberately permissive shape of the UNVERIFIED player-list parser: it declares the required
    /// 'name' group and an optional 'id' group on <c>entryPattern</c>, and two broad <c>ignorePatterns</c>
    /// (an empty-server sentinel and a blank-line guard) rather than an exact string believed — but not
    /// confirmed — to be ListPlayers' real reply.
    /// </summary>
    [Fact]
    public void Control_PlayersParser_IsPermissiveLinesKind_MatchingTheUnverifiedListPlayersComment()
    {
        var players = Parse().Definition!.Control.Players!;

        players.Preferred.Should().Equal("rcon.players");
        players.PollInterval.Should().Be(TimeSpan.FromSeconds(30));

        players.Parsers.Should().ContainKey("rcon.players");
        var parser = players.Parsers["rcon.players"].Should().BeOfType<PlayerParserSpec.Lines>().Subject;

        parser.HeaderPattern.Should().BeNull();
        parser.EntryPattern.HasGroup(PlayerParserGroups.Name).Should().BeTrue();
        parser.EntryPattern.HasGroup(PlayerParserGroups.Id).Should().BeTrue();
        parser.IgnorePatterns.Should().HaveCount(2);
    }

    [Fact]
    public void Settings_IncludeGameSpecificKeys_GroupedIntoIdentityNetworkingGameplaySecurity()
    {
        var groups = Parse().Definition!.Settings;

        groups.Select(g => g.Name).Should().Equal("Identity", "Networking", "Gameplay", "Security");

        var settings = groups.SelectMany(g => g.Items).ToList();
        settings.Select(s => s.Key).Should().BeEquivalentTo(
            "SESSION_NAME", "ASA_PORT", "RCON_PORT", "MAP_NAME", "MAX_PLAYERS", "CLUSTER_ID", "MOD_IDS",
            "RCON_ENABLED", "BATTLEEYE", "admin-password", "server-password");

        var mapName = settings.Single(s => s.Key == "MAP_NAME");
        mapName.Default.Should().Be("TheIsland");
        mapName.RequiresRecreate.Should().BeTrue();

        var maxPlayers = settings.Single(s => s.Key == "MAX_PLAYERS");
        maxPlayers.Type.Should().Be(SettingType.Int);
        maxPlayers.Default.Should().Be("70");
        maxPlayers.Constraints.Min.Should().Be(1);
        maxPlayers.Constraints.Max.Should().Be(255);

        // Secrets must never carry a literal default — the parser rejects that outright — and both secret
        // items here declare none.
        var adminPassword = settings.Single(s => s.Key == "admin-password");
        adminPassword.Type.Should().Be(SettingType.Secret);
        adminPassword.IsSecret.Should().BeTrue();
        adminPassword.Required.Should().BeTrue();
        adminPassword.Default.Should().BeNull();

        var serverPassword = settings.Single(s => s.Key == "server-password");
        serverPassword.Type.Should().Be(SettingType.Secret);
        serverPassword.IsSecret.Should().BeTrue();
        serverPassword.Required.Should().BeFalse();
        serverPassword.Default.Should().BeNull();
    }

    [Fact]
    public void Backup_IncludesSavedArksAndBothInis_ExcludesLogsAndProtonScratch_QuiescesWithSaveWorldOnly()
    {
        var backup = Parse().Definition!.Backup;

        backup.Include.Should().Equal(
            "${DATA_DIR}/SavedArks/**",
            "${DATA_DIR}/Config/WindowsServer/GameUserSettings.ini",
            "${DATA_DIR}/Config/WindowsServer/Game.ini",
            "${COMPOSE_DIR}/.env",
            "${COMPOSE_DIR}/compose.yaml");

        backup.Exclude.Should().Equal(
            "${DATA_DIR}/Logs/**",
            "${DATA_DIR}/**/*.bak",
            "${DATA_DIR}/**/*.tmp",
            "${DATA_DIR}/**/dxvk_cache/**");

        // ARK has no save-off/save-on pair the way some titles do, so quiescing is a single SaveWorld step —
        // unlike a hypothetical game with a bracketed quiesce/resume, and there is no 'resume' block here.
        backup.Quiesce.Should().ContainSingle();
        var quiesce = backup.Quiesce[0].Should().BeOfType<QuiesceStep.Control>().Subject;
        quiesce.Channel.Should().Be("rcon");
        quiesce.CommandId.Should().Be("save");
        quiesce.Timeout.Should().Be(TimeSpan.FromSeconds(60));
        backup.Resume.Should().BeEmpty();

        backup.Adopt.Should().BeEmpty();
        backup.DefaultRetention.Should().Be(new RetentionPolicy(KeepHourly: 6, KeepDaily: 7, KeepWeekly: 4));
    }

    /// <summary>
    /// Pins the documented schema workaround: <c>levelFile</c>/<c>metaFile</c> cannot interpolate
    /// <c>${MAP_NAME}</c> (confirmed by reading <c>GameDefinitionYamlParser.SavesAndMods.cs</c> — neither
    /// field is queued through <c>QueueTemplateTokens</c> the way <c>worldRoot</c> is), so a glob is used
    /// instead of a templated exact filename.
    /// </summary>
    [Fact]
    public void Saves_UseAGlobInsteadOfMapNameInterpolation_BecauseTheSchemaDoesNotSupportIt()
    {
        var saves = Parse().Definition!.Saves;

        saves.Should().NotBeNull();
        saves!.WorldRoot.Should().Be("${DATA_DIR}/SavedArks");
        saves.WorldIdPattern.Should().BeNull();
        saves.LevelFile.Should().Be("*_WP.ark");
        saves.MetaFile.Should().Be("*_WP.ark");
        saves.PlayerDir.Should().BeNull();
    }

    [Fact]
    public void Mods_SupportedIsTrue_ViaTheImagesOwnCurseForgeDownload()
    {
        Parse().Definition!.Mods.Supported.Should().BeTrue();
    }
}
