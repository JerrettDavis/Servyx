using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions;
using Servyx.Domain.Lifecycle;
using Servyx.Web.Models;

namespace Servyx.Web.Services;

/// <summary>
/// In-memory data mirroring a single adopted Palworld container (the motivating M1 target),
/// shaped so the wiring to a real <c>Servyx.Application</c> implementation is trivial later.
/// </summary>
public sealed class MockDashboardDataService : IDashboardDataService
{
    private const string ServerId = "palygondwanaland";

    private static readonly ServerSummary Server = new(
        Id: ServerId,
        Name: "Palygondwanaland",
        Game: "Palworld",
        State: ServerState.Running,
        Health: ContainerHealth.Unhealthy,
        HealthTooltip: "The container's own HEALTHCHECK calls http://localhost:8212/v1/api/info without " +
                       "admin credentials and receives 401 Unauthorized on every probe (FailingStreak: 293). " +
                       "The Palworld server itself is healthy \u2014 /v1/api/players returns OK on the same " +
                       "polling cycle. Servyx derives readiness from its own authenticated detectors, never " +
                       "from this signal.",
        PlayersOnline: 3,
        PlayersMax: 32,
        Uptime: TimeSpan.FromHours(24) + TimeSpan.FromMinutes(13),
        Host: "docker-desktop (npipe)",
        Ports:
        [
            new PortBinding(8211, "udp", "game", Published: true),
            new PortBinding(27015, "udp", "query", Published: true),
            new PortBinding(25575, "tcp", "rcon", Published: false),
            new PortBinding(8212, "tcp", "rest", Published: false),
        ]);

    private static readonly ServerDetail Detail = new(
        Summary: Server,
        Image: "thijsvanloef/palworld-server-docker:latest",
        MountHostPath: "/srv/palworld/data",
        MountContainerPath: "/palworld",
        Network: "palworld_default",
        IpAddress: "172.19.0.2",
        MemoryLimit: "8G",
        CpuLimit: "4");

    // ── Remote (ssh+docker) server ────────────────────────────────────────────────────────────────
    // Mirrors a real container adopted over SSH: `Host` carries the transport id ("ssh+docker", see
    // ServerQueryService.ToSummary), and health is the same false-negative Palworld unhealthy state as
    // the local server — the container's own HEALTHCHECK gets 401 Unauthorized while the game itself is
    // fine (ServerQueryService.PalworldUnhealthyExplanation). Kept as a second, independent server rather
    // than a variant of `Server` so both render side by side in the Servers list.
    private const string RemoteServerId = "example-remote-palworld";

    private static readonly ServerSummary RemoteServer = new(
        Id: RemoteServerId,
        Name: "Example Remote Palworld",
        Game: "Palworld",
        State: ServerState.Running,
        Health: ContainerHealth.Unhealthy,
        HealthTooltip: "The container's own HEALTHCHECK calls http://localhost:8212/v1/api/info without " +
                       "admin credentials and receives 401 Unauthorized on every probe. The Palworld " +
                       "server itself is healthy \u2014 /v1/api/players returns OK on the same polling " +
                       "cycle. Servyx derives readiness from its own authenticated detectors, never from " +
                       "this signal.",
        PlayersOnline: 7,
        PlayersMax: 32,
        Uptime: TimeSpan.FromDays(3) + TimeSpan.FromHours(6) + TimeSpan.FromMinutes(41),
        Host: "ssh+docker",
        Ports:
        [
            new PortBinding(8211, "udp", "game", Published: true),
            new PortBinding(27015, "udp", "query", Published: true),
            new PortBinding(25575, "tcp", "rcon", Published: false),
        ]);

    private static readonly ServerDetail RemoteDetail = new(
        Summary: RemoteServer,
        Image: "thijsvanloef/palworld-server-docker:latest",
        MountHostPath: "/opt/palworld/data",
        MountContainerPath: "/palworld",
        Network: "bridge",
        IpAddress: "172.18.0.3",
        MemoryLimit: "8G",
        CpuLimit: "4");

