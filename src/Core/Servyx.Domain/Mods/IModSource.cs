namespace Servyx.Domain.Mods;

/// <summary>A reference to a specific mod, at a specific version, from a specific source.</summary>
/// <param name="SourceId">Identifier of the <see cref="IModSource"/> this mod comes from.</param>
/// <param name="ModId">Identifier of the mod within its source.</param>
/// <param name="Version">The specific version referenced.</param>
public sealed record ModRef(string SourceId, string ModId, string Version);

/// <summary>Descriptive metadata about a mod, as returned by search or list operations.</summary>
/// <param name="Ref">The mod being described.</param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="Description">Human-readable description, if available.</param>
/// <param name="Authors">Credited authors.</param>
public sealed record ModDescriptor(ModRef Ref, string Name, string? Description, IReadOnlyList<string> Authors);

/// <summary>A previewed mod install, with exact file operations and expected hashes.</summary>
/// <param name="Id">Identifier for this install plan.</param>
/// <param name="Mod">The mod that would be installed.</param>
/// <param name="FileOperations">Human-readable description of each file operation that would be performed.</param>
/// <param name="ExpectedHashes">Expected content hash for each file path this install would write.</param>
/// <param name="SourceUrls">The URLs content would be downloaded from.</param>
public sealed record ModInstallPlan(
    string Id,
    ModRef Mod,
    IReadOnlyList<string> FileOperations,
    IReadOnlyDictionary<string, string> ExpectedHashes,
    IReadOnlyList<string> SourceUrls);

/// <summary>A source of mods for a given game (e.g. a mod repository or workshop).</summary>
public interface IModSource
{
    /// <summary>Identifier of this mod source.</summary>
    string SourceId { get; }

    /// <summary>Whether this source carries mods for the given game.</summary>
    bool Supports(string gameId);

    /// <summary>Searches this source's catalogue.</summary>
    Task<IReadOnlyList<ModDescriptor>> SearchAsync(string gameId, string query, CancellationToken ct = default);

    /// <summary>Lists mods currently installed on the given server.</summary>
    Task<IReadOnlyList<ModDescriptor>> ListInstalledAsync(string serverId, CancellationToken ct = default);

    /// <summary>
    /// Returns the exact file operations an install would perform — expected hashes and source URLs —
    /// before anything is downloaded; the user approves a concrete file list, not a vendor's promise.
    /// </summary>
    Task<ModInstallPlan> PlanInstallAsync(string serverId, ModRef mod, CancellationToken ct = default);

    /// <summary>Executes a previously previewed install plan.</summary>
    Task InstallAsync(string installPlanId, CancellationToken ct = default);

    /// <summary>
    /// Uninstalls a mod. Reports, and leaves in place, any file that has changed since install rather
    /// than silently deleting it.
    /// </summary>
    Task UninstallAsync(string serverId, ModRef mod, CancellationToken ct = default);
}
