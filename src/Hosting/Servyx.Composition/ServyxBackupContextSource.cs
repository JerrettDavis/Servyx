using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Servyx.Application.Servers;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;
using Servyx.Infrastructure.Docker.Backups;
using Servyx.Infrastructure.Process;
using GameDefinition = Servyx.Domain.Definitions.Model.GameDefinition;
using DefinitionQuiesceStep = Servyx.Domain.Definitions.Model.QuiesceStep;

namespace Servyx.Composition;

/// <summary>
/// Turns a server id into the <see cref="DockerBackupContext"/> <c>DockerBackupProvider</c> needs: an
/// execution target for the adopted container, the root its backup paths are relative to, where Servyx
/// writes its own archives, and where the image's own cron archives already live.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the piece only the composition root can supply</strong> — see
/// <c>AddServyxDockerBackups</c>'s remarks. Turning "palworld-server" into a container, a data directory,
/// and a set of include globs is host knowledge, and a plausible default living in the provider would
/// silently back up the wrong paths.
/// </para>
/// <para>
/// <strong>Sessions are cached and owned here.</strong> The provider never disposes an
/// <see cref="IExecutionTarget"/> it is handed, per <see cref="IDockerBackupContextSource"/>'s contract, so
/// one session per server is created on first use and disposed when this service is. Creating a session
/// per call would open a Docker client per listing.
/// </para>
/// <para>
/// <strong>Quiesce is attached exactly when a control channel exists, and never otherwise.</strong> When
/// the operator has configured an RCON channel for a server (see <see cref="RconWiringOptions"/>), this
/// source fills both <see cref="DockerBackupContext.Control"/> and
/// <see cref="DockerBackupContext.Quiesce"/> with the definition's own step — <c>rcon</c> <c>save</c>, 30s
/// — and <c>DockerBackupProvider</c> issues it before a single byte is archived. When no channel is
/// configured, both stay <see langword="null"/>: the provider treats that as "no flush was asked for" and
/// archives on-disk state, exactly as it did before, recording the absence in the manifest's
/// <c>quiesceCommand</c> field so an archive taken without a flush is distinguishable from one taken with
/// it. Naming a quiesce step with no channel to issue it on is refused outright by
/// <c>DockerBackupProvider.CreateAsync</c>, and rightly.
/// </para>
/// <para>
/// <strong>A configured quiesce that fails produces no archive — there is no fallback, by design.</strong>
/// <c>DockerBackupProvider.QuiesceAsync</c> converts every failure route (a refusal from the write guard, a
/// rejected credential, an unreachable endpoint, a 30-second timeout, a <c>Success: false</c> reply) into
/// <c>BackupQuiesceFailedException</c> before <c>CollectAsync</c> is reached, so no archive and no manifest
/// are written. Continuing "best effort" would produce a file that looks exactly like a good backup and is
/// not one — and the operator would only find out at restore time. Turning the channel <em>off</em> is the
/// explicit, per-server way to say "archive without flushing"; it is never what a failure silently
/// degrades into.
/// </para>
/// <para>
/// <strong>The capture set is sourced from the loaded <see cref="GameDefinition"/>, not hand-copied.</strong>
/// <see cref="GetAsync"/> reads <c>backup.include</c>/<c>backup.exclude</c>/<c>backup.adopt</c>/
/// <c>backup.quiesce</c> off <paramref name="definition"/>'s already-typed <c>Backup</c> block when an
/// explicit <see cref="BackupWiringOptions"/> value does not override it — see
/// <see cref="BackupWiringOptions"/>'s own remarks for the exact precedence. A definition's include/exclude
/// globs are declared relative to two root variables, <c>${DATA_DIR}</c> and <c>${COMPOSE_DIR}</c>; the
/// former becomes this server's single container-rooted <see cref="BackupSource"/>
/// (<see cref="BackupWiringOptions.DataSourceId"/>), and the latter — paths like <c>.env</c> and
/// <c>compose.yaml</c> that live on the host next to the compose file, not inside the container — becomes a
/// second, host-rooted <see cref="BackupSource"/> (<see cref="BackupWiringOptions.ComposeSourceId"/>) built
/// over <paramref name="composeTransport"/> whenever <see cref="BackupWiringOptions.ComposeDirectory"/> is
/// configured. <c>DockerBackupProvider</c> needed no changes to support this: <c>CollectAsync</c> already
/// loops over every <c>DockerBackupContext.Sources</c> entry, and both capture and restore already resolve
/// an archive entry back to its source by the <see cref="BackupSource.Id"/> prefix it was written with — see
/// <c>DockerBackupProvider.MapEntryToSource</c>. Without a configured compose directory the second source is
/// simply never built, which is the same "declared but not yet operative" gap this milestone closes for
/// everything a container-rooted source <em>can</em> express.
/// </para>
/// </remarks>
public sealed class ServyxBackupContextSource : IDockerBackupContextSource, IAsyncDisposable
{
    private readonly IServerQueryService _query;
    private readonly ITransport _transport;
    private readonly BackupWiringOptions _options;
    private readonly ServyxRconChannels _rcon;
    private readonly GameDefinition? _definition;
    private readonly ITransport? _composeTransport;
    private readonly ConcurrentDictionary<string, Lazy<Task<IExecutionTarget>>> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a context source.</summary>
    /// <param name="query">Resolves a server id to the adopted container and its mount.</param>
    /// <param name="transport">The (write-guarded) transport sessions are opened through.</param>
    /// <param name="options">Where archives are read from and written to.</param>
    /// <param name="rcon">
    /// The configured RCON control channels. Defaults to <see cref="ServyxRconChannels.None"/>, which
    /// reproduces the pre-M2 behaviour exactly: no control channel, therefore no quiesce step, therefore an
    /// archive of on-disk state that says so in its manifest.
    /// </param>
    /// <param name="definition">
    /// The single loaded game definition, or null when none (or more than one) is loaded — the same
    /// "exactly one definition loaded" rule <c>Program.cs</c> already applies to lifecycle and settings. A
    /// null definition degrades every value this constructor would otherwise have sourced from
    /// <c>backup.*</c> to <paramref name="options"/>'s own built-in fallbacks, exactly as before this
    /// parameter existed.
    /// </param>
    /// <param name="composeTransport">
    /// A transport reaching the host directory named by <see cref="BackupWiringOptions.ComposeDirectory"/>,
    /// or null when that option is unset. Must already be a <c>WriteGuardedTransport</c> — the composition
    /// root builds it as one over <c>LocalProcessTransport</c> and a <see cref="ComposeWriteModeResolver"/>,
    /// the same "wrap at the construction site" shape every other transport in this process follows — never
    /// a static, directory-scoped grant independent of which server the session is actually for. See
    /// <see cref="ComposeWriteModeResolver"/>'s remarks for why a directory-scoped-only grant would bypass
    /// the per-server write guard.
    /// </param>
    public ServyxBackupContextSource(
        IServerQueryService query,
        ITransport transport,
        BackupWiringOptions options,
        ServyxRconChannels? rcon = null,
        GameDefinition? definition = null,
        ITransport? composeTransport = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);