    private static readonly IReadOnlyList<SettingRow> Settings =
    [
        new SettingRow("Identity", "SERVER_NAME", "Server name", false,
            "Palygondwanaland", "Palygondwanaland", "Palygondwanaland", "Palygondwanaland",
            DriftKind.None, false),
        new SettingRow("Identity", "SERVER_DESCRIPTION", "Description", false,
            "A cozy dedicated Palworld server.", "A cozy dedicated Palworld server.",
            "A cozy dedicated Palworld server.", "A cozy dedicated Palworld server.",
            DriftKind.None, false),

        new SettingRow("Networking", "PORT", "Game port", false,
            "8211", "8211", "8211", "8211", DriftKind.None, false),
        new SettingRow("Networking", "RCON_PORT", "RCON port", false,
            "25575", "25575", "25575", "25575", DriftKind.None, false),

        new SettingRow("Gameplay", "PLAYERS", "Max players", false,
            "32", "32", "16", "16", DriftKind.AuthoritativeVsRendered, true),
        new SettingRow("Gameplay", "DIFFICULTY", "Difficulty", false,
            "None", "None", "None", "None", DriftKind.None, false),
        new SettingRow("Gameplay", "DAY_TIME_SPEEDRATE", "Day time speed", false,
            "1.000000", "1.000000", "1.000000", "1.000000", DriftKind.None, false),
        new SettingRow("Gameplay", "ENABLE_PLAYER_TO_PLAYER_DAMAGE", "Enable PvP", false,
            "False", "False", "False", "False", DriftKind.None, false),

        new SettingRow("Security", "ADMIN_PASSWORD", "Admin / RCON password", true,
            "********", "********", "********", "********", DriftKind.None, false),
        new SettingRow("Security", "SERVER_PASSWORD", "Join password", true,
            "********", "********", "********", "********", DriftKind.None, false),
    ];

    private static readonly IReadOnlyList<LogLine> Logs = BuildLogs();

    private static readonly SaveInfo Saves = new(
        WorldId: "F1FA89C5D3A74636A42816EBE4370739",
        LevelFileName: "Level.sav",
        LevelFileSizeBytes: 2_516_582, // ~2.4 MB
        LevelMetaFileName: "LevelMeta.sav",
        LevelMetaFileSizeBytes: 3_072,
        PlayerFiles:
        [
            new PlayerSaveFile("1F3A9B2C4D5E6F708192A3B4C5D6E7F8.sav", 41_216),
            new PlayerSaveFile("2A4B8C1D3E5F607182930A1B2C3D4E5F.sav", 38_904),
            new PlayerSaveFile("3B5C9D2E4F6071829304B1C2D3E4F5A6.sav", 52_330),
            new PlayerSaveFile("4C6D0E3F507182930415C2D3E4F5A6B7.sav", 29_771),
            new PlayerSaveFile("5D7E1F405182930415260D3E4F5A6B7C.sav", 46_002),
        ]);

    private static readonly IReadOnlyList<BackupEntry> Backups =
    [
        new BackupEntry(ServerId, "Palygondwanaland", "palworld-backup-2026-07-21_030000.tar.gz",
            new DateTimeOffset(2026, 7, 21, 3, 0, 0, TimeSpan.Zero), 431_400_000, BackupOwnership.Foreign),
        new BackupEntry(ServerId, "Palygondwanaland", "palworld-backup-2026-07-20_030000.tar.gz",
            new DateTimeOffset(2026, 7, 20, 3, 0, 0, TimeSpan.Zero), 428_900_000, BackupOwnership.Foreign),
        new BackupEntry(ServerId, "Palygondwanaland", "palworld-backup-2026-07-19_030000.tar.gz",
            new DateTimeOffset(2026, 7, 19, 3, 0, 0, TimeSpan.Zero), 426_150_000, BackupOwnership.Foreign),
        new BackupEntry(ServerId, "Palygondwanaland", "palworld-backup-2026-07-18_030000.tar.gz",
            new DateTimeOffset(2026, 7, 18, 3, 0, 0, TimeSpan.Zero), 424_800_000, BackupOwnership.Foreign),
        new BackupEntry(ServerId, "Palygondwanaland", "palworld-backup-2026-07-17_030000.tar.gz",
            new DateTimeOffset(2026, 7, 17, 3, 0, 0, TimeSpan.Zero), 419_300_000, BackupOwnership.Foreign),
    ];

