using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Transport;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Pages;

public class ServerSettingsTabTests : BunitContext
{
    // Not a test method, so the "no blocking task calls" analyzer does not apply here; the
    // underlying task is always already-completed (Task.FromResult), so this never blocks.
    private static IReadOnlyList<SettingRow> SampleSettings()
        => new MockDashboardDataService().GetServerSettingsAsync("palygondwanaland").GetAwaiter().GetResult();

    /// <summary>
    /// A hand-written <see cref="IServerSettingsService"/> rather than a substitute — these tests assert on
    /// the sequence of desired values the tab renders after saving, which is easier to read as a tiny
    /// in-memory implementation than as a stack of configured returns. Mirrors
    /// <c>WriteModeControlTests.FakeWriteGrantService</c>'s own rationale.
    /// </summary>
    private sealed class FakeServerSettingsService : IServerSettingsService
    {
        private readonly Servyx.Domain.Common.ServerId _serverId = Servyx.Domain.Common.ServerId.New();
        private readonly Dictionary<string, DesiredSettingValue> _values = new(StringComparer.Ordinal);
        private readonly bool _tracked;

        public FakeServerSettingsService(bool tracked = true) => _tracked = tracked;

        public int SaveCalls { get; private set; }

        public string? LastActor { get; private set; }

        public Task<ServerSettingsSnapshot?> LoadAsync(string containerId, CancellationToken ct = default) =>
            Task.FromResult(_tracked ? new ServerSettingsSnapshot(_serverId, _values) : null);

        public Task<SaveDesiredValueResult> SaveDesiredValueAsync(
            Servyx.Domain.Common.ServerId serverId, string key, string? value, string actor, CancellationToken ct = default)
        {
            SaveCalls++;
            LastActor = actor;

            if (serverId != _serverId)
            {
                return Task.FromResult(new SaveDesiredValueResult(SaveDesiredValueOutcome.ServerNotFound, null));
            }

            var recorded = new DesiredSettingValue(
                key, value ?? string.Empty, actor, new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
            _values[key] = recorded;
            return Task.FromResult(new SaveDesiredValueResult(SaveDesiredValueOutcome.Recorded, recorded));
        }
    }

    private static readonly SettingConstraints NoConstraints =
        new(null, null, null, null, null, null, null, null, null);

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
        var input = adminRow.QuerySelector("input[data-testid='setting-editor-control']")!;

        input.GetAttribute("type").Should().Be("password");

        // Never pre-filled — not with the real value (never modeled anywhere in this mock), and not even
        // with the "********" mask Authoritative renders: an operator seeing a value already sitting in the
        // Desired field could reasonably read that as "this is the current secret", which would be exactly
        // as dishonest as leaking the real one.
        input.GetAttribute("value").Should().BeNullOrEmpty(
            because: "a secret's Desired field must never round-trip any stored value into a rendered input, " +
                "masked or otherwise");

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

    // ── Write gating ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadOnly_cannot_save_a_desired_value()
    {
        var service = new FakeServerSettingsService();
        Services.AddSingleton<IServerSettingsService>(service);

        var cut = Render<ServerSettingsTab>(p => p
            .Add(x => x.Settings, SampleSettings())
            .Add(x => x.ServerId, "palygondwanaland")
            .Add(x => x.WriteMode, WriteMode.ReadOnly));

        var row = cut.Find("div.settings-row[data-setting-key='SERVER_NAME']");
        row.QuerySelector("[data-testid^='setting-save-']")!.HasAttribute("disabled").Should().BeTrue(
            because: "ReadOnly is this product's promise that Servyx will not record ANY operator intent for " +
                "a server, not only that it will not act on one");
    }

    [Fact]
    public void PreviewOnly_can_record_a_desired_value()
    {
        var service = new FakeServerSettingsService();
        Services.AddSingleton<IServerSettingsService>(service);

        var cut = Render<ServerSettingsTab>(p => p
            .Add(x => x.Settings, SampleSettings())
            .Add(x => x.ServerId, "palygondwanaland")
            .Add(x => x.WriteMode, WriteMode.PreviewOnly));

        var row = cut.Find("div.settings-row[data-setting-key='SERVER_NAME']");
        row.QuerySelector("input[data-testid='setting-editor-control']")!.Change("A new name");

        var saveButton = row.QuerySelector("[data-testid^='setting-save-']")!;
        saveButton.HasAttribute("disabled").Should().BeFalse(
            because: "PreviewOnly is exactly the tier for recording what a change would be without applying it");

        saveButton.Click();

        service.SaveCalls.Should().Be(1);
    }

