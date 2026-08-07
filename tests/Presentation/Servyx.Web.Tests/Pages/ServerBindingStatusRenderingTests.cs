using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Lifecycle;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;
using ServerBindingStatus = Servyx.Application.Servers.ServerBindingStatus;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for how <c>ServersList</c> (compact) and <c>ServerDetailPage</c> (fuller explanation)
/// render a server whose <see cref="ServerSummary.BindingStatus"/> is not <see cref="ServerBindingStatus.Bound"/>
/// — the presentation-layer half of per-server definition binding, which
/// <c>Servyx.Application.Servers.ServerQueryService</c>/<see cref="LiveDashboardDataService"/> already
/// computed but nothing rendered before this. <see cref="ServerBindingStatus.Bound"/> itself is asserted to
/// render byte-for-byte as it always has — no badge, no layout change — so this suite also guards against a
/// regression in the overwhelmingly common case.
/// </summary>
public class ServerBindingStatusRenderingTests : BunitContext
{
    private const string BoundId = "bound-server";
    private const string AmbiguousId = "ambiguous-server";
    private const string NeedsRebindId = "needs-rebind-server";

    private static ServerSummary Summary(
        string id,
        string name,
        string game,
        ServerBindingStatus status = ServerBindingStatus.Bound,
        IReadOnlyList<string>? candidates = null) => new(
        Id: id,
        Name: name,
        Game: game,
        State: ServerState.Running,
        Health: ContainerHealth.Healthy,
        HealthTooltip: "Healthy.",
        PlayersOnline: null,
        PlayersMax: null,
        Uptime: TimeSpan.FromHours(1),
        Host: "docker-desktop (npipe)",
        Ports: [],
        BindingStatus: status,
        AmbiguousCandidateGameIds: candidates);

    private static ServerDetail Detail(ServerSummary summary) => new(
        Summary: summary,
        Image: "thijsvanloef/palworld-server-docker:latest",
        MountHostPath: "/srv/data",
        MountContainerPath: "/palworld",
        Network: "bridge",
        IpAddress: "172.18.0.2",
        MemoryLimit: "8G",
        CpuLimit: "4");

    private static readonly ServerSummary BoundSummary = Summary(BoundId, "Bound Server", "Palworld Dedicated Server");

    private static readonly ServerSummary AmbiguousSummary = Summary(
        AmbiguousId, "Ambiguous Server", "Unknown (ambiguous binding)",
        ServerBindingStatus.Ambiguous, ["palworld", "palworld-modded"]);

    private static readonly ServerSummary NeedsRebindSummary = Summary(
        NeedsRebindId, "Needs Rebind Server", "Unknown (needs re-binding)",
        ServerBindingStatus.NeedsRebind, ["palworld"]);

    private IDashboardDataService BuildData() => new BindingStatusDashboardDataService(
        [BoundSummary, AmbiguousSummary, NeedsRebindSummary],
        new Dictionary<string, ServerDetail>
        {
            [BoundId] = Detail(BoundSummary),
            [AmbiguousId] = Detail(AmbiguousSummary),
            [NeedsRebindId] = Detail(NeedsRebindSummary),
        });

    // ── ServersList: compact ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void List_A_bound_server_shows_its_game_name_as_plain_text_with_no_badge()
    {
        Services.AddSingleton<IDashboardDataService>(BuildData());
        AddBunitPersistentComponentState();

        var cut = Render<ServersList>();
        cut.WaitForAssertion(() => cut.FindAll("a.svx-row-link").Should().HaveCount(3));

        var row = cut.Find($"a[href='servers/{BoundId}']");
        var gameCell = row.QuerySelector("[data-col-label='Game']")!;

        gameCell.TextContent.Trim().Should().Be("Palworld Dedicated Server");
        gameCell.QuerySelector(".binding-status-badge").Should().BeNull();
    }

