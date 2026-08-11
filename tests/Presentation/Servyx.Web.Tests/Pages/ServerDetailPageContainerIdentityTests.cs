using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Composition;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// Pins that <c>ServerDetailPage</c> resolves its route token to exactly one container identity and feeds
/// every identity-consuming control on the page from it.
/// </summary>
/// <remarks>
/// <para>
/// <c>/servers/{Id}</c>'s token is not an identity. <c>ServerQueryService.GetServerDetailAsync</c> matches
/// it against a discovered container's id <em>or</em> its name, so <c>/servers/palworld-server</c> is a
/// perfectly ordinary URL whose token is a display name. The page previously resolved its own
/// <see cref="WriteMode"/> from the discovered summary while handing the <em>raw route token</em> down to
/// <see cref="WriteModeControl"/>: for a name-navigated page the Power card reported the grant correctly and
/// the write-access card, asked about a name no grant is keyed on, claimed Servyx did not track the
/// container at all.
/// </para>
/// <para>
/// The resolution is deliberately one-directional: route token → discovered container id. The grant lookup
/// itself is never widened to accept a display name — two containers can carry the same name, and honouring
/// a grant against one would hand a different workload the other's write access. These tests therefore
/// assert not only that the right identity is used, but that <see cref="IWriteGrantService.DescribeAsync"/>
/// is never called with anything else.
/// </para>
/// </remarks>
public class ServerDetailPageContainerIdentityTests : BunitContext
{
    /// <summary>A real-shaped 64-hex container id — the identity every per-server grant is keyed on.</summary>
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
    /// An <see cref="IDashboardDataService"/> that answers to a container's id <em>and</em> to its name,
    /// exactly as <c>ServerQueryService.GetServerDetailAsync</c>'s two-step match does — which is what makes
    /// a name-shaped route token reach a real server in the first place.
    /// </summary>
    private static IDashboardDataService DataResolvingIdAndName()
    {
        var tracked = Detail(Summary(ContainerId, ContainerName));
        var unadopted = Detail(Summary(UnadoptedContainerId, UnadoptedContainerName));

        return new BindingStatusDashboardDataService(
            [tracked.Summary, unadopted.Summary],
            new Dictionary<string, ServerDetail>(StringComparer.OrdinalIgnoreCase)
            {
                [ContainerId] = tracked,
                [ContainerName] = tracked,
                [UnadoptedContainerId] = unadopted,
                [UnadoptedContainerName] = unadopted,
            });
    }

    /// <summary>
    /// A grant service that knows <see cref="ContainerId"/> and nothing else — the shape the real one has,
    /// since a grant row is keyed on <c>Server.ContainerId</c>. Any other argument (a display name, a route
    /// token, a container Servyx never adopted) resolves to "untracked".
    /// </summary>
    private static IWriteGrantService GrantServiceKnowingOnlyTheContainerId(ServerWriteMode mode)
    {
        var service = Substitute.For<IWriteGrantService>();

        service.DescribeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<WriteGrantState?>(null));

