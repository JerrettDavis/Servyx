using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>
/// Signs the current scenario's browser context in as an Admin against the default demonstration host.
/// </summary>
/// <remarks>
/// Needed because <c>Servyx__Authentication__Enabled=false</c> (the demonstration host's documented default
/// — see <c>ServyxAppProcess.StartAsync</c>) only removes the app-wide <c>AuthorizationOptions.FallbackPolicy</c>.
/// It does not touch a page's own, explicitly-declared <c>[Authorize(Policy = ...)]</c> — see
/// <c>AuthenticationServiceCollectionExtensions</c>'s own remarks — and <c>UsersPage</c>/<c>AuditPage</c> both
/// carry <see cref="Servyx.Web.Authentication.RoleAuthorization.Admin"/> unconditionally, gate open or closed.
/// Reaching either page therefore always needs a real, signed-in Admin account, even on this otherwise
/// anonymous-reachable host.
/// <para>
/// The account itself lives in the ONE shared app process every default-host scenario runs against (see
/// <c>TestRunContext.Fixture</c>), so it must be created exactly once for the whole run and every later
/// scenario needing it must sign in with the same credentials instead — <see cref="_creatorClaimed"/> and
/// <see cref="AccountReady"/> coordinate that the same way <c>WriteEnabledAppFixture</c>'s own
/// double-checked-locking-over-a-semaphore does, tolerating Reqnroll's ability to run scenarios in parallel
/// (see <c>ScenarioHooks</c>'s own remarks on that).
/// </para>
/// <para>
/// Each scenario still gets its own, fresh <see cref="IBrowserContext"/> (see <c>ScenarioHooks</c>), so the
/// signed-in session cookie itself is never shared — only the underlying account is.
/// </para>
/// </remarks>
[Binding]
public sealed class AdminSessionSteps(IPage page)
{
    private const string AdminUsername = "e2e-admin";

    // 12 is CreateUserResult.MinimumPasswordLength; comfortably over it so a future minimum bump doesn't
    // silently break this fixture.
    private const string AdminPassword = "Correct-Horse-Battery-Staple-1";

    private static int _creatorClaimed;
    private static readonly TaskCompletionSource<bool> AccountReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Given(@"^I am signed in as an administrator$")]
    public async Task GivenIAmSignedInAsAnAdministratorAsync()
    {
        var iAmCreator = Interlocked.Exchange(ref _creatorClaimed, 1) == 0;

        if (iAmCreator)
        {
            await page.GotoAsync("login");
            await page.Locator("[data-testid='setup-username']").FillAsync(AdminUsername);
            await page.Locator("[data-testid='new-password']").FillAsync(AdminPassword);
            await page.Locator("[data-testid='confirm-password']").FillAsync(AdminPassword);
            await page.Locator("[data-testid='set-password']").ClickAsync();
            await Expect(page.Locator("nav.svx-nav")).ToBeVisibleAsync();

            // Only signalled once the account demonstrably exists (nav rendered => the POST succeeded and
            // signed this session in) — a waiter must never be released into a login attempt that would race
            // the server-side account row actually being written.
            AccountReady.TrySetResult(true);
        }
        else
        {
            await AccountReady.Task;

            await page.GotoAsync("login");
            await page.Locator("[data-testid='username']").FillAsync(AdminUsername);
            await page.Locator("[data-testid='password']").FillAsync(AdminPassword);
            await page.Locator("[data-testid='sign-in']").ClickAsync();
            await Expect(page.Locator("nav.svx-nav")).ToBeVisibleAsync();
        }
    }
}
