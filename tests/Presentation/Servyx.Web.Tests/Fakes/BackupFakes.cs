using Microsoft.Extensions.Logging;
using Servyx.Application.Backups;
using Servyx.Domain.Backups;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// An <see cref="IDashboardDataService"/> whose <c>GetAllBackupsWithStatusAsync</c> answer is fixed by the
/// test. Exists for the closed-gate (<c>Servyx:Provisioning:Enabled</c> off) branch of the Backups page,
/// which reaches exactly that one member — every other member throws, so a page that started reaching for
/// more of this fake than intended fails loudly rather than silently answering with invented data.
/// </summary>
/// <remarks>
/// Web.Models types are referenced fully qualified throughout, deliberately not via a <c>using</c>: this
/// file also builds <see cref="Servyx.Domain.Backups.BackupOwnership"/> artifacts for
/// <see cref="FakeBackupDashboard"/> and <see cref="ScriptedBackupProvider"/> below, and
/// <c>Servyx.Web.Models</c> declares its own, differently-cased <c>BackupOwnership</c> — importing both
/// would make every unqualified use ambiguous.
/// </remarks>
public sealed class FixedBackupsListDataService : IDashboardDataService
{
    private readonly Servyx.Web.Models.BackupsListResult _result;

    /// <summary>Creates a fake whose backups listing is always <paramref name="result"/>.</summary>
    /// <param name="result">The result <c>GetAllBackupsWithStatusAsync</c> returns.</param>
    public FixedBackupsListDataService(Servyx.Web.Models.BackupsListResult result) => _result = result;

    /// <inheritdoc />
    public Task<Servyx.Web.Models.BackupsListResult> GetAllBackupsWithStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(_result);

    /// <inheritdoc />
    public Task<IReadOnlyList<Servyx.Web.Models.BackupEntry>> GetAllBackupsAsync(CancellationToken ct = default) =>
        Task.FromResult(_result.Backups);

