using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Composition;
using Servyx.Web.Components.Pages.AppSettings;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for the operator password rotation form.
/// </summary>
/// <remarks>
/// The security-relevant claim is what this form will <em>not</em> do: with no password set on the install it
/// renders an explanation and no form at all, because a control that sets a password without needing the
/// current one is a way in that requires no credential. Setting the first password stays <c>/login</c>'s
/// one-time flow, which <c>OperatorCredentialStore.TrySetInitialPasswordAsync</c> refuses to repeat.
/// </remarks>
public class OperatorPasswordSettingsPanelTests : BunitContext
{
    private const string Current = "correct-horse-battery-staple";
    private const string Replacement = "a-brand-new-operator-password";

    [Fact]
    public void An_uncomposed_credential_store_is_reported_rather_than_the_panel_vanishing()
    {
        Arrange();

        var cut = RenderPanel(OperatorCredentialSettingsSection.Unavailable(true, AuthenticationGate.ConfigurationKey));

        cut.Find("[data-testid=operator-password-unavailable]").Should().NotBeNull();
        cut.FindAll("input").Should().BeEmpty();
    }

    [Fact]
    public void With_no_password_set_there_is_no_form_at_all_only_an_explanation()
    {
        Arrange();

        var cut = RenderPanel(Section(passwordSet: false));

        cut.Find("[data-testid=operator-password-not-set]").Should().NotBeNull();
        cut.FindAll("input").Should().BeEmpty("a form that sets a password without needing the current one is a way in");
        cut.FindAll("[data-testid=change-password-button]").Should().BeEmpty();
    }

    [Fact]
    public void A_rotation_passes_both_passwords_through_and_reports_success()
    {
        var service = Arrange();

        var cut = RenderPanel(Section());
        Fill(cut, Current, Replacement, Replacement);
        cut.Find("[data-testid=change-password-button]").Click();

        service.PasswordChangeCalls.Should().ContainSingle();
        service.PasswordChangeCalls[0].Should().Be((Current, Replacement));
        cut.Find("[data-testid=operator-password-applied]").TextContent.Should().Contain("has been changed");
    }

    [Fact]
    public void A_mismatched_confirmation_is_caught_here_and_never_reaches_the_credential_store()
    {
        var service = Arrange();

        var cut = RenderPanel(Section());
        Fill(cut, Current, Replacement, "something-else-entirely");
        cut.Find("[data-testid=change-password-button]").Click();

        service.PasswordChangeCalls.Should().BeEmpty("a typo in this form is not a question for the store");
        cut.Find("[data-testid=operator-password-error]").TextContent.Should().Contain("do not match");
    }

    [Fact]
    public void An_incorrect_current_password_is_reported_as_having_changed_nothing()
    {
        var service = Arrange();
        service.PasswordChangeResult = OperatorPasswordChangeResult.CurrentPasswordIncorrect;

        var cut = RenderPanel(Section());
        Fill(cut, "wrong", Replacement, Replacement);
        cut.Find("[data-testid=change-password-button]").Click();

        cut.Find("[data-testid=operator-password-error]").TextContent.Should().Contain("Nothing was changed");
        cut.FindAll("[data-testid=operator-password-applied]").Should().BeEmpty();
    }

    [Fact]
    public void A_new_password_the_store_refuses_surfaces_the_stores_own_requirement()
    {
        var service = Arrange();
        service.PasswordChangeResult = new OperatorPasswordChangeResult(
            OperatorPasswordChangeOutcome.NewPasswordRejected,
            "The operator password must be at least 12 characters.");

        var cut = RenderPanel(Section());
        Fill(cut, Current, "short", "short");
        cut.Find("[data-testid=change-password-button]").Click();

        cut.Find("[data-testid=operator-password-error]").TextContent.Should().Contain("at least 12 characters");
    }

    [Fact]
    public void A_failed_write_says_the_existing_password_is_unchanged_and_leaks_nothing_else()
    {
        var service = Arrange();
        service.PasswordChangeResult = new OperatorPasswordChangeResult(
            OperatorPasswordChangeOutcome.Failed, "the secret store threw: " + Current);

        var cut = RenderPanel(Section());
        Fill(cut, Current, Replacement, Replacement);
        cut.Find("[data-testid=change-password-button]").Click();

        var error = cut.Find("[data-testid=operator-password-error]").TextContent;
        error.Should().Contain("existing password is unchanged");

        // Two plaintext passwords are in scope on this path, so the failure detail is logged rather than
        // rendered — the page says what happened without repeating anything the store handed back.
        cut.Markup.Should().NotContain(Current);
    }

    [Fact]
    public void Nothing_typed_into_the_form_is_ever_rendered_back_into_the_pages_markup()
    {
        Arrange();

        var cut = RenderPanel(Section());
        Fill(cut, Current, Replacement, Replacement);

        cut.Markup.Should().NotContain(Current);
        cut.Markup.Should().NotContain(Replacement);
    }

    [Fact]
    public void An_unauthenticated_process_is_told_that_rotating_protects_nothing_yet()
    {
        Arrange();

        var cut = RenderPanel(Section(authenticationEnabled: false));

        var note = cut.Find("[data-testid=operator-password-gate-open]").TextContent;
        note.Should().Contain(AuthenticationGate.ConfigurationKey);
        note.Should().Contain("protects nothing");
    }

    [Fact]
    public void An_authenticated_process_carries_no_such_warning()
    {
        Arrange();

        var cut = RenderPanel(Section());

        cut.FindAll("[data-testid=operator-password-gate-open]").Should().BeEmpty();
    }

    private static void Fill(
        IRenderedComponent<OperatorPasswordSettingsPanel> cut, string current, string next, string confirm)
    {
        cut.Find("[data-testid=current-password-input]").Change(current);
        cut.Find("[data-testid=new-password-input]").Change(next);
        cut.Find("[data-testid=confirm-password-input]").Change(confirm);
    }

    private FakeSettingsDataService Arrange()
    {
        var service = new FakeSettingsDataService();
        Services.AddSingleton<ISettingsDataService>(service);
        return service;
    }

    private IRenderedComponent<OperatorPasswordSettingsPanel> RenderPanel(OperatorCredentialSettingsSection section) =>
        Render<OperatorPasswordSettingsPanel>(parameters => parameters.Add(p => p.Section, section));

    private static OperatorCredentialSettingsSection Section(
        bool passwordSet = true, bool authenticationEnabled = true) =>
        new(Available: true, authenticationEnabled, passwordSet, 12, AuthenticationGate.ConfigurationKey);
}
