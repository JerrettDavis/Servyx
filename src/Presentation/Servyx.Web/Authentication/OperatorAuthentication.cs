namespace Servyx.Web.Authentication;

/// <summary>
/// The names, routes and log-event identifiers that make up Servyx's single-operator cookie authentication,
/// kept in one place so the composition root, the endpoints, the UI and the tests cannot drift apart on a
/// string literal.
/// </summary>
public static class OperatorAuthentication
{
    /// <summary>The one authentication scheme this app defines. There is no second identity provider.</summary>
    public const string SchemeName = "ServyxOperator";

    /// <summary>The auth cookie's name. Host-only, no domain, so it never leaks to a sibling host.</summary>
    public const string CookieName = "servyx.auth";

    /// <summary>The only anonymous-reachable page in the application.</summary>
    public const string LoginPath = "/login";

    /// <summary>Where an authenticated session is ended.</summary>
    public const string LogoutPath = "/logout";

    /// <summary>The query-string parameter carrying the originally requested, always-local path.</summary>
    public const string ReturnUrlParameter = "returnUrl";

    /// <summary>The logger category every authentication audit event is written under.</summary>
    public const string AuditLogCategory = "Servyx.Web.Authentication.Audit";

    /// <summary>The claim type carrying the operator's display name.</summary>
    public const string OperatorNameClaimValue = "operator";

    /// <summary>
    /// How long a session lives without activity. Sliding, so an operator working continuously is not
    /// logged out mid-task, but a forgotten browser tab stops being a way in after a day.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);
}