        _query = query;
        _transport = transport;
        _options = options;
        _rcon = rcon ?? ServyxRconChannels.None;
        _definition = definition;
        _composeTransport = composeTransport;
    }

    /// <inheritdoc />
    public async Task<DockerBackupContext> GetAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var detail = await _query.GetServerDetailAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"'{serverId}' is not an adopted server on this Docker daemon, so there is nothing to back up.");

        var containerName = detail.Summary.Name;
        var root = Normalize(_options.ContainerDataRoot ?? detail.MountContainerPath
            ?? throw new InvalidOperationException(
                $"No container data root is known for '{serverId}': '{BackupWiringOptions.SectionKey}:ContainerDataRoot' "
                + "is not configured, and the adopted container reports no mount path to fall back to."));
        var target = await SessionAsync(containerName, detail.Summary.Id, root, ct).ConfigureAwait(false);

        // Null unless the operator configured an RCON channel for this server. The pair below is all-or-
        // nothing on purpose: a context carrying a quiesce step with no channel is refused by the provider,
        // and a context carrying a channel with no step would open a control session it never used.
        var control = await _rcon.GetSessionAsync(detail.Summary.Id, containerName, ct).ConfigureAwait(false);

        var backup = _definition?.Backup;
        var (definitionDataInclude, definitionComposeInclude, unrecognizedInclude) = SplitByRoot(backup?.Include ?? []);
        var (definitionDataExclude, _, unrecognizedExclude) = SplitByRoot(backup?.Exclude ?? []);

        // A declared glob that names no recognised root variable must never be silently dropped from the
        // capture set — that is exactly the class of bug this whole effort exists to eliminate. The parser
        // accepts '${data_dir}'/'${Data_Dir}'/'${DATA_DIR}' interchangeably (case-insensitive on purpose,
        // see ValidateContainedPath's remarks), and SplitRoot matches the same way, so this only fires for a
        // genuinely unrecognised variable — never a differently-cased spelling of one Servyx does know.
        var unrecognizedRoots = unrecognizedInclude.Concat(unrecognizedExclude).ToList();
        if (unrecognizedRoots.Count > 0)
        {
            throw new InvalidOperationException(
                $"'{serverId}''s game definition declares 'backup.include'/'backup.exclude' entries rooted "
                + "at a variable this deployment does not model — only '${DATA_DIR}' and '${COMPOSE_DIR}' "
                + $"are understood: {string.Join(", ", unrecognizedRoots.Select(e => $"'{e}'"))}. Refusing to "
                + "silently drop them from the backup capture set.");
        }

        // config > definition > built-in default. There is no built-in default for Include: a definition
        // that fails to load, with no explicit override configured either, must fail backups loudly rather
        // than silently archive nothing (an empty include set) or another game's paths (a Palworld-shaped
        // constant).
        var include = _options.Include.Count > 0 ? _options.Include : definitionDataInclude;
        if (include.Count == 0)
        {
            throw new InvalidOperationException(
                $"No backup capture set is configured for '{serverId}': no game definition is loaded and "
                + $"'{BackupWiringOptions.SectionKey}:Include' names no paths. Configure an explicit include "
                + "list, or load a game definition, before backing up this server.");
        }

        var exclude = _options.Exclude.Count > 0 ? _options.Exclude : definitionDataExclude;

        // adopt: the definition's own backup.adopt entry — path, filename pattern, and adapter id — takes
        // over from options'/the constants' fallback the same way include/exclude do above.
        //
        // The hardcoded PalworldCronBackupAdopter.Id fallback applies ONLY when NO definition is loaded at
        // all — matching docs/schema.md's own stated contract for this block ("falling back to ... the
        // bundled PalworldCronBackupAdopter only when no definition is loaded at all"). A definition that
        // DID load but genuinely declares no 'backup.adopt' entry — e.g. definitions/minecraft-itzg.yaml,
        // whose itzg/minecraft-server image ships no cron-based backup rotation of its own, unlike
        // thijsvanloef/palworld-server-docker — must report no foreign backup source at all, not silently
        // inherit Palworld's adapter id. Before this fix the fallback fired whenever the CURRENT
        // definition's own adopt list was empty, regardless of whether a definition had loaded — the exact
        // "abstraction leak" class of bug M6's second-game exercise exists to surface.
        var adopt = backup?.Adopt.FirstOrDefault();
        var adoptRoot = adopt is not null ? SplitRoot(adopt.Path) : null;
        var foreignDirectory = adoptRoot is { Root: "DATA_DIR" } dataAdopt ? dataAdopt.Relative : _options.ForeignDirectory;
        var foreignPattern = adopt?.Pattern ?? "*.tar.gz";
        var foreignAdapterId = adopt?.Adapter ?? (_definition is null ? PalworldCronBackupAdopter.Id : null);

        var sources = new List<BackupSource>
        {
            new(
                BackupWiringOptions.DataSourceId,
                target,
                root,
                include,
                [.. exclude, foreignDirectory, foreignDirectory + "/**"]),
        };

        // The host-rooted half of the capture set: ${COMPOSE_DIR}-relative paths like '.env' and
        // 'compose.yaml' the definition declares but no container filesystem can serve. Built only when the
        // operator has told Servyx where that host directory actually is — there is no way to discover it
        // from inside a container — and only when the definition (or a future config-driven list) actually
        // names something to capture there.
        if (_options.ComposeDirectory is { } composeDirectory
            && _composeTransport is not null
            && definitionComposeInclude.Count > 0)
        {
            // The 'containerName' option is what lets ComposeWriteModeResolver — baked into _composeTransport
            // at the composition root, over the SAME per-server grants the Docker session above is gated by —
            // answer "is THIS server writable" for the compose session, rather than granting writes to the
            // compose directory independent of which server it belongs to. See that resolver's remarks for
            // the write-guard bypass this closes, and ServyxBackupContextSourceWriteGuardTests for the
            // regression coverage.
            var composeTarget = await _composeTransport.ConnectAsync(
                new TargetDescriptor(
                    LocalProcessTransport.Id,
                    composeDirectory,
                    CredentialUrn: null,
                    DockerContext: null,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [LocalProcessTransport.RootPathOption] = composeDirectory,
                        [ComposeWriteModeResolver.ContainerIdOption] = detail.Summary.Id,
                        [ComposeWriteModeResolver.ContainerNameOption] = containerName,
                    }),
                ct).ConfigureAwait(false);

            sources.Add(new BackupSource(
                BackupWiringOptions.ComposeSourceId,
                composeTarget,
                composeDirectory,
                definitionComposeInclude,
                []));
        }

        // quiesce: config > definition's backup.quiesce > RconWiringOptions' built-in fallback. The command
        // id/timeout are only ever placed in the context below when a channel exists to issue them on
        // (control is null otherwise) — see the Quiesce assignment.
        var definitionQuiesce = backup?.Quiesce.OfType<DefinitionQuiesceStep.Control>().FirstOrDefault();
        var quiesceCommandId = _options.QuiesceCommandId ?? definitionQuiesce?.CommandId ?? RconWiringOptions.QuiesceCommandId;
        var quiesceTimeout = _options.QuiesceTimeout ?? definitionQuiesce?.Timeout ?? RconWiringOptions.QuiesceTimeout;

        // resume: the definition's 'backup.resume' block, taken whole rather than reduced to a first entry
        // the way quiesce is above. The undo of a quiesce is frequently more than one command, and dropping
        // all but the first would silently leave the server half-restored. There is no options override and
        // no built-in fallback on purpose: a resume command Servyx invented could re-enable something the
        // operator never asked to have disabled. Attached only when a channel exists — with no channel there
        // is no quiesce either, so there is nothing to undo.
        List<QuiesceStep> resume = control is null || backup is null
            ? []
            : [.. backup.Resume
                .OfType<DefinitionQuiesceStep.Control>()
                .Select(step => new QuiesceStep(step.CommandId, null, step.Timeout))];

        // Only declare a foreign source when there is an actual adapter id to attribute it to — either the
        // definition's own 'backup.adopt' entry, or (no definition loaded at all) the hardcoded legacy
        // default. A definition that loaded successfully but genuinely names no adopt source (see the
        // remarks above 'foreignAdapterId') gets an honestly empty Foreign list, never a fabricated entry
        // pointing at another game's cron adapter.
        var foreign = foreignAdapterId is null
            ? []
            : new List<ForeignBackupSource>
            {
                new(
                    foreignAdapterId,
                    target,
                    root,
                    foreignDirectory,
                    foreignPattern,
                    // The cron archives' entries are relative to the same data root this source reads, so
                    // they are restorable. A null here would make them listable and inspectable only.
                    RestoreSourceId: BackupWiringOptions.DataSourceId),
            };

        return new DockerBackupContext(
            ServerId: detail.Summary.Id,
            DeploymentKind: "docker",
            Sources: sources,
            Store: new BackupStore(target, root, _options.StoreDirectory),
            Foreign: foreign,
            DefaultRetention: _options.DefaultRetention,

            // The definition's own backup.quiesce entry — { kind: control, channel: rcon, command: save,
            // timeout: 30s } — attached only when there is a channel to issue it on. If it fails, the
            // provider raises BackupQuiesceFailedException and writes nothing at all; there is deliberately
            // no "archive anyway" path, because an un-flushed archive is indistinguishable from a good one
            // until the day someone restores it.
            Quiesce: control is null
                ? null
                : new QuiesceStep(quiesceCommandId, null, quiesceTimeout),
            Control: control)
        {
            // The definition's own backup.resume entries, run by DockerBackupProvider from a finally block
            // around capture. Empty for a definition that declares none, which is every definition written
            // before the key existed.
            Resume = resume,
        };
    }

    /// <summary>
    /// Matches a leading <c>${DATA_DIR}/</c> or <c>${COMPOSE_DIR}/</c> root variable, case-insensitively on
    /// the variable name only — matching <c>GameDefinitionYamlParser.ValidateContainedPath</c>'s own
    /// case-insensitive acceptance of <c>${data_dir}</c>/<c>${Data_Dir}</c>/<c>${DATA_DIR}</c>. The path
    /// segment after the closing brace stays exactly as authored: POSIX filesystems are case-sensitive, and
    /// only the token name is a symbolic root variable, never part of a real path.
    /// </summary>
    private static readonly Regex RootVariablePrefix =
        new(@"^\$\{(DATA_DIR|COMPOSE_DIR)\}/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Splits a definition-declared backup glob (or path) into the root variable it is relative to and the
    /// path relative to that root, or null when it names a root variable this deployment does not model.
    /// <see cref="SplitByRoot"/> collects a null result rather than discarding it, so a caller can fail
    /// loudly instead of silently treating an unrecognised root as "nothing to capture here".
    /// </summary>
    private static (string Root, string Relative)? SplitRoot(string raw)
    {
        var match = RootVariablePrefix.Match(raw);
        if (!match.Success)
        {
            return null;
        }

        var root = string.Equals(match.Groups[1].Value, "DATA_DIR", StringComparison.OrdinalIgnoreCase)
            ? "DATA_DIR"
            : "COMPOSE_DIR";

        return (root, raw[match.Length..]);
    }

    /// <summary>
    /// Splits a definition-declared glob list into its <c>${DATA_DIR}</c>- and <c>${COMPOSE_DIR}</c>-relative
    /// halves, plus any entry naming a root variable this deployment does not recognise at all — the caller
    /// (<see cref="GetAsync"/>) treats a non-empty <c>Unrecognized</c> list as a loud failure, never a silent
    /// drop.
    /// </summary>
    private static (IReadOnlyList<string> DataRelative, IReadOnlyList<string> ComposeRelative, IReadOnlyList<string> Unrecognized) SplitByRoot(
        IReadOnlyList<string> raw)
    {
        var dataRelative = new List<string>();
        var composeRelative = new List<string>();
        var unrecognized = new List<string>();

        foreach (var entry in raw)
        {
            switch (SplitRoot(entry))
            {
                case { Root: "DATA_DIR" } data:
                    dataRelative.Add(data.Relative);
                    break;
                case { Root: "COMPOSE_DIR" } compose:
                    composeRelative.Add(compose.Relative);
                    break;
                default:
                    unrecognized.Add(entry);
                    break;
            }
        }

        return (dataRelative, composeRelative, unrecognized);
    }

    /// <summary>
    /// Opens (and caches) the container-rooted session every <see cref="BackupSource"/>, the
    /// <see cref="BackupStore"/>, and every <see cref="ForeignBackupSource"/> this context hands to
    /// <c>DockerBackupProvider</c> is served through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The transport is checked before it is asked, and an unfit one is refused.</strong> The
    /// descriptor built below names a container and a root <em>inside</em> it; honouring it requires a
    /// transport whose file members reach the container's filesystem, which is exactly what
    /// <see cref="TransportCapabilities.ContainerScopedFiles"/> declares. The ambient <see cref="ITransport"/>
    /// is whatever the composition root last registered, and <c>AddServyxSshDocker</c> legitimately replaces
    /// the Docker Engine transport with the ssh+docker one — which is container-correct on its control plane
    /// (<c>docker start</c>, discovery, logs, metrics all name the container in argv) and host-scoped on its
    /// file plane (SFTP against the SSH host's root, no notion of a container). Handing it this descriptor
    /// does not fail: it silently strips the container root, so a capture archives nothing while reporting
    /// success, and a restore writes the archive's bytes to real paths on the SSH host.
    /// </para>
    /// <para>
    /// <strong>Every backup operation flows through here, so refusing here refuses all of them.</strong>
    /// <see cref="GetAsync"/> is the only way to obtain a <see cref="DockerBackupContext"/>, and it always
    /// awaits this — so create, list, inspect, plan, restore and prune are each refused, and so is the
    /// foreign-archive adoption that reads through the same target. The check is opt-in on the transport's
    /// side (an absent flag refuses) so a transport added later cannot inherit the misrouting by saying
    /// nothing. It is made per call rather than in the constructor because this service is registered
    /// unconditionally: a deployment whose backups are configured over
    /// <c>Servyx:Servers:&lt;name&gt;:Ssh</c> — served by <c>ServyxSshBackupContextSource</c>, which archives
    /// host paths deliberately and correctly — must still start.
    /// </para>
    /// </remarks>
    /// <exception cref="ContainerScopedFilesNotSupportedException">
    /// The ambient transport does not provide container-scoped file operations.
    /// </exception>
    private Task<IExecutionTarget> SessionAsync(string containerName, string containerId, string root, CancellationToken ct)
    {
        RequireContainerScopedFiles(containerName, root);

        var lazy = _sessions.GetOrAdd(
            containerName,
            key => new Lazy<Task<IExecutionTarget>>(
                () => _transport.ConnectAsync(BuildDockerDescriptor(key, containerId, root), ct)));

        return lazy.Value;
    }

    /// <summary>
    /// Refuses to open a container-rooted session on a transport whose file operations are not themselves
    /// container-scoped. See <see cref="SessionAsync"/>'s remarks for why an unflagged transport is treated
    /// as unfit rather than trusted.
    /// </summary>
    private void RequireContainerScopedFiles(string containerName, string root)
    {
        if (_transport.Capabilities.HasFlag(TransportCapabilities.ContainerScopedFiles))
        {
            return;
        }

        throw new ContainerScopedFilesNotSupportedException(
            $"Refusing to back up '{containerName}': the configured '{_transport.TransportId}' transport does "
            + $"not provide container-scoped file access, so every path under the container root '{root}' "
            + "would be read from — and, on restore, written to — the remote host's own filesystem instead of "
            + "the container's. Capture would produce an empty archive that looks like a successful backup, "
            + "and restore would write its contents outside any container. Reach this daemon over a Docker "
            + "Engine endpoint to back it up, or configure the server's backups under "
            + "'Servyx:Servers:<name>:Ssh', which archives host paths deliberately.",
            _transport.TransportId,
            containerName,
            root);
    }

    /// <summary>Builds the <see cref="TargetDescriptor"/> a Docker session for <paramref name="containerName"/> is opened against.</summary>
    private static TargetDescriptor BuildDockerDescriptor(string containerName, string containerId, string root) =>
        new(
            "docker",
            DockerEndpointResolver.Resolve(explicitEndpoint: null).ToString(),
            CredentialUrn: null,
            DockerContext: null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // containerId is the identity the operator's per-server write grant is keyed on — a container
                // name can be reassigned to a different workload outside Servyx at any time, so a grant is
                // never honoured against one. Without it here, every backup session would resolve read-only
                // and a genuinely-granted server could not be restored to. The name is carried alongside it
                // because it is what refusal messages show an operator, and because the compose session in
                // GetAsync is attributed to the same server through both.
                ["containerId"] = containerId,
                ["containerName"] = containerName,
                ["rootPath"] = root,
            });

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _sessions.Values)
        {
            if (!lazy.IsValueCreated)
            {
                continue;
            }

            try
            {
                await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A session that failed to open, or that the daemon already closed, must not stop the
                // remaining sessions from being released during shutdown.
            }
        }

        _sessions.Clear();
    }

    private static string Normalize(string root)
    {
        var normalized = root.Replace('\\', '/').TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }
}
