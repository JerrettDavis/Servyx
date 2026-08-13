using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Servyx.Application.Servers;
using Servyx.Composition;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Mcp.Contracts;

namespace Servyx.Mcp.Tools;

/// <summary>
/// The write mode a server carries, sourced from the same <see cref="WritableServers"/> instance the write
/// guard itself resolves through — a live view over the operator's per-server grant rows, not a second read
/// of configuration — so this label can never disagree with what the guard would actually resolve. (The
/// legacy <c>Servyx:Servers:&lt;key&gt;:WriteMode</c> key is still read by <c>SshDockerWriteModes</c> and
/// <c>SshBackupWiringOptions</c> for explicitly-declared remote hosts, which mint no row; it grants nothing
/// to a server Servyx tracks.) See <see cref="For"/>.
/// </summary>
public sealed record WriteModeInfo(string WriteMode, bool MutationsAllowed)
{
    /// <summary>
    /// Resolves the write mode for a server, matched by <paramref name="serverId"/> first and
    /// <paramref name="serverName"/> second — the same two-identifier match <see cref="WritableServers.Mode"/>
    /// itself performs, because an operator may have granted writes under either spelling.
    /// </summary>
    public static WriteModeInfo For(WritableServers writable, string? serverId, string? serverName)
    {
        var mode = writable.Mode(serverId, serverName);
        // Fully qualified: WriteModeInfo's own WriteMode property shadows the Servyx.Domain.Transport.WriteMode
        // enum type name within this record's scope.
        return new WriteModeInfo(KebabCase.From(mode.ToString()), mode == Servyx.Domain.Transport.WriteMode.Enabled);
    }
}

/// <summary>One network port a discovered server exposes.</summary>
public sealed record ServerPortDto(int? HostPort, int ContainerPort, string Protocol, bool Published)
{
    public static ServerPortDto From(ServerPort port) => new(port.HostPort, port.ContainerPort, port.Protocol, port.Published);
}

/// <summary>One adopted server's read-model row, carrying the write mode the guard would resolve for it.</summary>
public sealed record ServerSummaryDto(
    string Id,
    string Name,
    string Game,
    string State,
    string Health,
    string? HealthDetail,
    DateTimeOffset? StartedAt,
    string Host,
    IReadOnlyList<ServerPortDto> Ports,
    string BindingStatus,
    IReadOnlyList<string> AmbiguousCandidateGameIds,
    string WriteMode,
    bool MutationsAllowed)
{
    public static ServerSummaryDto From(ServerSummary summary, WritableServers writable)
    {
        var writeMode = WriteModeInfo.For(writable, summary.Id, summary.Name);
        return new ServerSummaryDto(
            summary.Id,
            summary.Name,
            summary.Game,
            KebabCase.From(summary.State.ToString()),
            KebabCase.From(summary.Health.ToString()),
            summary.HealthDetail,
            summary.StartedAt,
            summary.Host,
            summary.Ports.Select(ServerPortDto.From).ToList(),
            KebabCase.From(summary.BindingStatus.ToString()),
            // AmbiguousCandidateGameIds is nullable with a null default on the domain record; normalised to
            // an empty list here so this contract never hands a caller a null where a list is promised.
            summary.AmbiguousCandidateGameIds ?? [],
            writeMode.WriteMode,
            writeMode.MutationsAllowed);
    }
}

/// <summary>The result of <see cref="ServerReadTools.ListAsync"/>.</summary>
public sealed record ServersListResult(string Outcome, IReadOnlyList<ServerSummaryDto> Servers, string? FailureDetail);

/// <summary>One setting value read from a server's live configuration surface. Secret values arrive already masked.</summary>
public sealed record ServerSettingValueDto(string Key, string Label, string Group, bool IsSecret, string? Authoritative)
{
    public static ServerSettingValueDto From(ServerSettingValue value) =>
        new(value.Key, value.Label, value.Group, value.IsSecret, value.Authoritative);
}

/// <summary>Full detail for a single adopted server.</summary>
public sealed record ServerDetailDto(
    ServerSummaryDto Summary,
    string Image,
    string? MountHostPath,
    string? MountContainerPath,
    string? Network,
    string? IpAddress,
    long? MemoryLimitBytes,
    double? CpuLimit,
    IReadOnlyList<ServerSettingValueDto> Settings)
{
    public static ServerDetailDto From(ServerDetail detail, WritableServers writable) => new(
        ServerSummaryDto.From(detail.Summary, writable),
        detail.Image,
        detail.MountHostPath,
        detail.MountContainerPath,
        detail.Network,
        detail.IpAddress,
        detail.MemoryLimitBytes,
        detail.CpuLimit,
        detail.Settings.Select(ServerSettingValueDto.From).ToList());
}

