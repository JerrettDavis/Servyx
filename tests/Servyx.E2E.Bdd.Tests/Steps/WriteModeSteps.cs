using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>
/// Assertions against the Deploy page's closed-gate copy (see DeployPage.razor), which is the one place in
/// the demonstration data set that names the <c>Servyx:Provisioning:Enabled</c> configuration key and warns
/// about the authentication/provisioning cross-check live, on screen.
/// </summary>
[Binding]
public sealed class WriteModeSteps(IPage page, AssertionLedger ledger)
{
    [Then(@"^the page explains that provisioning requires ""(.*)""$")]
    public async Task ThenThePageExplainsThatProvisioningRequiresAsync(string configurationKey)
    {
        var panel = page.Locator("[data-testid='provisioning-disabled']");
        await Expect(panel).ToBeVisibleAsync();
        await Expect(panel).ToContainTextAsync(configurationKey);
        ledger.Record();
    }

    [Then(@"^the page warns that authentication is disabled$")]
    public async Task ThenThePageWarnsThatAuthenticationIsDisabledAsync()
    {
        var authNote = page.Locator("[data-testid='provisioning-disabled-auth']");
        await Expect(authNote).ToBeVisibleAsync();
        await Expect(authNote).ToContainTextAsync("no authentication of any kind");
        ledger.Record();
    }
}
