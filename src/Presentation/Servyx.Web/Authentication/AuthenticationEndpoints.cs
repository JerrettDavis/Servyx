using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Servyx.Web.Components.Pages.Login;

namespace Servyx.Web.Authentication;

/// <summary>
/// The three HTTP endpoints that make up sign-in, first-run bootstrap and sign-out. Everything an anonymous
/// caller is allowed to reach is here, and it is all here — there is no other <c>AllowAnonymous</c> in the
/// application except the static assets this page needs to render.
/// </summary>
/// <remarks>
/// <para>
/// These are plain minimal-API endpoints rather than routable Blazor pages, on purpose. The login page must
/// work for a caller who has no circuit — and, with the fail-closed fallback policy in force, an
/// unauthenticated caller genuinely cannot open one, because the Blazor SignalR endpoint requires
/// authentication like everything else. Serving <c>/login</c> as a static, self-contained HTML document with
/// an ordinary <c>&lt;form method="post"&gt;</c> means the sign-in path depends on no JavaScript, no
/// WebSocket, and no anonymous component rendering at all.
/// </para>
/// </remarks>
public static class AuthenticationEndpoints
{
    /// <summary>Maps <c>GET /login</c>, <c>POST /login</c> and <c>POST /logout</c>.</summary>
    public static IEndpointRouteBuilder MapServyxOperatorAuthentication(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(OperatorAuthentication.LoginPath, GetLoginAsync).AllowAnonymous();
        endpoints.MapPost(OperatorAuthentication.LoginPath, PostLoginAsync).AllowAnonymous();
        endpoints.MapPost(OperatorAuthentication.LogoutPath, PostLogoutAsync);

        return endpoints;
    }

    private static async Task<IResult> GetLoginAsync(
        HttpContext http,
        IAntiforgery antiforgery,
        OperatorCredentialStore credentials)
    {
        var returnUrl = SanitizeReturnUrl(http.Request.Query[OperatorAuthentication.ReturnUrlParameter]);

        // Already signed in: there is nothing to ask for, and leaving the form up would invite an operator to
        // re-enter a password they did not need to.
        if (http.User.Identity?.IsAuthenticated == true)
        {
            return Results.LocalRedirect(returnUrl);
        }

        var setupRequired = !await credentials.IsPasswordSetAsync(http.RequestAborted).ConfigureAwait(false);

        return RenderLogin(http, antiforgery, setupRequired, returnUrl, error: null);
    }

    private static async Task<IResult> PostLoginAsync(
        HttpContext http,
        IAntiforgery antiforgery,
        OperatorCredentialStore credentials,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(OperatorAuthentication.AuditLogCategory);
        var remote = RemoteAddress(http);

        try
        {
            await antiforgery.ValidateRequestAsync(http).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            // Never evaluated as a credential attempt, because it never got as far as being one.
            logger.LogWarning(
                AuthenticationAudit.AntiforgeryRejected,
                "Login submission from {RemoteAddress} failed antiforgery validation and was not evaluated.",
                remote);

            return Results.BadRequest("The sign-in form expired. Reload /login and try again.");
        }

        var form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
        var returnUrl = SanitizeReturnUrl(form[OperatorAuthentication.ReturnUrlParameter]);
        var passwordAlreadySet = await credentials.IsPasswordSetAsync(http.RequestAborted).ConfigureAwait(false);

        var wantsBootstrap = string.Equals(
            form["intent"], LoginPage.SetPasswordIntent, StringComparison.Ordinal);

        if (wantsBootstrap)
        {
            return await BootstrapAsync(
                    http, antiforgery, credentials, logger, form, returnUrl, passwordAlreadySet, remote)
                .ConfigureAwait(false);
        }

        var candidate = form["password"].ToString();
        if (await credentials.VerifyPasswordAsync(candidate, http.RequestAborted).ConfigureAwait(false))
        {
            await SignInAsync(http).ConfigureAwait(false);

            logger.LogInformation(
                AuthenticationAudit.SignInSucceeded,
                "Operator sign-in succeeded from {RemoteAddress}.",
                remote);

            return Results.LocalRedirect(returnUrl);
        }

        // One message for "wrong password" and for "no password has been set yet, so nothing verifies",
        // because telling those two apart is useful to exactly one kind of caller.
        logger.LogWarning(
            AuthenticationAudit.SignInFailed,
            "Operator sign-in FAILED from {RemoteAddress}. A password was submitted and rejected; no session was created.",
            remote);

        return RenderLogin(
            http,
            antiforgery,
            setupRequired: !passwordAlreadySet,
            returnUrl,
            error: "That password was not accepted.");
    }

