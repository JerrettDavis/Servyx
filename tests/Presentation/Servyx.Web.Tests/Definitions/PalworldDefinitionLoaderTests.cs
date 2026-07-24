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
}
