using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Application.Provisioning;
using Servyx.Definitions;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Docker.Provisioning;
using Servyx.Web.Components.Pages.Deploy;
using Servyx.Web.Services;
using Servyx.Web.Tests.Definitions.Support;
using Servyx.Web.Tests.Documentation;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// Drives <c>/deploy</c>'s game selection over a real <see cref="GameDefinitionCatalog"/> — never a
/// hardcoded default — proving milestone's "replace the hardcoded 'palworld' default with real selection
/// over all definitions in the catalog" requirement as a rendered, request-shaping fact rather than a claim
/// about <c>DeployPage</c>'s source.
/// </summary>
/// <remarks>
/// Zero, one, and three loaded definitions are each their own honest state (see
/// <c>DeployPage</c>'s "Game" card): no game is ever silently substituted, one definition is preselected
/// automatically, and two or more render a real <c>&lt;select&gt;</c> whose options are never hardcoded to
/// any specific game. The three-definition tests use the two real, shipped definitions
/// (<c>definitions/palworld-docker.yaml</c>, <c>definitions/minecraft-itzg.yaml</c>) plus one synthetic,
/// schema-valid third definition — this repository ships only those two real definitions as of this task, so
/// a genuine third shipped file was not available; the synthetic one exercises exactly the same catalog and
/// parser path a third shipped file would.
/// </remarks>
public class DeployPageGameSelectionTests : BunitContext
{
    private const string DockerId = DockerContainerProvisioner.Id;

    private static ProvisioningPlan PlanFor(string provisionerId) => new(
        PlanId: $"{provisionerId}:preview",
        PlanHash: "abc123def456abc123def456abc123def456abc123def456abc123def456abcd",
        Stages: [new("create", provisionerId, "Create the resource.")],
        EstimatedCost: CostEstimate.Unknown("not billed here"),
        ExpiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Enables provisioning with a single fake Docker provisioner, recording every request it is asked to plan.</summary>
    private FakeProvisioner EnableDocker()
    {
        var ledger = new RecordingProvisioningLedger();
        var docker = new FakeProvisioner(DockerId, ProvisioningCapabilities.Create, PlanFor(DockerId));

        Services.AddSingleton(new ProvisioningGate(enabled: true));
        Services.AddSingleton<IProvisioningDashboard>(
            new ProvisioningDashboardService([docker], ledger, new ProvisioningExecutor(ledger)));

        return docker;
    }

    private static string RealDefinitionText(string fileName)
    {
        var repoRoot = RepoRootLocator.Find();
        return File.ReadAllText(Path.Combine(repoRoot.FullName, "definitions", fileName));
    }

    /// <summary>
    /// A third, schema-valid definition distinct from either real shipped file. Structured the same way
    /// <c>definitions/minecraft-itzg.yaml</c> is (one docker deployment, one dotenv surface, no control
    /// channels) so it exercises the identical parser and catalog path a genuine third shipped definition
    /// would, under a name that collides with neither real shipped game and is not itself one of the
    /// literals <c>GameNameLiteralSourceScanTests</c> forbids under <c>src/</c>.
    /// </summary>
    private const string ThirdDefinitionYaml = """
        apiVersion: servyx.dev/v1
        kind: GameDefinition
        metadata:
          id: gamma-test-server
          name: Gamma Test Server
          version: 1.0.0
          license: MIT
          tags: [test]

        capabilities:
          network:
            - { port: 9999, protocol: tcp, purpose: game, var: SERVER_PORT, published: true }
          filesystem:
            - { path: "${DATA_DIR}", access: rw, purpose: "world data" }
          egress: []
          shell: false
          privileged: false
          hostNetwork: false

        deployments:
          - id: docker-gamma
            kind: docker
            detect:
              imageRepo: "example.invalid/gamma-server"
              requiredMounts: [{ containerPath: /data }]
            image:
              default: "example.invalid/gamma-server:latest"
            dataDir: /data
            stopTimeout: 60s
            config:
              surfaces:
                - id: env
                  role: authoritative
                  format: dotenv
                  locator: { kind: host-file, path: "${COMPOSE_DIR}/.env" }
                  mergePolicy: preserve-unknown

        lifecycle:
          ready:
            - kind: log-regex
              pattern: 'Server started'
              timeout: 10m
          stop:
            - { kind: signal, signal: SIGTERM, timeout: 30s }
            - { kind: kill }
          crashDetection: []

        control:
          channels: []

        settings:
          - group: Identity
            items:
              - key: SERVER_PORT
                label: Game port
                type: port
                default: 9999
                bindings:
                  - { surface: env, direction: write, key: SERVER_PORT }

        backup:
          include:
            - "${DATA_DIR}/**"
          exclude: []
          quiesce: []
          defaultRetention: { keepHourly: 6, keepDaily: 7, keepWeekly: 4 }

        mods:
          supported: false
        """;

    private static async Task<GameDefinitionCatalog> BuildCatalogAsync(
        TempDefinitionsDirectory dir, params (string FileName, string Yaml)[] files)
    {
        foreach (var (fileName, yaml) in files)
        {
            dir.WriteFlat(fileName, yaml);
        }

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);
        await catalog.RefreshAsync();
        return catalog;
    }

