using Servyx.Domain.Lifecycle;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// An <see cref="IDashboardDataService"/> whose Docker-discovered server list is supplied by the test.
/// </summary>
/// <remarks>
/// <see cref="MockDashboardDataService"/> is deliberately a single fixed container, which is the right shape
/// for every page that renders one. The server-picker tests need to vary ids, names and ordering — and to
/// give a Docker server the same name as a configured SSH one — so they supply their own list here. Every
/// other member throws: this fake exists for the Backups page's picker and nothing else, and a page that
/// started reaching for the rest through it should fail loudly rather than assert against invented data.
/// </remarks>
public sealed class StubDashboardDataService : IDashboardDataService
{
    private readonly IReadOnlyList<ServerSummary> _servers;

    /// <summary>Creates a stub over the given Docker-discovered servers, in the order given.</summary>
    /// <param name="servers">The servers <see cref="GetServersAsync"/> returns.</param>
    public StubDashboardDataService(params ServerSummary[] servers) => _servers = servers;

    /// <summary>Builds a plausible discovered server with the given id and name.</summary>
    /// <param name="id">The discovery id.</param>
    /// <param name="name">The container name.</param>
    public static ServerSummary Server(string id, string name) => new(
        Id: id,
        Name: name,
        Game: "Palworld",
        State: ServerState.Running,
        Health: ContainerHealth.Healthy,
        HealthTooltip: "Healthy.",
        PlayersOnline: null,
        PlayersMax: null,
        Uptime: TimeSpan.FromHours(1),
        Host: "docker-desktop (npipe)",
        Ports: []);

    /// <inheritdoc />
    public Task<IReadOnlyList<ServerSummary>> GetServersAsync(CancellationToken ct = default) =>
        Task.FromResult(_servers);

    /// <inheritdoc />
    public Task<ServerListResult> GetServersWithStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(new ServerListResult(_servers, DiscoveryFailed: false, FailureDetail: null));

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupEntry>> GetAllBackupsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BackupEntry>>([]);

    /// <inheritdoc />
    public Task<BackupsListResult> GetAllBackupsWithStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(new BackupsListResult([], BackupsAvailability.Listed, null));

    /// <inheritdoc />
    public Task<ConnectionStatus> GetDockerConnectionStatusAsync(CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<DockerConnectionInfo> GetDockerConnectionInfoAsync(CancellationToken ct = default) => throw Unused();

    /// <inheritdoc />
    public Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default) => throw Unused();

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
    public Task<IReadOnlyList<GameCardSummary>> GetGamesAsync(CancellationToken ct = default) => throw Unused();

    private static NotSupportedException Unused() =>
        new($"{nameof(StubDashboardDataService)} answers the Backups page's server picker only.");
}
