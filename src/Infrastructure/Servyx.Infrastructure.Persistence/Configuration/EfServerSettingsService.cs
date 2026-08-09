using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Entities;

namespace Servyx.Infrastructure.Persistence.Configuration;

/// <summary>
/// The durable <see cref="IServerSettingsService"/>, backed by the <c>ServerSettingValues</c> table via
/// <see cref="ServyxDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Takes an <see cref="IDbContextFactory{TContext}"/> rather than a <see cref="ServyxDbContext"/> directly,
/// following <c>EfServerRepository</c>'s and <c>EfServerDefinitionBindingStore</c>'s pattern exactly: this
/// type is registered singleton (its consumer is a Blazor component resolved once per circuit, not scoped
/// per request the way <see cref="ServyxDbContext"/> itself is), so a singleton cannot hold a scoped context
/// directly. The factory is itself singleton-safe and creates a short-lived context per call, one unit of
/// work each.
/// </para>
/// <para>
/// <strong>Storage keys on <see cref="ServerId"/>, never the container id — see
/// <see cref="IServerSettingsService"/>'s own remarks for why.</strong> <see cref="LoadAsync"/> resolves the
/// container id to a <see cref="ServerId"/> once and returns it in the snapshot; <see cref="SaveDesiredValueAsync"/>
/// takes that resolved id directly and never re-resolves a container id itself. The composite (ServerId, Key)
/// primary key <c>ServerSettingValueConfiguration</c> declares, together with its cascade-delete foreign key
/// to <c>Server</c>, is what actually prevents a forgotten-then-re-adopted container from resurrecting a
/// stale desired value: a fresh adopt mints a brand new <see cref="ServerId"/> (see
/// <c>ServerAdoptionService.AdoptAsync</c>), which starts with zero rows in this table, and forgetting the
/// old server discards its old rows outright via the cascade delete rather than leaving them to be silently
/// re-matched by anything.
/// </para>
/// </remarks>
public sealed class EfServerSettingsService : IServerSettingsService
{
    private readonly IDbContextFactory<ServyxDbContext> _contextFactory;
    private readonly TimeProvider _time;

    /// <summary>Creates a service that opens a short-lived context per call via <paramref name="contextFactory"/>.</summary>
    /// <param name="time">Supplies the recorded timestamp. Optional; defaults to <see cref="TimeProvider.System"/>.</param>
    public EfServerSettingsService(IDbContextFactory<ServyxDbContext> contextFactory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        _contextFactory = contextFactory;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ServerSettingsSnapshot?> LoadAsync(string containerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var server = await context.Servers.AsNoTracking()
            .SingleOrDefaultAsync(row => row.ContainerId == containerId, ct).ConfigureAwait(false);

        if (server is null)
        {
            // Untracked container: honestly "nothing to load", not an error — mirrors
            // IWriteGrantService.DescribeAsync's own null-for-untracked convention.
            return null;
        }

        var rows = await context.ServerSettingValues.AsNoTracking()
            .Where(row => row.ServerId == server.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var values = rows.ToDictionary(
            row => row.Key,
            row => new DesiredSettingValue(row.Key, row.Value, row.UpdatedBy, row.UpdatedAt),
            StringComparer.Ordinal);

        return new ServerSettingsSnapshot(server.Id, values);
    }

    /// <inheritdoc />
    public async Task<SaveDesiredValueResult> SaveDesiredValueAsync(
        ServerId serverId, string key, string? value, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var serverExists = await context.Servers.AsNoTracking()
            .AnyAsync(row => row.Id == serverId, ct).ConfigureAwait(false);

        if (!serverExists)
        {
            return new SaveDesiredValueResult(SaveDesiredValueOutcome.ServerNotFound, null);
        }

        // Tracked (not AsNoTracking) on purpose, matching EfServerRepository.SetWriteModeAsync's own
        // remarks: this is the branch that writes an existing row, so EF needs to observe the mutation.
        var existing = await context.ServerSettingValues
            .SingleOrDefaultAsync(row => row.ServerId == serverId && row.Key == key, ct).ConfigureAwait(false);

        var now = _time.GetUtcNow();

        // ServerSettingValue.Value is required (never null) — "no value recorded" is the row's absence, not
        // a null column. A caller passing null here means "the operator left the field blank"; that is
        // recorded as an explicit empty string rather than refused, so clearing a field is a normal save.
        var normalizedValue = value ?? string.Empty;

        if (existing is null)
        {
            existing = new ServerSettingValue
            {
                ServerId = serverId,
                Key = key,
                Value = normalizedValue,
                UpdatedBy = actor,
                UpdatedAt = now,
            };
            context.ServerSettingValues.Add(existing);
        }
        else
        {
            existing.Value = normalizedValue;
            existing.UpdatedBy = actor;
            existing.UpdatedAt = now;
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);

        return new SaveDesiredValueResult(
            SaveDesiredValueOutcome.Recorded,
            new DesiredSettingValue(key, normalizedValue, actor, now));
    }
}