    private static readonly IReadOnlyList<GameCardSummary> Games =
    [
        new GameCardSummary(
            Id: "palworld",
            Name: "Palworld Dedicated Server",
            Version: "1.0.0",
            Tags: ["survival", "steam", "unreal"],
            Trust: TrustTier.Builtin,
            ModsSupported: false,
            DeploymentProfiles:
            [
                new DeploymentProfileSummary("docker-thijsvanloef", "docker",
                    "thijsvanloef/palworld-server-docker container. .env is authoritative; PalWorldSettings.ini is derived and regenerated on every restart."),
                new DeploymentProfileSummary("native-steamcmd", "process",
                    "Bare-metal SteamCMD install (app 2394010). PalWorldSettings.ini is authoritative directly."),
            ]),
    ];

    public Task<ConnectionStatus> GetDockerConnectionStatusAsync(CancellationToken ct = default)
        => Task.FromResult(ConnectionStatus.Connected);

    public Task<DockerConnectionInfo> GetDockerConnectionInfoAsync(CancellationToken ct = default)
        => Task.FromResult(new DockerConnectionInfo(
            ConnectionStatus.Connected,
            "docker",
            "Docker 27.3.1 (API 1.47) on linux/amd64, kernel 6.6.87.2-microsoft-standard-WSL2"));

