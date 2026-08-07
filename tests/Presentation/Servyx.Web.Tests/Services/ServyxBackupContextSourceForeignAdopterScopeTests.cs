using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;
using Servyx.Web.Services;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Regression coverage for a leak <c>definitions/minecraft-itzg.yaml</c> — the second real game definition
/// authored to prove M6's own acceptance criterion — surfaced in <see cref="ServyxBackupContextSource.GetAsync"/>:
/// <c>foreignAdapterId</c> fell back to the hardcoded <c>PalworldCronBackupAdopter.Id</c> whenever the
/// CURRENT definition's own <c>backup.adopt</c> list was empty, not only when NO definition had loaded at
/// all — contradicting <c>docs/schema.md</c>'s own stated contract for this block. A Minecraft server (whose
/// itzg image ships no cron-based backup rotation, so its definition correctly declares no <c>adopt</c>
/// entry) was therefore reported as having a foreign backup source attributed to Palworld's adapter. Fixed
/// so a loaded definition with no adopt entry reports an honestly empty <see cref="Servyx.Infrastructure.Docker.Backups.DockerBackupContext.Foreign"/>
/// list instead.
/// </summary>
public class ServyxBackupContextSourceForeignAdopterScopeTests
{
    private const string Container = "minecraft-server";

    private static GameDefinition RealMinecraftDefinition()
    {
        var repoRoot = RepoRootLocator.Find();
        var yaml = File.ReadAllText(Path.Combine(repoRoot.FullName, "definitions", "minecraft-itzg.yaml"));
        var result = new GameDefinitionYamlParser().Parse(yaml);
        return result.Definition ?? throw new InvalidOperationException(
            "definitions/minecraft-itzg.yaml failed to parse: " + string.Join("; ", result.Report.Issues.Select(i => i.Message)));
    }

    private static IServerQueryService Query(string image, string mountContainerPath)
    {
        var query = Substitute.For<IServerQueryService>();
        query.GetServerDetailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ServerDetail?>(new ServerDetail(
                new ServerSummary(Container, Container, "minecraft-itzg", ServerState.Running, ServerHealthStatus.Healthy, null, null, "localhost", []),
                image + ":latest",
                "/srv/minecraft",
                mountContainerPath,
                null, null, null, null, [])));

        return query;
    }

    private static ITransport FakeTransport()
    {
        var transport = Substitute.For<ITransport>();

        transport.Capabilities.Returns(
            TransportCapabilities.FileRead | TransportCapabilities.DirectoryList |
            TransportCapabilities.ContainerApi | TransportCapabilities.ContainerScopedFiles);
        transport.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Substitute.For<IExecutionTarget>()));

        return transport;
    }

    [Fact]
    public async Task LoadedDefinitionWithNoAdoptEntry_ReportsNoForeignSource_RatherThanPalworldsAdapter()
    {
        var definition = RealMinecraftDefinition();
        definition.Backup.Adopt.Should().BeEmpty("definitions/minecraft-itzg.yaml genuinely declares no backup.adopt entry");

        await using var source = new ServyxBackupContextSource(
            Query("itzg/minecraft-server", "/data"), FakeTransport(), new BackupWiringOptions(), rcon: null, definition);

        var context = await source.GetAsync(Container);

        context.Foreign.Should().BeEmpty(
            "a loaded definition that declares no adopt source must report an honestly empty Foreign list, "
                + "never a fabricated entry attributed to another game's adapter");
    }

    [Fact]
    public async Task NoDefinitionLoadedAtAll_StillUsesTheLegacyHardcodedFallback()
    {
        await using var source = new ServyxBackupContextSource(
            Query("itzg/minecraft-server", "/data"),
            FakeTransport(),
            new BackupWiringOptions(include: ["some/path/**"]),
            rcon: null,
            definition: null);

        var context = await source.GetAsync(Container);

        context.Foreign.Should().ContainSingle(
            "with no definition loaded at all, docs/schema.md's documented contract still applies: fall "
                + "back to the bundled PalworldCronBackupAdopter, unchanged from before this fix")
            .Which.AdapterId.Should().Be("palworld-docker-cron");
    }
}
