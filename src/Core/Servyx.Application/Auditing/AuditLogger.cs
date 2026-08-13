using Microsoft.Extensions.Logging;
using Servyx.Domain.Auditing;
using Servyx.Domain.Entities;

namespace Servyx.Application.Auditing;

/// <summary>
/// <see cref="IAuditLogger"/> implementation, backed by <see cref="IAuditEntryRepository"/>.
/// </summary>
/// <remarks>
/// <strong>A write failure here is swallowed, logged, and never propagated.</strong> Every call site this
/// logger is wired into (<c>UserService</c>, <c>HostRegistrationService</c>, <c>ServerAdoptionService</c>,
/// <c>PlanExecutor</c>) calls <see cref="RecordAsync(AuditEntry, CancellationToken)"/> AFTER its own write has
/// already succeeded — the audited action is not conditioned on the audit row landing. A database outage that
/// takes down audit logging must not also take down user creation, host registration, or a configuration
/// apply; it should be visible in the log, not turned into a user-facing failure of an unrelated write. The
/// one exception is <see cref="OperationCanceledException"/>, which is allowed to propagate exactly as every
/// other method in this codebase's application-layer services does.
/// </remarks>
public sealed class AuditLogger : IAuditLogger
{
    private readonly IAuditEntryRepository _repository;
    private readonly ILogger<AuditLogger> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates an <see cref="AuditLogger"/>.</summary>
    /// <param name="timeProvider">Clock used to stamp <see cref="AuditEntry.TimestampUtc"/>. Defaults to <see cref="TimeProvider.System"/>.</param>
    public AuditLogger(IAuditEntryRepository repository, ILogger<AuditLogger> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task RecordAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            await _repository.AddAsync(entry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Logged, not rethrown — see this type's own remarks. The action this entry describes already
            // happened; losing the breadcrumb is a lesser failure than rolling back or failing a write that
            // otherwise succeeded.
            _logger.LogWarning(
                ex,
                "Failed to record an audit entry (actor '{Actor}', action '{Action}', target {TargetType}/{TargetId}). "
                + "The action itself was not affected.",
                entry.Actor,
                entry.Action,
                entry.TargetType,
                entry.TargetId);
        }
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
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        return RecordAsync(
            new AuditEntry
            {
                Id = Guid.NewGuid(),
                TimestampUtc = _timeProvider.GetUtcNow(),
                Actor = actor,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Details = details,
            },
            ct);
    }
}
