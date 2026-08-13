using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Servyx.Application.Users;
using Servyx.Domain.Entities;
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
/// <para>
/// <strong>Sign-in is username + password, verified against <see cref="IUserService"/>.</strong> The
/// pre-multi-user single shared operator password (<see cref="OperatorCredentialStore"/>) is no longer
/// consulted here at all — it survives only as the source an upgrading install's password is migrated from,
/// once, at startup (see <c>UserBootstrapMigration</c>), and as the backing store the settings page's
/// self-service password rotation used to write to before this increment.
/// </para>
/// </remarks>
public static class AuthenticationEndpoints
{
    /// <summary>
    /// Guards the one-time first-run account creation the same way
    /// <c>OperatorCredentialStore.TrySetInitialPasswordAsync</c> guarded the single shared password: a check
    /// and a write happening under one lock, so two simultaneous first-run submissions cannot both create an
    /// account.
    /// </summary>
    private static readonly SemaphoreSlim BootstrapLock = new(1, 1);

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
        IUserService users)
    {
        var returnUrl = SanitizeReturnUrl(http.Request.Query[OperatorAuthentication.ReturnUrlParameter]);

        // Already signed in: there is nothing to ask for, and leaving the form up would invite the caller to
        // re-enter a credential they did not need to.
        if (http.User.Identity?.IsAuthenticated == true)
        {
            return Results.LocalRedirect(returnUrl);
        }

        var setupRequired = await NoAccountsExistAsync(users, http.RequestAborted).ConfigureAwait(false);

        return RenderLogin(http, antiforgery, setupRequired, returnUrl, error: null);
    }

    private static async Task<IResult> PostLoginAsync(
        HttpContext http,
        IAntiforgery antiforgery,
        IUserService users,
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
        var accountsAlreadyExist = !await NoAccountsExistAsync(users, http.RequestAborted).ConfigureAwait(false);

        var wantsBootstrap = string.Equals(
            form["intent"], LoginPage.SetPasswordIntent, StringComparison.Ordinal);

        if (wantsBootstrap)
        {
            return await BootstrapAsync(
                    http, antiforgery, users, logger, form, returnUrl, accountsAlreadyExist, remote)
                .ConfigureAwait(false);
        }

        var username = form["username"].ToString();
        var candidate = form["password"].ToString();

        if (await users.VerifyPasswordAsync(username, candidate, http.RequestAborted).ConfigureAwait(false))
        {
            // VerifyPasswordAsync only reports true/false; the role claim needs the row itself. A concurrent
            // deactivation between the two reads is the only way this comes back null despite VerifyPasswordAsync
            // having just succeeded — fail closed rather than sign in with no role claim.
            var user = await users.TryGetByUsernameAsync(username, http.RequestAborted).ConfigureAwait(false);
            if (user is not null)
            {
                await SignInAsync(http, user.Username, user.Role).ConfigureAwait(false);

                logger.LogInformation(
                    AuthenticationAudit.SignInSucceeded,
                    "Sign-in succeeded for '{Username}' from {RemoteAddress}.",
                    user.Username,
                    remote);

                return Results.LocalRedirect(returnUrl);
            }
        }

        // One message whether the username is unknown, the account is deactivated, or the password is wrong,
        // because telling those apart is useful to exactly one kind of caller.
        logger.LogWarning(
            AuthenticationAudit.SignInFailed,
            "Sign-in FAILED for '{Username}' from {RemoteAddress}. No session was created.",
            username,
            remote);

        return RenderLogin(
            http,
            antiforgery,
            setupRequired: !accountsAlreadyExist,
            returnUrl,
            error: "That username or password was not accepted.");
    }

    private static async Task<IResult> BootstrapAsync(
        HttpContext http,
        IAntiforgery antiforgery,
        IUserService users,
        ILogger logger,
        IFormCollection form,
        string returnUrl,
        bool accountsAlreadyExist,
        string remote)
    {
        // The check that makes first-run a bootstrap rather than a back door. Repeated inside the lock below,
        // the same "check and write under one lock" discipline OperatorCredentialStore.TrySetInitialPasswordAsync
        // used for the single shared password, so a request that slipped past this one still creates nothing.
        if (accountsAlreadyExist)
        {
            logger.LogWarning(
                AuthenticationAudit.InitialPasswordRefused,
                "First-run account-creation submission from {RemoteAddress} REFUSED: an account already exists "
                + "on this install, and the bootstrap flow is one-time. No session was created.",
                remote);

            return RenderLogin(
                http,
                antiforgery,
                setupRequired: false,
                returnUrl,
                error: "An account has already been set up on this install. Sign in with it instead.");
        }

        var username = form["username"].ToString();
        var newPassword = form["newPassword"].ToString();
        var confirmPassword = form["confirmPassword"].ToString();

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return RenderLogin(http, antiforgery, setupRequired: true, returnUrl,
                error: "The two passwords did not match.");
        }

        CreateUserResult result;
        await BootstrapLock.WaitAsync(http.RequestAborted).ConfigureAwait(false);
        try
        {
            if ((await users.ListAsync(http.RequestAborted).ConfigureAwait(false)).Count > 0)
            {
                logger.LogWarning(
                    AuthenticationAudit.InitialPasswordRefused,
                    "First-run account-creation submission from {RemoteAddress} REFUSED at the service: an "
                    + "account was created by another request first. No session was created.",
                    remote);

                return RenderLogin(http, antiforgery, setupRequired: false, returnUrl,
                    error: "An account has already been set up on this install. Sign in with it instead.");
            }

            result = await users
                .CreateAsync(username, newPassword, UserRole.Admin, actor: "servyx.web/bootstrap", http.RequestAborted)
                .ConfigureAwait(false);
        }
        finally
        {
            BootstrapLock.Release();
        }

        if (result.Outcome != CreateUserOutcome.Created)
        {
            return RenderLogin(http, antiforgery, setupRequired: true, returnUrl, error: result.Detail);
        }

        await SignInAsync(http, username.Trim(), UserRole.Admin).ConfigureAwait(false);

        logger.LogWarning(
            AuthenticationAudit.InitialPasswordSet,
            "The first account on this install ('{Username}', role Admin) was created from {RemoteAddress}. "
            + "The first-run flow is now closed and will not create another account without one already existing.",
            username.Trim(),
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
                "Signed out from {RemoteAddress}.",
                RemoteAddress(http));

        return Results.LocalRedirect(OperatorAuthentication.LoginPath);
    }

    private static Task SignInAsync(HttpContext http, string username, UserRole role)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, role.ToString()),
            ],
            OperatorAuthentication.SchemeName);

        return http.SignInAsync(
            OperatorAuthentication.SchemeName,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>Whether this install has no accounts yet, in which case <c>/login</c> offers the first-run form.</summary>
    private static async Task<bool> NoAccountsExistAsync(IUserService users, CancellationToken ct) =>
        (await users.ListAsync(ct).ConfigureAwait(false)).Count == 0;

    /// <summary>
    /// Renders <see cref="LoginPage"/> with a freshly issued antiforgery token pair. Always 200: a 401 or 403
    /// here would be re-executed by <c>UseStatusCodePagesWithReExecute</c> and the caller would be shown a
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
