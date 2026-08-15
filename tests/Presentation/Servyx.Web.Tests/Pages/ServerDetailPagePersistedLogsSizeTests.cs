using System.Reflection;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Lifecycle;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Models;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// Pins the fix for the circuit-killing bug in <c>ServerDetailPage.PersistData</c>: for any server with
/// enough console log history, persisting the Console tab's full log buffer through
/// <see cref="PersistentComponentState"/> for the prerender-to-interactive handoff produced a payload large
/// enough to exceed SignalR's default 32KB hub message size cap, throwing
/// <c>System.IO.InvalidDataException</c> and killing the circuit before the page ever became interactive —
/// every button/tab on <c>/servers/{id}</c> (Start/Stop/Console/Settings/Saves/Backups) went permanently
/// unresponsive. Reproduced here by feeding a fake <see cref="IDashboardDataService"/> a log buffer shaped
/// like the real failure: 200 lines (the real hard-coded cap in
/// <c>LiveDashboardDataService.GetServerLogsAsync</c>) plus one deliberately oversized line, proving the fix
/// holds regardless of how long any single log line is — a fixed line count alone would not have been a real
/// fix, since one arbitrarily long line (e.g. a stack trace) can blow the byte budget on its own.
/// </summary>
public class ServerDetailPagePersistedLogsSizeTests : BunitContext
{
    private const string ServerIdValue = "long-uptime-server";

    /// <summary>The real SignalR default hub message size cap that killed the circuit.</summary>
    private const int SignalRDefaultMessageSizeCapBytes = 32 * 1024;

    private static ServerSummary Summary() => new(
        Id: ServerIdValue,
        Name: "Long Uptime Server",
        Game: "Palworld Dedicated Server",
        State: ServerState.Running,
        Health: ContainerHealth.Healthy,
        HealthTooltip: "Healthy.",
        PlayersOnline: null,
        PlayersMax: null,
        Uptime: TimeSpan.FromHours(19),
        Host: "docker-desktop (npipe)",
        Ports: []);

    private static ServerDetail Detail() => new(
        Summary: Summary(),
        Image: "thijsvanloef/palworld-server-docker:latest",
        MountHostPath: "/srv/data",
        MountContainerPath: "/palworld",
        Network: "bridge",
        IpAddress: "172.18.0.2",
        MemoryLimit: "8G",
        CpuLimit: "4");

    /// <summary>
    /// 200 lines (matching the real hard-coded <c>maxLines: 200</c>) of ordinary-length messages, plus one
    /// line shaped like a stack trace that alone is larger than the entire SignalR cap — this is what an
    /// arbitrarily long single log line looks like in production and what any "cap the line count" fix
    /// would still fail against.
    /// </summary>
    private static IReadOnlyList<LogLine> HugeLogBuffer()
    {
        var lines = new List<LogLine>(201);
        for (var i = 0; i < 200; i++)
        {
            lines.Add(new LogLine(
                DateTimeOffset.UtcNow.AddSeconds(-i),
                "INFO",
                $"[Player{i}] joined the server from 203.0.113.{i % 255} after a routine matchmaking handshake."));
        }

        lines.Add(new LogLine(DateTimeOffset.UtcNow, "ERROR", new string('x', 40 * 1024)));
        return lines;
    }

    private sealed class HugeLogsDashboardDataService : IDashboardDataService
    {
        public Task<ServerDetail?> GetServerDetailAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult<ServerDetail?>(serverId == ServerIdValue ? Detail() : null);

        public Task<IReadOnlyList<SettingRow>> GetServerSettingsAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SettingRow>>([]);

        public Task<IReadOnlyList<LogLine>> GetServerLogsAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult(HugeLogBuffer());

        public Task<SaveInfo?> GetServerSavesAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult<SaveInfo?>(null);

        public Task<IReadOnlyList<BackupEntry>> GetServerBackupsAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BackupEntry>>([]);

