using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;

namespace Servyx.Composition;

/// <summary>One file under a save world's player directory — see <see cref="SavesReadResult"/>.</summary>
public sealed record SavesReadPlayerFile(string FileName, long SizeBytes);

/// <summary>
/// Read-only view of a server's save world, as read by <see cref="ServerSavesReader"/>. Models exactly one
/// world: when a definition's <c>saves.worldRoot</c> holds more than one world directory (an old save kept
/// alongside the active one, for instance), <see cref="ServerSavesReader"/> picks the most-recently-modified
/// — see <see cref="ServerSavesReader.ReadServerSavesAsync"/>'s remarks for why that, rather than a list, is
/// the deliberate choice here.
/// </summary>
public sealed record SavesReadWorld(
    string WorldId,
    string LevelFileName,
    long LevelFileSizeBytes,
    string LevelMetaFileName,
    long LevelMetaFileSizeBytes,
    IReadOnlyList<SavesReadPlayerFile> PlayerFiles,
    bool WorldCandidatesTruncated = false,
    bool PlayerFilesTruncated = false);

/// <summary>
/// Mirrors <c>Servyx.Web.Models.SavesAvailability</c>'s four-way shape — see that type's remarks for what
/// each case means. Kept as a distinct type here so <c>Servyx.Composition</c> does not need to reference
/// <c>Servyx.Web</c>'s presentation models; <c>LiveDashboardDataService.GetServerSavesWithStatusAsync</c> maps
/// this enum onto its own one-for-one.
/// </summary>
public enum SavesReadAvailability
{
    /// <summary>The world root was read successfully — see <see cref="SavesReadResult.Save"/>.</summary>
    Listed,

    /// <summary>The world root could not be read. Not the same fact as "there are none".</summary>
    Failed,

    /// <summary>No save layout is available to read at all. Distinct from <see cref="Failed"/> — nothing was even attempted.</summary>
    NotConfigured,

    /// <summary>The wired transport is not container-scoped and reading through it could silently be wrong.</summary>
    UnsupportedTransport,
}

/// <summary>
/// Result of reading a single server's save world, distinguishing a genuine (possibly empty) read from a
/// read failure from "nothing is configured to read saves at all" from "this deployment's transport cannot
/// safely be read for saves" — see <see cref="SavesReadAvailability"/>.
/// </summary>
/// <param name="Save">The world found, when <see cref="Availability"/> is <see cref="SavesReadAvailability.Listed"/> and one matched; otherwise <see langword="null"/>.</param>
/// <param name="Availability">Which of the four cases this result reports.</param>
/// <param name="FailureDetail">Present when <paramref name="Availability"/> is <see cref="SavesReadAvailability.Failed"/> or <see cref="SavesReadAvailability.UnsupportedTransport"/>.</param>
public sealed record SavesReadResult(SavesReadWorld? Save, SavesReadAvailability Availability, string? FailureDetail);

/// <summary>
/// The whole of the definition-driven, read-only "inspect a server's save world" implementation — extracted
/// out of <c>LiveDashboardDataService.GetServerSavesWithStatusAsync</c> (and its private helpers) so a second
/// host can offer save inspection without depending on <c>Servyx.Web</c>. <c>LiveDashboardDataService</c>
/// itself becomes a thin adapter that calls <see cref="ReadServerSavesAsync"/> and maps
/// <see cref="SavesReadResult"/> onto its own <c>Servyx.Web.Models.SavesResult</c> shape.
/// </summary>
public static class ServerSavesReader
{
    /// <summary>Deadline for the whole connect-list-stat sequence a single <see cref="ReadServerSavesAsync"/> call performs.</summary>
    private static readonly TimeSpan SavesReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Caps how many entries under <c>saves.worldRoot</c> are considered as world-directory candidates, so a
    /// directory with an unexpectedly large fan-out cannot turn one page load into unbounded enumeration.
    /// </summary>
    private const int MaxWorldDirectoriesScanned = 200;

