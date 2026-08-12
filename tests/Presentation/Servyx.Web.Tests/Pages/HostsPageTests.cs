using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Application.Hosts;
using Servyx.Web.Components.Pages.Hosts;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for <c>HostsPage</c>'s composition: it wires <c>HostRegistrationPanel</c>'s
/// <c>OnRegistered</c> callback to <c>RegisteredHostsPanel.ReloadAsync</c> via a component reference, without
/// either panel reaching into the other directly. This is the one behavior that only exists at the page
/// level — everything else is already covered by each panel's own tests.
/// </summary>
public class HostsPageTests : BunitContext
{
    [Fact]
    public void Registering_a_host_refreshes_the_registered_hosts_list_without_a_page_reload()
    {
        var fake = new FakeHostRegistrationService();
        Services.AddSingleton<IHostRegistrationService>(fake);

        var cut = Render<HostsPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=registered-hosts-empty-state]").Should().HaveCount(1));

        cut.Find("[data-testid=endpoint-input]").Change("ssh:user@10.0.0.4:22");
        cut.Find("[data-testid=probe-button]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=host-fingerprint-step]").Should().HaveCount(1));

        cut.Find("[data-testid=fingerprint-confirm-checkbox]").Change(true);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=host-credential-step]").Should().HaveCount(1));

        cut.Find("[data-testid=host-name-input]").Change("my-remote-box");
        cut.Find("[data-testid=private-key-textarea]").Change("key-bytes");
        cut.Find("[data-testid=register-button]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=registered-host-row]").Should().HaveCount(1));
        cut.Find("[data-testid=registered-host-row]").TextContent.Should().Contain("my-remote-box");
        cut.FindAll("[data-testid=registered-hosts-empty-state]").Should().BeEmpty();
    }
}
