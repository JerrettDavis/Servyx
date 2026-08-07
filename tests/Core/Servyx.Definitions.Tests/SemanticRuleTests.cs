using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;

namespace Servyx.Definitions.Tests;

/// <summary>
/// One test per semantic rule enforced by <see cref="GameDefinitionYamlParser"/>, each built by taking a
/// single, targeted mutation of the real <c>definitions/palworld-docker.yaml</c> — see
/// <see cref="DefinitionYamlFixture.Mutate"/> — so the fixture stays realistic and the test isolates
/// exactly the rule under test.
/// </summary>
public class SemanticRuleTests
{
    private static DefinitionParseResult ParseYaml(string yaml) => new GameDefinitionYamlParser().Parse(yaml);

    private static void AssertSingleError(DefinitionParseResult result, string messageContains)
    {
        result.Definition.Should().BeNull();
        result.Report.IsValid.Should().BeFalse();
        result.Report.Issues.Should().Contain(i => i.Severity == ValidationSeverity.Error && i.Message.Contains(messageContains, StringComparison.Ordinal));
    }

    [Fact]
    public void SettingBinding_UnknownSurface_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "{ surface: env,  direction: write, key: SERVER_NAME }",
            "{ surface: nope,  direction: write, key: SERVER_NAME }");

        AssertSingleError(ParseYaml(yaml), "surface 'nope'");
    }

    /// <summary>
    /// The brief's default severity for this rule is Error. This project downgrades it to Warning — see the
    /// remarks in <c>GameDefinitionYamlParser.Semantics.cs</c> — because the real, shipped
    /// <c>definitions/palworld-docker.yaml</c> itself declares <c>var: QUERY_PORT</c> and
    /// <c>var: REST_API_PORT</c> with no matching settings-catalogue entry; enforcing Error here would fail
    /// this project's own primary fixture. Flagged as a conflict between the brief and the real data in this
    /// phase's final report, not silently resolved.
    /// </summary>
    [Fact]
    public void CapabilitiesNetworkVar_NotADeclaredSettingsKey_IsWarning_NotError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "var: PORT,       published: true }",
            "var: NOPEVAR,    published: true }");

        var result = ParseYaml(yaml);

        result.Definition.Should().NotBeNull();
        result.Report.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Warning && i.Message.Contains("'${NOPEVAR}'", StringComparison.Ordinal));
        result.Report.Issues.Should().NotContain(i => i.Severity == ValidationSeverity.Error && i.Message.Contains("NOPEVAR", StringComparison.Ordinal));
    }

    [Fact]
    public void LifecycleStopControlStage_UnknownCommandOnReferencedChannel_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "{ kind: control, channel: rcon, command: doexit, timeout: 15s }",
            "{ kind: control, channel: rcon, command: nope, timeout: 15s }");

        AssertSingleError(ParseYaml(yaml), "RCON command 'nope'");
    }

    [Fact]
    public void LifecycleStopControlStage_NonRconChannel_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "{ kind: control, channel: rcon, command: doexit, timeout: 15s }",
            "{ kind: control, channel: rest, command: doexit, timeout: 15s }");

        AssertSingleError(ParseYaml(yaml), "only 'rcon' is currently supported");
    }

    [Fact]
    public void StopLadder_FinalStageNotKill_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "    - { kind: signal, signal: SIGINT, timeout: 30s }\n    - { kind: kill }",
            "    - { kind: kill }\n    - { kind: signal, signal: SIGINT, timeout: 30s }");

        AssertSingleError(ParseYaml(yaml), "final stage must be");
    }

    [Fact]
    public void PlayersPreferred_UnknownChannel_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "preferred: [rest.players, rcon.players, query]",
            "preferred: [rest.players, rcon.players, bogus]");

        AssertSingleError(ParseYaml(yaml), "channel 'bogus'");
    }

    [Fact]
    public void PlayersPreferred_UnknownOperationOnKnownChannel_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "preferred: [rest.players, rcon.players, query]",
            "preferred: [rest.bogus, rcon.players, query]");

        AssertSingleError(ParseYaml(yaml), "operation 'bogus'");
    }

    [Fact]
    public void PointerBinding_OnDotenvSurface_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "{ surface: env,  direction: write, key: SERVER_NAME }",
            "{ surface: env,  direction: write, pointer: \"/SERVER_NAME\" }");

        AssertSingleError(ParseYaml(yaml), "'pointer' addressing scheme is not valid");
    }

    [Fact]
    public void EnabledWhen_UnsupportedShape_IsRejectedNotEvaluated()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "enabledWhen: \"env.RCON_ENABLED == 'true'\"",
            "enabledWhen: \"env.RCON_ENABLED != 'true'\"");

        AssertSingleError(ParseYaml(yaml), "only supported shape");
    }

    [Fact]
    public void SecretRef_UnsupportedScheme_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate("passwordRef: \"secret:admin-password\"", "passwordRef: \"vault:admin-password\"");

        AssertSingleError(ParseYaml(yaml), "only 'secret:' is accepted");
    }

    [Fact]
    public void UnknownTemplateVariable_InPathField_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "{ path: \"${DATA_DIR}\", access: rw, purpose: \"config, saves, image-managed backups\" }",
            "{ path: \"${BOGUS_DIR}\", access: rw, purpose: \"config, saves, image-managed backups\" }");

        AssertSingleError(ParseYaml(yaml), "'${BOGUS_DIR}'");
    }

    [Fact]
    public void PathField_ContainingTraversalSegment_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "\"${DATA_DIR}/Pal/Saved/Logs/**\"",
            "\"${DATA_DIR}/../etc/passwd\"");

        AssertSingleError(ParseYaml(yaml), "path-traversal segment");
    }

    [Fact]
    public void PathField_AbsoluteWithoutDeclaredRootVariable_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "worldRoot: \"${DATA_DIR}/Pal/Saved/SaveGames/0\"",
            "worldRoot: \"/etc/palworld/saves\"");

        AssertSingleError(ParseYaml(yaml), "escape the server root");
    }

    [Fact]
    public void NetworkPortPurpose_DuplicatedWithinCapabilities_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "purpose: query, var: QUERY_PORT",
            "purpose: game, var: QUERY_PORT");

        AssertSingleError(ParseYaml(yaml), "'purpose: game' more than once");
    }

    [Fact]
    public void SecretSetting_WithLiteralDefault_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "        type: secret\n        required: true\n        bindings:\n          - { surface: env, direction: write, key: ADMIN_PASSWORD }",
            "        type: secret\n        required: true\n        default: \"hunter2\"\n        bindings:\n          - { surface: env, direction: write, key: ADMIN_PASSWORD }");

        AssertSingleError(ParseYaml(yaml), "secrets must always originate from the secret store");
    }

    [Fact]
    public void DockerDeployment_MissingImage_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "    image:\n      default: \"thijsvanloef/palworld-server-docker:latest\"\n",
            string.Empty);

        AssertSingleError(ParseYaml(yaml), "declares no 'image.default'");
    }

    [Fact]
    public void ProcessDeployment_MissingExecutable_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "    executable: { linux: \"./PalServer.sh\", windows: \"PalServer.exe\" }\n",
            string.Empty);

        AssertSingleError(ParseYaml(yaml), "declares no 'executable'");
    }

    [Fact]
    public void Metadata_MissingId_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate("  id: palworld\n", string.Empty);

        AssertSingleError(ParseYaml(yaml), "'metadata' declares no 'id'");
    }

    [Fact]
    public void Deployments_EmptyList_IsError()
    {
        var yaml = DefinitionYamlFixture.RealYaml;
        var start = yaml.IndexOf("deployments:", StringComparison.Ordinal);
        var end = yaml.IndexOf("lifecycle:", StringComparison.Ordinal);
        yaml = string.Concat(yaml.AsSpan(0, start), "deployments: []\n\n", yaml.AsSpan(end));

        AssertSingleError(ParseYaml(yaml), "at least one deployment profile is required");
    }

    /// <summary>
    /// Ports the former <c>PalworldDefinitionLoaderTests.Parse_Throws_WhenNoDeploymentHasDockerKind</c>,
    /// which had no equivalent against the new <see cref="GameDefinitionYamlParser"/>/catalog pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The old hand-rolled loader threw when no deployment declared <c>kind: docker</c>, because it hardcoded
    /// the assumption that a docker profile must exist. <see cref="GameDefinitionYamlParser"/> enforces no
    /// such rule directly — nowhere does it require a <see cref="DeploymentKind.Docker"/> entry among
    /// <c>deployments</c>, only that the list is non-empty (see <c>ParseRoot</c>'s <c>deployments.Count == 0</c>
    /// check) and that every individual entry is internally well-formed.
    /// </para>
    /// <para>
    /// This test re-kinds the shipped definition's <c>docker-thijsvanloef</c> profile to <c>kind: process</c>
    /// (adding the <c>executable</c> block a process-kind profile requires) rather than deleting it outright
    /// — deleting it removes that profile's <c>config.surfaces</c> entries too (<c>env</c>, <c>compose</c>,
    /// <c>live</c>), and <c>settings.bindings</c> references several of those by id, so a bare deletion fails
    /// for an entangled reason (unresolved surface references) rather than isolating the "no docker kind"
    /// case this test exists to pin. With re-kinding, no deployment has <c>kind: docker</c> yet every surface
    /// reference still resolves, so the result below reflects the deployment-kind rule alone.
    /// </para>
    /// <para>
    /// The pinned result: the document still parses cleanly, with zero <see cref="ValidationSeverity.Error"/>
    /// issues. Consumers that need a docker deployment (e.g. container adoption) are expected to degrade
    /// gracefully by design when none is declared, rather than the parser refusing the document outright.
    /// This test pins that actual, current behavior; it does not assert the behavior is desirable.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoDeploymentHasDockerKind_StillParsesCleanly_ConsumersDegradeGracefullyInstead()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "    kind: docker\n",
            "    kind: process\n    executable: { linux: \"./PalServer.sh\", windows: \"PalServer.exe\" }\n");

        var result = ParseYaml(yaml);

        result.Report.Issues.Should().NotContain(i => i.Severity == ValidationSeverity.Error);
        result.Definition.Should().NotBeNull();
        result.Definition!.Deployments.Should().HaveCount(2);
        result.Definition.Deployments.Should().NotContain(d => d.Kind == DeploymentKind.Docker);
        result.Definition.Deployments.Should().OnlyContain(d => d.Kind == DeploymentKind.Process);
    }

    [Fact]
    public void UnknownFieldWithinKnownBlock_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate("  id: palworld\n", "  id: palworld\n  bogusField: 1\n");

        AssertSingleError(ParseYaml(yaml), "unrecognized field 'bogusField'");
    }

    [Fact]
    public void UnknownTopLevelSection_IsError()
    {
        var yaml = DefinitionYamlFixture.Mutate("kind: GameDefinition\n", "kind: GameDefinition\nbogusSection: 1\n");

        AssertSingleError(ParseYaml(yaml), "unrecognized field 'bogusSection'");
    }

    [Fact]
    public void BackupAdoptAdapter_AlwaysProducesAWarning_NeverAnError()
    {
        var result = ParseYaml(DefinitionYamlFixture.RealYaml);

        result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Warning && i.Message.Contains("palworld-docker-cron", StringComparison.Ordinal));
        result.Report.Issues.Should().NotContain(i => i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void SignatureBlock_IsRecognizedButNotVerified_AndProducesOnlyAWarning()
    {
        var yaml = DefinitionYamlFixture.Mutate("kind: GameDefinition\n", "kind: GameDefinition\nsignature:\n  algorithm: ed25519\n  value: deadbeef\n");

        var result = ParseYaml(yaml);

        result.Definition.Should().NotBeNull();
        result.Report.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Warning && i.Message.Contains("not parsed or verified", StringComparison.Ordinal));
        result.Report.Issues.Should().NotContain(i => i.Severity == ValidationSeverity.Error);
    }
}
