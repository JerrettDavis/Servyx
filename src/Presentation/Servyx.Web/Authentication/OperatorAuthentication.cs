namespace Servyx.Web.Authentication;

/// <summary>
/// The names, routes and log-event identifiers that make up Servyx's cookie authentication, kept in one place
/// so the composition root, the endpoints, the UI and the tests cannot drift apart on a string literal.
/// </summary>
/// <remarks>
/// The scheme name and cookie mechanics predate multi-user accounts and are unchanged by their arrival — only
/// <em>who</em> gets authenticated and <em>what claims</em> they receive changed (see
/// <c>AuthenticationEndpoints.SignInAsync</c>, which now mints the real signed-in username and a
/// <see cref="System.Security.Claims.ClaimTypes.Role"/> claim instead of one constant identity for everyone).
/// </remarks>
public static class OperatorAuthentication
{
    /// <summary>The one authentication scheme this app defines. There is no second identity provider.</summary>
    public const string SchemeName = "ServyxOperator";

    /// <summary>The auth cookie's name. Host-only, no domain, so it never leaks to a sibling host.</summary>
    public const string CookieName = "servyx.auth";

    /// <summary>The only anonymous-reachable page in the application.</summary>
    public const string LoginPath = "/login";

    /// <summary>
    /// Where an authenticated caller who fails a page's own <c>[Authorize(Policy = ...)]</c> — e.g.
    /// <c>Servyx.Role.Admin</c> — lands, via <see cref="Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions.AccessDeniedPath"/>.
    /// Deliberately <em>not</em> <see cref="LoginPath"/>: the cookie handler redirects here with
    /// <see cref="ReturnUrlParameter"/> set to the page that refused them, and <c>AuthenticationEndpoints.GetLoginAsync</c>
    /// bounces an already-authenticated caller straight back to that same <c>returnUrl</c> — pointing
    /// <c>AccessDeniedPath</c> at <see cref="LoginPath"/> therefore ping-pongs forever between the two for
    /// every caller a role policy ever refuses. This was unreachable before any page declared a policy of its
    /// own; see <c>RoleAuthorization</c>'s own remarks for the increment that introduced the first one.
    /// </summary>
    public const string HomePath = "/";

    /// <summary>Where an authenticated session is ended.</summary>
    public const string LogoutPath = "/logout";

    /// <summary>The query-string parameter carrying the originally requested, always-local path.</summary>
    public const string ReturnUrlParameter = "returnUrl";

    /// <summary>The logger category every authentication audit event is written under.</summary>
    public const string AuditLogCategory = "Servyx.Web.Authentication.Audit";

    /// <summary>
    /// The actor label used when a caller's real, signed-in username genuinely cannot be resolved — a bUnit
    /// render (or any other context) with no cascading <c>AuthenticationState</c> at all. Behind the
    /// fallback policy this is no longer what an ordinary signed-in caller resolves to (see
    /// <c>AuthenticationEndpoints.SignInAsync</c>, which mints the real username), but every
    /// actor-attribution call site keeps this as its last-resort fallback rather than throwing.
    /// </summary>
    public const string OperatorNameClaimValue = "operator";

    /// <summary>
    /// How long a session lives without activity. Sliding, so an operator working continuously is not
    /// logged out mid-task, but a forgotten browser tab stops being a way in after a day.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);
}
