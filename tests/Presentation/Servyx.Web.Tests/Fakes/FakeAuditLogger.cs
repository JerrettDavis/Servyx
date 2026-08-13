using Servyx.Application.Auditing;
using Servyx.Domain.Entities;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// A recording <see cref="IAuditLogger"/> fake for bUnit/service tests that construct a real
/// <c>UserService</c>/<c>HostRegistrationService</c> but do not need to exercise the real, persistence-backed
/// <c>AuditLogger</c>. Mirrors <see cref="FakeUserRepository"/>'s own "state-carrying" discipline.
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