/// <summary>The result of <see cref="ServerReadTools.GetAsync"/>.</summary>
public sealed record ServerGetResult(string Outcome, ServerDetailDto? Server);

/// <summary>The result of <see cref="ServerReadTools.StatusAsync"/>.</summary>
public sealed record ServerStatusToolResult(
    string Outcome,
    string? State,
    DateTimeOffset? StartedAt,
    TimeSpan? Uptime,
    string? WriteMode,
    bool? MutationsAllowed,
    Unavailable? Unavailable);

/// <summary>The result of <see cref="ServerReadTools.MetricsAsync"/>.</summary>
public sealed record ResourceSampleDto(DateTimeOffset Timestamp, double CpuPercent, long MemoryBytes, long NetworkRxBytes, long NetworkTxBytes)
{
    public static ResourceSampleDto From(ResourceSample sample) =>
        new(sample.Timestamp, sample.CpuPercent, sample.MemoryBytes, sample.NetworkRxBytes, sample.NetworkTxBytes);
}

/// <summary>The result of <see cref="ServerReadTools.MetricsAsync"/>.</summary>
public sealed record ServerMetricsToolResult(string Outcome, ResourceSampleDto? Sample);

/// <summary>One line of console output.</summary>
public sealed record ConsoleLineDto(long Offset, string Text, DateTimeOffset Timestamp, string Stream)
{
    public static ConsoleLineDto From(ConsoleLine line) =>
        new(line.Offset, line.Text, line.Timestamp, KebabCase.From(line.Stream.ToString()));
}

/// <summary>The result of <see cref="ServerReadTools.LogsAsync"/>.</summary>
public sealed record ServerLogsToolResult(string Outcome, IReadOnlyList<ConsoleLineDto> Lines);

/// <summary>The result of <see cref="ServerReadTools.SettingsAsync"/>.</summary>
public sealed record ServerSettingsToolResult(string Outcome, IReadOnlyList<ServerSettingValueDto> Settings);

/// <summary>One save world, as read by <see cref="ServerSavesReader"/>.</summary>
public sealed record SavesReadPlayerFileDto(string FileName, long SizeBytes)
{
    public static SavesReadPlayerFileDto From(SavesReadPlayerFile file) => new(file.FileName, file.SizeBytes);
}

/// <summary>One save world, as read by <see cref="ServerSavesReader"/>.</summary>
public sealed record SavesReadWorldDto(
    string WorldId,
    string LevelFileName,
    long LevelFileSizeBytes,
    string LevelMetaFileName,
    long LevelMetaFileSizeBytes,
    IReadOnlyList<SavesReadPlayerFileDto> PlayerFiles,
    bool WorldCandidatesTruncated,
    bool PlayerFilesTruncated)
{
    public static SavesReadWorldDto From(SavesReadWorld world) => new(
        world.WorldId, world.LevelFileName, world.LevelFileSizeBytes, world.LevelMetaFileName,
        world.LevelMetaFileSizeBytes, world.PlayerFiles.Select(SavesReadPlayerFileDto.From).ToList(),
        world.WorldCandidatesTruncated, world.PlayerFilesTruncated);
}

/// <summary>The result of <see cref="ServerReadTools.SavesAsync"/>.</summary>
public sealed record ServerSavesToolResult(string Outcome, SavesReadWorldDto? Save, string? FailureDetail, Unavailable? Unavailable);

/// <summary>
/// Read-only tools over adopted servers: listing, detail, status, metrics, logs, settings, and save
/// inspection. Every tool here that names a server checks existence first, so "the server does not exist"
/// and "the server exists but this particular fact about it is unavailable" are always distinguishable
/// outcomes rather than both collapsing to the same empty/null shape.
/// </summary>
[McpServerToolType]
public static class ServerReadTools
{
    [McpServerTool(Name = "servyx_servers_list", UseStructuredContent = true)]
    [Description(
        "Lists every adopted server with its current status and write mode. The Outcome distinguishes a " +
        "genuinely empty fleet from a failed discovery attempt — an empty Servers list with Outcome " +
        "'discovery-failed' means the list could not be read, never that zero servers are adopted.")]
    public static async Task<ServersListResult> ListAsync(
        IServerQueryService query, WritableServers writable, CancellationToken cancellationToken)
    {
        var result = await query.GetAdoptedServersWithStatusAsync(cancellationToken).ConfigureAwait(false);

        return new ServersListResult(
            Outcome: result.DiscoveryFailed ? "discovery-failed" : "listed",
            Servers: result.Servers.Select(s => ServerSummaryDto.From(s, writable)).ToList(),
            FailureDetail: result.FailureDetail);
    }

