using Bunit;
using FluentAssertions;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Models;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Pages;

public class ServerSettingsTabTests : BunitContext
{
    // Not a test method, so the "no blocking task calls" analyzer does not apply here; the
    // underlying task is always already-completed (Task.FromResult), so this never blocks.
    private static IReadOnlyList<SettingRow> SampleSettings()
        => new MockDashboardDataService().GetServerSettingsAsync("palygondwanaland").GetAwaiter().GetResult();

    [Fact]
    public void RendersFourValueColumns_AndDriftBadgeForDriftedSetting()
    {
        var cut = Render<ServerSettingsTab>(p => p.Add(x => x.Settings, SampleSettings()));

        // The header names all four SettingState columns, plus the drift column.
        var header = cut.Find(".settings-grid-header");
        header.TextContent.Should().Contain("Desired");
        header.TextContent.Should().Contain("Authoritative");
        header.TextContent.Should().Contain("Rendered");
        header.TextContent.Should().Contain("Runtime");
        header.TextContent.Should().Contain("Drift");

        // PLAYERS is deliberately drifted: Desired/Authoritative=32, Rendered/Runtime=16.
        var playersRow = cut.Find("div.settings-row[data-setting-key='PLAYERS']");

        playersRow.QuerySelector("input")!.GetAttribute("value").Should().Be("32");
        playersRow.QuerySelector("[data-col-label='Authoritative (.env)']")!.TextContent.Trim().Should().Be("32");
        playersRow.QuerySelector("[data-col-label='Rendered (INI)']")!.TextContent.Trim().Should().Be("16");
        playersRow.QuerySelector("[data-col-label='Runtime']")!.TextContent.Trim().Should().Be("16");

        var badge = playersRow.QuerySelector(".drift-present");
        badge.Should().NotBeNull();
        badge!.TextContent.Should().Contain("AuthoritativeVsRendered");
        badge.TextContent.Should().Contain("restart required");

        playersRow.ClassList.Should().Contain("has-drift");
    }

    [Fact]
    public void UndriftedSetting_ShowsNoDriftBadge()
    {
        var cut = Render<ServerSettingsTab>(p => p.Add(x => x.Settings, SampleSettings()));

        var nameRow = cut.Find("div.settings-row[data-setting-key='SERVER_NAME']");
        nameRow.QuerySelector(".drift-none").Should().NotBeNull();
        nameRow.ClassList.Should().NotContain("has-drift");
    }

    [Fact]
    public void SecretSettings_RenderMasked_AndNeverEmitARealValue()
    {
        var cut = Render<ServerSettingsTab>(p => p.Add(x => x.Settings, SampleSettings()));

        var adminRow = cut.Find("div.settings-row[data-setting-key='ADMIN_PASSWORD']");
        var input = adminRow.QuerySelector("input")!;

        input.GetAttribute("type").Should().Be("password");
        input.GetAttribute("value").Should().Be("********");

        adminRow.QuerySelector("[data-col-label='Authoritative (.env)']")!.TextContent.Trim().Should().Be("********");
        adminRow.QuerySelector("[data-col-label='Runtime']")!.TextContent.Trim().Should().Be("********");

        // No secret placeholder ever resembles a plausible real credential, and no real value
        // is modeled anywhere for the mock to leak.
        var markup = cut.Markup;
        markup.Should().NotContain("hunter2");
        markup.Should().NotContain("changeme");
        markup.Should().NotContain("P@ssw0rd");
    }

    [Fact]
    public void AllValueInputs_AreDisabled()
    {
        var cut = Render<ServerSettingsTab>(p => p.Add(x => x.Settings, SampleSettings()));

        foreach (var fieldset in cut.FindAll("fieldset.gated-control"))
        {
            fieldset.HasAttribute("disabled").Should().BeTrue();
        }
    }
}
