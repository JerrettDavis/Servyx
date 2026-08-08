using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Web.Components.Pages.Deploy;
using Servyx.Web.Components.Shared;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Authentication;

/// <summary>
/// The router's fail-closed gate, exercised directly. Its contract is default-deny: a route is rendered only
/// when authentication is switched off wholesale, the route is explicitly
/// <see cref="AllowAnonymousAttribute"/>, or the caller is authenticated. Anything else — including "nobody
/// registered the gate" and "there is no cascading authentication state" — renders nothing and leaves for
/// the sign-in page.
/// </summary>
public class AuthenticationBoundaryTests : BunitContext
{
    private const string ChildMarkup = "<div data-testid='protected-content'>secret</div>";

    /// <summary>A routable component that has opted out. Nothing in the real app does; the login page is not even a route.</summary>
    [AllowAnonymous]
    private sealed class AnonymousPage : ComponentBase;

    /// <summary>A routable component that has not opted out — which is every page Servyx actually has.</summary>
    private sealed class ProtectedPage : ComponentBase;

    private static RouteData RouteFor<TPage>() where TPage : IComponent
        => new(typeof(TPage), new Dictionary<string, object?>(StringComparer.Ordinal));

    /// <summary>
    /// Renders the boundary around a marker child. bUnit's <c>AddAuthorization()</c> supplies the cascading
    /// <c>Task&lt;AuthenticationState&gt;</c> as well as the authorization services, so a test that wants
    /// <em>no</em> authentication state simply does not call it.
    /// </summary>
    private IRenderedComponent<AuthenticationBoundary> RenderBoundary(RouteData routeData)
        => Render<AuthenticationBoundary>(parameters => parameters
            .Add(p => p.RouteData, routeData)
            .AddChildContent(ChildMarkup));

    private string CurrentUri => Services.GetRequiredService<NavigationManager>().Uri;

    [Fact]
    public void AnAnonymousCaller_SeesNothing_AndIsSentToTheLoginPage()
    {
        Services.AddSingleton(new AuthenticationGate(enabled: true));
        AddAuthorization().SetNotAuthorized();

        var cut = RenderBoundary(RouteFor<ProtectedPage>());

        cut.FindAll("[data-testid='protected-content']").Should().BeEmpty(
            "an unauthenticated caller must not have the page rendered for them at all");
        cut.Markup.Should().NotContain("secret");
        CurrentUri.Should().Contain("/login");
    }

    [Fact]
    public void AnAuthenticatedCaller_SeesThePage()
    {
        Services.AddSingleton(new AuthenticationGate(enabled: true));
        AddAuthorization().SetAuthorized("operator");

        var cut = RenderBoundary(RouteFor<ProtectedPage>());

        cut.Find("[data-testid='protected-content']").TextContent.Should().Be("secret");
        CurrentUri.Should().NotContain("/login");
    }

    [Fact]
    public void WithAuthenticationSwitchedOff_TheDocumentedBypassApplies()
    {
        // The explicitly configured unauthenticated mode: no login is demanded of anyone. Program.cs logs
        // about this loudly at startup (see StartupSafetyWarningsTests); the boundary simply steps aside.
        Services.AddSingleton(new AuthenticationGate(enabled: false));
        AddAuthorization().SetNotAuthorized();

        var cut = RenderBoundary(RouteFor<ProtectedPage>());

        cut.Find("[data-testid='protected-content']").TextContent.Should().Be("secret");
        CurrentUri.Should().NotContain("/login");
    }

    [Fact]
    public void WithNoGateRegisteredAtAll_ItStillDemandsALogin()
    {
        // "Nobody composed the gate" must mean "demand a login", not "wave everyone through" — the opposite
        // default from NavMenu's ProvisioningGate lookup, and deliberately so.
        AddAuthorization().SetNotAuthorized();

        var cut = RenderBoundary(RouteFor<ProtectedPage>());

        cut.FindAll("[data-testid='protected-content']").Should().BeEmpty();
        CurrentUri.Should().Contain("/login");
    }

    [Fact]
    public void WithNoCascadingAuthenticationState_ItStillDemandsALogin()
    {
        // No authorization services and therefore no cascading authentication state: nothing in this render
        // tree can vouch for the caller. That is an answer of "no", not an absence of one.
        Services.AddSingleton(new AuthenticationGate(enabled: true));

        var cut = RenderBoundary(RouteFor<ProtectedPage>());

        cut.FindAll("[data-testid='protected-content']").Should().BeEmpty();
        CurrentUri.Should().Contain("/login");
    }

    [Fact]
    public void AnExplicitlyAnonymousRoute_IsTheOnlyOptOut()
    {
        Services.AddSingleton(new AuthenticationGate(enabled: true));
        AddAuthorization().SetNotAuthorized();

        var cut = RenderBoundary(RouteFor<AnonymousPage>());

        cut.Find("[data-testid='protected-content']").TextContent.Should().Be("secret");
    }

    [Fact]
    public void TheRouteBeingLeftIsCarriedBackAsALocalReturnUrl()
    {
        Services.AddSingleton(new AuthenticationGate(enabled: true));
        AddAuthorization().SetNotAuthorized();

        Services.GetRequiredService<NavigationManager>().NavigateTo("deploy");

        RenderBoundary(RouteFor<DeployPage>());

        CurrentUri.Should().Contain("/login");
        CurrentUri.Should().Contain("returnUrl=%2Fdeploy");
    }
}