    [Fact]
    public void Enabled_can_record_a_desired_value()
    {
        var service = new FakeServerSettingsService();
        Services.AddSingleton<IServerSettingsService>(service);

        var cut = Render<ServerSettingsTab>(p => p
            .Add(x => x.Settings, SampleSettings())
            .Add(x => x.ServerId, "palygondwanaland")
            .Add(x => x.WriteMode, WriteMode.Enabled));

        var row = cut.Find("div.settings-row[data-setting-key='SERVER_NAME']");
        row.QuerySelector("input[data-testid='setting-editor-control']")!.Change("A new name");
        row.QuerySelector("[data-testid^='setting-save-']")!.Click();

        service.SaveCalls.Should().Be(1);
    }

    [Fact]
    public void RequiresRecreate_settings_render_locked_regardless_of_write_mode()
    {
        var descriptor = new SettingDescriptor(
            Key: "SERVER_NAME",
            Label: "Server name",
            Group: "Identity",
            Type: SettingType.String,
            Required: false,
            Default: null,
            RenderFormat: null,
            RequiresRecreate: true,
            PublishByDefault: null,
            Constraints: NoConstraints,
            Bindings: []);

        var service = new FakeServerSettingsService();
        Services.AddSingleton<IServerSettingsService>(service);

        var cut = Render<ServerSettingsTab>(p => p
            .Add(x => x.Settings, SampleSettings())
            .Add(x => x.Descriptors, new Dictionary<string, SettingDescriptor>(StringComparer.Ordinal) { ["SERVER_NAME"] = descriptor })
            .Add(x => x.ServerId, "palygondwanaland")
            .Add(x => x.WriteMode, WriteMode.Enabled));

        var row = cut.Find("div.settings-row[data-setting-key='SERVER_NAME']");
        row.QuerySelector("[data-testid='setting-locked-recreate']").Should().NotBeNull();
        row.QuerySelector("[data-testid^='setting-save-']")!.HasAttribute("disabled").Should().BeTrue(
            because: "recording an intent Servyx cannot even theoretically honor yet (no IPlanExecutor, no " +
                "container-recreate plan) would itself be the dishonesty this phase exists to avoid");
        row.QuerySelector("fieldset.gated-control")!.GetAttribute("title").Should().Contain("recreated");
    }

    [Fact]
    public void An_untracked_container_offers_nothing_to_save_and_says_so()
    {
        var service = new FakeServerSettingsService(tracked: false);
        Services.AddSingleton<IServerSettingsService>(service);

        var cut = Render<ServerSettingsTab>(p => p
            .Add(x => x.Settings, SampleSettings())
            .Add(x => x.ServerId, "palygondwanaland")
            .Add(x => x.WriteMode, WriteMode.Enabled));

        cut.Find("[data-testid='settings-untracked']").TextContent.Should().Contain("Adopt it");

        var row = cut.Find("div.settings-row[data-setting-key='SERVER_NAME']");
        row.QuerySelector("[data-testid^='setting-save-']")!.HasAttribute("disabled").Should().BeTrue();
    }

    // ── The core honesty guarantee ───────────────────────────────────────────────────────────────────

    [Fact]
    public void A_saved_value_is_shown_as_desired_and_never_implies_it_reached_the_running_server()
    {
        var service = new FakeServerSettingsService();
        Services.AddSingleton<IServerSettingsService>(service);

        var cut = Render<ServerSettingsTab>(p => p
            .Add(x => x.Settings, SampleSettings())
            .Add(x => x.ServerId, "palygondwanaland")
            .Add(x => x.WriteMode, WriteMode.Enabled));

        var row = cut.Find("div.settings-row[data-setting-key='SERVER_NAME']");
        row.QuerySelector("input[data-testid='setting-editor-control']")!.Change("A brand new name");
        row.QuerySelector("[data-testid^='setting-save-']")!.Click();

        var status = row.QuerySelector("[data-testid='setting-save-status']");
        status.Should().NotBeNull();
        status!.TextContent.Should().Contain("Not applied to the running server",
            because: "a saved value is Servyx's recorded intent, never a value that reached the server");

        // A save here must not move the columns that report the REAL server state — that is the entire
        // point of "desired, not applied".
        row.QuerySelector("[data-col-label='Authoritative (.env)']")!.TextContent.Trim().Should().Be("Palygondwanaland");
        row.QuerySelector("[data-col-label='Runtime']")!.TextContent.Trim().Should().Be("Palygondwanaland");

        cut.Markup.Should().NotContain("has been applied");
        cut.Markup.Should().NotContain("was applied");
    }
}