    /// <summary>Caps how many files under the chosen world's <c>saves.playerDir</c> are listed, for the same reason.</summary>
    private const int MaxPlayerFilesListed = 500;

    private static readonly Regex DataDirPrefix = new(@"^\$\{DATA_DIR\}/?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The single loaded <see cref="GameDefinition"/>, or <see langword="null"/> when zero or more than one
    /// is loaded — the same rule <c>ServyxCoreComposition</c>'s single-definition mode and
    /// <c>ServyxBackupContextSource</c> already apply. Saves has no per-server definition binding (out of
    /// scope for this milestone, same as backups), so this is the only honest way to find "the" definition a
    /// server's save layout comes from.
    /// </summary>
    private static GameDefinition? SingleLoadedDefinition(GameDefinitionCatalog? catalog) =>
        catalog is { DefinitionsById.Count: 1 } ? catalog.DefinitionsById.Values.Single().Document as GameDefinition : null;

    /// <summary>Builds the <see cref="TargetDescriptor"/> a saves-inspection session for <paramref name="containerName"/> is opened against.</summary>
    /// <remarks>
    /// Deliberately identical in shape to <c>ServyxBackupContextSource.BuildDockerDescriptor</c> — "docker"
    /// transport id, <c>containerName</c>/<c>rootPath</c> options — rather than a second, independently
    /// evolving way to address the same container. See that method's remarks for the write-guard-grant key
    /// this shares.
    /// </remarks>
    private static TargetDescriptor BuildSavesDescriptor(string containerName, string dataRoot) =>
        new(
            "docker",
            DockerEndpointResolver.Resolve(explicitEndpoint: null).ToString(),
            CredentialUrn: null,
            DockerContext: null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["containerName"] = containerName,
                ["rootPath"] = dataRoot,
            });

    /// <summary>
    /// Strips a leading <c>${DATA_DIR}/</c> (case-insensitive on the variable name only, matching
    /// <c>GameDefinitionYamlParser</c>'s own acceptance of <c>${data_dir}</c>/<c>${Data_Dir}</c>/<c>${DATA_DIR}</c>)
    /// from a definition-declared saves path, leaving whatever remains — including the whole string
    /// unchanged when it does not start with the variable at all — to be resolved relative to the server's
    /// data root by <see cref="SandboxedPathResolver"/>.
    /// </summary>
    private static string StripDataDirPrefix(string raw)
    {
        var match = DataDirPrefix.Match(raw);
        return match.Success ? raw[match.Length..] : raw;
    }

