using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Composition;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Entities;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// Pins that <c>ServerDetailPage</c> feeds <see cref="ServerSettingsTab"/> the resolved container identity —
/// not the raw <c>{Id}</c> route token — so the whole change-plan pipeline hanging off it agrees with the
/// write grant on which server it is talking about.
/// </summary>
/// <remarks>
/// <para>
/// Companion to <c>ServerDetailPageContainerIdentityTests</c> (commit <c>839a2756</c>), which pinned the
/// Overview tab and the write-mode card against the same class of bug: <c>/servers/{Id}</c>'s token is not an
/// identity, since <c>ServerQueryService.GetServerDetailAsync</c> matches it against a discovered container's
/// id <em>or</em> its name. That fix introduced a single resolved <c>_containerId</c> field but did not carry
/// it into <c>&lt;ServerSettingsTab ServerId="@@Id" .../&gt;</c>, which still read the raw route token.
/// </para>
/// <para>
/// <strong>Why this matters beyond the settings tab itself.</strong> <see cref="IServerSettingsService.LoadAsync"/>
/// matches strictly on <c>Server.ContainerId</c> (see <c>EfServerSettingsService.LoadAsync</c>) — exactly like
/// the write grant lookup <c>839a2756</c> fixed. <c>ChangePlanPanel</c> passes the very same <c>ServerId</c> it
/// was handed straight into <see cref="IPlanExecutor.PreviewAsync"/>, and <c>PlanExecutor.PreviewAsync</c>
/// itself resolves through that same <see cref="IServerSettingsService.LoadAsync"/> call — so a display-name
/// token reaching the settings tab does not just mislabel one card, it makes every recorded desired value
/// read as "untracked" and makes a preview throw <c>InvalidOperationException</c> for a server Servyx
/// genuinely tracks.
/// </para>
/// </remarks>
public class ServerSettingsTabContainerIdentityTests : BunitContext
{
    /// <summary>A real-shaped 64-hex container id — the identity <see cref="IServerSettingsService"/> and <see cref="IPlanExecutor"/> are keyed on.</summary>
    private const string ContainerId = "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b";

    /// <summary>The same container's display name, which is also a valid route token.</summary>
    private const string ContainerName = "palworld-server";

    /// <summary>A container id that discovery resolves but which Servyx has never adopted.</summary>
    private const string UnadoptedContainerId = "0000111122223333444455556666777788889999aaaabbbbccccddddeeeeffff";

    private const string UnadoptedContainerName = "unadopted-server";

    private static ServerSummary Summary(string id, string name) => new(
        Id: id,
        Name: name,
        Game: "Palworld Dedicated Server",
        State: ServerState.Running,
        Health: ContainerHealth.Healthy,
        HealthTooltip: "Healthy.",
        PlayersOnline: null,
        PlayersMax: null,
        Uptime: TimeSpan.FromHours(1),
        Host: "docker-desktop (npipe)",
        Ports: []);

    private static ServerDetail Detail(ServerSummary summary) => new(
        Summary: summary,
        Image: "thijsvanloef/palworld-server-docker:latest",
        MountHostPath: "/srv/data",
        MountContainerPath: "/palworld",
        Network: "bridge",
        IpAddress: "172.18.0.2",
        MemoryLimit: "8G",
        CpuLimit: "4");

    /// <summary>
    /// One settings row, so <c>ServerSettingsTab</c> renders past its "no settings catalogue" empty state —
    /// <c>ChangePlanPanel</c> and <c>ChangePlanHistoryPanel</c> are both skipped entirely when
    /// <c>Settings.Count == 0</c>.
    /// </summary>
    private static IReadOnlyList<SettingRow> OneSettingRow() =>
    [
        new SettingRow(
            Group: "General",
            Key: "PORT",
            Label: "Port",
            IsSecret: false,
            Desired: null,
            Authoritative: "8211",
            Rendered: "8211",
            Runtime: "8211",
            Drift: DriftKind.None,
            PendingRegeneration: false),
    ];

    /// <summary>
    /// Delegates everything to an inner <see cref="IDashboardDataService"/> except
    /// <see cref="GetServerSettingsAsync"/>, which always answers with <see cref="OneSettingRow"/> — see that
    /// method's remarks for why a non-empty settings list is required to reach the change-plan components at
    /// all.
    /// </summary>
    private sealed class DataWithOneSetting(IDashboardDataService inner) : IDashboardDataService
    {
        public Task<ConnectionStatus> GetDockerConnectionStatusAsync(CancellationToken ct = default) =>
            inner.GetDockerConnectionStatusAsync(ct);