        public Task<IReadOnlyList<ServerSummary>> GetServersAsync(CancellationToken ct = default) =>
            throw new NotImplementedException("Not reached by ServerDetailPage.");

        public Task<ServerListResult> GetServersWithStatusAsync(CancellationToken ct = default) =>
            throw new NotImplementedException("Not reached by ServerDetailPage.");

        public Task<IReadOnlyList<BackupEntry>> GetAllBackupsAsync(CancellationToken ct = default) =>
            throw new NotImplementedException("Not reached by ServerDetailPage.");

        public Task<BackupsListResult> GetAllBackupsWithStatusAsync(CancellationToken ct = default) =>
            throw new NotImplementedException("Not reached by ServerDetailPage.");

        public Task<ConnectionStatus> GetDockerConnectionStatusAsync(CancellationToken ct = default) =>
            throw new NotImplementedException("Not reached by ServerDetailPage.");

        public Task<DockerConnectionInfo> GetDockerConnectionInfoAsync(CancellationToken ct = default) =>
            throw new NotImplementedException("Not reached by ServerDetailPage.");

        public Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default) =>
            throw new NotImplementedException("Not reached by ServerDetailPage.");

        public Task<IReadOnlyList<GameCardSummary>> GetGamesAsync(CancellationToken ct = default) =>
            throw new NotImplementedException("Not reached by ServerDetailPage.");
    }

    /// <summary>
    /// Reads bUnit's fake <see cref="PersistentComponentState"/> store back out via its public
    /// <see cref="IPersistentComponentStateStore"/> face. bUnit 2.7.2 exposes no other way to inspect the
    /// raw serialized bytes it captured — <see cref="BunitPersistentComponentState.TryTake{TValue}"/> only
    /// hands back deserialized values — but the concrete store type merely implements that public ASP.NET
    /// Core interface internally, so reflecting out the private field and reading it through the public
    /// interface is the accurate, non-fragile way to measure exactly what a real circuit would have to ship
    /// over the wire.
    /// </summary>
    private static async Task<IDictionary<string, byte[]>> ReadPersistedBytesAsync(BunitPersistentComponentState state)
    {
        var storeField = typeof(BunitPersistentComponentState)
            .GetField("store", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var store = (IPersistentComponentStateStore)storeField.GetValue(state)!;
        return await store.GetPersistedStateAsync();
    }

    [Fact]
    public async Task Persisting_a_server_with_a_huge_log_buffer_never_exceeds_SignalRs_message_cap()
    {
        Services.AddSingleton<IDashboardDataService>(new HugeLogsDashboardDataService());
        var persistState = AddBunitPersistentComponentState();

        var cut = Render<ServerDetailPage>(p => p.Add(x => x.Id, ServerIdValue));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Long Uptime Server"));

        persistState.TriggerOnPersisting();

        var persisted = await ReadPersistedBytesAsync(persistState);
        var totalBytes = persisted.Values.Sum(v => v.Length);

        totalBytes.Should().BeLessThan(SignalRDefaultMessageSizeCapBytes,
            because: "the whole point of the fix is that a 200-line-plus-one-giant-line log buffer must " +
                     "never be able to push the persisted payload anywhere near the 32KB hub message cap " +
                     "that used to kill the circuit");
    }

    [Fact]
    public void Persisting_never_hands_the_log_buffer_to_PersistentComponentState_at_all()
    {
        Services.AddSingleton<IDashboardDataService>(new HugeLogsDashboardDataService());
        var persistState = AddBunitPersistentComponentState();

        var cut = Render<ServerDetailPage>(p => p.Add(x => x.Id, ServerIdValue));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Long Uptime Server"));

        persistState.TriggerOnPersisting();

        persistState.TryTake<IReadOnlyList<LogLine>>($"ServerDetailPage.{ServerIdValue}.Logs", out _)
            .Should().BeFalse(because: "the log buffer must be re-fetched live on the interactive pass, " +
                                        "never round-tripped through PersistentComponentState");
    }
}