    [Fact]
    public void List_An_ambiguous_server_shows_a_badge_instead_of_the_raw_unknown_string()
    {
        Services.AddSingleton<IDashboardDataService>(BuildData());
        AddBunitPersistentComponentState();

        var cut = Render<ServersList>();
        cut.WaitForAssertion(() => cut.FindAll("a.svx-row-link").Should().HaveCount(3));

        var row = cut.Find($"a[href='servers/{AmbiguousId}']");
        var gameCell = row.QuerySelector("[data-col-label='Game']")!;

        var badge = gameCell.QuerySelector("[data-testid='binding-status-badge']");
        badge.Should().NotBeNull();
        badge!.ClassList.Should().Contain("binding-status-ambiguous");
        badge.TextContent.Trim().Should().Be("Ambiguous binding");

        // The literal "Unknown (ambiguous binding)" string must never reach the DOM as plain game-name text.
        gameCell.TextContent.Should().NotContain("Unknown (ambiguous binding)");
    }

    [Fact]
    public void List_A_needs_rebind_server_shows_a_badge_instead_of_the_raw_unknown_string()
    {
        Services.AddSingleton<IDashboardDataService>(BuildData());
        AddBunitPersistentComponentState();

        var cut = Render<ServersList>();
        cut.WaitForAssertion(() => cut.FindAll("a.svx-row-link").Should().HaveCount(3));

        var row = cut.Find($"a[href='servers/{NeedsRebindId}']");
        var gameCell = row.QuerySelector("[data-col-label='Game']")!;

        var badge = gameCell.QuerySelector("[data-testid='binding-status-badge']");
        badge.Should().NotBeNull();
        badge!.ClassList.Should().Contain("binding-status-needsrebind");
        badge.TextContent.Trim().Should().Be("Needs rebind");

        gameCell.TextContent.Should().NotContain("Unknown (needs re-binding)");
    }

    // ── ServerDetailPage: fuller explanation ─────────────────────────────────────────────────────

    [Fact]
    public void Detail_A_bound_server_shows_no_binding_status_notice()
    {
        Services.AddSingleton<IDashboardDataService>(BuildData());
        AddBunitPersistentComponentState();

        var cut = Render<ServerDetailPage>(p => p.Add(x => x.Id, BoundId));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Bound Server"));

        cut.FindAll("[data-testid='binding-status-notice']").Should().BeEmpty();
        cut.FindAll(".binding-status-badge").Should().BeEmpty();
    }

    [Fact]
    public void Detail_An_ambiguous_server_names_every_tied_candidate_and_says_servyx_did_not_guess()
    {
        Services.AddSingleton<IDashboardDataService>(BuildData());
        AddBunitPersistentComponentState();

        var cut = Render<ServerDetailPage>(p => p.Add(x => x.Id, AmbiguousId));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='binding-status-notice']").Should().HaveCount(1));

        var notice = cut.Find("[data-testid='binding-status-notice']");
        notice.ClassList.Should().Contain("binding-status-notice-ambiguous");
        notice.TextContent.Should().Contain("did not guess");

        var candidates = cut.Find("[data-testid='binding-status-candidates']");
        candidates.TextContent.Should().Contain("palworld").And.Contain("palworld-modded");
    }

    [Fact]
    public void Detail_A_needs_rebind_server_names_the_previous_definition_and_admits_no_fix_is_available()
    {
        Services.AddSingleton<IDashboardDataService>(BuildData());
        AddBunitPersistentComponentState();

        var cut = Render<ServerDetailPage>(p => p.Add(x => x.Id, NeedsRebindId));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='binding-status-notice']").Should().HaveCount(1));

        var notice = cut.Find("[data-testid='binding-status-notice']");
        notice.ClassList.Should().Contain("binding-status-notice-needsrebind");

        var previous = cut.Find("[data-testid='binding-status-previous-id']");
        previous.TextContent.Trim().Should().Be("palworld");

        // The review's explicit requirement: this state must never imply a fix exists in the UI today.
        var body = cut.Find("[data-testid='binding-status-notice-body']");
        body.TextContent.Should().Contain("no action in Servyx's UI to resolve this");
    }
}
