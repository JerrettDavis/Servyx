using Servyx.Domain.Backups;

namespace Servyx.Application.Tests.Backups;

/// <summary>
/// A hand-written <see cref="IBackupProvider"/> that records every call and answers from scripted state.
/// </summary>
/// <remarks>
/// Hand-written rather than substituted because most of these tests assert <em>non-invocation</em> — that
/// previewing a restore never restores, and that a dry run never deletes. A counter that starts at zero
/// and is only ever incremented by the real member is the clearest possible evidence of that, and it can
/// be misconfigured in exactly one place.
/// </remarks>
public sealed class RecordingBackupProvider : IBackupProvider
{
    private readonly List<BackupArtifact> _artifacts = [];

    /// <summary>Artifact ids <see cref="PruneAsync"/> reports, whatever their ownership.</summary>
    public List<string> PruneReturns { get; } = [];

    /// <summary>Foreign artifacts <see cref="PruneAsync"/> claims to have skipped.</summary>
    public int PruneSkippedForeign { get; set; }

    /// <summary>Thrown by <see cref="CreateAsync"/> when set.</summary>
    public Exception? CreateThrows { get; set; }

    /// <summary>Thrown by <see cref="ListAsync"/> when set.</summary>
    public Exception? ListThrows { get; set; }

    /// <summary>Thrown by <see cref="RestoreAsync"/> when set.</summary>
    public Exception? RestoreThrows { get; set; }

    /// <summary>Paths the next <see cref="PlanRestoreAsync"/> reports as affected.</summary>
    public List<string> AffectedPaths { get; } = ["/palworld/Pal/Saved/SaveGames/0/world/Level.sav"];

    /// <summary>How many times <see cref="CreateAsync"/> was called.</summary>
    public int CreateCalls { get; private set; }

    /// <summary>How many times <see cref="PlanRestoreAsync"/> was called.</summary>
    public int PlanRestoreCalls { get; private set; }

    /// <summary>How many times <see cref="RestoreAsync"/> — the member that overwrites data — was called.</summary>
    public int RestoreCalls { get; private set; }

    /// <summary>How many times <see cref="PruneAsync"/> was called with <c>dryRun: true</c>.</summary>
    public int DryRunPruneCalls { get; private set; }

    /// <summary>How many times <see cref="PruneAsync"/> — the member that deletes — was called for real.</summary>
    public int LivePruneCalls { get; private set; }

    /// <summary>Adds an artifact to the listing.</summary>
    /// <param name="id">The artifact id.</param>
    /// <param name="ownership">Who owns it.</param>
    public RecordingBackupProvider With(string id, BackupOwnership ownership)
    {
        _artifacts.Add(new BackupArtifact(id, ownership, DateTimeOffset.UnixEpoch.AddDays(_artifacts.Count), 1024, $"/palworld/{id}"));
        return this;
    }

    /// <inheritdoc />
    public Task<BackupArtifact> CreateAsync(string serverId, CancellationToken ct = default)
    {
        CreateCalls++;

        if (CreateThrows is not null)
        {
            return Task.FromException<BackupArtifact>(CreateThrows);
        }

        var artifact = new BackupArtifact(
            $"{serverId}::/palworld/servyx-backups/servyx-new.tar.gz",
            BackupOwnership.Servyx,
            DateTimeOffset.UnixEpoch.AddDays(99),
            2048,
            "/palworld/servyx-backups/servyx-new.tar.gz");

        _artifacts.Add(artifact);
        return Task.FromResult(artifact);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupArtifact>> ListAsync(string serverId, CancellationToken ct = default) =>
        ListThrows is not null
            ? Task.FromException<IReadOnlyList<BackupArtifact>>(ListThrows)
            : Task.FromResult<IReadOnlyList<BackupArtifact>>([.. _artifacts]);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> InspectAsync(string backupId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(["data/Pal/Saved/SaveGames/0/world/Level.sav"]);

    /// <inheritdoc />
    public Task<RestorePlan> PlanRestoreAsync(string backupId, CancellationToken ct = default)
    {
        PlanRestoreCalls++;
        return Task.FromResult(new RestorePlan($"restore-{PlanRestoreCalls}", backupId, [.. AffectedPaths]));
    }

    /// <inheritdoc />
    public Task RestoreAsync(string restorePlanId, CancellationToken ct = default)
    {
        RestoreCalls++;
        return RestoreThrows is not null ? Task.FromException(RestoreThrows) : Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PruneResult> PruneAsync(string serverId, RetentionPolicy policy, bool dryRun, CancellationToken ct = default)
    {
        if (dryRun)
        {
            DryRunPruneCalls++;
        }
        else
        {
            LivePruneCalls++;
        }

        return Task.FromResult(new PruneResult([.. PruneReturns], PruneSkippedForeign));
    }
}

/// <summary>
/// Stands in for the infrastructure's <c>RestorePlanStaleException</c>, which the Application layer
/// matches by type <em>name</em> because it does not reference the assembly that declares it.
/// </summary>
public sealed class RestorePlanStaleException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The refusal message.</param>
    public RestorePlanStaleException(string message) : base(message)
    {
    }
}