    /// <summary>
    /// Compiles a definition-authored <c>worldIdPattern</c> under <see cref="RegexOptions.NonBacktracking"/>
    /// — guaranteed linear-time matching — with an explicit <see cref="Regex.MatchTimeout"/> as a second,
    /// belt-and-suspenders bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Deliberately no backtracking fallback.</strong> An earlier version of this method caught
    /// <see cref="NotSupportedException"/> (thrown when a pattern needs a backreference/lookaround the
    /// linear-time engine cannot run) and fell back to <see cref="RegexOptions.Compiled"/> with the same
    /// timeout. That fallback was dead code reachable only by bypassing the parser, and worse, unsound if it
    /// ever <em>had</em> been reached: <see cref="Regex.MatchTimeout"/> bounds one match attempt, not the
    /// loop that calls it — up to <see cref="MaxWorldDirectoriesScanned"/> adversarial directory names could
    /// each cost up to a full timeout, which the <see cref="SavesReadTimeout"/>
    /// <see cref="CancellationTokenSource"/> deadline does <em>not</em> preempt, because a synchronous
    /// CPU-bound <c>Regex.IsMatch</c> call does not observe a cancellation token. Deleting the fallback
    /// removes that risk entirely rather than merely asserting it is unreachable.
    /// </para>
    /// <para>
    /// <strong>This relies on a real, enforced invariant, not a hope.</strong>
    /// <c>GameDefinitionYamlParser.ValidateSafeRegex</c> compiles every definition-declared
    /// <c>saves.worldIdPattern</c> under <see cref="RegexOptions.NonBacktracking"/> at load time and rejects
    /// the whole definition with a validation <c>Error</c> if that fails (see
    /// <c>GameDefinitionYamlParser.SavesAndMods.cs</c>) — so any <see cref="SavesLayout"/> reachable through
    /// <see cref="GameDefinitionCatalog"/> is guaranteed to compile here too. The only way to reach a pattern
    /// that does not is to construct a <see cref="SavesLayout"/> directly, bypassing the parser (exactly what
    /// this codebase's own adversarial-pattern test does, deliberately) — and for that case, throwing
    /// synchronously and immediately (caught by <see cref="ReadServerSavesAsync"/>'s outer handler and
    /// reported as <see cref="SavesReadAvailability.Failed"/>) is strictly safer than the deleted fallback:
    /// the failure is instant, not paid for once per directory.
    /// </para>
    /// </remarks>
    private static Regex? CompileWorldIdPattern(string? pattern) =>
        string.IsNullOrEmpty(pattern) ? null : new Regex(pattern, RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Reads <paramref name="layout"/>'s world root under <paramref name="target"/>, sandboxed to
    /// <paramref name="dataRoot"/>, and returns the most-recently-modified matching world as a
    /// <see cref="SavesReadWorld"/> — or a null save, still <see cref="SavesReadAvailability.Listed"/>, when
    /// the root does not exist or holds no matching world. See <see cref="ReadServerSavesAsync"/>'s remarks
    /// for why a missing path is "empty" rather than "failed" at this point in the call.
    /// </summary>
    private static async Task<SavesReadResult> ReadSavesAsync(
        IExecutionTarget target, string dataRoot, SavesLayout layout, CancellationToken ct)
    {
        var resolver = new SandboxedPathResolver(dataRoot);
        var worldRootRelative = StripDataDirPrefix(layout.WorldRoot);

        IReadOnlyList<FileEntry> worldEntries;
        try
        {
            worldEntries = await target.ListDirectoryAsync(resolver.Resolve(worldRootRelative), ct).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException)
        {
            // The world root does not exist (yet) inside a container GetServerDetailAsync already confirmed
            // is adopted — a genuine, trustworthy "no saves", never "could not read".
            return new SavesReadResult(null, SavesReadAvailability.Listed, null);
        }

        var pattern = CompileWorldIdPattern(layout.WorldIdPattern);
        var matching = worldEntries
            .Where(e => e.IsDirectory)
            .Where(e => pattern is null || SafeIsMatch(pattern, e.Name))
            .ToList();

        // Computed against the full matching set, before the cap below is applied — this is what makes
        // WorldCandidatesTruncated an honest signal that "most recently modified" was decided among fewer
        // than the true full set, rather than always false because it was measured on the already-capped list.
        var worldCandidatesTruncated = matching.Count > MaxWorldDirectoriesScanned;
        var candidates = matching.Take(MaxWorldDirectoriesScanned).ToList();

        if (candidates.Count == 0)
        {
            return new SavesReadResult(null, SavesReadAvailability.Listed, null);
        }

        // Deterministic pick when more than one world directory exists (e.g. an old save kept alongside the
        // active one): most-recently-modified first, then ordinal name as a stable tiebreak. Never "whatever
        // the target happened to enumerate first" — see SavesReadWorld's remarks.
        var chosen = candidates
            .OrderByDescending(e => e.ModifiedAt ?? DateTimeOffset.MinValue)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .First();

        var worldRelative = worldRootRelative.Length == 0 ? chosen.Name : worldRootRelative.TrimEnd('/') + "/" + chosen.Name;

        var levelStat = await target.StatAsync(resolver.Resolve(worldRelative + "/" + layout.LevelFile), ct).ConfigureAwait(false);
        var metaStat = await target.StatAsync(resolver.Resolve(worldRelative + "/" + layout.MetaFile), ct).ConfigureAwait(false);

        var playerFiles = new List<SavesReadPlayerFile>();
        var playerFilesTruncated = false;
        if (!string.IsNullOrEmpty(layout.PlayerDir))
        {
            try
            {
                var playerEntries = await target
                    .ListDirectoryAsync(resolver.Resolve(worldRelative + "/" + layout.PlayerDir), ct)
                    .ConfigureAwait(false);

                var files = playerEntries.Where(e => !e.IsDirectory).ToList();
                playerFilesTruncated = files.Count > MaxPlayerFilesListed;
                playerFiles = files
                    .Take(MaxPlayerFilesListed)
                    .OrderBy(e => e.Name, StringComparer.Ordinal)
                    .Select(e => new SavesReadPlayerFile(e.Name, e.SizeBytes ?? 0))
                    .ToList();
            }
            catch (DirectoryNotFoundException)
            {
                // No player has joined this world yet — still a genuine, populated SavesReadWorld, just with
                // no player files, exactly like the bundled definition's own Players/ directory before anyone
                // connects.
            }
        }

        var save = new SavesReadWorld(
            WorldId: chosen.Name,
            LevelFileName: layout.LevelFile,
            LevelFileSizeBytes: levelStat.SizeBytes ?? 0,
            LevelMetaFileName: layout.MetaFile,
            LevelMetaFileSizeBytes: metaStat.SizeBytes ?? 0,
            PlayerFiles: playerFiles,
            WorldCandidatesTruncated: worldCandidatesTruncated,
            PlayerFilesTruncated: playerFilesTruncated);

        return new SavesReadResult(save, SavesReadAvailability.Listed, null);
    }

    /// <summary>
    /// Evaluates <paramref name="pattern"/> against <paramref name="candidate"/>, treating a
    /// <see cref="RegexMatchTimeoutException"/> as "did not match" — the same "one adversarial input fails
    /// this match, scanning continues" treatment a definition-authored regex gets elsewhere, rather than
    /// letting a single pathological world-directory name fail the whole read.
    /// </summary>
    private static bool SafeIsMatch(Regex pattern, string candidate)
    {
        try
        {
            return pattern.IsMatch(candidate);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads server <paramref name="serverId"/>'s save world, driven entirely by the loaded definition's
    /// <c>saves</c> block (<see cref="SavesLayout"/>) — there is no hardcoded per-game path here.
    /// <see cref="SavesReadAvailability.NotConfigured"/> covers every case where nothing can even be
    /// attempted: no single game definition is loaded (the same "exactly one definition loaded" rule
    /// <c>ServyxBackupContextSource</c> applies), the loaded definition declares no <c>saves</c> block, or no
    /// <paramref name="transport"/> was supplied at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Reachability, not existence, is what separates <see cref="SavesReadAvailability.Failed"/> from
    /// a genuinely empty <see cref="SavesReadAvailability.Listed"/>.</strong> By the time this method lists
    /// <c>saves.worldRoot</c>, <paramref name="query"/>'s server-detail lookup has already confirmed the
    /// container exists, so a "path not found" response from the execution target honestly means "no world
    /// has been created there yet" (<see cref="SavesReadAvailability.Listed"/> with a null save) — while a
    /// connection failure, a container that vanished between the two calls, or a definition-declared path
    /// that fails <see cref="SandboxedPathResolver"/> containment all surface as
    /// <see cref="SavesReadAvailability.Failed"/>, never silently as "empty".
    /// </para>
    /// <para>
    /// <strong>Session lifetime.</strong> No session is cached: callers are expected to be plain DI singletons
    /// with no disposal hook of their own, and the Saves tab is loaded rarely enough that opening and
    /// disposing a fresh session per call is the simpler, safer choice over adding one.
    /// </para>
    /// <para>
    /// <strong>Bounding.</strong> The whole read — connect, list, stat — is wrapped in a
    /// <see cref="SavesReadTimeout"/> deadline, and both the number of world directories considered and the
    /// number of player files listed are capped (<see cref="MaxWorldDirectoriesScanned"/>,
    /// <see cref="MaxPlayerFilesListed"/>). Those caps bound what is <em>considered</em> after a directory
    /// listing comes back, not the cost of producing that listing in the first place — a truly enormous save
    /// directory therefore still costs real time and memory to enumerate even though only the first entries
    /// are kept — a real, documented limitation, not a silently unbounded one. What <em>is</em> capped is
    /// what gets shown: a truncated read is never presented as complete — see
    /// <see cref="SavesReadWorld.WorldCandidatesTruncated"/>/<see cref="SavesReadWorld.PlayerFilesTruncated"/>.
    /// </para>
    /// <para>
    /// <strong>Transport gating.</strong> Only a transport that declares
    /// <see cref="TransportCapabilities.ContainerScopedFiles"/> is safe to read through. When ssh+docker is
    /// wired instead, the same <see cref="TargetDescriptor"/> this method would build resolves against the
    /// SSH host's own filesystem, not the container's, so a container-internal path becomes a literal path
    /// segment on the SSH host. Reading through that would risk displaying host files as container save data,
    /// which is worse than not reading at all, so this method checks <see cref="ITransport.Capabilities"/>
    /// before opening any session and reports <see cref="SavesReadAvailability.UnsupportedTransport"/> instead
    /// of attempting a read whose result could be silently wrong.
    /// </para>
    /// </remarks>
    public static async Task<SavesReadResult> ReadServerSavesAsync(
        IServerQueryService query,
        ITransport? transport,
        GameDefinitionCatalog? catalog,
        string serverId,
        ILogger? logger,
        CancellationToken ct = default)
    {
        var layout = SingleLoadedDefinition(catalog)?.Saves;
        if (layout is null)
        {
            return new SavesReadResult(null, SavesReadAvailability.NotConfigured, null);
        }

        if (transport is null)
        {
            return new SavesReadResult(null, SavesReadAvailability.NotConfigured, null);
        }

        // Refuse before opening a single session: only a transport whose file operations are themselves
        // container-scoped is safe here. ssh+docker's file operations resolve against the SSH host's own
        // filesystem, not the container's — see SavesReadAvailability.UnsupportedTransport's remarks and this
        // method's own remarks. Checked via TransportCapabilities.ContainerScopedFiles, not by attempting a
        // read and hoping it fails safely: WriteGuardedTransport delegates Capabilities to whatever it
        // wraps, so this is a cheap, purely local check that never touches the network.
        if (!transport.Capabilities.HasFlag(TransportCapabilities.ContainerScopedFiles))
        {
            return new SavesReadResult(
                null,
                SavesReadAvailability.UnsupportedTransport,
                $"This process is wired to the '{transport.TransportId}' transport, which does not provide "
                + "container-scoped file access. Save inspection requires a transport whose file operations "
                + "are rooted inside the container, so nothing was attempted.");
        }

        using var timeoutCts = new CancellationTokenSource(SavesReadTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var detail = await query.GetServerDetailAsync(serverId, linked.Token).ConfigureAwait(false);
            if (detail is null)
            {
                return new SavesReadResult(null, SavesReadAvailability.Failed, $"'{serverId}' is not an adopted server.");
            }

            var dataRoot = detail.MountContainerPath;
            if (string.IsNullOrWhiteSpace(dataRoot))
            {
                return new SavesReadResult(
                    null, SavesReadAvailability.Failed, $"No container data root is known for '{serverId}'.");
            }

            var target = await transport.ConnectAsync(BuildSavesDescriptor(detail.Summary.Name, dataRoot), linked.Token)
                .ConfigureAwait(false);
            try
            {
                return await ReadSavesAsync(target, dataRoot, layout, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                await target.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SavesReadResult(null, SavesReadAvailability.Failed, $"Reading save data timed out after {SavesReadTimeout}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read save data for server '{ServerId}'.", serverId);
            return new SavesReadResult(null, SavesReadAvailability.Failed, ex.Message);
        }
    }
}
