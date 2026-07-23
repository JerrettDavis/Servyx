using Servyx.Web.Models;

namespace Servyx.Web.Services;

/// <summary>
/// Local, Servyx.Web-only abstraction over the data this dashboard shell needs. Milestone 1 binds this
/// to an in-memory mock implementation; a later milestone rebinds it to the Application layer without
/// changing any page. Deliberately does not reference Servyx.Infrastructure.Docker, which is under
/// separate, concurrent development.
/// </summary>
public interface IDashboardDataService
{
    Task<ConnectionStatus> GetDockerConnectionStatusAsync(CancellationToken ct = default);

    Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ServerSummary>> GetServersAsync(CancellationToken ct = default);

    Task<ServerDetail?> GetServerDetailAsync(string serverId, CancellationToken ct = default);

    Task<IReadOnlyList<SettingRow>> GetServerSettingsAsync(string serverId, CancellationToken ct = default);

    Task<IReadOnlyList<LogLine>> GetServerLogsAsync(string serverId, CancellationToken ct = default);

    Task<SaveInfo?> GetServerSavesAsync(string serverId, CancellationToken ct = default);

    Task<IReadOnlyList<BackupEntry>> GetServerBackupsAsync(string serverId, CancellationToken ct = default);

    Task<IReadOnlyList<BackupEntry>> GetAllBackupsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<GameCardSummary>> GetGamesAsync(CancellationToken ct = default);
}
