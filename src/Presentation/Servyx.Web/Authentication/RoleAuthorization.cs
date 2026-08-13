using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Servyx.Domain.Entities;

namespace Servyx.Web.Authentication;

/// <summary>
/// The minimum-<see cref="UserRole"/> policies this process defines, and the requirement/handler pair that
/// backs them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Foundation only — nothing is gated by these policies yet.</strong> This increment wires the login
/// pipeline to mint a <see cref="ClaimTypes.Role"/> claim carrying the signed-in user's <see cref="UserRole"/>,
/// and registers one authorization policy per role here so a later increment can gate a specific page
/// (starting with the not-yet-built Users management page, which needs <see cref="Admin"/>) by adding
/// <c>[Authorize(Policy = RoleAuthorization.Admin)]</c> — or the routable-component equivalent — and nothing
/// else. No page in this process carries one of these policies today; every page still inherits protection
/// exclusively from the <see cref="AuthorizationOptions.FallbackPolicy"/> installed by
/// <see cref="AuthenticationServiceCollectionExtensions.AddServyxOperatorAuthentication"/>, exactly as before
/// this increment.
/// </para>
/// <para>
/// <strong>Minimum, not exact, role.</strong> <see cref="UserRole"/>'s own remarks explain why its values are
/// gapped (<c>Viewer=0, Operator=10, Admin=20</c>): so a numeric comparison such as <c>role &gt;= Operator</c>
/// keeps working if a role is inserted between two existing ones later. <see cref="MinimumRoleRequirement"/>
/// is exactly that comparison, wrapped as an <see cref="IAuthorizationRequirement"/> — a caller whose role
/// claim parses to a value at or above the policy's <see cref="MinimumRoleRequirement.MinimumRole"/> succeeds,
/// which is what lets <see cref="Admin"/> imply <see cref="Operator"/> and <see cref="Viewer"/> access without
/// three separate, independently-maintained requirement types.
/// </para>
/// </remarks>
public static class RoleAuthorization
{
    /// <summary>The policy name requiring at least <see cref="UserRole.Viewer"/> — i.e., any signed-in user.</summary>
    public const string Viewer = "Servyx.Role.Viewer";

    /// <summary>The policy name requiring at least <see cref="UserRole.Operator"/>.</summary>
    public const string Operator = "Servyx.Role.Operator";

    /// <summary>The policy name requiring at least <see cref="UserRole.Admin"/>.</summary>
    public const string Admin = "Servyx.Role.Admin";

    /// <summary>
    /// Registers <see cref="Viewer"/>, <see cref="Operator"/> and <see cref="Admin"/> against
    /// <paramref name="options"/>.
    /// </summary>
    /// <remarks>
    /// Mutates only <paramref name="options"/> — never the container. This is called from inside the
    /// <c>services.AddAuthorization(options => ...)</c> configure delegate, which the options framework may
    /// invoke lazily, well after <c>IServiceCollection.BuildServiceProvider()</c> has made the collection
    /// read-only; registering a service from in here (as an earlier version of this method did, via
    /// <c>services.AddSingleton&lt;IAuthorizationHandler, ...&gt;</c>) throws
    /// <see cref="InvalidOperationException"/> the moment anything resolves
    /// <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider"/> — which every request
    /// does. <see cref="AuthenticationServiceCollectionExtensions.AddServyxOperatorAuthentication"/> registers
    /// <see cref="MinimumRoleAuthorizationHandler"/> directly, outside this delegate, for exactly that reason.
    /// </remarks>
    public static void AddServyxRolePolicies(this AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddPolicy(Viewer, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new MinimumRoleRequirement(UserRole.Viewer)));

        options.AddPolicy(Operator, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new MinimumRoleRequirement(UserRole.Operator)));

        options.AddPolicy(Admin, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new MinimumRoleRequirement(UserRole.Admin)));
    }
}

/// <summary>An authorization requirement satisfied by any role at or above <see cref="MinimumRole"/>.</summary>
/// <param name="MinimumRole">The least-privileged role that satisfies this requirement.</param>
public sealed record MinimumRoleRequirement(UserRole MinimumRole) : IAuthorizationRequirement;

/// <summary>
/// Evaluates <see cref="MinimumRoleRequirement"/> against the caller's <see cref="ClaimTypes.Role"/> claim,
/// minted by <c>AuthenticationEndpoints.SignInAsync</c> from the signed-in <c>User.Role</c>.
/// </summary>
/// <remarks>
/// A missing, blank, or unparseable role claim fails the requirement rather than succeeding — an absent or
/// malformed claim must never be read as "every role", the same fail-closed posture every credential check in
/// this codebase takes.
/// </remarks>
public sealed class MinimumRoleAuthorizationHandler : AuthorizationHandler<MinimumRoleRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, MinimumRoleRequirement requirement)
    {
        var roleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value;

        if (!string.IsNullOrWhiteSpace(roleClaim)
            && Enum.TryParse<UserRole>(roleClaim, ignoreCase: false, out var role)
            && role >= requirement.MinimumRole)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
