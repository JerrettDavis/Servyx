using Servyx.Domain.Common;
using Servyx.Domain.Entities;

namespace Servyx.Domain.Tests.Entities;

/// <summary>
/// <see cref="User"/> and <see cref="UserRole"/>: what the entity requires to construct, and the invariants
/// <see cref="UserRole"/>'s numbering is meant to hold.
/// </summary>
public class UserTests
{
    [Fact]
    public void ARequiredField_LeftUnset_FailsToCompile()
    {
        // User.Id, Username, PasswordHash, Role, IsActive, and CreatedAt are all `required`, so the compiler
        // — not a runtime check — is what enforces "username required" etc. for this plain-POCO entity, the
        // same convention Host and Server already follow. This test exists only to document that fact and to
        // exercise the one shape the compiler does allow: every required member supplied.
        var user = new User
        {
            Id = UserId.New(),
            Username = "alice",
            PasswordHash = "PBKDF2-SHA256$600000$c2FsdA==$a2V5$",
            Role = UserRole.Operator,
            IsActive = true,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

        user.Username.Should().Be("alice");
        user.Role.Should().Be(UserRole.Operator);
        user.IsActive.Should().BeTrue();
    }

    [Fact]
    public void TwoUsers_WithDifferentIds_AreDifferentReferences()
    {
        // User is a sealed reference type with no overridden equality (matching Host/Server), so identity is
        // reference identity — this pins that down rather than leaving it to accident.
        var a = NewUser();
        var b = NewUser();

        a.Should().NotBeSameAs(b);
        a.Id.Should().NotBe(b.Id);
    }

    private static User NewUser() => new()
    {
        Id = UserId.New(),
        Username = "user-" + Guid.NewGuid(),
        PasswordHash = "PBKDF2-SHA256$600000$c2FsdA==$a2V5$",
        Role = UserRole.Viewer,
        IsActive = true,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };
}

public class UserRoleTests
{
    [Fact]
    public void EveryDefinedRole_HasTheExpectedOrdinal()
    {
        // Pinned explicitly: these values are what's persisted (as names, not ordinals — see
        // UserConfiguration), so pinning the numbers here is about the numbering's OWN contract — gapped so a
        // new role can be inserted later without renumbering anything that already shipped — not about
        // storage compatibility.
        ((int)UserRole.Viewer).Should().Be(0);
        ((int)UserRole.Operator).Should().Be(10);
        ((int)UserRole.Admin).Should().Be(20);
    }

    [Fact]
    public void TheDefinedRoles_AreOrderedLeastToMostPrivileged()
    {
        // Not a runtime-enforced invariant — nothing stops a caller comparing role >= Operator today — but the
        // ordering is deliberate and this test is what would fail if a future edit broke it.
        ((int)UserRole.Viewer).Should().BeLessThan((int)UserRole.Operator);
        ((int)UserRole.Operator).Should().BeLessThan((int)UserRole.Admin);
    }

    [Fact]
    public void ThereAreGaps_BetweenEveryDefinedRole()
    {
        // The gaps are the point: room to insert a role between two existing ones later without shifting
        // every value that already shipped in a persisted (name-based, but still numerically-ordered-in-code)
        // enum.
        (((int)UserRole.Operator) - ((int)UserRole.Viewer)).Should().BeGreaterThan(1);
        (((int)UserRole.Admin) - ((int)UserRole.Operator)).Should().BeGreaterThan(1);
    }

    [Theory]
    [InlineData(UserRole.Viewer, "Viewer")]
    [InlineData(UserRole.Operator, "Operator")]
    [InlineData(UserRole.Admin, "Admin")]
    public void EveryDefinedRole_RoundTripsThroughItsName(UserRole role, string expectedName)
    {
        // What actually gets persisted (UserConfiguration stores the name, not the ordinal) — parsing back
        // from that name must reproduce the exact same value.
        role.ToString().Should().Be(expectedName);
        Enum.Parse<UserRole>(expectedName).Should().Be(role);
    }
}
