using System.ComponentModel;
using ModelContextProtocol.Server;
using Servyx.Application.Servers;
using Servyx.Composition;
using Servyx.Domain.Transport;
using Servyx.Mcp.Contracts;

namespace Servyx.Mcp.Tools;

/// <summary>One capability row, as reported by <see cref="ServyxCapabilityReport"/>, crossed to the wire.</summary>
public sealed record CapabilityRow(
    string Capability, bool Available, string? ReasonCode, string? Explanation, IReadOnlyList<string> Contributing)
{
    /// <summary>Maps a domain <see cref="CapabilityStatus"/> to its wire shape.</summary>
    public static CapabilityRow From(CapabilityStatus status) => new(
        KebabCase.From(status.Capability.ToString()), status.Available, status.ReasonCode, status.Explanation,
        status.Contributing);
}

/// <summary>The result of <see cref="HostTools.DescribeAsync"/>.</summary>
public sealed record HostDescribeResult(
    string Outcome,
    string TransportId,
    string Endpoint,
    bool Reachable,
    string? ConnectionDetail,
    string CatalogMode,
    IReadOnlyList<string> LoadedDefinitionIds,
    bool ProvisioningEnabled,
    bool BackupProviderConfigured,
    IReadOnlyList<CapabilityRow> Capabilities,
    IReadOnlyList<string> WritableServerKeys,
    string WithheldOperationsNotice);

/// <summary>
/// The one tool an agent should call first: what this process is, what it can reach, and — just as
/// important — what it deliberately will not do.
/// </summary>
[McpServerToolType]
public static class HostTools
{
    /// <summary>
    /// Fixed prose naming every operation this build's tool surface deliberately does not expose, so an
    /// agent is told <em>why</em> they are absent rather than concluding the server is merely incomplete.
    /// Kept as a single shared constant so no two tools (or a future <c>servyx_host_describe</c> revision)
    /// can drift on the list.
    /// </summary>
    public const string WithheldOperationsNotice =
        "This build deliberately exposes no tool for: applying or updating a provisioning plan, creating a " +
        "backup, applying a restore, applying a prune, sending a raw control-channel command, or recreating " +
        "a container. Every one of those is irreversible or infrastructure-mutating; where a read-only " +
        "preview exists (a restore plan, a prune preview), report it to a human operator, who applies it " +
        "through the web UI.";

    /// <summary>
    /// Describes this Servyx process: its execution-target transport and reachability, how many game
    /// definitions loaded, provisioning/backup availability, every capability row, which servers carry a
    /// write grant, and what this build deliberately withholds.
    /// </summary>
    [McpServerTool(Name = "servyx_host_describe", UseStructuredContent = true)]
    [Description(
        "Describes this Servyx process: its execution-target transport and reachability, how many game " +
        "definitions loaded, provisioning/backup availability, every capability row, which servers carry a " +
        "write grant, and a fixed notice naming every operation this build deliberately does not expose. " +
        "Call this first.")]
    public static async Task<HostDescribeResult> DescribeAsync(
        ServyxCoreComposition composition,
        IServerQueryService query,
        TargetDescriptor target,
        WritableServers writable,
        CancellationToken cancellationToken)
    {
        var state = await query.GetConnectionStateAsync(target, cancellationToken).ConfigureAwait(false);

        return new HostDescribeResult(
            Outcome: "described",
            TransportId: target.TransportId,
            Endpoint: state.Endpoint,
            Reachable: state.Reachable,
            ConnectionDetail: state.Detail,
            CatalogMode: KebabCase.From(composition.CatalogMode.ToString()),
            LoadedDefinitionIds: composition.DefinitionCatalog.DefinitionsById.Keys
                .OrderBy(id => id, StringComparer.Ordinal).ToList(),
            ProvisioningEnabled: composition.Provisioning.Enabled,
            BackupProviderConfigured: composition.Capabilities.Get(ServyxCapability.BackupProvider).Available,
            Capabilities: composition.Capabilities.All.Select(CapabilityRow.From).ToList(),
            WritableServerKeys: writable.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList(),
            WithheldOperationsNotice: WithheldOperationsNotice);
    }
}
