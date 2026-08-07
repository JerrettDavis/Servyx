using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Backups;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;

namespace Servyx.Definitions.Tests.RoundTrip;

/// <summary>
/// Parses the real, shipped <c>definitions/palworld-docker.yaml</c> and asserts every block of the typed
/// model deeply. This is the acceptance bar for the parser: the real definition must parse with zero
/// <see cref="ValidationSeverity.Error"/> issues, and every field the file declares must round-trip into
/// the model unchanged.
/// </summary>
public class PalworldDefinitionRoundTripTests
{
    private static DefinitionParseResult Parse() => new GameDefinitionYamlParser().Parse(DefinitionYamlFixture.RealYaml);

    [Fact]
    public void RealDefinition_ParsesWithNoErrors()
    {
        var result = Parse();

        result.Report.Issues.Where(i => i.Severity == ValidationSeverity.Error).Should().BeEmpty();
        result.Report.IsValid.Should().BeTrue();
        result.Definition.Should().NotBeNull();
    }

    [Fact]
    public void RealDefinition_ProducesOnlyTheExpectedWarnings()
    {
        var result = Parse();

        // Every Warning the real, unmodified definition produces, and why — no Errors, per
        // RealDefinition_ParsesWithNoErrors above, but these five Warnings are real and expected:
        //  - backup.adopt[].adapter can never be checked against DI-registered IBackupAdopter instances
        //    from this layer (see GameDefinitionYamlParser.Backup.cs).
        //  - capabilities.network declares 'var: QUERY_PORT' and 'var: REST_API_PORT', and the 'query' and
        //    'rest' channels declare 'port: "${QUERY_PORT}"'/'port: "${REST_API_PORT}"' respectively — none
        //    of the four has a matching settings-catalogue entry (only PORT and RCON_PORT are exposed as
        //    user-configurable settings). See GameDefinitionYamlParser.Semantics.cs for why this is a
        //    Warning rather than the brief's default Error, and this phase's final report for the flagged
        //    conflict between that default and this real data.
        result.Report.Issues.Where(i => i.Severity == ValidationSeverity.Warning).Should().HaveCount(5);
        result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Warning && i.Message.Contains("palworld-docker-cron", StringComparison.Ordinal));
        result.Report.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Warning && i.Message.Contains("'${QUERY_PORT}'", StringComparison.Ordinal));
        result.Report.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Warning && i.Message.Contains("'${REST_API_PORT}'", StringComparison.Ordinal));
    }

    [Fact]
    public void Metadata_MatchesTheFile()
    {
        var definition = Parse().Definition!;

        definition.ApiVersion.Should().Be("servyx.dev/v1");
        definition.Metadata.Id.Should().Be("palworld");
        definition.Metadata.Name.Should().Be("Palworld Dedicated Server");
        definition.Metadata.Version.Should().Be("1.0.0");
        definition.Metadata.License.Should().Be("MIT");
        definition.Metadata.Tags.Should().Equal("survival", "steam", "unreal");
    }

    [Fact]
    public void Capabilities_MatchesTheFile()
    {
        var capabilities = Parse().Definition!.Capabilities;

        capabilities.Network.Should().HaveCount(4);

        var game = capabilities.Network.Single(n => n.Purpose == "game");
        game.Port.Should().BeEquivalentTo(new PortRef.Literal(8211));
        game.Protocol.Should().Be(NetworkProtocol.Udp);
        game.Var.Should().Be("PORT");
        game.Published.Should().BeTrue();

        var query = capabilities.Network.Single(n => n.Purpose == "query");
        query.Port.Should().BeEquivalentTo(new PortRef.Literal(27015));
        query.Protocol.Should().Be(NetworkProtocol.Udp);
        query.Published.Should().BeTrue();

        var rcon = capabilities.Network.Single(n => n.Purpose == "rcon");
        rcon.Port.Should().BeEquivalentTo(new PortRef.Literal(25575));
        rcon.Protocol.Should().Be(NetworkProtocol.Tcp);
        rcon.Published.Should().BeFalse();

        var rest = capabilities.Network.Single(n => n.Purpose == "rest");
        rest.Port.Should().BeEquivalentTo(new PortRef.Literal(8212));
        rest.Protocol.Should().Be(NetworkProtocol.Tcp);
        rest.Published.Should().BeFalse();

        capabilities.Filesystem.Should().ContainSingle();
        capabilities.Filesystem[0].Path.Should().Be("${DATA_DIR}");
        capabilities.Filesystem[0].Access.Should().Be(FilesystemAccess.ReadWrite);

        capabilities.Egress.Should().BeEmpty();
        capabilities.Shell.Should().BeFalse();
        capabilities.Privileged.Should().BeFalse();
        capabilities.HostNetwork.Should().BeFalse();
    }

    [Fact]
    public void Deployments_BothProfiles_ParseInFull()
    {
        var deployments = Parse().Definition!.Deployments;

        deployments.Should().HaveCount(2);

        var docker = deployments.Single(d => d.Id == "docker-thijsvanloef");
        docker.Kind.Should().Be(DeploymentKind.Docker);
        docker.Detect.Should().NotBeNull();
        docker.Detect!.ImageRepo.Should().Be("thijsvanloef/palworld-server-docker");
        docker.Detect.RequiredMounts.Should().ContainSingle().Which.ContainerPath.Should().Be("/palworld");
        docker.Image.Should().NotBeNull();
        docker.Image!.Default.Should().Be("thijsvanloef/palworld-server-docker:latest");
        docker.DataDir.Should().Be("/palworld");
        docker.StopTimeout.Should().Be(TimeSpan.FromSeconds(60));
        docker.Executable.Should().BeNull();
        docker.Install.Should().BeEmpty();

        docker.Surfaces.Should().HaveCount(4);
        var env = docker.Surfaces.Single(s => s.Id == "env");
        env.Role.Should().Be(SurfaceRole.Authoritative);
        env.Format.Should().Be(SurfaceFormat.Dotenv);
        env.Locator.Should().BeEquivalentTo(new SurfaceLocator.HostFile("${COMPOSE_DIR}/.env"));
        env.MergePolicy.Should().Be(MergePolicy.PreserveUnknown);

        var compose = docker.Surfaces.Single(s => s.Id == "compose");
        compose.Format.Should().Be(SurfaceFormat.Yaml);
        compose.ManagedSubtree.Should().Be("services.palworld");
        compose.Locator.Should().BeEquivalentTo(new SurfaceLocator.HostFile("${COMPOSE_DIR}/compose.yaml"));

        var palworldSettings = docker.Surfaces.Single(s => s.Id == "palworldsettings");
        palworldSettings.Role.Should().Be(SurfaceRole.Derived);
        palworldSettings.Format.Should().Be(SurfaceFormat.Ini);
        palworldSettings.Codec.Should().Be("unreal-option-settings");
        palworldSettings.CodecPath.Should().Be("[\"/Script/Pal.PalGameWorldSettings\"].OptionSettings");
        palworldSettings.DerivedFrom.Should().Equal("env");
        palworldSettings.Regeneration.Should().NotBeNull();
        palworldSettings.Regeneration!.Kind.Should().Be(RegenerationKind.ContainerRestart);

        var live = docker.Surfaces.Single(s => s.Id == "live");
        live.Role.Should().Be(SurfaceRole.Runtime);
        live.Format.Should().Be(SurfaceFormat.Json);
        live.Locator.Should().BeEquivalentTo(new SurfaceLocator.ControlChannel("rest", "/v1/api/settings"));
        live.DerivedFrom.Should().Equal("palworldsettings");

        docker.Ignored.Should().ContainSingle();
        docker.Ignored[0].Path.Should().Be("${DATA_DIR}/Pal/Saved/Config/LinuxServer/GameUserSettings.ini");

        var native = deployments.Single(d => d.Id == "native-steamcmd");
        native.Kind.Should().Be(DeploymentKind.Process);
        native.Image.Should().BeNull();
        native.Executable.Should().NotBeNull();
        native.Executable!.Linux.Should().Be("./PalServer.sh");
        native.Executable.Windows.Should().Be("PalServer.exe");

        native.Install.Should().HaveCount(2);
        native.Install[0].Should().BeEquivalentTo(new InstallStep.SteamCmd(2394010, true));
        native.Install[1].Should().BeOfType<InstallStep.EnsureDir>()
            .Which.Path.Should().Be("${DATA_DIR}/Pal/Saved/Config/LinuxServer");

        native.Surfaces.Should().ContainSingle();
        native.Surfaces[0].Id.Should().Be("palworldsettings");
        native.Surfaces[0].Role.Should().Be(SurfaceRole.Authoritative);
        native.Surfaces[0].Format.Should().Be(SurfaceFormat.Ini);
        native.Ignored.Should().BeEmpty();
    }

    [Fact]
    public void Lifecycle_MatchesTheFile_IncludingTheFullOrderedStopLadder()
    {
        var lifecycle = Parse().Definition!.Lifecycle;

        lifecycle.Ready.Should().HaveCount(2);
        lifecycle.Ready[0].Should().BeOfType<ReadinessProbeDefinition.LogRegex>()
            .Which.Timeout.Should().Be(TimeSpan.FromMinutes(10));
        lifecycle.Ready[1].Should().BeOfType<ReadinessProbeDefinition.ControlProbe>()
            .Which.Channel.Should().Be("rcon");

        lifecycle.Stop.Stages.Should().HaveCount(4);

        var shutdown = lifecycle.Stop.Stages[0].Should().BeOfType<StopStage.Rcon>().Subject;
        shutdown.CommandId.Should().Be("shutdown");
        shutdown.Timeout.Should().Be(TimeSpan.FromSeconds(45));
        shutdown.Args.Should().Contain(new KeyValuePair<string, string>("seconds", "30"));
        shutdown.Args.Should().Contain(new KeyValuePair<string, string>("message", "Server shutting down"));

        var doexit = lifecycle.Stop.Stages[1].Should().BeOfType<StopStage.Rcon>().Subject;
        doexit.CommandId.Should().Be("doexit");
        doexit.Timeout.Should().Be(TimeSpan.FromSeconds(15));

        var signal = lifecycle.Stop.Stages[2].Should().BeOfType<StopStage.Signal>().Subject;
        signal.SignalName.Should().Be("SIGINT");
        signal.Timeout.Should().Be(TimeSpan.FromSeconds(30));

        lifecycle.Stop.Stages[3].Should().BeOfType<StopStage.Kill>();

        lifecycle.CrashDetection.Should().ContainSingle();
        lifecycle.CrashDetection[0].Pattern.Should().Be("Fatal error|Assertion failed");
        lifecycle.CrashDetection[0].Action.Should().Be("mark-crashed");

        lifecycle.HealthSignal.Should().NotBeNull();
        lifecycle.HealthSignal!.Trust.Should().Be(HealthSignalTrust.Ignore);
        lifecycle.HealthSignal.Explanation.Should().Be(
            "The container's own HEALTHCHECK calls http://localhost:8212/v1/api/info without admin "
            + "credentials and receives 401 Unauthorized on every probe. The Palworld server itself is "
            + "healthy — /v1/api/players returns OK on the same polling cycle. Servyx derives readiness "
            + "from its own authenticated detectors, never from this signal.");
    }

    [Fact]
    public void Control_RconChannel_HasAllEightCommands_WithCorrectReadOnlyClassification()
    {
        var control = Parse().Definition!.Control;

        var rcon = control.Channels.Single(c => c.Id == "rcon");
        rcon.Protocol.Should().Be("source-rcon");
        rcon.Port.Should().BeEquivalentTo(new PortRef.SettingRef("RCON_PORT"));
        rcon.PasswordRef.Should().Be(new SecretRef("secret", "admin-password"));
        rcon.EnabledWhen.Should().Be(new EnabledWhenPredicate("env", "RCON_ENABLED", "true"));
        rcon.Reachability.Should().HaveCount(3);
        rcon.Reachability[0].Should().BeOfType<ReachabilityStrategy.DirectTcp>();
        rcon.Reachability[1].Should().BeOfType<ReachabilityStrategy.DockerExecTool>()
            .Which.Tool.Should().Be("rcon-cli");
        rcon.Reachability[2].Should().BeOfType<ReachabilityStrategy.DockerExecNetwork>();

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

        rcon.Commands["shutdown"].Template.Should().Be("Shutdown {seconds} \"{message}\"");
    }

    [Fact]
    public void Control_RestAndQueryChannels_MatchTheFile()
    {
        var control = Parse().Definition!.Control;

        var rest = control.Channels.Single(c => c.Id == "rest");
        rest.Protocol.Should().Be("palworld-rest");
        rest.Auth.Should().BeOfType<AuthSpec.Basic>().Which.User.Should().Be("admin");
        rest.Endpoints.Should().HaveCount(4);
        rest.Endpoints.Values.Should().OnlyContain(e => e.ReadOnly);

        var query = control.Channels.Single(c => c.Id == "query");
        query.Protocol.Should().Be("a2s");
        query.Reachability.Should().ContainSingle().Which.Should().BeOfType<ReachabilityStrategy.DirectTcp>();
        query.Commands.Should().BeEmpty();
        query.Endpoints.Should().BeEmpty();
    }

    [Fact]
    public void Control_Players_MatchesTheFile()
    {
        var players = Parse().Definition!.Control.Players;

        players.Should().NotBeNull();
        players!.Preferred.Should().Equal("rest.players", "rcon.players", "query");
        players.PollInterval.Should().Be(TimeSpan.FromSeconds(30));
        players.Parsers.Should().ContainKey("rcon.players");
        players.Parsers["rcon.players"].Should().BeOfType<PlayerParserSpec.CsvWithHeader>()
            .Which.Columns.Should().Equal("name", "playerUid", "steamId");
    }

    [Fact]
    public void Settings_AllGroupsAndItems_MatchTheFile()
    {
        var settings = Parse().Definition!.Settings;

        settings.Should().HaveCount(4);
        settings.SelectMany(g => g.Items).Should().HaveCount(10);

        var serverName = settings.SelectMany(g => g.Items).Single(i => i.Key == "SERVER_NAME");
        serverName.Type.Should().Be(SettingType.String);
        serverName.Constraints.MaxLength.Should().Be(64);
        serverName.Bindings.Should().HaveCount(3);
        serverName.Bindings.Should().ContainSingle(b => IsWritableByKey(b));
        serverName.WritableSurface.Should().NotBeNull();
        serverName.WritableSurface!.SurfaceId.Should().Be("env");

        var port = settings.SelectMany(g => g.Items).Single(i => i.Key == "PORT");
        port.Type.Should().Be(SettingType.Port);
        port.Default.Should().Be("8211");
        port.RequiresRecreate.Should().BeTrue();
        port.Bindings.Should().Contain(b => IsPublishUdpPointer(b));

        var rconPort = settings.SelectMany(g => g.Items).Single(i => i.Key == "RCON_PORT");
        rconPort.PublishByDefault.Should().BeFalse();

        var players = settings.SelectMany(g => g.Items).Single(i => i.Key == "PLAYERS");
        players.Constraints.Min.Should().Be(1);
        players.Constraints.Max.Should().Be(32);

        var difficulty = settings.SelectMany(g => g.Items).Single(i => i.Key == "DIFFICULTY");
        difficulty.Constraints.Values.Should().Equal("None", "Casual", "Normal", "Hard");

        var speedRate = settings.SelectMany(g => g.Items).Single(i => i.Key == "DAY_TIME_SPEEDRATE");
        speedRate.Constraints.Step.Should().Be(0.1);
        speedRate.RenderFormat.Should().Be("F6");

        var pvp = settings.SelectMany(g => g.Items).Single(i => i.Key == "ENABLE_PLAYER_TO_PLAYER_DAMAGE");
        pvp.Constraints.TrueValue.Should().Be("True");
        pvp.Constraints.FalseValue.Should().Be("False");

        var adminPassword = settings.SelectMany(g => g.Items).Single(i => i.Key == "admin-password");
        adminPassword.Type.Should().Be(SettingType.Secret);
        adminPassword.Required.Should().BeTrue();
        adminPassword.Default.Should().BeNull();
        adminPassword.IsSecret.Should().BeTrue();
    }

    private static bool IsWritableByKey(SettingBinding binding) =>
        binding is SettingBinding.ByKey { Direction: BindingDirection.Write };

    private static bool IsPublishUdpPointer(SettingBinding binding) =>
        binding is SettingBinding.ByPointer { Strategy: "publish-udp" };

    [Fact]
    public void Backup_MatchesTheFile()
    {
        var backup = Parse().Definition!.Backup;

        backup.Include.Should().HaveCount(4);
        backup.Exclude.Should().HaveCount(2);

        backup.Quiesce.Should().ContainSingle();
        var quiesce = backup.Quiesce[0].Should().BeOfType<QuiesceStep.Control>().Subject;
        quiesce.Channel.Should().Be("rcon");
        quiesce.CommandId.Should().Be("save");
        quiesce.Timeout.Should().Be(TimeSpan.FromSeconds(30));

        backup.Adopt.Should().ContainSingle();
        backup.Adopt[0].Adapter.Should().Be("palworld-docker-cron");
        backup.Adopt[0].Pattern.Should().Be("*.tar.gz");
        backup.Adopt[0].Ownership.Should().Be(BackupOwnership.Foreign);

        backup.DefaultRetention.Should().Be(new RetentionPolicy(6, 7, 4));
    }

    [Fact]
    public void Saves_MatchesTheFile()
    {
        var saves = Parse().Definition!.Saves;

        saves.Should().NotBeNull();
        saves!.WorldRoot.Should().Be("${DATA_DIR}/Pal/Saved/SaveGames/0");
        saves.WorldIdPattern.Should().Be("^[0-9A-F]{32}$");
        saves.LevelFile.Should().Be("Level.sav");
        saves.MetaFile.Should().Be("LevelMeta.sav");
        saves.PlayerDir.Should().Be("Players");
    }

    [Fact]
    public void Mods_MatchesTheFile()
    {
        Parse().Definition!.Mods.Supported.Should().BeFalse();
    }
}
