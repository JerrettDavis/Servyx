using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Auditing;
using Servyx.Domain.Entities;
using Servyx.Web.Components.Pages.Audit;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for the Admin-only audit reader page: listing (newest first), filtering by actor and by
/// action prefix, pagination, and the empty state when nothing matches.
/// </summary>
/// <remarks>
/// Exercises <see cref="AuditPage"/> against a real <see cref="FakeAuditEntryRepository"/> rather than a
/// hand-rolled service double, mirroring <c>UsersPageTests</c>'s own choice — see its remarks.
///
/// bUnit cannot see whether <c>[Authorize(Policy = RoleAuthorization.Admin)]</c> actually gates this route in
/// a real browser — see <c>UsersPageTests</c>' and <c>InteractiveRenderModeTests</c>'s own remarks on why that
/// class of bug needs a real ASP.NET Core pipeline. That is verified separately, live, against the real app.
/// </remarks>
public class AuditPageTests : BunitContext
{
    [Fact]
    public void An_uncomposed_repository_is_reported_rather_than_the_page_vanishing()
    {
        var cut = Render<AuditPage>();

        cut.Find("[data-testid=audit-unavailable]").Should().NotBeNull();
        cut.FindAll("[data-testid=audit-filter-section]").Should().BeEmpty();
    }

    [Fact]
    public void With_no_entries_the_empty_state_is_shown()
    {
        Arrange();

        var cut = Render<AuditPage>();

        cut.Find("[data-testid=audit-empty-state]").Should().NotBeNull();
        cut.FindAll("[data-testid=audit-row]").Should().BeEmpty();
    }