    [McpServerTool(Name = "servyx_server_get", UseStructuredContent = true)]
    [Description("Gets full detail for a single adopted server: image, mounts, network, resource limits, and its settings surface.")]
    public static async Task<ServerGetResult> GetAsync(
        [Description("The server's discovery id, as returned by servyx_servers_list.")] string serverId,
        IServerQueryService query,
        WritableServers writable,
        CancellationToken cancellationToken)
    {
        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        return detail is null
            ? new ServerGetResult("server-not-found", null)
            : new ServerGetResult("found", ServerDetailDto.From(detail, writable));
    }

    [McpServerTool(Name = "servyx_server_status_get", UseStructuredContent = true)]
    [Description(
        "Gets a single server's observed lifecycle status (running/stopped/starting/stopping/crashed/unknown), " +
        "when the process has a lifecycle definition to observe it through.")]
    public static async Task<ServerStatusToolResult> StatusAsync(
        [Description("The server's discovery id.")] string serverId,
        IServerQueryService query,
        ServyxServerLifecycles lifecycles,
        ServyxCoreComposition composition,
        WritableServers writable,
        CancellationToken cancellationToken)
    {
        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return new ServerStatusToolResult("server-not-found", null, null, null, null, null, null);
        }

        var writeMode = WriteModeInfo.For(writable, detail.Summary.Id, detail.Summary.Name);

        // ServyxServerLifecycles.GetAsync returns null both when no lifecycle definition is loaded at all
        // (DefinitionCatalogMode.None/Multiple) AND when the single loaded definition declares no `lifecycle`
        // block (StopPlan null even in Single mode) — two different process-level facts the capability report
        // only distinguishes for the first. The None/Multiple case reads the shared ServyxCapabilityReport, so
        // it never re-derives a reason code the report already computed; the "declares none" case is emitted
        // here because BuildCapabilityReport's StopEscalationLadder status does not itself check
        // singleDefinition.Lifecycle the way it checks singleDefinition.Saves for SaveInspection.
        if (composition.CatalogMode != DefinitionCatalogMode.Single)
        {
            var status = composition.Capabilities.Get(ServyxCapability.StopEscalationLadder);
            return new ServerStatusToolResult(
                "unavailable", null, null, null, writeMode.WriteMode, writeMode.MutationsAllowed,
                new Unavailable(
                    "server-lifecycle-status",
                    status.ReasonCode ?? "unknown",
                    status.Explanation ?? "No further explanation was recorded.",
                    status.Contributing));
        }

        if (lifecycles.StopPlan is null)
        {
            return new ServerStatusToolResult(
                "unavailable", null, null, null, writeMode.WriteMode, writeMode.MutationsAllowed,
                new Unavailable(
                    "server-lifecycle-status",
                    UnavailableReason.DefinitionDeclaresNone,
                    "The loaded game definition declares no 'lifecycle' block, so this server's status cannot be observed through it.",
                    []));
        }

        var lifecycle = await lifecycles.GetAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (lifecycle is null)
        {
            // Existence was already confirmed above, so a null lifecycle here means the server is not (or no
            // longer) adopted by the time this second call ran — a real, if narrow, TOCTOU window, reported
            // honestly rather than papered over.
            return new ServerStatusToolResult("server-not-found", null, null, null, writeMode.WriteMode, writeMode.MutationsAllowed, null);
        }