    // ── Zero definitions ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ZeroDefinitions_NoGameIsSilentlySelected_EmptyStateIsShown()
    {
        using var dir = new TempDefinitionsDirectory();
        var catalog = await BuildCatalogAsync(dir);
        catalog.DefinitionsById.Should().BeEmpty();

        var docker = EnableDocker();
        Services.AddSingleton(catalog);

        var cut = Render<DeployPage>();

        cut.FindAll("[data-testid='no-games']").Should().ContainSingle();
        cut.FindAll("[data-testid='single-game']").Should().BeEmpty();
        cut.FindAll("[data-testid='game-select']").Should().BeEmpty();

        cut.Find("[data-testid='preview-plan']").Click();
        cut.WaitForAssertion(() => docker.LastRequest.Should().NotBeNull());

        // No game was silently substituted for the honest "none loaded" state — never Palworld, never any
        // other specific game.
        docker.LastRequest!.GameDefinitionId.Should().BeEmpty();
    }

    // ── One definition ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OneDefinition_IsPreselectedAutomatically()
    {
        using var dir = new TempDefinitionsDirectory();
        var catalog = await BuildCatalogAsync(dir, ("minecraft.yaml", RealDefinitionText("minecraft-itzg.yaml")));
        catalog.DefinitionsById.Should().HaveCount(1);

        var docker = EnableDocker();
        Services.AddSingleton(catalog);

        var cut = Render<DeployPage>();

        cut.FindAll("[data-testid='no-games']").Should().BeEmpty();
        cut.FindAll("[data-testid='game-select']").Should().BeEmpty();

        var singleGame = cut.Find("[data-testid='single-game']");
        singleGame.GetAttribute("data-game-id").Should().Be("minecraft-itzg");
        singleGame.TextContent.Should().Contain("Minecraft Server (itzg)");

        cut.Find("[data-testid='preview-plan']").Click();
        cut.WaitForAssertion(() => docker.LastRequest.Should().NotBeNull());

        docker.LastRequest!.GameDefinitionId.Should().Be("minecraft-itzg");

        // The single loaded definition's own docker image and declared game port reached the form's
        // defaults, not the page's last-resort literal.
        docker.LastRequest.Parameters["image"].Should().Be("itzg/minecraft-server:latest");
        docker.LastRequest.Parameters.Should().ContainKey("port:25565/tcp");
    }

    /// <summary>
    /// <strong>The regression this feature never had.</strong> <c>definitions/palworld-docker.yaml</c>'s
    /// Docker profile declares <c>stopGracePeriodSeconds: 100</c> — parsed and validated by
    /// <c>GameDefinitionYamlParser</c>, but until now never actually reached a provisioned container: nothing
    /// copied <c>DeploymentProfile.StopGracePeriod</c> into the <c>stopGracePeriodSeconds</c> provisioning
    /// parameter <c>DockerContainerProvisioner.BuildSpec</c> reads. This test renders the real definition,
    /// previews through the real form, and asserts the value is present in the request handed to the
    /// provisioner — the same request <c>DockerContainerProvisionerTests</c> already proves
    /// <c>BuildSpec</c> turns into <c>CreateContainerParameters.StopTimeout</c>, so together the two tests
    /// cover the whole definition-to-Docker path.
    /// </summary>
    [Fact]
    public async Task OneDefinition_DeclaredStopGracePeriodReachesTheProvisioningRequest()
    {
        using var dir = new TempDefinitionsDirectory();
        var catalog = await BuildCatalogAsync(dir, ("palworld.yaml", RealDefinitionText("palworld-docker.yaml")));
        catalog.DefinitionsById.Should().HaveCount(1);

        var docker = EnableDocker();
        Services.AddSingleton(catalog);

        var cut = Render<DeployPage>();

        // Reached the rendered field before any click — proving the derivation, not just the eventual
        // request shape.
        cut.Find("[data-testid='stop-grace-period-seconds']").GetAttribute("value").Should().Be("100");

        cut.Find("[data-testid='preview-plan']").Click();
        cut.WaitForAssertion(() => docker.LastRequest.Should().NotBeNull());

        docker.LastRequest!.GameDefinitionId.Should().Be("palworld");
        docker.LastRequest.Parameters.Should().ContainKey("stopGracePeriodSeconds")
            .WhoseValue.Should().Be("100");
    }

    // ── Three definitions ─────────────────────────────────────────────────────────────────────────────

