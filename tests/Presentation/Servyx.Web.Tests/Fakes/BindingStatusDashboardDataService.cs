using Servyx.Web.Models;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// An <see cref="IDashboardDataService"/> that serves caller-supplied <see cref="ServerSummary"/>/
/// <see cref="ServerDetail"/> rows for the servers list/detail pages, delegating everything else
/// (backups, dashboard summary, games) to a real <see cref="MockDashboardDataService"/> instance.
/// </summary>
/// <remarks>
/// Exists so binding-status (<see cref="ServerBindingStatus"/>) rendering tests can seed Ambiguous/
/// NeedsRebind rows without touching <see cref="MockDashboardDataService"/>'s own fixed "Palygondwanaland"
/// seed data, which every other test in this suite depends on staying exactly as it is — the same reason
/// <c>StubDashboardDataService</c> exists for the Backups page's server picker.
/// </remarks>
public sealed class BindingStatusDashboardDataService : IDashboardDataService
{
    private readonly MockDashboardDataService _inner = new();
    private readonly IReadOnlyList<ServerSummary> _servers;
    private readonly IReadOnlyDictionary<string, ServerDetail> _details;

    public BindingStatusDashboardDataService(
        IReadOnlyList<ServerSummary> servers,
        IReadOnlyDictionary<string, ServerDetail> details)
    {
        _servers = servers;
        _details = details;
    }

    public Task<IReadOnlyList<ServerSummary>> GetServersAsync(CancellationToken ct = default) =>
        Task.FromResult(_servers);

    public Task<ServerListResult> GetServersWithStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(new ServerListResult(_servers, DiscoveryFailed: false, FailureDetail: null));

    public Task<ServerDetail?> GetServerDetailAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult(_details.TryGetValue(serverId, out var detail) ? detail : null);

    public Task<IReadOnlyList<SettingRow>> GetServerSettingsAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SettingRow>>([]);

    public Task<IReadOnlyList<LogLine>> GetServerLogsAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LogLine>>([]);

    public Task<SaveInfo?> GetServerSavesAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult<SaveInfo?>(null);

    public Task<IReadOnlyList<BackupEntry>> GetServerBackupsAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BackupEntry>>([]);

    public Task<IReadOnlyList<BackupEntry>> GetAllBackupsAsync(CancellationToken ct = default) =>
        _inner.GetAllBackupsAsync(ct);

    public Task<BackupsListResult> GetAllBackupsWithStatusAsync(CancellationToken ct = default) =>
        _inner.GetAllBackupsWithStatusAsync(ct);

    public Task<ConnectionStatus> GetDockerConnectionStatusAsync(CancellationToken ct = default) =>
        _inner.GetDockerConnectionStatusAsync(ct);

    public Task<DockerConnectionInfo> GetDockerConnectionInfoAsync(CancellationToken ct = default) =>
        _inner.GetDockerConnectionInfoAsync(ct);

    public Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default) =>
        _inner.GetDashboardSummaryAsync(ct);

    public Task<IReadOnlyList<GameCardSummary>> GetGamesAsync(CancellationToken ct = default) =>
        _inner.GetGamesAsync(ct);
}