    [Fact]
    public void Entries_render_newest_first_with_actor_action_target_and_details()
    {
        var repository = Arrange();
        Seed(repository, "alice", AuditActions.UserCreated, "user", "bob", "role Viewer",
            new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));
        Seed(repository, "operator", AuditActions.HostRegistered, "host", "prod-host", "ssh:steam@10.0.0.4",
            new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero));

        var cut = Render<AuditPage>();

        var rows = cut.FindAll("[data-testid=audit-row]");
        rows.Should().HaveCount(2);

        // Newest (host.registered) first.
        rows[0].GetAttribute("data-action").Should().Be(AuditActions.HostRegistered);
        rows[0].TextContent.Should().Contain("operator");
        rows[0].TextContent.Should().Contain("host: prod-host");
        rows[0].TextContent.Should().Contain("ssh:steam@10.0.0.4");

        rows[1].GetAttribute("data-action").Should().Be(AuditActions.UserCreated);
        rows[1].TextContent.Should().Contain("alice");
    }

    [Fact]
    public void Filtering_by_actor_narrows_the_list_to_an_exact_match()
    {
        var repository = Arrange();
        Seed(repository, "alice", AuditActions.UserCreated, "user", "bob");
        Seed(repository, "operator", AuditActions.HostRegistered, "host", "prod-host");

        var cut = Render<AuditPage>();
        cut.Find("[data-testid=filter-actor-input]").Change("alice");

        var rows = cut.FindAll("[data-testid=audit-row]");
        rows.Should().ContainSingle();
        rows[0].GetAttribute("data-actor").Should().Be("alice");
    }

    [Fact]
    public void Filtering_by_action_prefix_narrows_the_list_to_that_noun_group()
    {
        var repository = Arrange();
        Seed(repository, "alice", AuditActions.UserCreated, "user", "bob");
        Seed(repository, "alice", AuditActions.UserRoleChanged, "user", "bob");
        Seed(repository, "operator", AuditActions.HostRegistered, "host", "prod-host");

        var cut = Render<AuditPage>();
        cut.Find("[data-testid=filter-action-select]").Change("user.");

        var rows = cut.FindAll("[data-testid=audit-row]");
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.GetAttribute("data-action")!.StartsWith("user."));
    }

    [Fact]
    public void A_filter_matching_nothing_shows_the_empty_state_rather_than_the_unfiltered_list()
    {
        var repository = Arrange();
        Seed(repository, "alice", AuditActions.UserCreated, "user", "bob");

        var cut = Render<AuditPage>();
        cut.Find("[data-testid=filter-actor-input]").Change("nobody");

        cut.Find("[data-testid=audit-empty-state]").Should().NotBeNull();
        cut.FindAll("[data-testid=audit-row]").Should().BeEmpty();
    }

    [Fact]
    public void Clearing_filters_restores_the_full_list()
    {
        var repository = Arrange();
        Seed(repository, "alice", AuditActions.UserCreated, "user", "bob");
        Seed(repository, "operator", AuditActions.HostRegistered, "host", "prod-host");

        var cut = Render<AuditPage>();
        cut.Find("[data-testid=filter-actor-input]").Change("alice");
        cut.FindAll("[data-testid=audit-row]").Should().ContainSingle();

        cut.Find("[data-testid=clear-filters-button]").Click();

        cut.FindAll("[data-testid=audit-row]").Should().HaveCount(2);
    }

    [Fact]
    public void Pagination_advances_to_the_next_page_and_back()
    {
        var repository = Arrange();
        for (var i = 0; i < 60; i++)
        {
            Seed(repository, $"actor-{i:D2}", AuditActions.UserCreated, "user", $"target-{i}",
                timestamp: DateTimeOffset.UnixEpoch.AddMinutes(i));
        }

        var cut = Render<AuditPage>();

        // Page size is 50: the first page is full, "Previous" is disabled, and "Next" is enabled.
        cut.FindAll("[data-testid=audit-row]").Should().HaveCount(50);
        cut.Find("[data-testid=prev-page-button]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=next-page-button]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=page-indicator]").TextContent.Should().Contain("Page 1 of 2");

        // Newest entry (actor-59, minute 59) is on page 1.
        cut.FindAll("[data-testid=audit-row]").Should().Contain(r => r.GetAttribute("data-actor") == "actor-59");

        cut.Find("[data-testid=next-page-button]").Click();

        cut.FindAll("[data-testid=audit-row]").Should().HaveCount(10);
        cut.Find("[data-testid=page-indicator]").TextContent.Should().Contain("Page 2 of 2");
        cut.Find("[data-testid=next-page-button]").HasAttribute("disabled").Should().BeTrue();
        // Oldest entry (actor-00, minute 0) is on the last page.
        cut.FindAll("[data-testid=audit-row]").Should().Contain(r => r.GetAttribute("data-actor") == "actor-00");

        cut.Find("[data-testid=prev-page-button]").Click();

        cut.FindAll("[data-testid=audit-row]").Should().HaveCount(50);
        cut.Find("[data-testid=page-indicator]").TextContent.Should().Contain("Page 1 of 2");
    }

    [Fact]
    public void Changing_a_filter_resets_pagination_to_the_first_page()
    {
        var repository = Arrange();
        for (var i = 0; i < 60; i++)
        {
            Seed(repository, "alice", AuditActions.UserCreated, "user", $"target-{i}",
                timestamp: DateTimeOffset.UnixEpoch.AddMinutes(i));
        }
        Seed(repository, "bob", AuditActions.HostRegistered, "host", "prod-host");

        var cut = Render<AuditPage>();
        cut.Find("[data-testid=next-page-button]").Click();
        cut.Find("[data-testid=page-indicator]").TextContent.Should().Contain("Page 2 of 2");

        cut.Find("[data-testid=filter-actor-input]").Change("bob");

        cut.Find("[data-testid=page-indicator]").TextContent.Should().Contain("Page 1 of 1");
        cut.FindAll("[data-testid=audit-row]").Should().ContainSingle();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private FakeAuditEntryRepository Arrange()
    {
        var repository = new FakeAuditEntryRepository();
        Services.AddSingleton<IAuditEntryRepository>(repository);
        return repository;
    }

    private static void Seed(
        FakeAuditEntryRepository repository,
        string actor,
        string action,
        string? targetType,
        string? targetId,
        string? details = null,
        DateTimeOffset? timestamp = null)
    {
        repository.Rows.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            TimestampUtc = timestamp ?? DateTimeOffset.UtcNow,
            Actor = actor,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Details = details,
        });
    }
}
