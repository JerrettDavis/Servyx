using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>
/// Assertions for the <c>/audit</c> page (see <c>AuditPage.razor</c>) — a real, server-side-filtered reader
/// over Servyx's cross-cutting accountability trail.
/// </summary>
[Binding]
public sealed class OperatorAdministrationSteps(IPage page, AssertionLedger ledger)
{
    [Then(@"^the audit page lists the accountability trail$")]
    public async Task ThenTheAuditPageListsTheAccountabilityTrailAsync()
    {
        await Expect(page.Locator("[data-testid='audit-filter-section']")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-testid='audit-list-section']")).ToBeVisibleAsync();
        ledger.Record();
    }
}
