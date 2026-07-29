using Servyx.Web.Definitions;

namespace Servyx.Web.Tests.Definitions;

/// <summary>
/// <see cref="PalworldDefinitionLoader.Parse"/> must select the <c>docker</c>-kind entry of
/// <c>deployments</c> by its <c>kind</c> field, not by list position — <c>definitions/palworld-docker.yaml</c>
/// happens to declare the docker profile first today, but that ordering is not a contract.
/// </summary>
public class PalworldDefinitionLoaderTests
{
    private const string MetadataBlock = """
        metadata:
          id: palworld
          name: Palworld Dedicated Server
          version: 1.0.0
          tags: [survival, steam, unreal]
        """;

    private const string DockerDeployment = """
          - id: docker-thijsvanloef
            kind: docker
            detect:
              imageRepo: "thijsvanloef/palworld-server-docker"
              requiredMounts: [{ containerPath: /palworld }]
            image:
              default: "thijsvanloef/palworld-server-docker:latest"
        """;

    private const string ProcessDeployment = """
          - id: native-steamcmd
            kind: process
            executable: { linux: "./PalServer.sh", windows: "PalServer.exe" }
        """;

    [Fact]
    public void Parse_ResolvesTheDockerProfile_WhenItIsFirstInDeployments()
    {
        var yaml = $"""
            {MetadataBlock}
            deployments:
            {DockerDeployment}
            {ProcessDeployment}
            """;

        var info = PalworldDefinitionLoader.Parse(yaml);

        info.ImageRepository.Should().Be("thijsvanloef/palworld-server-docker");
        info.RequiredMountContainerPath.Should().Be("/palworld");
        info.DefaultImage.Should().Be("thijsvanloef/palworld-server-docker:latest");
    }

    /// <summary>
    /// Regression guard for the position-based-selection bug: reorder the YAML so the non-docker
    /// (<c>process</c>-kind) profile comes first — index 0 — and prove the docker profile is still
    /// resolved correctly rather than throwing or silently returning the wrong profile's data.
    /// </summary>
    [Fact]
    public void Parse_ResolvesTheDockerProfile_WhenItIsReorderedToNotBeFirst()
    {
        var yaml = $"""
            {MetadataBlock}
            deployments:
            {ProcessDeployment}
            {DockerDeployment}
            """;

        var info = PalworldDefinitionLoader.Parse(yaml);

        info.ImageRepository.Should().Be("thijsvanloef/palworld-server-docker");
        info.RequiredMountContainerPath.Should().Be("/palworld");
        info.DefaultImage.Should().Be("thijsvanloef/palworld-server-docker:latest");
    }

    [Fact]
    public void Parse_Throws_WhenNoDeploymentHasDockerKind()
    {
        var yaml = $"""
            {MetadataBlock}
            deployments:
            {ProcessDeployment}
            """;

        var act = () => PalworldDefinitionLoader.Parse(yaml);

        act.Should().Throw<InvalidOperationException>().WithMessage("*kind: docker*");
    }

    [Fact]
    public void TryLoad_ReturnsNull_WhenTheFileDoesNotExist()
    {
        var info = PalworldDefinitionLoader.TryLoad(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        info.Should().BeNull();
    }

    private const string ControlBlock = """
        control:
          channels:
            - id: rest
              protocol: palworld-rest
              endpoints:
                players:  { method: GET, path: "/v1/api/players",  readOnly: true }
            - id: rcon
              protocol: source-rcon
              commands:
                info:      { template: "Info",                       readOnly: true }
                players:   { template: "ShowPlayers",                readOnly: true }
                save:      { template: "Save",                       readOnly: false }
                shutdown:  { template: "Shutdown {seconds} \"{message}\"", readOnly: false }
        """;

    /// <summary>
    /// The catalogue must be selected by channel <c>id</c>, not by list position. The REST channel is
    /// declared first here on purpose: picking index 0 would bind that channel's <c>endpoints</c> block —
    /// or nothing — and the write guard would then be gating a vocabulary the definition never declared.
    /// </summary>
    [Fact]
    public void ParseRconCommands_SelectsTheRconChannelByIdNotByPosition()
    {
        var commands = PalworldDefinitionLoader.ParseRconCommands(ControlBlock);

        commands.Should().HaveCount(4);
        commands.Should().ContainSingle(c => c.Id == "save").Which.Template.Should().Be("Save");
    }

    [Fact]
    public void ParseRconCommands_CarriesTheReadOnlyClassificationVerbatim()
    {
        var commands = PalworldDefinitionLoader.ParseRconCommands(ControlBlock)
            .ToDictionary(c => c.Id, c => c.ReadOnly, StringComparer.Ordinal);

        commands["info"].Should().BeTrue();
        commands["players"].Should().BeTrue();
        commands["save"].Should().BeFalse();
        commands["shutdown"].Should().BeFalse();
    }

    [Fact]
    public void ParseRconCommands_ReadsAMissingReadOnlyFlagAsMutating()
    {
        // The safe reading of an absent classification is the one that makes the write guard refuse.
        var yaml = """
            control:
              channels:
                - id: rcon
                  commands:
                    mystery: { template: "Mystery" }
            """;

        PalworldDefinitionLoader.ParseRconCommands(yaml).Should().ContainSingle().Which.ReadOnly.Should().BeFalse();
    }

    [Fact]
    public void ParseRconCommands_Throws_WhenNoChannelHasRconId()
    {
        var yaml = """
            control:
              channels:
                - id: rest
                  endpoints:
                    players: { method: GET, path: "/v1/api/players" }
            """;

        var act = () => PalworldDefinitionLoader.ParseRconCommands(yaml);

        act.Should().Throw<InvalidOperationException>().WithMessage("*id: rcon*");
    }

    [Fact]
    public void ParseRconCommands_Throws_WhenTheChannelDeclaresNoCommands()
    {
        var yaml = """
            control:
              channels:
                - id: rcon
                  protocol: source-rcon
            """;

        var act = () => PalworldDefinitionLoader.ParseRconCommands(yaml);

        act.Should().Throw<InvalidOperationException>().WithMessage("*no 'commands'*");
    }

    [Fact]
    public void TryLoadRconCommands_ReturnsNull_WhenTheFileDoesNotExist()
    {
        // No hardcoded fallback catalogue: a definition that cannot be read yields no RCON vocabulary at
        // all, which is a visible absence rather than a silent substitution.
        PalworldDefinitionLoader
            .TryLoadRconCommands(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))
            .Should().BeNull();
    }
}