        public Task<DockerConnectionInfo> GetDockerConnectionInfoAsync(CancellationToken ct = default) =>
            inner.GetDockerConnectionInfoAsync(ct);

        public Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default) =>
            inner.GetDashboardSummaryAsync(ct);

        public Task<IReadOnlyList<ServerSummary>> GetServersAsync(CancellationToken ct = default) =>
            inner.GetServersAsync(ct);

        public Task<ServerListResult> GetServersWithStatusAsync(CancellationToken ct = default) =>
            inner.GetServersWithStatusAsync(ct);

        public Task<ServerDetail?> GetServerDetailAsync(string serverId, CancellationToken ct = default) =>
            inner.GetServerDetailAsync(serverId, ct);

        public Task<IReadOnlyList<SettingRow>> GetServerSettingsAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult(OneSettingRow());

        public Task<IReadOnlyList<LogLine>> GetServerLogsAsync(string serverId, CancellationToken ct = default) =>
            inner.GetServerLogsAsync(serverId, ct);

        public Task<SaveInfo?> GetServerSavesAsync(string serverId, CancellationToken ct = default) =>
            inner.GetServerSavesAsync(serverId, ct);

        public Task<IReadOnlyList<BackupEntry>> GetServerBackupsAsync(string serverId, CancellationToken ct = default) =>
            inner.GetServerBackupsAsync(serverId, ct);

        public Task<IReadOnlyList<BackupEntry>> GetAllBackupsAsync(CancellationToken ct = default) =>
            inner.GetAllBackupsAsync(ct);

        public Task<BackupsListResult> GetAllBackupsWithStatusAsync(CancellationToken ct = default) =>
            inner.GetAllBackupsWithStatusAsync(ct);