    /// <inheritdoc />
    public Task<Servyx.Web.Models.ConnectionStatus> GetDockerConnectionStatusAsync(CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<Servyx.Web.Models.DockerConnectionInfo> GetDockerConnectionInfoAsync(CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<Servyx.Web.Models.DashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<IReadOnlyList<Servyx.Web.Models.ServerSummary>> GetServersAsync(CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<Servyx.Web.Models.ServerListResult> GetServersWithStatusAsync(CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<Servyx.Web.Models.ServerDetail?> GetServerDetailAsync(string serverId, CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<IReadOnlyList<Servyx.Web.Models.SettingRow>> GetServerSettingsAsync(string serverId, CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<IReadOnlyList<Servyx.Web.Models.LogLine>> GetServerLogsAsync(string serverId, CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<Servyx.Web.Models.SaveInfo?> GetServerSavesAsync(string serverId, CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<IReadOnlyList<Servyx.Web.Models.BackupEntry>> GetServerBackupsAsync(string serverId, CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<IReadOnlyList<Servyx.Web.Models.GameCardSummary>> GetGamesAsync(CancellationToken ct = default) => throw Unused();

    private static NotSupportedException Unused() =>
        new($"{nameof(FixedBackupsListDataService)} answers the closed-gate Backups listing only.");
}

/// <summary>
/// A hand-written <see cref="IBackupDashboard"/> that records which members a page or the scheduler
/// actually reached.
/// </summary>
/// <remarks>
/// The tests that matter most here assert <em>non-invocation</em>: that rendering a restore preview never
/// applied one, and that a prune dry run never deleted anything. Counters incremented only by the real
/// member are the plainest evidence of that.
/// </remarks>
public sealed class FakeBackupDashboard : IBackupDashboard
{
    private readonly List<BackupArtifact> _artifacts = [];

    /// <inheritdoc />
    public bool ProviderConfigured { get; set; } = true;

    /// <summary>The result <see cref="CreateAsync"/> returns. Defaults to success.</summary>
    public BackupCreateResult? CreateResult { get; set; }

    /// <summary>Ids <see cref="PreviewPruneAsync"/> reports as candidates.</summary>
    public List<string> PruneCandidates { get; } = [];

    /// <summary>Foreign artifacts reported as skipped by both prune members.</summary>
    public int SkippedForeign { get; set; }

    /// <summary>Paths the next restore plan names.</summary>
    public List<string> AffectedPaths { get; } = ["/palworld/Pal/Saved/SaveGames/0/world/Level.sav"];

    /// <summary>How many times <see cref="CreateAsync"/> was reached.</summary>
    public int CreateCalls { get; private set; }

    /// <summary>How many times <see cref="PlanRestoreAsync"/> was reached.</summary>
    public int PlanRestoreCalls { get; private set; }

    /// <summary>How many times <see cref="ApplyRestoreAsync"/> — the member that overwrites data — was reached.</summary>
    public int ApplyRestoreCalls { get; private set; }

    /// <summary>How many times <see cref="PreviewPruneAsync"/> was reached.</summary>
    public int PreviewPruneCalls { get; private set; }

    /// <summary>How many times <see cref="ApplyPruneAsync"/> — the member that deletes — was reached.</summary>
    public int ApplyPruneCalls { get; private set; }

    /// <summary>Adds an artifact to the listing.</summary>
    /// <param name="id">The artifact id.</param>
    /// <param name="ownership">Who owns it.</param>
    public FakeBackupDashboard With(string id, Servyx.Domain.Backups.BackupOwnership ownership)
    {
        _artifacts.Add(new BackupArtifact(
            id,
            ownership,
            new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero).AddDays(_artifacts.Count),
            5L * 1024 * 1024,
            $"/palworld/{(ownership == BackupOwnership.Foreign ? "backups" : "servyx-backups")}/{id}.tar.gz"));

        return this;
    }

    /// <inheritdoc />
    public Task<BackupListResult> ListAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult<BackupListResult>(new BackupListResult.Listed(
            [.. _artifacts.Where(a => a.Ownership == BackupOwnership.Servyx)],
            [.. _artifacts.Where(a => a.Ownership == BackupOwnership.Foreign)]));

    /// <inheritdoc />
    public Task<BackupCreateResult> CreateAsync(string serverId, CancellationToken ct = default)
    {
        CreateCalls++;
        return Task.FromResult(CreateResult ?? new BackupCreateResult.Created(new BackupArtifact(
            "servyx-new",
            BackupOwnership.Servyx,
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            1024,
            "/palworld/servyx-backups/servyx-new.tar.gz")));
    }

    /// <inheritdoc />
    public Task<BackupInspectResult> InspectAsync(string backupId, CancellationToken ct = default) =>
        Task.FromResult<BackupInspectResult>(new BackupInspectResult.Inspected(
            backupId,
            ["data/Pal/Saved/SaveGames/0/world/Level.sav"]));

    /// <inheritdoc />
    public Task<RestorePlanResult> PlanRestoreAsync(string backupId, CancellationToken ct = default)
    {
        PlanRestoreCalls++;
        return Task.FromResult<RestorePlanResult>(new RestorePlanResult.Planned(
            new RestorePlan($"restore-{PlanRestoreCalls}", backupId, [.. AffectedPaths])));
    }

    /// <inheritdoc />
    public Task<RestoreApplyResult> ApplyRestoreAsync(string restorePlanId, int expectedPathCount, CancellationToken ct = default)
    {
        ApplyRestoreCalls++;
        return Task.FromResult<RestoreApplyResult>(new RestoreApplyResult.Restored(restorePlanId, expectedPathCount));
    }

    /// <inheritdoc />
    public Task<BackupPruneResult> PreviewPruneAsync(string serverId, RetentionPolicy policy, CancellationToken ct = default)
    {
        PreviewPruneCalls++;
        return Task.FromResult<BackupPruneResult>(new BackupPruneResult.Previewed([.. PruneCandidates], SkippedForeign));
    }

    /// <inheritdoc />
    public Task<BackupPruneResult> ApplyPruneAsync(string serverId, RetentionPolicy policy, CancellationToken ct = default)
    {
        ApplyPruneCalls++;
        return Task.FromResult<BackupPruneResult>(new BackupPruneResult.Pruned([.. PruneCandidates], SkippedForeign));
    }
}

/// <summary>
/// An <see cref="IBackupDashboard"/> whose <see cref="CreateAsync"/> blocks until released, so a test can
/// hold one run open and drive a second one at the same server.
/// </summary>
public sealed class BlockingBackupDashboard : IBackupDashboard
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Signalled once <see cref="CreateAsync"/> has been entered.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How many times <see cref="CreateAsync"/> was reached.</summary>
    public int CreateCalls { get; private set; }

    /// <summary>How many times <see cref="ApplyPruneAsync"/> was reached.</summary>
    public int ApplyPruneCalls { get; private set; }

    /// <inheritdoc />
    public bool ProviderConfigured => true;

    /// <summary>Lets the in-flight <see cref="CreateAsync"/> complete.</summary>
    public void Release() => _release.TrySetResult();

    /// <inheritdoc />
    public async Task<BackupCreateResult> CreateAsync(string serverId, CancellationToken ct = default)
    {
        CreateCalls++;
        Entered.TrySetResult();
        await _release.Task.ConfigureAwait(false);

        return new BackupCreateResult.Created(new BackupArtifact(
            "servyx-blocked",
            BackupOwnership.Servyx,
            DateTimeOffset.UnixEpoch,
            1,
            "/palworld/servyx-backups/servyx-blocked.tar.gz"));
    }

    /// <inheritdoc />
    public Task<BackupPruneResult> ApplyPruneAsync(string serverId, RetentionPolicy policy, CancellationToken ct = default)
    {
        ApplyPruneCalls++;
        return Task.FromResult<BackupPruneResult>(new BackupPruneResult.Pruned([], 0));
    }

    /// <inheritdoc />
    public Task<BackupListResult> ListAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult<BackupListResult>(new BackupListResult.Listed([], []));

    /// <inheritdoc />
    public Task<BackupInspectResult> InspectAsync(string backupId, CancellationToken ct = default) =>
        Task.FromResult<BackupInspectResult>(new BackupInspectResult.Inspected(backupId, []));

    /// <inheritdoc />
    public Task<RestorePlanResult> PlanRestoreAsync(string backupId, CancellationToken ct = default) =>
        Task.FromResult<RestorePlanResult>(new RestorePlanResult.Planned(new RestorePlan("restore-1", backupId, [])));

    /// <inheritdoc />
    public Task<RestoreApplyResult> ApplyRestoreAsync(string restorePlanId, int expectedPathCount, CancellationToken ct = default) =>
        Task.FromResult<RestoreApplyResult>(new RestoreApplyResult.Restored(restorePlanId, expectedPathCount));

    /// <inheritdoc />
    public Task<BackupPruneResult> PreviewPruneAsync(string serverId, RetentionPolicy policy, CancellationToken ct = default) =>
        Task.FromResult<BackupPruneResult>(new BackupPruneResult.Previewed([], 0));
}

/// <summary>
/// An <see cref="IBackupProvider"/> that answers from scripted state, so a test can drive the scheduler
/// over the <em>real</em> <see cref="BackupDashboardService"/> rather than over a stand-in for it.
/// </summary>
/// <remarks>
/// This is what makes "the scheduled path respects the foreign-artifact barrier" a real claim: the
/// scheduler, the dashboard, and its dry-run-then-audit are all the production code, and only the
/// filesystem underneath is substituted.
/// </remarks>
public sealed class ScriptedBackupProvider : IBackupProvider
{
    private readonly List<BackupArtifact> _artifacts = [];

    /// <summary>Ids <see cref="PruneAsync"/> reports as removed, whatever their ownership.</summary>
    public List<string> PruneReturns { get; } = [];

    /// <summary>Thrown by <see cref="CreateAsync"/> when set.</summary>
    public Exception? CreateThrows { get; set; }

    /// <summary>How many times <see cref="PruneAsync"/> was called with <c>dryRun: false</c>.</summary>
    public int LivePruneCalls { get; private set; }

    /// <summary>How many times <see cref="CreateAsync"/> was reached.</summary>
    public int CreateCalls { get; private set; }

    /// <summary>Adds an artifact to the listing.</summary>
    /// <param name="id">The artifact id.</param>
    /// <param name="ownership">Who owns it.</param>
    public ScriptedBackupProvider With(string id, Servyx.Domain.Backups.BackupOwnership ownership)
    {
        _artifacts.Add(new BackupArtifact(id, ownership, DateTimeOffset.UnixEpoch.AddDays(_artifacts.Count), 1024, $"/palworld/{id}"));
        return this;
    }

    /// <inheritdoc />
    public Task<BackupArtifact> CreateAsync(string serverId, CancellationToken ct = default)
    {
        CreateCalls++;
        return CreateThrows is not null
            ? Task.FromException<BackupArtifact>(CreateThrows)
            : Task.FromResult(new BackupArtifact("servyx-new", BackupOwnership.Servyx, DateTimeOffset.UnixEpoch, 1, "/palworld/servyx-new"));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupArtifact>> ListAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BackupArtifact>>([.. _artifacts]);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> InspectAsync(string backupId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public Task<RestorePlan> PlanRestoreAsync(string backupId, CancellationToken ct = default) =>
        Task.FromResult(new RestorePlan("restore-1", backupId, []));

    /// <inheritdoc />
    public Task RestoreAsync(string restorePlanId, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<PruneResult> PruneAsync(string serverId, RetentionPolicy policy, bool dryRun, CancellationToken ct = default)
    {
        if (!dryRun)
        {
            LivePruneCalls++;
        }

        return Task.FromResult(new PruneResult([.. PruneReturns], 0));
    }
}

/// <summary>A <see cref="RecordingLogger"/> usable where an <see cref="ILogger{TCategoryName}"/> is required.</summary>
/// <typeparam name="T">The log category type.</typeparam>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly RecordingLogger _inner = new();

    /// <summary>Everything written, in order.</summary>
    public IReadOnlyList<RecordingLogger.Entry> Entries => _inner.Entries;

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _inner.Log(logLevel, eventId, state, exception, formatter);
}
