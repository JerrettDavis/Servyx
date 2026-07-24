using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>Assertions against the server detail page's "Settings" tab (see ServerSettingsTab.razor).</summary>
[Binding]
public sealed class SettingsSteps(IPage page, AssertionLedger ledger)
{
    /// <summary>Maps a scenario's business-language setting label to the Mock data source's setting key.</summary>
    private static string KeyFor(string label) => label switch
    {
        "Max players" => "PLAYERS",
        "Admin / RCON password" => "ADMIN_PASSWORD",
        _ => throw new ArgumentOutOfRangeException(
            nameof(label), label, $"No known settings-row mapping for label '{label}'."),
    };

    private ILocator RowFor(string label) => page.Locator($"div.settings-row[data-setting-key='{KeyFor(label)}']");

    [Then(@"^the ""(.*)"" setting shows Desired ""(.*)"", Authoritative \(\.env\) ""(.*)"", Rendered \(INI\) ""(.*)"" and Runtime ""(.*)""$")]
    public async Task ThenTheSettingShowsFourColumnsAsync(
        string label, string desired, string authoritative, string rendered, string runtime)
    {
        var row = RowFor(label);

        await Expect(row.Locator("[data-col-label='Desired'] input")).ToHaveValueAsync(desired);
        await Expect(row.Locator("[data-col-label='Authoritative (.env)']")).ToContainTextAsync(authoritative);
        await Expect(row.Locator("[data-col-label='Rendered (INI)']")).ToContainTextAsync(rendered);
        await Expect(row.Locator("[data-col-label='Runtime']")).ToContainTextAsync(runtime);

        ledger.Record();
    }

    [Then(@"^the ""(.*)"" setting is flagged as drifted$")]
    public async Task ThenTheSettingIsFlaggedAsDriftedAsync(string label)
    {
        await Expect(RowFor(label).Locator(".drift-present")).ToBeVisibleAsync();
        ledger.Record();
    }

    [Then(@"^the ""(.*)"" setting's authoritative value is masked as ""(.*)""$")]
    public async Task ThenTheSettingsAuthoritativeValueIsMaskedAsAsync(string label, string masked)
    {
        await Expect(RowFor(label).Locator("[data-col-label='Authoritative (.env)']")).ToContainTextAsync(masked);
        ledger.Record();
    }

    [Then(@"^the ""(.*)"" setting's desired-value field is a password field$")]
    public async Task ThenTheSettingsDesiredValueFieldIsAPasswordFieldAsync(string label)
    {
        await Expect(RowFor(label).Locator("[data-col-label='Desired'] input")).ToHaveAttributeAsync("type", "password");
        ledger.Record();
    }
}
