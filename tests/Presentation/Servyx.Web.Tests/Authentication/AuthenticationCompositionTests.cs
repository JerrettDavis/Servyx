using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Servyx.Web.Authentication;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Authentication;

/// <summary>
/// Composes authentication exactly as <c>Program.cs</c> does, so the one registration everything else rests
/// on — the authorization <em>fallback</em> policy — is checked by a test rather than only by an operator
/// discovering that a new page was reachable without a login.
/// </summary>
/// <remarks>
/// The fallback policy is what makes protection the default state of an endpoint. A per-page
/// <c>[Authorize]</c> scheme would be one forgotten attribute away from an unprotected page; a fallback
/// policy is one deliberate <c>AllowAnonymous</c> away from an unprotected page, and there are exactly two
/// of those in the whole application.
/// </remarks>
public class AuthenticationCompositionTests
{
    private static ServiceProvider Compose(bool authenticationEnabled, bool isDevelopment = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddServyxOperatorAuthentication(
            new AuthenticationGate(authenticationEnabled),
            isDevelopment,
            Path.Combine(Path.GetTempPath(), "servyx-tests", Guid.NewGuid().ToString("n")));

        return services.BuildServiceProvider();
    }

    private static CookieAuthenticationOptions CookieOptionsFrom(ServiceProvider provider)
    {
        // Only the Configure delegates are applied, deliberately: those are the ones this codebase wrote.
        // Running the framework's post-configure step as well would drag in Data Protection and a key ring
        // for no gain, since none of it changes the four properties under test here.
        var options = new CookieAuthenticationOptions();

        foreach (var configure in provider.GetServices<IConfigureOptions<CookieAuthenticationOptions>>())
        {
            if (configure is IConfigureNamedOptions<CookieAuthenticationOptions> named)
            {
                named.Configure(OperatorAuthentication.SchemeName, options);
            }
        }

        return options;
    }

    [Fact]
    public async Task WithAuthenticationEnabled_EveryEndpointWithoutItsOwnPolicy_RequiresAnAuthenticatedUser()
    {
        using var provider = Compose(authenticationEnabled: true);

        var fallback = await provider
            .GetRequiredService<IAuthorizationPolicyProvider>()
            .GetFallbackPolicyAsync();

        fallback.Should().NotBeNull(
            "the fallback policy is the whole mechanism: without it, a page with no [Authorize] attribute is "
            + "anonymously reachable, which is every page in this application");

        fallback!.Requirements.Should().ContainSingle()
            .Which.Should().BeOfType<DenyAnonymousAuthorizationRequirement>();

        fallback.AuthenticationSchemes.Should().ContainSingle()
            .Which.Should().Be(OperatorAuthentication.SchemeName);
    }

    [Fact]
    public async Task WithAuthenticationDisabled_NoFallbackPolicyExists_WhichIsTheDocumentedBypass()
    {
        using var provider = Compose(authenticationEnabled: false);

        var fallback = await provider
            .GetRequiredService<IAuthorizationPolicyProvider>()
            .GetFallbackPolicyAsync();

        fallback.Should().BeNull(
            "with the gate closed the app must behave exactly as it did before authentication existed");
    }

    [Fact]
    public async Task TheOperatorSchemeIsRegistered_AndIsTheDefaultForEverything()
    {
        using var provider = Compose(authenticationEnabled: true);
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        var scheme = await schemes.GetSchemeAsync(OperatorAuthentication.SchemeName);
        scheme.Should().NotBeNull();
        scheme!.HandlerType.Should().Be<CookieAuthenticationHandler>();

        (await schemes.GetDefaultAuthenticateSchemeAsync())!.Name
            .Should().Be(OperatorAuthentication.SchemeName);
        (await schemes.GetDefaultChallengeSchemeAsync())!.Name
            .Should().Be(OperatorAuthentication.SchemeName);
    }

    [Fact]
    public void TheAuthCookieIsHardened()
    {
        using var provider = Compose(authenticationEnabled: true);
        var options = CookieOptionsFrom(provider);

        options.Cookie.Name.Should().Be(OperatorAuthentication.CookieName);
        options.Cookie.HttpOnly.Should().BeTrue("script must never be able to read the session cookie");
        options.Cookie.SameSite.Should().Be(SameSiteMode.Strict,
            "no cross-site request may carry the session cookie — which is also what makes sign-out safe "
            + "without an antiforgery token of its own");
        options.SlidingExpiration.Should().BeTrue();
        options.ExpireTimeSpan.Should().Be(OperatorAuthentication.SessionLifetime);
        options.LoginPath.Value.Should().Be(OperatorAuthentication.LoginPath);
    }

    [Fact]
    public void OutsideDevelopment_TheCookieIsAlwaysSecure()
    {
        using var provider = Compose(authenticationEnabled: true, isDevelopment: false);

        CookieOptionsFrom(provider).Cookie.SecurePolicy.Should().Be(CookieSecurePolicy.Always);
    }

    [Fact]
    public void InDevelopment_TheCookieFollowsTheRequestScheme_SoPlainHttpLoopbackStillWorks()
    {
        // The single concession, and it is scoped to Development only: an always-Secure cookie over the
        // plain-HTTP loopback address the dev launch profile uses would make it impossible to log in at all.
        using var provider = Compose(authenticationEnabled: true, isDevelopment: true);

        CookieOptionsFrom(provider).Cookie.SecurePolicy.Should().Be(CookieSecurePolicy.SameAsRequest);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/deploy", "/deploy")]
    [InlineData("/servers/palworld-01", "/servers/palworld-01")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    [InlineData("http://127.0.0.1/deploy", "/")]
    [InlineData("/login", "/")]
    [InlineData("/logout", "/")]
    [InlineData("deploy", "/")]
    public void AReturnUrlIsReducedToSomethingLocal_OrToTheRoot(string? candidate, string expected)
        => AuthenticationEndpoints.SanitizeReturnUrl(candidate).Should().Be(
            expected,
            "an open redirect on a sign-in page is how a convincing credential-phishing chain starts");
}
