using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>Assertions against the server detail page's "Console" tab (see ServerConsoleTab.razor).</summary>
[Binding]
public sealed class ConsoleSteps(IPage page, AssertionLedger ledger)
{
    [Then(@"^the console shows (\d+) timestamped log lines$")]
    public async Task ThenTheConsoleShowsTimestampedLogLinesAsync(int expectedCount)
    {
        var lines = page.Locator(".console-line");
        await Expect(lines).ToHaveCountAsync(expectedCount);

        var firstTimestamp = await lines.First.Locator(".console-timestamp").InnerTextAsync();
        firstTimestamp.Should().MatchRegex(@"^\d{2}:\d{2}:\d{2}$");

        ledger.Record();
    }

    [Then(@"^the line mentioning ""(.*)"" is highlighted as a warning$")]
    public async Task ThenTheLineMentioningIsHighlightedAsAWarningAsync(string text)
    {
        var warnLine = page.Locator(".console-line-warn");
        await Expect(warnLine).ToBeVisibleAsync();
        await Expect(warnLine).ToContainTextAsync(text);
        ledger.Record();
    }
}
