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

    /// <summary>
    /// Same signal as <see cref="GetDockerConnectionStatusAsync"/>, plus the specific transport-reported
    /// detail text and the transport id actually in play. See <see cref="DockerConnectionInfo"/> for why
    /// this exists — the status enum alone cannot honestly justify a UI tooltip about the transport.
    /// </summary>
    Task<DockerConnectionInfo> GetDockerConnectionInfoAsync(CancellationToken ct = default);

    Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ServerSummary>> GetServersAsync(CancellationToken ct = default);

    /// <summary>
    /// Same listing as <see cref="GetServersAsync"/>, but reports whether discovery itself failed rather
    /// than flattening that into an indistinguishable empty list. See <see cref="ServerListResult"/>.
    /// </summary>
    Task<ServerListResult> GetServersWithStatusAsync(CancellationToken ct = default);

    Task<ServerDetail?> GetServerDetailAsync(string serverId, CancellationToken ct = default);

    Task<IReadOnlyList<SettingRow>> GetServerSettingsAsync(string serverId, CancellationToken ct = default);

    Task<IReadOnlyList<LogLine>> GetServerLogsAsync(string serverId, CancellationToken ct = default);

    Task<SaveInfo?> GetServerSavesAsync(string serverId, CancellationToken ct = default);

    /// <summary>
    /// Same read as <see cref="GetServerSavesAsync"/>, but reports whether the read itself failed, or
    /// whether nothing is configured to read saves at all, rather than flattening either into the
    /// indistinguishable-from-"no saves" <see langword="null"/> that member returns. See
    /// <see cref="SavesResult"/>.
    /// </summary>
    /// <remarks>
    /// Default implementation: delegates to <see cref="GetServerSavesAsync"/> and reports
    /// <see cref="SavesAvailability.Listed"/> when it returns a save, or <see cref="SavesAvailability.NotConfigured"/>
    /// when it returns <see langword="null"/> — exactly what every pre-existing <see cref="IDashboardDataService"/>
    /// implementation (the mock data source, test fakes) already behaved as before this member existed, so
    /// none of them need to be touched to keep compiling. <see cref="LiveDashboardDataService"/> overrides
    /// this with the real three-way distinction.
    /// </remarks>
    async Task<SavesResult> GetServerSavesWithStatusAsync(string serverId, CancellationToken ct = default)
    {
        var save = await GetServerSavesAsync(serverId, ct).ConfigureAwait(false);
        return new SavesResult(save, save is null ? SavesAvailability.NotConfigured : SavesAvailability.Listed, null);
    }

    Task<IReadOnlyList<BackupEntry>> GetServerBackupsAsync(string serverId, CancellationToken ct = default);

    Task<IReadOnlyList<BackupEntry>> GetAllBackupsAsync(CancellationToken ct = default);

    /// <summary>
    /// Same listing as <see cref="GetAllBackupsAsync"/>, but reports whether the listing itself failed, or
    /// whether no backup provider is configured in this process at all, rather than flattening either into
    /// an indistinguishable empty list. See <see cref="BackupsListResult"/>.
    /// </summary>
    Task<BackupsListResult> GetAllBackupsWithStatusAsync(CancellationToken ct = default);

    Task<IReadOnlyList<GameCardSummary>> GetGamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Every definition the catalog attempted to load but could not — a parse failure, a semantic
    /// validation error, a duplicate id, or similar. The Games page renders each of these as a visibly
    /// distinct "failed to load" card alongside <see cref="GetGamesAsync"/>'s successful ones, so dropping a
    /// malformed <c>definitions/*.yaml</c> in never silently does nothing. Defaults to an empty list so
    /// existing <see cref="IDashboardDataService"/> implementations that predate this member (the mock data
    /// source, test fakes) do not need to be touched to keep compiling.
    /// </summary>
    Task<IReadOnlyList<GameDefinitionFaultSummary>> GetGameDefinitionFaultsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GameDefinitionFaultSummary>>([]);
}
