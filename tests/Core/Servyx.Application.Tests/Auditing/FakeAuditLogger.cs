using Servyx.Application.Auditing;
using Servyx.Domain.Entities;

namespace Servyx.Application.Tests.Auditing;

/// <summary>
/// A recording <see cref="IAuditLogger"/> fake, for tests that need to assert what got audited without
/// exercising the real, persistence-backed <see cref="AuditLogger"/>. Mirrors
/// <c>HostRegistrationServiceTests</c>' own fakes' "state-carrying, not a sequence of stubbed returns"
/// discipline.
/// </summary>
public sealed class FakeAuditLogger : IAuditLogger
{
    /// <summary>Every entry recorded so far, in call order.</summary>
    public List<AuditEntry> Entries { get; } = [];

    /// <inheritdoc />
    public Task RecordAsync(AuditEntry entry, CancellationToken ct = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordAsync(
        string actor,
        string action,
        string? targetType = null,
        string? targetId = null,
        string? details = null,
        CancellationToken ct = default)
    {
        Entries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            TimestampUtc = DateTimeOffset.UtcNow,
            Actor = actor,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Details = details,
        });
        return Task.CompletedTask;
    }
}
