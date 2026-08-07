using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>
/// Assertions for the <c>/audit</c> page (see <c>AuditPage.razor</c>) — currently an empty-state
/// placeholder, so this checks the placeholder copy itself rather than any real audit data. There is no
/// audit UI to exercise yet; see <c>Servyx.Web.Authentication.AuthenticationAudit</c> for the actual,
/// structured-log-only audit trail that exists today.
/// </summary>
[Binding]
public sealed class OperatorAdministrationSteps(IPage page, AssertionLedger ledger)
{
    [Then(@"^the audit page explains it has no dedicated UI yet$")]
    public async Task ThenTheAuditPageExplainsItHasNoDedicatedUiYetAsync()
    {
        await Expect(page.Locator(".svx-empty-state h3")).ToHaveTextAsync("No audit events recorded yet");
        await Expect(page.Locator(".svx-empty-state")).ToContainTextAsync("Milestone 7");
        ledger.Record();
    }
}