    private static async Task<GameDefinitionCatalog> BuildThreeDefinitionCatalogAsync(TempDefinitionsDirectory dir) =>
        await BuildCatalogAsync(
            dir,
            ("palworld.yaml", RealDefinitionText("palworld-docker.yaml")),
            ("minecraft.yaml", RealDefinitionText("minecraft-itzg.yaml")),
            ("gamma.yaml", ThirdDefinitionYaml));

    [Fact]
    public async Task ThreeDefinitions_AllThreeAreOffered_NoneHardcodedOrPreselectedAsPalworld()
    {
        using var dir = new TempDefinitionsDirectory();
        var catalog = await BuildThreeDefinitionCatalogAsync(dir);
        catalog.Faults.Should().BeEmpty("all three definitions must parse with zero validation Errors");
        catalog.DefinitionsById.Should().HaveCount(3);

        EnableDocker();
        Services.AddSingleton(catalog);

        var cut = Render<DeployPage>();

        cut.FindAll("[data-testid='no-games']").Should().BeEmpty();
        cut.FindAll("[data-testid='single-game']").Should().BeEmpty();

        var options = cut.Find("[data-testid='game-select']")
            .QuerySelectorAll("option")
            .Select(o => o.GetAttribute("value") ?? string.Empty)
            .ToList();

        options.Should().BeEquivalentTo(["palworld", "minecraft-itzg", "gamma-test-server"]);

        // Alphabetical by display name ("Gamma Test Server", "Minecraft Server (itzg)", "Palworld Dedicated
        // Server"), and the first of those — not Palworld — is what a fresh render selects. Read through the
        // Docker form's own "image" field rather than the <select>'s own value/attribute state, which a
        // native HTML <select> does not expose as a plain attribute: the selection that actually reached the
        // rest of the page is the fact that matters here.
        cut.Find("[data-testid='image']").GetAttribute("value")
            .Should().Be("example.invalid/gamma-server:latest", "Gamma sorts first and nothing hardcodes Palworld as the default");
    }

    [Fact]
    public async Task ThreeDefinitions_ChangingSelection_ChangesResolvedImagePortsAndDeploymentProfile()
    {
        using var dir = new TempDefinitionsDirectory();
        var catalog = await BuildThreeDefinitionCatalogAsync(dir);

        var docker = EnableDocker();
        Services.AddSingleton(catalog);

        var cut = Render<DeployPage>();

        // Starts on the alphabetically-first game (Gamma), whose own image/port are already in the form.
        cut.Find("[data-testid='image']").GetAttribute("value")
            .Should().Be("example.invalid/gamma-server:latest");
        cut.Find("[data-testid='host-port']").GetAttribute("value").Should().Be("9999");

        // Selecting Palworld re-derives the form to Palworld's own image and game port.
        cut.Find("[data-testid='game-select']").Change("palworld");
        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='image']").GetAttribute("value")
                .Should().Be("thijsvanloef/palworld-server-docker:latest"));
        cut.Find("[data-testid='host-port']").GetAttribute("value").Should().Be("8211");

        cut.Find("[data-testid='preview-plan']").Click();
        cut.WaitForAssertion(() => docker.LastRequest.Should().NotBeNull());
        docker.LastRequest!.GameDefinitionId.Should().Be("palworld");
        docker.LastRequest.DeploymentProfileId.Should().Be("docker");
        docker.LastRequest.Parameters["image"].Should().Be("thijsvanloef/palworld-server-docker:latest");
        docker.LastRequest.Parameters.Should().ContainKey("port:8211/tcp");

        // Selecting Minecraft re-derives again, to Minecraft's own image and game port — never a value
        // carried over from Palworld or from the initial Gamma selection.
        cut.Find("[data-testid='game-select']").Change("minecraft-itzg");
        cut.WaitForAssertion(() =>
            cut.Find("[data-testid='image']").GetAttribute("value")
                .Should().Be("itzg/minecraft-server:latest"));
        cut.Find("[data-testid='host-port']").GetAttribute("value").Should().Be("25565");

        cut.Find("[data-testid='preview-plan']").Click();
        cut.WaitForAssertion(() => docker.LastRequest!.GameDefinitionId.Should().Be("minecraft-itzg"));
        docker.LastRequest!.Parameters["image"].Should().Be("itzg/minecraft-server:latest");
        docker.LastRequest.Parameters.Should().ContainKey("port:25565/tcp");
    }

    [Fact]
    public async Task ThreeDefinitions_ChangingSelection_DiscardsAPendingPlanForTheOldGame()
    {
        using var dir = new TempDefinitionsDirectory();
        var catalog = await BuildThreeDefinitionCatalogAsync(dir);

        EnableDocker();
        Services.AddSingleton(catalog);

        var cut = Render<DeployPage>();

        cut.Find("[data-testid='preview-plan']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='plan-preview']").Should().NotBeEmpty());

        cut.Find("[data-testid='game-select']").Change("palworld");

        cut.FindAll("[data-testid='plan-preview']").Should().BeEmpty();
        cut.FindAll("[data-testid='confirm-step']").Should().BeEmpty();
    }
}
