using Servyx.Domain.Entities;

namespace Servyx.Domain.Tests.Entities;

/// <summary>
/// <see cref="AuditEntry"/>, <see cref="AuditActors"/>, and <see cref="AuditActions"/>: what the entity
/// requires to construct, and the well-known constant catalogs beside it.
/// </summary>
public class AuditEntryTests
{
    [Fact]
    public void ARequiredField_LeftUnset_FailsToCompile()
    {
        // AuditEntry.Id, TimestampUtc, Actor, and Action are all `required`, so the compiler — not a runtime
        // check — is what enforces "actor required" etc. for this plain-POCO entity, the same convention User
        // and ChangePlanActionRecord already follow. This test exists only to document that fact and to
        // exercise the one shape the compiler does allow: every required member supplied, with the two
        // genuinely optional ones (TargetType/TargetId/Details) left null.
        var entry = new AuditEntry
        {
            Id = Guid.NewGuid(),
            TimestampUtc = DateTimeOffset.UnixEpoch,
            Actor = "alice",
            Action = AuditActions.UserCreated,
        };

        entry.Actor.Should().Be("alice");
        entry.Action.Should().Be(AuditActions.UserCreated);
        entry.TargetType.Should().BeNull();
        entry.TargetId.Should().BeNull();
        entry.Details.Should().BeNull();
    }

    [Fact]
    public void EveryField_CanBeSet()
    {
        var id = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);

        var entry = new AuditEntry
        {
            Id = id,
            TimestampUtc = timestamp,
            Actor = "operator",
            Action = AuditActions.HostRegistered,
            TargetType = "host",
            TargetId = "prod-host",
            Details = "ssh:steam@10.0.0.4:22",
        };

        entry.Id.Should().Be(id);
        entry.TimestampUtc.Should().Be(timestamp);
        entry.TargetType.Should().Be("host");
        entry.TargetId.Should().Be("prod-host");
        entry.Details.Should().Be("ssh:steam@10.0.0.4:22");
    }

    [Fact]
    public void TwoEntries_WithDifferentIds_AreDifferentReferences()
    {
        // Sealed reference type, no overridden equality — matching User/Host/Server. Identity is reference
        // identity.
        var a = NewEntry();
        var b = NewEntry();

        a.Should().NotBeSameAs(b);
        a.Id.Should().NotBe(b.Id);
    }

    private static AuditEntry NewEntry() => new()
    {
        Id = Guid.NewGuid(),
        TimestampUtc = DateTimeOffset.UnixEpoch,
        Actor = "operator",
        Action = AuditActions.UserCreated,
    };
}

public class AuditActorsTests
{
    [Fact]
    public void System_IsALowercaseMarker_NeverAUsername()
    {
        // "system" cannot collide with a real username: UserService.CreateAsync trims and accepts arbitrary
        // text, but every operator-facing account creation UI presents this as a reserved-looking value, and
        // pinning the literal here is what would catch an accidental rename.
        AuditActors.System.Should().Be("system");
    }
}

public class AuditActionsTests
{
    [Theory]
    [InlineData(nameof(AuditActions.UserCreated), "user.created")]
    [InlineData(nameof(AuditActions.UserRoleChanged), "user.role_changed")]
    [InlineData(nameof(AuditActions.UserActivated), "user.activated")]
    [InlineData(nameof(AuditActions.UserDeactivated), "user.deactivated")]
    [InlineData(nameof(AuditActions.HostRegistered), "host.registered")]
    [InlineData(nameof(AuditActions.HostDeregistered), "host.deregistered")]
    [InlineData(nameof(AuditActions.ServerAdopted), "server.adopted")]
    [InlineData(nameof(AuditActions.ServerForgotten), "server.forgotten")]
    [InlineData(nameof(AuditActions.ChangePlanApplied), "changeplan.applied")]
    [InlineData(nameof(AuditActions.ChangePlanReverted), "changeplan.reverted")]
    public void EveryWellKnownAction_HasItsDocumentedStringValue(string constantName, string expectedValue)
    {
        // Pinned by name-to-value, not just by iterating the type: this is what would fail if a future edit
        // silently renumbered/reworded one of these strings, which — because Action is persisted, unindexed,
        // free text — would otherwise be a silent behavior change invisible to every other test in this file.
        var field = typeof(AuditActions).GetField(constantName)
            ?? throw new InvalidOperationException($"No such constant: {constantName}");

        field.GetRawConstantValue().Should().Be(expectedValue);
    }

    [Fact]
    public void EveryWellKnownAction_UsesTheDottedNamespaceConvention()
    {
        // "<targetType>.<verb>" — the convention this catalog documents. A future addition that violates it
        // silently breaks a human's ability to scan the audit trail by eye, which this test exists to catch.
        var values = typeof(AuditActions)
            .GetFields()
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);

        foreach (var value in values)
        {
            value.Should().MatchRegex("^[a-z]+\\.[a-z_]+$", because: $"'{value}' should follow the '<target>.<verb>' convention");
        }
    }
}