    private static async Task<IResult> BootstrapAsync(
        HttpContext http,
        IAntiforgery antiforgery,
        OperatorCredentialStore credentials,
        ILogger logger,
        IFormCollection form,
        string returnUrl,
        bool passwordAlreadySet,
        string remote)
    {
        // The check that makes first-run a bootstrap rather than a back door. It is repeated inside
        // OperatorCredentialStore.TrySetInitialPasswordAsync under a lock, so a request that slipped past
        // this one still writes nothing.
        if (passwordAlreadySet)
        {
            logger.LogWarning(
                AuthenticationAudit.InitialPasswordRefused,
                "First-run set-password submission from {RemoteAddress} REFUSED: an operator password already "
                + "exists, and the bootstrap flow is one-time. No session was created.",
                remote);

            return RenderLogin(
                http,
                antiforgery,
                setupRequired: false,
                returnUrl,
                error: "An operator password has already been set on this install. Sign in with it instead.");
        }

        var newPassword = form["newPassword"].ToString();
        var confirmPassword = form["confirmPassword"].ToString();

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return RenderLogin(http, antiforgery, setupRequired: true, returnUrl,
                error: "The two passwords did not match.");
        }

        if (newPassword.Length < OperatorCredentialStore.MinimumPasswordLength)
        {
            return RenderLogin(http, antiforgery, setupRequired: true, returnUrl,
                error: $"The operator password must be at least {OperatorCredentialStore.MinimumPasswordLength} characters.");
        }

        if (!await credentials.TrySetInitialPasswordAsync(newPassword, http.RequestAborted).ConfigureAwait(false))
        {
            logger.LogWarning(
                AuthenticationAudit.InitialPasswordRefused,
                "First-run set-password submission from {RemoteAddress} REFUSED at the store: a password was "
                + "set by another request first. No session was created.",
                remote);

            return RenderLogin(http, antiforgery, setupRequired: false, returnUrl,
                error: "An operator password has already been set on this install. Sign in with it instead.");
        }

        await SignInAsync(http).ConfigureAwait(false);

        logger.LogWarning(
            AuthenticationAudit.InitialPasswordSet,
            "The operator password was set for the first time from {RemoteAddress}. The first-run flow is now "
            + "closed and will not grant access again without this password.",
            remote);

        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> PostLogoutAsync(HttpContext http, ILoggerFactory loggerFactory)
    {
        // No antiforgery token is required here, and that is not an oversight: the auth cookie is
        // SameSite=Strict, so a cross-site POST never carries it and therefore never signs anyone out. The
        // worst a forged request can do is nothing.
        await http.SignOutAsync(OperatorAuthentication.SchemeName).ConfigureAwait(false);

        loggerFactory
            .CreateLogger(OperatorAuthentication.AuditLogCategory)
            .LogInformation(
                AuthenticationAudit.SignedOut,
                "Operator signed out from {RemoteAddress}.",
                RemoteAddress(http));

        return Results.LocalRedirect(OperatorAuthentication.LoginPath);
    }

    private static Task SignInAsync(HttpContext http)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, OperatorAuthentication.OperatorNameClaimValue)],
            OperatorAuthentication.SchemeName);

        return http.SignInAsync(
            OperatorAuthentication.SchemeName,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>
    /// Renders <see cref="LoginPage"/> with a freshly issued antiforgery token pair. Always 200: a 401 or 403
    /// here would be re-executed by <c>UseStatusCodePagesWithReExecute</c> and the operator would be shown a
    /// not-found page instead of the reason their sign-in failed.
    /// </summary>
    private static IResult RenderLogin(
        HttpContext http, IAntiforgery antiforgery, bool setupRequired, string returnUrl, string? error)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);

        return new RazorComponentResult<LoginPage>(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(LoginPage.SetupRequired)] = setupRequired,
            [nameof(LoginPage.AntiforgeryFieldName)] = tokens.FormFieldName,
            [nameof(LoginPage.AntiforgeryToken)] = tokens.RequestToken ?? string.Empty,
            [nameof(LoginPage.ReturnUrl)] = returnUrl,
            [nameof(LoginPage.ErrorMessage)] = error,
        });
    }

    /// <summary>
    /// Reduces anything a caller supplied to a safe, app-local path. Anything absolute, protocol-relative,
    /// backslash-smuggled, or pointing back at the login flow itself collapses to <c>"/"</c> — an open
    /// redirect on a login page is how a convincing credential-phishing chain starts.
    /// </summary>
    internal static string SanitizeReturnUrl(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate[0] != '/')
        {
            return "/";
        }

        // "//evil.example" and "/\evil.example" are both browser-absolute despite the leading slash.
        if (candidate.Length > 1 && (candidate[1] == '/' || candidate[1] == '\\'))
        {
            return "/";
        }

        if (candidate.StartsWith(OperatorAuthentication.LoginPath, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(OperatorAuthentication.LogoutPath, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return candidate;
    }

    private static string RemoteAddress(HttpContext http)
        => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