        var observed = await lifecycle.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return new ServerStatusToolResult(
            "observed", KebabCase.From(observed.State.ToString()), observed.StartedAt, observed.Uptime,
            writeMode.WriteMode, writeMode.MutationsAllowed, null);
    }

    [McpServerTool(Name = "servyx_server_metrics_get", UseStructuredContent = true)]
    [Description(
        "Takes a single best-effort resource-usage sample for a server. Distinguishes 'the server does not " +
        "exist' from 'the server exists but no sample could be taken right now' — the two are never collapsed.")]
    public static async Task<ServerMetricsToolResult> MetricsAsync(
        [Description("The server's discovery id.")] string serverId,
        IServerQueryService query,
        CancellationToken cancellationToken)
    {
        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return new ServerMetricsToolResult("server-not-found", null);
        }

        var sample = await query.GetMetricsSampleAsync(serverId, cancellationToken).ConfigureAwait(false);
        return sample is null
            ? new ServerMetricsToolResult("not-sampled", null)
            : new ServerMetricsToolResult("sampled", ResourceSampleDto.From(sample));
    }

    [McpServerTool(Name = "servyx_server_logs_read", UseStructuredContent = true)]
    [Description("Reads up to maxLines (clamped 1..2000, default 200) of recent console output as a bounded snapshot, not an open-ended follow.")]
    public static async Task<ServerLogsToolResult> LogsAsync(
        [Description("The server's discovery id.")] string serverId,
        IServerQueryService query,
        CancellationToken cancellationToken,
        [Description("Maximum number of recent lines to return. Clamped to 1..2000; defaults to 200.")] int maxLines = 200)
    {
        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return new ServerLogsToolResult("server-not-found", []);
        }

        var clamped = Math.Clamp(maxLines, 1, 2000);
        var lines = await query.ReadRecentLogsAsync(serverId, clamped, cancellationToken).ConfigureAwait(false);
        return new ServerLogsToolResult("read", lines.Select(ConsoleLineDto.From).ToList());
    }

    [McpServerTool(Name = "servyx_server_settings_list", UseStructuredContent = true)]
    [Description(
        "Lists a server's settings surface as read from its live container environment. Secret values arrive " +
        "already masked as '********' — this tool never resolves or exposes a real secret value.")]
    public static async Task<ServerSettingsToolResult> SettingsAsync(
        [Description("The server's discovery id.")] string serverId,
        IServerQueryService query,
        CancellationToken cancellationToken)
    {
        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        return detail is null
            ? new ServerSettingsToolResult("server-not-found", [])
            : new ServerSettingsToolResult("found", detail.Settings.Select(ServerSettingValueDto.From).ToList());
    }

    [McpServerTool(Name = "servyx_server_saves_get", UseStructuredContent = true)]
    [Description(
        "Reads a server's save world, driven entirely by the loaded game definition's declared save layout. " +
        "Distinguishes 'not configured' (no single definition, or the definition declares no saves) from " +
        "'unsupported transport' from 'failed to read' from a genuinely empty (Save is null) but successful read.")]
    public static async Task<ServerSavesToolResult> SavesAsync(
        [Description("The server's discovery id.")] string serverId,
        IServerQueryService query,
        ITransport transport,
        ServyxCoreComposition composition,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        // Process-level fact ("no single definition loaded" / "the loaded definition declares no saves
        // block") comes from the shared capability report, never re-derived here. What remains — reachability,
        // and whether THIS process's wired transport supports container-scoped file reads at all — is what
        // ServerSavesReader itself is the only correct emitter for; see the switch below.
        var saveInspection = composition.Capabilities.Get(ServyxCapability.SaveInspection);
        if (!saveInspection.Available)
        {
            return new ServerSavesToolResult("unavailable", null, null, UnavailableFactory.From(saveInspection));
        }

        // ServerSavesReader takes a plain ILogger, and ServerReadTools is a static tool-method container
        // (ILogger<ServerReadTools> is not constructible over a static type) — so the logger is created from
        // the factory under this tool's own category name instead.
        var logger = loggerFactory.CreateLogger("Servyx.Mcp.Tools.ServerReadTools");

        // Resolved optionally (see ArchiveTools' own remarks on this pattern) rather than as a normal injected
        // parameter: this process may never have called AddServyxSshDocker at all, in which case nothing
        // implements IServerExecutionTargetResolver, and a required parameter would make the SDK's DI
        // resolution throw before ServerSavesReader's own graceful UnsupportedTransport refusal ever ran.
        var executionTargetResolver = services.GetService<IServerExecutionTargetResolver>();
        var result = await ServerSavesReader.ReadServerSavesAsync(
            query, transport, composition.DefinitionCatalog, serverId, logger, cancellationToken, executionTargetResolver)
            .ConfigureAwait(false);

        return result.Availability switch
        {
            SavesReadAvailability.Listed => new ServerSavesToolResult(
                "listed", result.Save is null ? null : SavesReadWorldDto.From(result.Save), null, null),

            SavesReadAvailability.Failed => new ServerSavesToolResult("failed", null, result.FailureDetail, null),

            // Per-server-transport fact ServyxCapabilityReport has no dedicated capability for: it is a
            // property of the wired transport, not of a definition or of provisioning, so this tool emits it
            // directly — using the shared UnavailableReason vocabulary, never an inline string.
            SavesReadAvailability.UnsupportedTransport => new ServerSavesToolResult(
                "unavailable", null, null,
                new Unavailable(
                    "save-inspection", UnavailableReason.TransportUnsupported,
                    result.FailureDetail ?? "The wired transport does not support container-scoped file access.", [])),

            SavesReadAvailability.NotConfigured => new ServerSavesToolResult(
                "unavailable", null, null,
                new Unavailable(
                    "save-inspection", UnavailableReason.NotConfiguredForServer,
                    "Save inspection is not configured for this process.", [])),

            _ => throw new NotSupportedException($"Unrecognized {nameof(SavesReadAvailability)} '{result.Availability}'."),
        };
    }
}
