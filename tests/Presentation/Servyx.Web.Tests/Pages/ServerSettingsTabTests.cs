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

        public Task<SaveDesiredValueResult> SetMirrorToDerivedAsync(
            Servyx.Domain.Common.ServerId serverId, string key, bool? mirrorToDerived, string actor, CancellationToken ct = default)
        {
            if (serverId != _serverId)
            {
                return Task.FromResult(new SaveDesiredValueResult(SaveDesiredValueOutcome.ServerNotFound, null));
            }

            if (!_values.TryGetValue(key, out var existing))
            {
                return Task.FromResult(
                    new SaveDesiredValueResult(SaveDesiredValueOutcome.NoDesiredValueRecorded, null));
            }

            var recorded = existing with { MirrorToDerived = mirrorToDerived, UpdatedBy = actor };
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
        playersRow.QuerySelector("[data-testid='setting-rendered-value']")!.TextContent.Trim().Should().Be("16");
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

    // ── INI-sourced-value warning ────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_setting_pending_regeneration_shows_the_ini_drift_warning_next_to_Rendered()
    {
        var cut = Render<ServerSettingsTab>(p => p.Add(x => x.Settings, SampleSettings()));

        // PLAYERS is deliberately drifted with PendingRegeneration=true (Rendered/Runtime=16 while
        // Desired/Authoritative=32) — the exact condition a restart would silently overwrite in favor of
        // the .env value, discarding whatever is currently live in the INI.
        var playersRow = cut.Find("div.settings-row[data-setting-key='PLAYERS']");
        var renderedCell = playersRow.QuerySelector("[data-col-label='Rendered (INI)']")!;

        var warning = renderedCell.QuerySelector("[data-testid='setting-ini-drift-warning']");
        warning.Should().NotBeNull();
        warning!.GetAttribute("title").Should().Contain("not durable");
        warning.GetAttribute("title").Should().Contain("next restart or recreate");
    }

    [Fact]
    public void A_setting_with_no_drift_shows_no_ini_drift_warning()
    {
        var cut = Render<ServerSettingsTab>(p => p.Add(x => x.Settings, SampleSettings()));

        // SERVER_NAME and DIFFICULTY both carry DriftKind.None in the mock catalogue, so neither should
        // ever surface a warning about a value that is about to be silently discarded.
        foreach (var key in new[] { "SERVER_NAME", "DIFFICULTY" })
        {
            var row = cut.Find($"div.settings-row[data-setting-key='{key}']");
            row.QuerySelector("[data-testid='setting-ini-drift-warning']").Should().BeNull(
                because: $"{key} has no Authoritative/Rendered drift in the mock catalogue");
        }
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
        var lockedNote = row.QuerySelector("[data-testid='setting-locked-recreate']");
        lockedNote.Should().NotBeNull();
        lockedNote!.TextContent.Should().Contain("nothing in Servyx recreates a container",
            because: "IPlanExecutor now exists and can write the bytes — the honest reason this row stays " +
                "locked is that nothing recreates the container to pick them up, not a missing executor");
        lockedNote.TextContent.Should().NotContain("Phase 4b");

        row.QuerySelector("[data-testid^='setting-save-']")!.HasAttribute("disabled").Should().BeTrue(
            because: "recording an intent Servyx can write but never make the running workload honor would " +
                "itself be the dishonesty this phase exists to avoid");

        var title = row.QuerySelector("fieldset.gated-control")!.GetAttribute("title");
        title.Should().Contain("recreated");
        title.Should().Contain("nothing in Servyx recreates a container");
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
    public void The_desired_not_applied_notice_points_at_review_changes_instead_of_claiming_nothing_can_apply()
    {
        var cut = Render<ServerSettingsTab>(p => p.Add(x => x.Settings, SampleSettings()));

        var notice = cut.Find("[data-testid='settings-desired-not-applied-notice']").TextContent;
        notice.Should().Contain("Review changes",
            because: "IPlanExecutor now has an implementation — the honest next step is pointing at the " +
                "control that previews a plan, not asserting nothing can apply a desired value");
        notice.Should().NotContain("Nothing in this codebase can apply a desired value");
        notice.Should().NotContain("Phase 4b");
    }

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

    // ── The recorded/unsaved seam handed down to ChangePlanPanel ─────────────────────────────────────
    //
    // ChangePlanPanelTests proves the panel forwards whatever DesiredValues it is given, verbatim, to
    // IPlanExecutor.PreviewAsync. Nothing there can prove WHAT this tab hands down, so the two suites would
    // otherwise meet at a seam neither covers: a tab that merged _edits into RecordedDesiredValues would put
    // unsaved editor text on the wire to PreviewAsync, and every test in both suites would still pass. The
    // tests below assert the dictionary this tab actually passes to the child component.

    [Fact]
    public void The_plan_panel_is_handed_only_recorded_desired_values_never_unsaved_editor_text()
    {
        var service = new FakeServerSettingsService();
        Services.AddSingleton<IServerSettingsService>(service);

        var cut = Render<ServerSettingsTab>(p => p
            .Add(x => x.Settings, SampleSettings())
            .Add(x => x.ServerId, "palygondwanaland")
            .Add(x => x.WriteMode, WriteMode.Enabled));

        // Record one value for real, so _desired is genuinely populated by the same path production uses.
        var nameRow = cut.Find("div.settings-row[data-setting-key='SERVER_NAME']");
        nameRow.QuerySelector("input[data-testid='setting-editor-control']")!.Change("recorded-name");
        nameRow.QuerySelector("[data-testid^='setting-save-']")!.Click();

        // Then diverge the editor from what was recorded, and add an edit for a key that was NEVER recorded.
        cut.Find("div.settings-row[data-setting-key='SERVER_NAME'] input[data-testid='setting-editor-control']")
            .Change("typed-but-never-saved");
        cut.Find("div.settings-row[data-setting-key='PLAYERS'] input[data-testid='setting-editor-control']")
            .Change("9999");

        var handedDown = cut.FindComponent<ChangePlanPanel>().Instance.DesiredValues;

        handedDown.Should().BeEquivalentTo(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["SERVER_NAME"] = "recorded-name" },
            because: "preview must read ONLY what Servyx's own database recorded — an approved plan whose " +
                "bytes came from unsaved editor text is exactly the divergence the desired-values table exists " +
                "to prevent");

        handedDown.Values.Should().NotContain("typed-but-never-saved");
        handedDown.Should().NotContainKey("PLAYERS",
            because: "an unsaved edit for a key with no recorded value must not appear in the plan input at all");
    }

    [Fact]
    public void Unsaved_edits_are_surfaced_to_the_plan_panel_as_keys_rather_than_as_values()
    {
        var service = new FakeServerSettingsService();
        Services.AddSingleton<IServerSettingsService>(service);

        var cut = Render<ServerSettingsTab>(p => p
            .Add(x => x.Settings, SampleSettings())
            .Add(x => x.ServerId, "palygondwanaland")
            .Add(x => x.WriteMode, WriteMode.Enabled));

        cut.Find("div.settings-row[data-setting-key='PLAYERS'] input[data-testid='setting-editor-control']")
            .Change("9999");
        cut.Find("div.settings-row[data-setting-key='PORT'] input[data-testid='setting-editor-control']")
            .Change("7777");

        var panel = cut.FindComponent<ChangePlanPanel>().Instance;

        // The edits are not silently dropped either — they are named, so the panel can refuse to preview
        // around them instead of quietly omitting a row.
        panel.HasUnsavedEdits.Should().BeTrue();
        panel.UnsavedKeys.Should().BeEquivalentTo(["PLAYERS", "PORT"]);
        panel.DesiredValues.Should().BeEmpty(
            because: "nothing has been recorded yet — an unsaved edit is intent Servyx has not written down");
    }
}