        service.DescribeAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<WriteGrantState?>(new WriteGrantState(
                ServerId.New(), ContainerName, ContainerId, mode, ChangedBy: null, ChangedAt: null)));

        return service;
    }

    private IRenderedComponent<ServerDetailPage> RenderPage(
        string routeToken, IWriteGrantService grants, WritableServers writable)
    {
        Services.AddSingleton(DataResolvingIdAndName());
        Services.AddSingleton(grants);
        Services.AddSingleton(writable);
        Services.AddSingleton(new ProvisioningGate(enabled: true));
        AddBunitPersistentComponentState();

        return Render<ServerDetailPage>(p => p.Add(x => x.Id, routeToken));
    }

    /// <summary>
    /// A grant set keyed on the container id alone — the shape <c>WritableServers.Live</c> has in every real
    /// host, where the display name is never a key.
    /// </summary>
    private static WritableServers WritableByContainerId(WriteMode mode) =>
        new([new KeyValuePair<string, WriteMode>(ContainerId, mode)]);

    // ── 1. Navigating by name ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The regression this suite exists for. <see cref="WriteMode.PreviewOnly"/> is chosen because both
    /// halves of the page render an observable, distinguishable state for it with no
    /// <c>IServerLifecycle</c> composed: the Power card renders its stop-plan preview note, and the
    /// write-access card names the posture. Before the fix the first said "preview only" and the second said
    /// Servyx did not track the container — one page, two irreconcilable claims about the same server.
    /// </summary>
    [Fact]
    public void Navigating_by_name_resolves_the_container_id_so_the_write_card_agrees_with_the_power_card()
    {
        var grants = GrantServiceKnowingOnlyTheContainerId(ServerWriteMode.PreviewOnly);

        var cut = RenderPage(ContainerName, grants, WritableByContainerId(WriteMode.PreviewOnly));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='power-card']").Should().HaveCount(1));

        cut.Find("[data-testid='lifecycle-preview-note']").TextContent
            .Should().Contain("preview-only mode");

        cut.FindAll("[data-testid='write-mode-untracked']").Should().BeEmpty(
            because: "the container IS tracked — only the route token was its name rather than its id");

        cut.Find("[data-testid='write-mode-current']").TextContent.Should().Contain("Preview only");
    }

    [Fact]
    public void Navigating_by_name_never_asks_the_grant_lookup_about_the_name()
    {
        var grants = GrantServiceKnowingOnlyTheContainerId(ServerWriteMode.PreviewOnly);

        var cut = RenderPage(ContainerName, grants, WritableByContainerId(WriteMode.PreviewOnly));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='write-mode-card']").Should().HaveCount(1));

        grants.Received().DescribeAsync(ContainerId, Arg.Any<CancellationToken>());
        grants.DidNotReceive().DescribeAsync(
            Arg.Is<string>(id => !string.Equals(id, ContainerId, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    // ── 2. Navigating by id ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Navigating_by_id_still_resolves_exactly_as_it_always_has()
    {
        var grants = GrantServiceKnowingOnlyTheContainerId(ServerWriteMode.PreviewOnly);

        var cut = RenderPage(ContainerId, grants, WritableByContainerId(WriteMode.PreviewOnly));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='power-card']").Should().HaveCount(1));

        cut.Find("[data-testid='lifecycle-preview-note']").TextContent.Should().Contain("preview-only mode");
        cut.Find("[data-testid='write-mode-current']").TextContent.Should().Contain("Preview only");
        cut.FindAll("[data-testid='write-mode-untracked']").Should().BeEmpty();

        grants.Received().DescribeAsync(ContainerId, Arg.Any<CancellationToken>());
        grants.DidNotReceive().DescribeAsync(
            Arg.Is<string>(id => !string.Equals(id, ContainerId, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    // ── 3. Unresolvable / unadopted tokens fail closed ───────────────────────────────────────────

    /// <summary>
    /// Discovery finds this container, so the page renders — but Servyx never adopted it, so no
    /// <c>Server</c> row exists to hold a grant. Every control has to be locked and the write-access card
    /// has to say why, and the grant lookup still has to be asked about a container id rather than a name.
    /// </summary>
    [Fact]
    public void An_unadopted_container_renders_the_untracked_state_and_locks_every_control()
    {
        var grants = GrantServiceKnowingOnlyTheContainerId(ServerWriteMode.Enabled);

        var cut = RenderPage(UnadoptedContainerName, grants, WritableByContainerId(WriteMode.Enabled));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='write-mode-card']").Should().HaveCount(1));

        cut.Find("[data-testid='write-mode-untracked']").TextContent.Should().Contain("Adopt it");
        cut.FindAll("[data-testid='write-mode-review']").Should().BeEmpty();

        foreach (var button in cut.FindAll("[data-testid=gated-button]"))
        {
            button.HasAttribute("disabled").Should().BeTrue(
                because: "a container with no grant row resolves ReadOnly, which must never render a live control");
        }

        grants.Received().DescribeAsync(UnadoptedContainerId, Arg.Any<CancellationToken>());
        grants.DidNotReceive().DescribeAsync(UnadoptedContainerName, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Nothing discovery knows about matches the token at all: the page renders "not found" and never asks
    /// the grant lookup anything, rather than offering a control keyed on an unresolved string.
    /// </summary>
    [Fact]
    public void A_token_that_resolves_to_nothing_renders_not_found_and_never_reaches_the_grant_lookup()
    {
        var grants = GrantServiceKnowingOnlyTheContainerId(ServerWriteMode.Enabled);

        var cut = RenderPage("no-such-server", grants, WritableByContainerId(WriteMode.Enabled));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Server not found"));

        cut.FindAll("[data-testid='write-mode-review']").Should().BeEmpty();
        cut.FindAll("[data-testid=gated-button]").Should().BeEmpty();

        grants.DidNotReceive().DescribeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