    public Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default)
    {
        var now = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        var servers = new[] { Server, RemoteServer };

        // Mirrors LiveDashboardDataService.GetDashboardSummaryAsync's aggregation exactly for
        // ServersOnline/ServersTotal/AlertsCount (running-state count, list count, unhealthy-health count).
        // Live always reports TotalPlayers/TotalPlayerCapacity as null — an authenticated RCON/REST session
        // is M2+ scope there — but the mock's ServerSummary rows *do* carry demo player figures (so the
        // per-server tiles have something to show), so the dashboard-wide tiles sum across the whole seeded
        // list rather than reading only the first server, the same way Live would if/when it had the data.
        return Task.FromResult(new DashboardSummary(
            ServersOnline: servers.Count(s => s.State == ServerState.Running),
            ServersTotal: servers.Length,
            TotalPlayers: SumIfAnyKnown(servers.Select(s => s.PlayersOnline)),
            TotalPlayerCapacity: SumIfAnyKnown(servers.Select(s => s.PlayersMax)),
            ForeignBackupsCount: Backups.Count,
            AlertsCount: servers.Count(s => s.Health == ContainerHealth.Unhealthy),
            CpuSparkline: BuildSparkline(now, seed: 11, baseline: 34, spread: 14),
            MemorySparkline: BuildSparkline(now, seed: 47, baseline: 62, spread: 8)));
    }

    /// <summary>
    /// Aggregates a per-server nullable count (<see cref="ServerSummary.PlayersOnline"/> or
    /// <see cref="ServerSummary.PlayersMax"/>) the same honest way the per-server field itself works: a
    /// server's count is <see langword="null"/> when it was never sampled, not a fabricated <c>0</c> — see
    /// <see cref="ServerSummary.PlayersOnline"/>'s remarks — and the aggregate must not conflate that
    /// "unknown" into a real total either. If every server's value is <see langword="null"/> (nothing was
    /// sampled anywhere), the aggregate is <see langword="null"/> too. Otherwise the aggregate is the sum of
    /// whichever values ARE known, the same "sum what you have" convention
    /// <c>LiveDashboardDataService.GetDashboardSummaryAsync</c> would need the day it reads a partial roster
    /// (today it only ever has the all-or-nothing case, reporting <see langword="null"/> outright).
    /// </summary>
    internal static int? SumIfAnyKnown(IEnumerable<int?> values)
    {
        var list = values.ToList();
        return list.All(v => v is null) ? null : list.Sum(v => v ?? 0);
    }

    public Task<IReadOnlyList<ServerSummary>> GetServersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ServerSummary>>([Server, RemoteServer]);

    public Task<ServerListResult> GetServersWithStatusAsync(CancellationToken ct = default)
        => Task.FromResult(new ServerListResult([Server, RemoteServer], DiscoveryFailed: false, FailureDetail: null));

    public Task<ServerDetail?> GetServerDetailAsync(string serverId, CancellationToken ct = default)
    {
        if (string.Equals(serverId, ServerId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<ServerDetail?>(Detail);
        }

        if (string.Equals(serverId, RemoteServerId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<ServerDetail?>(RemoteDetail);
        }

        return Task.FromResult<ServerDetail?>(null);
    }

    public Task<IReadOnlyList<SettingRow>> GetServerSettingsAsync(string serverId, CancellationToken ct = default)
        => Task.FromResult(string.Equals(serverId, ServerId, StringComparison.OrdinalIgnoreCase)
            ? Settings
            : (IReadOnlyList<SettingRow>)[]);

    public Task<IReadOnlyList<LogLine>> GetServerLogsAsync(string serverId, CancellationToken ct = default)
        => Task.FromResult(string.Equals(serverId, ServerId, StringComparison.OrdinalIgnoreCase)
            ? Logs
            : (IReadOnlyList<LogLine>)[]);

    public Task<SaveInfo?> GetServerSavesAsync(string serverId, CancellationToken ct = default)
        => Task.FromResult(string.Equals(serverId, ServerId, StringComparison.OrdinalIgnoreCase) ? Saves : null);

    public Task<IReadOnlyList<BackupEntry>> GetServerBackupsAsync(string serverId, CancellationToken ct = default)
        => Task.FromResult(string.Equals(serverId, ServerId, StringComparison.OrdinalIgnoreCase)
            ? Backups
            : (IReadOnlyList<BackupEntry>)[]);

    public Task<IReadOnlyList<BackupEntry>> GetAllBackupsAsync(CancellationToken ct = default)
        => Task.FromResult(Backups);

    public Task<BackupsListResult> GetAllBackupsWithStatusAsync(CancellationToken ct = default)
        => Task.FromResult(new BackupsListResult(Backups, BackupsAvailability.Listed, null));

    public Task<IReadOnlyList<GameCardSummary>> GetGamesAsync(CancellationToken ct = default)
        => Task.FromResult(Games);

    private static IReadOnlyList<SparklinePoint> BuildSparkline(DateTimeOffset now, int seed, double baseline, double spread)
    {
        var rng = new Random(seed);
        var points = new List<SparklinePoint>(20);
        for (var i = 19; i >= 0; i--)
        {
            var value = Math.Clamp(baseline + (rng.NextDouble() - 0.5) * spread, 0, 100);
            points.Add(new SparklinePoint(now - TimeSpan.FromMinutes(i * 5), Math.Round(value, 1)));
        }

        return points;
    }

    private static IReadOnlyList<LogLine> BuildLogs()
    {
        var t0 = new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero);
        string[] lines =
        [
            "Starting Palworld dedicated server entrypoint...",
            "Rendering .env into PalWorldSettings.ini (150 keys)...",
            "Running Palworld dedicated server on 0.0.0.0:8211",
            "[RCON] Listening on 0.0.0.0:25575",
            "[REST] Listening on 0.0.0.0:8212",
            "Player 'Xylo' (steam:76561198000000123) joined.",
            "Player 'Braxos' (steam:76561198000000456) joined.",
            "[cron] Starting scheduled backup...",
            "[cron] Backup complete: palworld-backup-2026-07-21_030000.tar.gz (431.4 MB)",
            "Player 'Xylo' (steam:76561198000000123) left.",
            "Autosave complete (World_0).",
            "Player 'Mireille' (steam:76561198000000789) joined.",
            "[healthcheck] GET /v1/api/info -> 401 Unauthorized (FailingStreak: 293)",
            "[REST] GET /v1/api/players -> 200 OK",
            "Autosave complete (World_0).",
        ];

        var logs = new List<LogLine>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var level = lines[i].Contains("401 Unauthorized", StringComparison.Ordinal) ? "WARN" : "INFO";
            logs.Add(new LogLine(t0 + TimeSpan.FromMinutes(i * 7), level, lines[i]));
        }

        return logs;
    }
}
