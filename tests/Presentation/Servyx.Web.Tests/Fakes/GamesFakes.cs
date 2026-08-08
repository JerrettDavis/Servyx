using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// An <see cref="IDashboardDataService"/> whose <c>GetGamesAsync</c>/<c>GetGameDefinitionFaultsAsync</c>
/// answers are fixed by the test. Exists for <c>GamesPage</c> tests, which reach exactly those two
/// members — every other member throws, so a page that started reaching for more of this fake than intended
/// fails loudly rather than silently answering with invented data. Mirrors
/// <see cref="FixedBackupsListDataService"/>'s own shape for the Backups page.
/// </summary>
public sealed class FixedGamesDataService : IDashboardDataService
{
    private readonly IReadOnlyList<GameCardSummary> _games;
    private readonly IReadOnlyList<GameDefinitionFaultSummary> _faults;

    /// <summary>Creates a fake whose games/faults listings are always <paramref name="games"/>/<paramref name="faults"/>.</summary>
    public FixedGamesDataService(
        IReadOnlyList<GameCardSummary>? games = null,
        IReadOnlyList<GameDefinitionFaultSummary>? faults = null)
    {
        _games = games ?? [];
        _faults = faults ?? [];
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GameCardSummary>> GetGamesAsync(CancellationToken ct = default) =>
        Task.FromResult(_games);

    /// <inheritdoc />
    public Task<IReadOnlyList<GameDefinitionFaultSummary>> GetGameDefinitionFaultsAsync(CancellationToken ct = default) =>
        Task.FromResult(_faults);

    /// <inheritdoc />
    public Task<ConnectionStatus> GetDockerConnectionStatusAsync(CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<DockerConnectionInfo> GetDockerConnectionInfoAsync(CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<IReadOnlyList<ServerSummary>> GetServersAsync(CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<ServerListResult> GetServersWithStatusAsync(CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<ServerDetail?> GetServerDetailAsync(string serverId, CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<IReadOnlyList<SettingRow>> GetServerSettingsAsync(string serverId, CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<IReadOnlyList<LogLine>> GetServerLogsAsync(string serverId, CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<SaveInfo?> GetServerSavesAsync(string serverId, CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupEntry>> GetServerBackupsAsync(string serverId, CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupEntry>> GetAllBackupsAsync(CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<BackupsListResult> GetAllBackupsWithStatusAsync(CancellationToken ct = default) => throw Unused();

    private static NotSupportedException Unused() =>
        new($"{nameof(FixedGamesDataService)} answers the Games page's two reads only.");
}
