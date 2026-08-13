using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Application.Users;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Secrets;
using Servyx.Web.Components.Pages.Users;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for the Admin-only user-management page: list rendering, account creation (including the
/// duplicate-username failure the unique index also enforces), role changes, and deactivate/reactivate,
/// including the last-active-Admin lockout guard.
/// </summary>
/// <remarks>
/// This exercises a real <see cref="UserService"/> behind a <see cref="FakeUserRepository"/> rather than a
/// hand-rolled <c>IUserService</c> double — the same choice <c>FakeUserRepository</c>'s own doc comment
/// describes — so these tests also incidentally cover the service's outcome-to-UI wiring, not just the
/// component's own state machine.
///
/// bUnit cannot see whether <c>[Authorize(Policy = RoleAuthorization.Admin)]</c> actually gates this route in
/// a real browser — see <c>InteractiveRenderModeTests</c>'s own remarks on why that class of bug needs a real
/// ASP.NET Core pipeline. That is verified separately, live, against the real app.
/// </remarks>
public class UsersPageTests : BunitContext
{
    private const string Actor = "operator";
    private const string Password = "correct-horse-battery-staple";

    [Fact]
    public void An_uncomposed_user_service_is_reported_rather_than_the_page_vanishing()
    {
        var cut = Render<UsersPage>();

        cut.Find("[data-testid=users-unavailable]").Should().NotBeNull();
        cut.FindAll("[data-testid=create-user-section]").Should().BeEmpty();
    }

    [Fact]
    public void With_no_accounts_the_empty_state_is_shown()
    {
        Arrange();

        var cut = Render<UsersPage>();

        cut.Find("[data-testid=users-empty-state]").Should().NotBeNull();
        cut.FindAll("[data-testid=user-row]").Should().BeEmpty();
    }

    [Fact]
    public async Task Every_account_is_listed_with_its_role_status_and_created_date()
    {
        var (_, repository) = Arrange();
        await Seed(repository, "alice", UserRole.Admin, isActive: true);
        await Seed(repository, "bob", UserRole.Operator, isActive: false);

        var cut = Render<UsersPage>();

        var rows = cut.FindAll("[data-testid=user-row]");
        rows.Should().HaveCount(2);

        var aliceRow = cut.FindAll("[data-testid=user-row]").Single(r => r.GetAttribute("data-username") == "alice");
        aliceRow.QuerySelector("[data-testid=role-select]")!.GetAttribute("value").Should().Be("Admin");
        aliceRow.TextContent.Should().Contain("Active");

        var bobRow = cut.FindAll("[data-testid=user-row]").Single(r => r.GetAttribute("data-username") == "bob");
        bobRow.TextContent.Should().Contain("Inactive");
        bobRow.QuerySelector("[data-testid=reactivate-button]").Should().NotBeNull();
    }

    [Fact]
    public async Task Creating_a_user_succeeds_and_the_new_account_appears_in_the_list()
    {
        var (_, repository) = Arrange();

        var cut = Render<UsersPage>();
        FillCreateForm(cut, "charlie", Password, UserRole.Operator);
        cut.Find("[data-testid=create-user-button]").Click();

        repository.Rows.Should().ContainSingle(u => u.Username == "charlie" && u.Role == UserRole.Operator);
        cut.Find("[data-testid=create-user-applied]").TextContent.Should().Contain("charlie");
        cut.FindAll("[data-testid=user-row]").Should().ContainSingle(r => r.GetAttribute("data-username") == "charlie");
    }

    [Fact]
    public async Task Creating_a_user_under_a_taken_username_fails_cleanly_rather_than_throwing()
    {
        var (_, repository) = Arrange();
        await Seed(repository, "dave", UserRole.Viewer, isActive: true);

        var cut = Render<UsersPage>();
        FillCreateForm(cut, "dave", Password, UserRole.Viewer);
        cut.Find("[data-testid=create-user-button]").Click();

        repository.Rows.Should().ContainSingle("no second row may be created under a taken username");
        cut.Find("[data-testid=create-user-error]").TextContent.Should().Contain("already exists");
        cut.FindAll("[data-testid=create-user-applied]").Should().BeEmpty();
    }

    [Fact]
    public async Task Creating_a_user_with_a_weak_password_fails_cleanly()
    {
        Arrange();

        var cut = Render<UsersPage>();
        FillCreateForm(cut, "erin", "short", UserRole.Viewer);
        cut.Find("[data-testid=create-user-button]").Click();

        cut.Find("[data-testid=create-user-error]").TextContent.Should().Contain("at least");
        cut.FindAll("[data-testid=user-row]").Should().BeEmpty();
    }

    [Fact]
    public async Task Changing_an_operators_role_to_admin_is_applied_immediately()
    {
        var (_, repository) = Arrange();
        await Seed(repository, "admin", UserRole.Admin, isActive: true);
        var frank = await Seed(repository, "frank", UserRole.Operator, isActive: true);

        var cut = Render<UsersPage>();
        var select = cut.Find("[data-testid=role-select][data-username=frank]");
        select.Change(nameof(UserRole.Admin));

        repository.Rows.Single(u => u.Id == frank.Id).Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task Deactivating_a_user_takes_two_deliberate_steps()
    {
        var (_, repository) = Arrange();
        await Seed(repository, "admin", UserRole.Admin, isActive: true);
        var gina = await Seed(repository, "gina", UserRole.Operator, isActive: true);

        var cut = Render<UsersPage>();
        cut.Find("[data-testid=deactivate-review][data-username=gina]").Click();

        repository.Rows.Single(u => u.Id == gina.Id).IsActive.Should().BeTrue("reviewing must never be the act itself");
        cut.Find("[data-testid=deactivate-confirm-body]").TextContent.Should().Contain("Nothing has changed yet");

        cut.Find("[data-testid=deactivate-confirm][data-username=gina]").Click();

        repository.Rows.Single(u => u.Id == gina.Id).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_the_deactivate_confirmation_changes_nothing()
    {
        var (_, repository) = Arrange();
        await Seed(repository, "admin", UserRole.Admin, isActive: true);
        var henry = await Seed(repository, "henry", UserRole.Operator, isActive: true);

        var cut = Render<UsersPage>();
        cut.Find("[data-testid=deactivate-review][data-username=henry]").Click();
        cut.Find("[data-testid=deactivate-cancel]").Click();

        repository.Rows.Single(u => u.Id == henry.Id).IsActive.Should().BeTrue();
        cut.FindAll("[data-testid=deactivate-confirm-step]").Should().BeEmpty();
    }

    [Fact]
    public async Task Reactivating_a_deactivated_user_needs_no_confirmation()
    {
        var (_, repository) = Arrange();
        var ivy = await Seed(repository, "ivy", UserRole.Viewer, isActive: false);

        var cut = Render<UsersPage>();
        cut.Find("[data-testid=reactivate-button][data-username=ivy]").Click();

        repository.Rows.Single(u => u.Id == ivy.Id).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task The_sole_active_admin_cannot_deactivate_themselves()
    {
        var (_, repository) = Arrange();
        var solo = await Seed(repository, "solo", UserRole.Admin, isActive: true);

        var cut = Render<UsersPage>();

        var button = cut.Find("[data-testid=deactivate-review][data-username=solo]");
        button.HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=last-admin-note][data-username=solo]").Should().NotBeNull();

        repository.Rows.Single(u => u.Id == solo.Id).IsActive.Should().BeTrue("the guard must not have been bypassed");
    }

    [Fact]
    public async Task The_sole_active_admin_cannot_be_demoted()
    {
        var (_, repository) = Arrange();
        var solo = await Seed(repository, "solo", UserRole.Admin, isActive: true);

        var cut = Render<UsersPage>();

        var select = cut.Find("[data-testid=role-select][data-username=solo]");
        select.HasAttribute("disabled").Should().BeTrue();

        repository.Rows.Single(u => u.Id == solo.Id).Role.Should().Be(UserRole.Admin, "the guard must not have been bypassed");
    }

    [Fact]
    public async Task A_second_active_admin_frees_the_first_to_be_demoted_or_deactivated()
    {
        var (_, repository) = Arrange();
        var first = await Seed(repository, "first", UserRole.Admin, isActive: true);
        await Seed(repository, "second", UserRole.Admin, isActive: true);

        var cut = Render<UsersPage>();

        cut.Find("[data-testid=role-select][data-username=first]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=deactivate-review][data-username=first]").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll("[data-testid=last-admin-note][data-username=first]").Should().BeEmpty();

        cut.Find("[data-testid=deactivate-review][data-username=first]").Click();
        cut.Find("[data-testid=deactivate-confirm][data-username=first]").Click();

        repository.Rows.Single(u => u.Id == first.Id).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task An_inactive_admin_does_not_block_deactivating_the_sole_active_one()
    {
        // An Admin who is already deactivated cannot vouch for the account being able to manage users, so
        // they must not count toward "another active Admin exists".
        var (_, repository) = Arrange();
        var active = await Seed(repository, "active-admin", UserRole.Admin, isActive: true);
        await Seed(repository, "retired-admin", UserRole.Admin, isActive: false);

        var cut = Render<UsersPage>();

        cut.Find("[data-testid=deactivate-review][data-username=active-admin]").HasAttribute("disabled").Should().BeTrue();
        repository.Rows.Single(u => u.Id == active.Id).IsActive.Should().BeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private (UserService Service, FakeUserRepository Repository) Arrange()
    {
        var repository = new FakeUserRepository();
        var service = new UserService(repository, new FakeAuditLogger(), NullLogger<UserService>.Instance);
        Services.AddSingleton<IUserService>(service);
        return (service, repository);
    }

    private static async Task<User> Seed(FakeUserRepository repository, string username, UserRole role, bool isActive)
    {
        var user = new User
        {
            Id = UserId.New(),
            Username = username,
            PasswordHash = PasswordHash.Create(Password),
            Role = role,
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await repository.AddAsync(user);
        return user;
    }

    private static void FillCreateForm(IRenderedComponent<UsersPage> cut, string username, string password, UserRole role)
    {
        cut.Find("[data-testid=new-username-input]").Change(username);
        cut.Find("[data-testid=new-password-input]").Change(password);
        cut.Find("[data-testid=new-role-select]").Change(role.ToString());
    }
}
