using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Servyx.Domain.Entities;
using Servyx.Web.Authentication;

namespace Servyx.Web.Tests.Authentication;

/// <summary>
/// Tests for <see cref="MinimumRoleRequirement"/>/<see cref="MinimumRoleAuthorizationHandler"/> — the
/// role-minimum comparison backing the <c>Servyx.Role.*</c> policies. Nothing in the application applies one
/// of these policies to a page yet (see <see cref="RoleAuthorization"/>'s own remarks), so this file is the
/// only place the comparison itself is exercised end to end.
/// </summary>
public class RoleAuthorizationTests
{
    private static async Task<bool> SucceedsAsync(UserRole minimum, ClaimsPrincipal principal)
    {
        var requirement = new MinimumRoleRequirement(minimum);
        var context = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await new MinimumRoleAuthorizationHandler().HandleAsync(context);

        return context.HasSucceeded;
    }

    private static ClaimsPrincipal PrincipalWithRole(string? role)
    {
        var claims = role is null ? [] : new[] { new Claim(ClaimTypes.Role, role) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));
    }

    [Theory]
    [InlineData(nameof(UserRole.Admin), UserRole.Viewer)]
    [InlineData(nameof(UserRole.Admin), UserRole.Operator)]
    [InlineData(nameof(UserRole.Admin), UserRole.Admin)]
    [InlineData(nameof(UserRole.Operator), UserRole.Viewer)]
    [InlineData(nameof(UserRole.Operator), UserRole.Operator)]
    public async Task A_role_at_or_above_the_minimum_satisfies_the_requirement(string actualRole, UserRole minimum)
    {
        (await SucceedsAsync(minimum, PrincipalWithRole(actualRole))).Should().BeTrue(
            $"'{actualRole}' is at or above the '{minimum}' minimum, matching UserRole's gapped-value ordering");
    }

    [Theory]
    [InlineData(nameof(UserRole.Viewer), UserRole.Operator)]
    [InlineData(nameof(UserRole.Viewer), UserRole.Admin)]
    [InlineData(nameof(UserRole.Operator), UserRole.Admin)]
    public async Task A_role_below_the_minimum_fails_the_requirement(string actualRole, UserRole minimum)
    {
        (await SucceedsAsync(minimum, PrincipalWithRole(actualRole))).Should().BeFalse(
            $"'{actualRole}' is below the '{minimum}' minimum");
    }

    [Fact]
    public async Task No_role_claim_at_all_fails_the_requirement_rather_than_matching_every_role()
    {
        (await SucceedsAsync(UserRole.Viewer, PrincipalWithRole(role: null))).Should().BeFalse(
            "an absent role claim must never be read as satisfying even the lowest-privilege policy — a " +
            "missing claim is a fail-closed case, not a wildcard");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SuperAdmin")]
    [InlineData("admin")]
    public async Task An_unparseable_role_claim_fails_the_requirement(string role)
    {
        (await SucceedsAsync(UserRole.Viewer, PrincipalWithRole(role))).Should().BeFalse(
            $"'{role}' does not parse to a UserRole (case-sensitive — 'admin' is not 'Admin'), and must not " +
            "be read as satisfying any policy");
    }
}
