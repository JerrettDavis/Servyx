using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Definitions;
using Servyx.Infrastructure.Persistence.Entities;

namespace Servyx.Infrastructure.Persistence.Definitions;

/// <summary>
/// The durable <see cref="IServerDefinitionBindingStore"/>, backed by the <c>ServerDefinitionBindings</c>
/// table via <see cref="ServyxDbContext"/>.
/// </summary>
/// <remarks>
/// Takes an <see cref="IDbContextFactory{TContext}"/> rather than a <see cref="ServyxDbContext"/> directly,
/// unlike <c>EfProvisioningLedger</c>. <c>ServerQueryService</c> — the sole consumer — is a process-lifetime
/// singleton (it is resolved once per discovery pass across the whole app, not per web request), and
/// <see cref="ServyxDbContext"/> is registered scoped; a singleton cannot hold a scoped dependency. The
/// factory is itself singleton-safe and creates a short-lived context per call, one unit of work each,
/// which also happens to match the "one unit of work per call" discipline <c>EfProvisioningLedger</c>
/// documents for its own, differently-lifetimed case.
/// </remarks>
public sealed class EfServerDefinitionBindingStore : IServerDefinitionBindingStore
{
    private readonly IDbContextFactory<ServyxDbContext> _contextFactory;

    /// <summary>Creates a store that opens a short-lived context per call via <paramref name="contextFactory"/>.</summary>
    public EfServerDefinitionBindingStore(IDbContextFactory<ServyxDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<ServerDefinitionBinding?> TryGetAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Untracked: a read-only lookup that ServerQueryService performs on every discovery pass, over a
        // context that is disposed at the end of this call anyway.
        var record = await context.ServerDefinitionBindings
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.ServerId == serverId, ct)
            .ConfigureAwait(false);

        return record is null ? null : ToDomain(record);
    }

    /// <inheritdoc />
    public async Task SaveAsync(ServerDefinitionBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await context.ServerDefinitionBindings
            .SingleOrDefaultAsync(row => row.ServerId == binding.ServerId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            context.ServerDefinitionBindings.Add(ToRecord(binding));
        }
        else
        {
            existing.State = binding.State;
            existing.DefinitionId = binding.Definition?.Id;
            existing.DefinitionContentHash = binding.Definition?.ContentHash;
            existing.DefinitionSourceId = binding.Definition?.SourceId;
            existing.DefinitionSourcePath = binding.Definition?.SourcePath;
            existing.CandidateDefinitionIds = binding.CandidateDefinitionIds;
            existing.UpdatedAt = binding.UpdatedAt;
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await context.ServerDefinitionBindings
            .SingleOrDefaultAsync(row => row.ServerId == serverId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return;
        }

        context.ServerDefinitionBindings.Remove(existing);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static ServerDefinitionBindingRecord ToRecord(ServerDefinitionBinding binding) => new()
    {
        ServerId = binding.ServerId,
        State = binding.State,
        DefinitionId = binding.Definition?.Id,
        DefinitionContentHash = binding.Definition?.ContentHash,
        DefinitionSourceId = binding.Definition?.SourceId,
        DefinitionSourcePath = binding.Definition?.SourcePath,
        CandidateDefinitionIds = binding.CandidateDefinitionIds,
        UpdatedAt = binding.UpdatedAt,
    };

    private static ServerDefinitionBinding ToDomain(ServerDefinitionBindingRecord record) => new(
        record.ServerId,
        record.State,
        record.DefinitionId is null || record.DefinitionContentHash is null || record.DefinitionSourceId is null
            ? null
            : new GameDefinitionRef(record.DefinitionId, record.DefinitionContentHash, record.DefinitionSourceId, record.DefinitionSourcePath),
        record.CandidateDefinitionIds,
        record.UpdatedAt);
}