        public Task<IReadOnlyList<GameCardSummary>> GetGamesAsync(CancellationToken ct = default) =>
            inner.GetGamesAsync(ct);
    }

    /// <summary>
    /// An <see cref="IDashboardDataService"/> that answers to a container's id <em>and</em> to its name,
    /// exactly as <c>ServerQueryService.GetServerDetailAsync</c>'s two-step match does — which is what makes
    /// a name-shaped route token reach a real server in the first place.
    /// </summary>
    private static IDashboardDataService DataResolvingIdAndName()
    {
        var tracked = Detail(Summary(ContainerId, ContainerName));
        var unadopted = Detail(Summary(UnadoptedContainerId, UnadoptedContainerName));

        var inner = new BindingStatusDashboardDataService(
            [tracked.Summary, unadopted.Summary],
            new Dictionary<string, ServerDetail>(StringComparer.OrdinalIgnoreCase)
            {
                [ContainerId] = tracked,
                [ContainerName] = tracked,
                [UnadoptedContainerId] = unadopted,
                [UnadoptedContainerName] = unadopted,
            });

        return new DataWithOneSetting(inner);
    }

    /// <summary>
    /// An <see cref="IServerSettingsService"/> that knows <see cref="ContainerId"/> and nothing else — the
    /// shape the real one has, since <see cref="IServerSettingsService.LoadAsync"/> matches strictly on
    /// <c>Server.ContainerId</c>. Any other argument (a display name, an unadopted container id) resolves to
    /// <see langword="null"/> — "Servyx tracks no server for this container id at all".
    /// </summary>
    private static IServerSettingsService SettingsServiceKnowingOnlyTheContainerId()
    {
        var service = Substitute.For<IServerSettingsService>();

        service.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ServerSettingsSnapshot?>(null));

        service.LoadAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ServerSettingsSnapshot?>(new ServerSettingsSnapshot(
                ServerId.New(), new Dictionary<string, DesiredSettingValue>(StringComparer.Ordinal))));

        return service;
    }

    private IRenderedComponent<ServerDetailPage> RenderPageAndSelectSettingsTab(
        string routeToken,
        IServerSettingsService settingsService,
        IPlanExecutor? planExecutor = null,
        WritableServers? writable = null)
    {
        Services.AddSingleton(DataResolvingIdAndName());
        Services.AddSingleton(settingsService);
        if (planExecutor is not null)
        {
            Services.AddSingleton(planExecutor);
        }

        Services.AddSingleton(writable ?? WritableServers.None);
        Services.AddSingleton(new ProvisioningGate(enabled: true));
        AddBunitPersistentComponentState();

        var cut = Render<ServerDetailPage>(p => p.Add(x => x.Id, routeToken));

        cut.WaitForAssertion(() => cut.Find("#tab-Settings"));
        cut.Find("#tab-Settings").Click();

        return cut;
    }

    // ── 1. Navigating by name ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The regression this suite exists for. Before the fix, <c>ServerDetailPage.razor</c> handed
    /// <c>ServerSettingsTab</c> the raw route token, so a name-navigated tracked server's settings tab asked
    /// <see cref="IServerSettingsService.LoadAsync"/> about the name and got back "untracked" for a server
    /// Servyx genuinely tracks.
    /// </summary>
    [Fact]
    public void Navigating_by_name_gives_the_settings_tab_the_resolved_container_id()
    {
        var settingsService = SettingsServiceKnowingOnlyTheContainerId();

        var cut = RenderPageAndSelectSettingsTab(ContainerName, settingsService);

        cut.WaitForAssertion(() =>
            settingsService.Received().LoadAsync(ContainerId, Arg.Any<CancellationToken>()));

        settingsService.DidNotReceive().LoadAsync(ContainerName, Arg.Any<CancellationToken>());

        cut.FindAll("[data-testid='settings-untracked']").Should().BeEmpty(
            because: "the container IS tracked — only the route token was its name rather than its id");
    }

    [Fact]
    public void Navigating_by_id_still_resolves_exactly_as_it_always_has()
    {
        var settingsService = SettingsServiceKnowingOnlyTheContainerId();

        var cut = RenderPageAndSelectSettingsTab(ContainerId, settingsService);

        cut.WaitForAssertion(() =>
            settingsService.Received().LoadAsync(ContainerId, Arg.Any<CancellationToken>()));

        cut.FindAll("[data-testid='settings-untracked']").Should().BeEmpty();
    }

    // ── 2. Unresolvable / unadopted tokens fail closed ───────────────────────────────────────────

    /// <summary>
    /// Discovery finds this container by name, so the page renders — but Servyx never adopted it, so
    /// <see cref="IServerSettingsService.LoadAsync"/> returns <see langword="null"/> and the settings tab must
    /// render its untracked/locked state. Critically, the lookup must still be asked about the resolved
    /// container id, never the name, even though both answer "untracked" here.
    /// </summary>
    [Fact]
    public void An_unadopted_container_navigated_by_name_renders_the_settings_tab_untracked_without_asking_for_the_name()
    {
        var settingsService = SettingsServiceKnowingOnlyTheContainerId();

        var cut = RenderPageAndSelectSettingsTab(UnadoptedContainerName, settingsService);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='settings-untracked']").Should().HaveCount(1));

        settingsService.Received().LoadAsync(UnadoptedContainerId, Arg.Any<CancellationToken>());
        settingsService.DidNotReceive().LoadAsync(UnadoptedContainerName, Arg.Any<CancellationToken>());
    }

    // ── 3. The change-plan preview itself must never see a display name ─────────────────────────

    /// <summary>
    /// <c>ChangePlanPanel</c> passes the exact <c>ServerId</c> it is handed straight into
    /// <see cref="IPlanExecutor.PreviewAsync"/>. Clicking "Review changes" on a name-navigated server must
    /// preview against the resolved container id, never the display name — otherwise
    /// <c>PlanExecutor.PreviewAsync</c> would throw <c>InvalidOperationException</c> ("Servyx tracks no
    /// server for container id '&lt;name&gt;'") for a server that is, in fact, tracked.
    /// </summary>
    [Fact]
    public void Navigating_by_name_never_previews_a_change_plan_against_a_display_name()
    {
        var settingsService = SettingsServiceKnowingOnlyTheContainerId();
        var executor = Substitute.For<IPlanExecutor>();
        executor.PreviewAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ConfigChangePlan("plan-1", [], [], new Dictionary<string, string>())));

        var writable = new WritableServers([new KeyValuePair<string, WriteMode>(ContainerId, WriteMode.PreviewOnly)]);

        var cut = RenderPageAndSelectSettingsTab(ContainerName, settingsService, executor, writable);

        cut.WaitForAssertion(() =>
        {
            var button = cut.Find("[data-testid='plan-preview-button']");
            button.HasAttribute("disabled").Should().BeFalse();
        });

        cut.Find("[data-testid='plan-preview-button']").Click();

        cut.WaitForAssertion(() => executor.Received().PreviewAsync(
            ContainerId, Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>()));

        executor.DidNotReceive().PreviewAsync(
            ContainerName, Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }
}
