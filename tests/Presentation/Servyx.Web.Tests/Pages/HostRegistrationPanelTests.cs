using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Application.Hosts;
using Servyx.Web.Components.Pages.Hosts;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for <c>HostRegistrationPanel</c> — the three-step "probe, confirm the fingerprint out of
/// band, then supply a name and credential" wizard. <see cref="FakeHostRegistrationService"/> stands in for
/// <c>IHostRegistrationService</c>. The properties that matter most here are the ones the increment's brief
/// called non-negotiable: the confirmed fingerprint sent to <c>RegisterAsync</c> is always exactly what
/// <c>ProbeAsync</c> returned (never something a user could type), the credential step never advances
/// without an explicit confirmation click, and the private key/passphrase fields are gone from component
/// state — not merely hidden — after any registration attempt, success or failure.
/// </summary>
public class HostRegistrationPanelTests : BunitContext
{
    private FakeHostRegistrationService RegisterFake()
    {
        var fake = new FakeHostRegistrationService();
        Services.AddSingleton<IHostRegistrationService>(fake);
        return fake;
    }

    private IRenderedComponent<HostRegistrationPanel> ProbeToFingerprintStep(
        string endpoint = "ssh:user@10.0.0.4:22",
        Action<ComponentParameterCollectionBuilder<HostRegistrationPanel>>? configure = null)
    {
        var cut = configure is null ? Render<HostRegistrationPanel>() : Render<HostRegistrationPanel>(configure);
        cut.Find("[data-testid=endpoint-input]").Change(endpoint);
        cut.Find("[data-testid=probe-button]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=host-fingerprint-step]").Should().HaveCount(1));
        return cut;
    }

    [Fact]
    public void Nothing_registered_renders_gracefully_instead_of_throwing()
    {
        var cut = Render<HostRegistrationPanel>();

        cut.Markup.Should().NotContain("host-endpoint-step");
        cut.Find("[data-testid=host-registration-unavailable]").TextContent.Should().Contain("cannot be registered");
    }

    [Fact]
    public void The_scope_limitation_note_is_always_rendered()
    {
        RegisterFake();
        var cut = Render<HostRegistrationPanel>();

        cut.Find("[data-testid=host-registration-scope-note]").TextContent
            .Should().Contain("settings, live logs, backups")
            .And.Contain("Registering a host here does not change that yet");
    }

    [Fact]
    public void A_successful_probe_shows_the_algorithm_and_fingerprint()
    {
        var fake = RegisterFake();
        fake.ProbeResultFactory = _ => new HostProbeResult(
            HostProbeOutcome.Reached, "10.0.0.4", 22, "ssh-ed25519", "SHA256:TESTFINGERPRINT", null);

        var cut = Render<HostRegistrationPanel>();
        cut.Find("[data-testid=endpoint-input]").Change("ssh:user@10.0.0.4:22");
        cut.Find("[data-testid=probe-button]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=fingerprint-value]").TextContent.Should().Contain("SHA256:TESTFINGERPRINT"));
        cut.Find("[data-testid=fingerprint-algorithm]").TextContent.Should().Contain("ssh-ed25519");
        fake.ProbeCalls.Should().ContainSingle().Which.Should().Be("ssh:user@10.0.0.4:22");
    }

    [Fact]
    public void An_unreachable_probe_shows_an_inline_error_and_no_fingerprint_step()
    {
        var fake = RegisterFake();
        fake.ProbeResultFactory = _ =>
            new HostProbeResult(HostProbeOutcome.Unreachable, "10.0.0.4", 22, null, null, "connection refused");

        var cut = Render<HostRegistrationPanel>();
        cut.Find("[data-testid=endpoint-input]").Change("ssh:user@10.0.0.4:22");
        cut.Find("[data-testid=probe-button]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=probe-error]").TextContent.Should().Contain("connection refused"));
        cut.FindAll("[data-testid=host-fingerprint-step]").Should().BeEmpty();
    }

    [Fact]
    public void The_credential_step_does_not_appear_until_the_fingerprint_checkbox_is_checked()
    {
        RegisterFake();
        var cut = ProbeToFingerprintStep();

        cut.FindAll("[data-testid=host-credential-step]").Should().BeEmpty(
            because: "the operator must take an explicit affirmative action before the wizard advances");

        cut.Find("[data-testid=fingerprint-confirm-checkbox]").Change(true);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=host-credential-step]").Should().HaveCount(1));
    }

    [Fact]
    public void Editing_the_endpoint_after_a_probe_hides_the_fingerprint_and_credential_steps()
    {
        RegisterFake();
        var cut = ProbeToFingerprintStep();
        cut.Find("[data-testid=fingerprint-confirm-checkbox]").Change(true);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=host-credential-step]").Should().HaveCount(1));

        cut.Find("[data-testid=endpoint-input]").Change("ssh:user@10.0.0.5:22");

        cut.FindAll("[data-testid=host-fingerprint-step]").Should().BeEmpty(
            because: "a confirmed fingerprint belongs to the endpoint that was actually probed, never to whatever is currently typed");
        cut.FindAll("[data-testid=host-credential-step]").Should().BeEmpty();
    }

    [Fact]
    public void Registering_sends_exactly_the_fingerprint_the_probe_returned()
    {
        var fake = RegisterFake();
        fake.ProbeResultFactory = _ => new HostProbeResult(
            HostProbeOutcome.Reached, "10.0.0.4", 22, "ssh-ed25519", "SHA256:OBSERVED", null);

        var cut = ProbeToFingerprintStep();
        cut.Find("[data-testid=fingerprint-confirm-checkbox]").Change(true);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=host-credential-step]").Should().HaveCount(1));

        cut.Find("[data-testid=host-name-input]").Change("my-remote-box");
        cut.Find("[data-testid=private-key-textarea]").Change("-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----");
        cut.Find("[data-testid=register-button]").Click();

        cut.WaitForAssertion(() => fake.RegisterCalls.Should().ContainSingle());
        var call = fake.RegisterCalls[0];
        call.Name.Should().Be("my-remote-box");
        call.Endpoint.Should().Be("ssh:user@10.0.0.4:22");
        call.ConfirmedFingerprint.Should().Be("SHA256:OBSERVED");
        call.PrivateKeyByteCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_successful_registration_clears_the_form_shows_a_success_banner_and_raises_OnRegistered()
    {
        var fake = RegisterFake();
        var raised = 0;

        var cut = ProbeToFingerprintStep(
            configure: p => p.Add(x => x.OnRegistered, () => { raised++; return Task.CompletedTask; }));
        cut.Find("[data-testid=fingerprint-confirm-checkbox]").Change(true);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=host-credential-step]").Should().HaveCount(1));

        cut.Find("[data-testid=host-name-input]").Change("my-remote-box");
        cut.Find("[data-testid=private-key-textarea]").Change("key-bytes");
        cut.Find("[data-testid=register-button]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=register-success]").Should().HaveCount(1));
        cut.Find("[data-testid=register-success]").TextContent.Should().Contain("my-remote-box");
        raised.Should().Be(1);

        // The wizard resets: no lingering endpoint, fingerprint, or credential step.
        cut.FindAll("[data-testid=host-fingerprint-step]").Should().BeEmpty();
        cut.FindAll("[data-testid=host-credential-step]").Should().BeEmpty();
        cut.Find("[data-testid=endpoint-input]").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public void A_non_success_outcome_is_shown_and_the_form_is_not_reset()
    {
        var fake = RegisterFake();
        fake.RegisterResultFactory = _ => RegistrationResult.AlreadyExists("my-remote-box", Servyx.Domain.Common.HostId.New());

        var cut = ProbeToFingerprintStep();
        cut.Find("[data-testid=fingerprint-confirm-checkbox]").Change(true);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=host-credential-step]").Should().HaveCount(1));

        cut.Find("[data-testid=host-name-input]").Change("my-remote-box");
        cut.Find("[data-testid=private-key-textarea]").Change("key-bytes");
        cut.Find("[data-testid=register-button]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=register-outcome-error]").TextContent.Should().Contain("already registered"));
        cut.FindAll("[data-testid=register-success]").Should().BeEmpty();

        // Refused, not reset: the operator can fix the name without re-probing and re-confirming.
        cut.FindAll("[data-testid=host-credential-step]").Should().HaveCount(1);
    }

    [Fact]
    public void The_private_key_field_is_cleared_after_a_refused_registration_and_never_re_rendered()
    {
        var fake = RegisterFake();
        fake.RegisterResultFactory = _ => RegistrationResult.InvalidName("bad name");

        var cut = ProbeToFingerprintStep();
        cut.Find("[data-testid=fingerprint-confirm-checkbox]").Change(true);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=host-credential-step]").Should().HaveCount(1));

        cut.Find("[data-testid=host-name-input]").Change("bad name");
        cut.Find("[data-testid=private-key-textarea]").Change("super-secret-key-material");
        cut.Find("[data-testid=register-button]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=register-outcome-error]").Should().HaveCount(1));

        cut.Find("[data-testid=private-key-textarea]").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Markup.Should().NotContain("super-secret-key-material");
    }

    [Fact]
    public void An_unexpected_exception_during_registration_shows_a_generic_message_never_the_raw_exception()
    {
        var fake = RegisterFake();
        fake.RegisterResultFactory = _ => throw new InvalidOperationException("failed for key super-secret-key-material");

        var cut = ProbeToFingerprintStep();
        cut.Find("[data-testid=fingerprint-confirm-checkbox]").Change(true);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=host-credential-step]").Should().HaveCount(1));

        cut.Find("[data-testid=host-name-input]").Change("my-remote-box");
        cut.Find("[data-testid=private-key-textarea]").Change("super-secret-key-material");
        cut.Find("[data-testid=register-button]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=register-error]").Should().HaveCount(1));
        var errorText = cut.Find("[data-testid=register-error]").TextContent;
        errorText.Should().Contain("unexpectedly");
        errorText.Should().NotContain("super-secret-key-material");
        cut.Markup.Should().NotContain("super-secret-key-material");
    }

    [Fact]
    public void A_private_key_file_over_the_64KB_cap_is_rejected_with_a_clear_message()
    {
        RegisterFake();
        var cut = ProbeToFingerprintStep();
        cut.Find("[data-testid=fingerprint-confirm-checkbox]").Change(true);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=host-credential-step]").Should().HaveCount(1));

        var oversized = new string('a', 70 * 1024);
        var file = InputFileContent.CreateFromText(oversized, "oversized.pem");

        cut.FindComponent<InputFile>().UploadFiles(file);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=register-error]").Should().HaveCount(1));
        cut.Find("[data-testid=register-error]").TextContent.Should().Contain("64 KB");
        cut.Find("[data-testid=private-key-textarea]").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public void A_private_key_file_within_the_cap_populates_the_textarea()
    {
        RegisterFake();
        var cut = ProbeToFingerprintStep();
        cut.Find("[data-testid=fingerprint-confirm-checkbox]").Change(true);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=host-credential-step]").Should().HaveCount(1));

        var file = InputFileContent.CreateFromText("-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----", "key.pem");
        cut.FindComponent<InputFile>().UploadFiles(file);

        cut.WaitForAssertion(() => cut.Find("[data-testid=private-key-textarea]").GetAttribute("value").Should().Contain("BEGIN PRIVATE KEY"));
    }
}
