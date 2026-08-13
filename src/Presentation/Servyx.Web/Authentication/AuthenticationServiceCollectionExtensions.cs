using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servyx.Infrastructure;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Authentication;

/// <summary>
/// The one registration that turns Servyx from "everyone who can reach the port is an administrator" into
/// "prove you hold the operator password first".
/// </summary>
public static class AuthenticationServiceCollectionExtensions
{
    /// <summary>
    /// Registers cookie authentication, the fail-closed authorization fallback, the operator credential
    /// store, and the Blazor Server authentication-state provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The load-bearing line is <see cref="AuthorizationOptions.FallbackPolicy"/>.</strong> It applies
    /// to every endpoint that carries no authorization metadata of its own — which is every page in this
    /// application, including pages that do not exist yet. Protection is therefore the default state of a new
    /// route rather than something a future author has to remember to opt into with an attribute, and the
    /// only way to become anonymously reachable is to say <c>AllowAnonymous</c> out loud (which exactly two
    /// things do: the login endpoints and the static assets the login page needs to render).
    /// </para>
    /// <para>
    /// When <paramref name="gate"/> is closed, no fallback policy is installed at all and the app behaves
    /// exactly as it did before authentication existed. That is the documented, deliberately-configured
    /// bypass; <see cref="StartupSafetyWarnings"/> makes sure nobody arrives in it by accident and quietly.
    /// The authentication handler and the credential store are still registered in that mode, so
    /// <c>/login</c> keeps working and switching the flag back on needs no other change.
    /// </para>
    /// <para>
    /// <strong>Cookie hardening.</strong> <c>HttpOnly</c> so script cannot read it, <c>SameSite=Strict</c> so
    /// no cross-site request ever carries it (which is also what makes sign-out safe without its own
    /// antiforgery token), and <c>Secure</c> — unconditionally outside Development, and
    /// <see cref="CookieSecurePolicy.SameAsRequest"/> inside it, because the development loopback host is
    /// routinely plain HTTP and an always-Secure cookie there would make it impossible to log in at all.
    /// </para>
    /// </remarks>
    /// <param name="services">The container to register into.</param>
    /// <param name="gate">Whether this process requires authentication.</param>
    /// <param name="isDevelopment">Whether the host is running in the Development environment.</param>
    /// <param name="secretsRootDirectory">
    /// Optional override for where the encrypted secret files live; <see langword="null"/> keeps the
    /// <c>servyx-data/secrets</c> default that the rest of the app already uses.
    /// </param>
    public static IServiceCollection AddServyxOperatorAuthentication(
        this IServiceCollection services,
        AuthenticationGate gate,
        bool isDevelopment,
        string? secretsRootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gate);

        // The operator password verifier is a secret, so it lives in the secret store like every other
        // secret — Data Protection encrypted, one file per URN, sandboxed path resolution — rather than in a
        // bespoke file this feature invented for itself.
        services.AddServyxSecrets(options =>
        {
            if (!string.IsNullOrWhiteSpace(secretsRootDirectory))
            {
                options.SecretsRootDirectory = secretsRootDirectory;
            }
        });

        services.AddSingleton<OperatorCredentialStore>();

        services
            .AddAuthentication(OperatorAuthentication.SchemeName)
            .AddCookie(OperatorAuthentication.SchemeName, options =>
            {
                options.Cookie.Name = OperatorAuthentication.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = isDevelopment
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                options.Cookie.IsEssential = true;

                options.LoginPath = OperatorAuthentication.LoginPath;
                options.LogoutPath = OperatorAuthentication.LogoutPath;
                // NOT OperatorAuthentication.LoginPath — see OperatorAuthentication.HomePath's own remarks for
                // the redirect loop that pairing produces the moment any page declares its own role policy.
                options.AccessDeniedPath = OperatorAuthentication.HomePath;
                options.ReturnUrlParameter = OperatorAuthentication.ReturnUrlParameter;

                options.ExpireTimeSpan = OperatorAuthentication.SessionLifetime;
                options.SlidingExpiration = true;
            });

        // The handler backing every Servyx.Role.* policy. Registered directly against the container, NOT from
        // inside the AddAuthorization configure delegate below — that delegate can run lazily, after the
        // container has been built and made read-only, and a service registration attempted from inside it
        // throws at first request rather than at startup. See RoleAuthorization.AddServyxRolePolicies's own
        // remarks for the failure this sidesteps.
        services.AddSingleton<IAuthorizationHandler, MinimumRoleAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            // The role-minimum policies are registered regardless of whether the gate is open. They are inert
            // until a page opts into one with [Authorize(Policy = ...)] — see RoleAuthorization's own remarks —
            // so registering them with the gate closed costs nothing and means switching the gate back on
            // later needs no second registration pass to catch up.
            options.AddServyxRolePolicies();

            if (!gate.Enabled)
            {
                return;
            }

            options.FallbackPolicy = new AuthorizationPolicyBuilder(OperatorAuthentication.SchemeName)
                .RequireAuthenticatedUser()
                .Build();
        });

        // What makes the signed-in operator visible to components. ServerAuthenticationStateProvider is
        // seeded by the framework from the circuit's own authenticated user, so a Blazor Server circuit sees
        // the same principal the HTTP request that opened it did — TryAdd because the hosting model may
        // already have registered one.
        services.TryAddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();

        return services;
    }
}
