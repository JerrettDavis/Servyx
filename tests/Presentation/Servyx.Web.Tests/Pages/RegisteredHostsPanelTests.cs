using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Application.Hosts;
using Servyx.Domain.Common;
using Servyx.Web.Components.Pages.Hosts;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for <c>RegisteredHostsPanel</c>: list registered hosts, and deregister one behind a
/// two-step confirm whose copy is explicit — matching <c>AdoptionPanel</c>'s "Forget" — that nothing but
/// Servyx's own host row is touched.
/// </summary>
public class RegisteredHostsPanelTests : BunitContext
{
    private static RegisteredHost Host(string name = "my-remote-box", bool enabled = true, string? registeredBy = "operator") =>
        new(HostId.New(), name, "ssh:user@10.0.0.4:22", "requirePinned", "SHA256:AAAA", enabled, registeredBy, DateTimeOffset.UtcNow);

    private FakeHostRegistrationService RegisterFake()
    {
        var fake = new FakeHostRegistrationService();
        Services.AddSingleton<IHostRegistrationService>(fake);
        return fake;
    }

    [Fact]
    public void Nothing_registered_renders_gracefully_instead_of_throwing()
    {
        var cut = Render<RegisteredHostsPanel>();

        cut.Find("[data-testid=registered-hosts-unavailable]").TextContent.Should().Contain("cannot be read");
        cut.FindAll("[data-testid=registered-host-row]").Should().BeEmpty();
    }

    [Fact]
    public void Empty_state_renders_when_nothing_is_registered()
    {
        RegisterFake();

        var cut = Render<RegisteredHostsPanel>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=registered-hosts-empty-state]").Should().HaveCount(1));
        cut.FindAll("[data-testid=registered-host-row]").Should().BeEmpty();
    }

    [Fact]
    public void Registered_hosts_render_with_name_endpoint_enabled_and_registered_by()
    {
        var fake = RegisterFake();
        fake.Hosts.Add(Host());

        var cut = Render<RegisteredHostsPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=registered-host-row]").Should().HaveCount(1));

        var row = cut.Find("[data-testid=registered-host-row]");
        row.TextContent.Should().Contain("my-remote-box");
        row.TextContent.Should().Contain("ssh:user@10.0.0.4:22");
        row.TextContent.Should().Contain("Enabled");
        row.TextContent.Should().Contain("operator");
    }

    [Fact]
    public void An_honest_unavailable_message_renders_when_listing_fails()
    {
        var fake = RegisterFake();
        fake.ListingFailureDetail = "database unreachable";

        var cut = Render<RegisteredHostsPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=registered-hosts-degraded]").Should().HaveCount(1));

        cut.FindAll("[data-testid=registered-hosts-empty-state]").Should().BeEmpty();
        cut.Find("[data-testid=registered-hosts-degraded-detail]").TextContent.Should().Contain("database unreachable");
    }

    [Fact]
    public void Deregister_requires_the_second_confirm_click()
    {
        var fake = RegisterFake();
        fake.Hosts.Add(Host());

        var cut = Render<RegisteredHostsPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=deregister-button]").Should().HaveCount(1));

        cut.Find("[data-testid=deregister-button]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=deregister-confirm-step]").Should().HaveCount(1));
        fake.DeregisterCalls.Should().BeEmpty();

        var confirmText = cut.Find("[data-testid=deregister-confirm-step]").TextContent;
        confirmText.Should().Contain("does").And.Contain("not");
        confirmText.Should().Contain("stored SSH credential");
        confirmText.Should().Contain("pinned host key");

        cut.Find("[data-testid=deregister-confirm]").Click();
        cut.WaitForAssertion(() => fake.DeregisterCalls.Should().ContainSingle());
        fake.DeregisterCalls[0].Name.Should().Be("my-remote-box");
    }

    [Fact]
    public void Cancelling_the_deregister_confirm_step_calls_nothing()
    {
        var fake = RegisterFake();
        fake.Hosts.Add(Host());

        var cut = Render<RegisteredHostsPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=deregister-button]").Should().HaveCount(1));
        cut.Find("[data-testid=deregister-button]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=deregister-confirm-step]").Should().HaveCount(1));

        cut.Find("[data-testid=deregister-cancel]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=deregister-confirm-step]").Should().BeEmpty());
        fake.DeregisterCalls.Should().BeEmpty();
        cut.FindAll("[data-testid=registered-host-row]").Should().HaveCount(1);
    }

    [Fact]
    public void A_successful_deregister_removes_the_row()
    {
        var fake = RegisterFake();
        fake.Hosts.Add(Host());

        var cut = Render<RegisteredHostsPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=deregister-button]").Should().HaveCount(1));
        cut.Find("[data-testid=deregister-button]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=deregister-confirm]").Should().HaveCount(1));
        cut.Find("[data-testid=deregister-confirm]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=registered-host-row]").Should().BeEmpty());
        cut.FindAll("[data-testid=registered-hosts-empty-state]").Should().HaveCount(1);
    }

    [Fact]
    public void A_not_found_outcome_is_surfaced_and_still_reloads()
    {
        var fake = RegisterFake();
        fake.Hosts.Add(Host());
        fake.DeregisterResultFactory = name => DeregistrationResult.NotFound(name);

        var cut = Render<RegisteredHostsPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=deregister-button]").Should().HaveCount(1));
        cut.Find("[data-testid=deregister-button]").Click();
        cut.Find("[data-testid=deregister-confirm]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=deregister-error]").Should().HaveCount(1));
        cut.Find("[data-testid=deregister-error]").TextContent.Should().Contain("No host is registered");
    }

    [Fact]
    public async Task ReloadAsync_can_be_driven_externally_after_a_registration_elsewhere()
    {
        var fake = RegisterFake();
        var cut = Render<RegisteredHostsPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=registered-hosts-empty-state]").Should().HaveCount(1));

        fake.Hosts.Add(Host());
        await cut.InvokeAsync(() => cut.Instance.ReloadAsync());

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=registered-host-row]").Should().HaveCount(1));
    }
}
