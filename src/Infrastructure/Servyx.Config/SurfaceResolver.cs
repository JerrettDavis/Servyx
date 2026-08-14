using System.Text.RegularExpressions;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Transport;

namespace Servyx.Config;

/// <summary>
/// The default <see cref="ISurfaceResolver"/>: expands a declared surface's root variables against one
/// server's deployment facts, checks the resulting path against what the session can actually reach, and
/// reports whatever it cannot resolve instead of guessing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every refusal here is a write that would otherwise have landed somewhere wrong.</strong> The
/// three that matter most: an unexpanded <c>${VAR}</c> would produce a literal directory named
/// <c>${DATA_DIR}</c>; a container-rooted path on a host-scoped file channel would read nothing and write
/// into the SSH user's own filesystem; and a <see cref="SurfaceFormat"/> with no registered
/// <see cref="IConfigAdapter"/> would hand a null adapter to a parser. None of the three fails loudly on its
/// own, which is why all three are checked before a <see cref="ConfigSurface"/> is produced rather than
/// after.
/// </para>
/// <para>
/// <strong>Format ids are mapped explicitly, then looked up dynamically.</strong>
/// <see cref="DeclaredConfigSurface.Format"/> is a closed enum; <see cref="IConfigAdapter.FormatId"/> is a
/// string. <see cref="FormatIdFor"/> owns the one-way map between them — a compile-time-exhaustive switch,
/// so adding a <see cref="SurfaceFormat"/> member without deciding its id is a build error rather than a
/// runtime null. Whether an adapter for that id <em>exists</em> is a separate, dynamic question answered
/// against the injected <see cref="IConfigAdapter"/> set, so a format gains support the moment its adapter
/// is registered and needs no change here. Today <see cref="SurfaceFormat.Yaml"/> maps to <c>"yaml"</c> and
/// no adapter claims that id, so every shipped definition's <c>compose</c> surface resolves to an explicit
/// "no adapter registered" failure — not a crash, and not a silent omission.
/// </para>
/// <para>
/// <strong>Path construction goes through <see cref="SandboxedPathResolver"/> twice, not string concatenation
/// once.</strong> Containment is checked at both bounds and both must hold: the declared remainder against
/// the <c>${DATA_DIR}</c>/<c>${COMPOSE_DIR}</c> root it claims to be relative to, and the joined result
/// against the session's own root. The inner bound is not redundant — on the whole-host SSH/SFTP topology
/// the session root is <c>"/"</c>, which every absolute path satisfies, so the outer check alone would let
/// <c>${COMPOSE_DIR}/../../../etc/passwd</c> resolve. Definition YAML is semi-trusted (definitions can be
/// imported by an operator from outside this project), so a locator escaping either bound becomes a failure
/// entry rather than a path.
/// </para>
/// <para>
/// <strong>Containment is lexical and host-OS-sensitive, and that is inherited deliberately.</strong>
/// <see cref="SandboxedPathResolver"/> normalizes with <see cref="Path.GetFullPath(string)"/>, so
/// development runs on Windows and production runs against Linux targets do not agree in every corner:
/// containment compares case-insensitively on Windows and case-sensitively elsewhere, and a backslash is a
/// separator on Windows but an ordinary filename character on Linux. Neither difference lets a traversal
/// through on Linux that Windows would catch — <c>/</c> and <c>..</c> are handled identically on both, so
/// the tested behaviour is the conservative one. The residual divergence is that Windows may refuse a
/// locator Linux would have accepted (a literal backslash in a filename, or two surfaces differing only in
/// case). That is the same trade <c>SshBackupProvider</c> already makes with its own <c>HostPaths</c>
/// resolver, and <see cref="SandboxedPathResolver"/>'s remarks are the authority on why lexical containment
/// is not the last line of defence: infrastructure that turns a <see cref="TargetPath"/> into real I/O must
/// still canonicalize and re-verify, because no lexical check can see a symlink.
/// </para>
/// </remarks>
public sealed class SurfaceResolver : ISurfaceResolver
{
    /// <summary>
    /// Matches a leading <c>${DATA_DIR}</c> or <c>${COMPOSE_DIR}</c> root variable. Case-insensitive on the
    /// variable name to match <c>GameDefinitionYamlParser</c>'s own deliberate acceptance of
    /// <c>${data_dir}</c>/<c>${Data_Dir}</c>/<c>${DATA_DIR}</c> — a differently-cased spelling of a root
    /// Servyx does know must never be reported as an unrecognised one.
    /// </summary>
    private static readonly Regex RootVariable =
        new(@"^\$\{(DATA_DIR|COMPOSE_DIR)\}(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Any remaining <c>${...}</c> token after root expansion — always a refusal, never a literal.</summary>
    private static readonly Regex AnyVariable = new(@"\$\{[^}]*\}", RegexOptions.Compiled);

    private readonly ISurfaceResolutionContextSource _contexts;
    private readonly IReadOnlyDictionary<string, IConfigAdapter> _adaptersByFormatId;

    /// <summary>
    /// Creates a resolver over the per-server facts in <paramref name="contexts"/> and the currently
    /// registered <paramref name="adapters"/>.
    /// </summary>
    /// <param name="contexts">Supplies each server's session root, root-variable expansions, and capabilities.</param>
    /// <param name="adapters">
    /// The registered format adapters, keyed by <see cref="IConfigAdapter.FormatId"/>. Injected as a set
    /// rather than named individually so a newly registered adapter (a YAML one, when it lands) is picked up
    /// with no change to this type.
    /// </param>
    public SurfaceResolver(ISurfaceResolutionContextSource contexts, IEnumerable<IConfigAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(adapters);

        _contexts = contexts;
        _adaptersByFormatId = adapters.ToDictionary(a => a.FormatId, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<SurfaceResolution> ResolveAsync(
        string serverId,
        IExecutionTarget target,
        IReadOnlyList<DeclaredConfigSurface> surfaces,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(surfaces);

        var context = await _contexts.GetAsync(serverId, target, ct).ConfigureAwait(false);
        if (context is null)
        {
            // Not an error: the honest answer is one actionable failure per surface, so an operator sees
            // the whole set they are missing rather than a single exception naming the first one.
            return new SurfaceResolution(
                [],
                [.. surfaces.Select(s => new SurfaceResolutionFailure(
                    s.Id,
                    $"No surface-resolution context is known for server '{serverId}', so this surface's "
                    + "locator cannot be expanded against any real deployment.",
                    "Register an ISurfaceResolutionContextSource that maps this server to its session root, "
                    + "its '${DATA_DIR}' and '${COMPOSE_DIR}' expansions, and the capabilities of the "
                    + "transport its session was opened over."))]);
        }

        var resolved = new List<ConfigSurface>();
        var unresolvable = new List<SurfaceResolutionFailure>();

        foreach (var surface in surfaces)
        {
            ct.ThrowIfCancellationRequested();

            var outcome = Resolve(serverId, surface, context);
            if (outcome.Surface is { } configSurface)
            {
                resolved.Add(configSurface);
            }
            else
            {
                unresolvable.Add(outcome.Failure!);
            }
        }

        return new SurfaceResolution(resolved, unresolvable);
    }

    /// <summary>
    /// The whole per-surface decision, ordered cheapest-and-most-fundamental first so an operator is told
    /// about a missing adapter or an unknown root before being told about a capability they would only need
    /// once those were fixed.
    /// </summary>
    private (ConfigSurface? Surface, SurfaceResolutionFailure? Failure) Resolve(
        string serverId,
        DeclaredConfigSurface surface,
        SurfaceResolutionContext context)
    {
        if (surface.Locator is SurfaceLocator.ControlChannel channel)
        {
            // Excluded rather than resolved-with-a-null-path. This method's product is a path a caller can
            // hand to IExecutionTarget's file members, and a control-channel surface has none — putting one
            // in Resolved would push a null check onto every consumer and reintroduce exactly the class of
            // bug the nullable ConfigSurface.Path exists to make visible. Palworld's 'live' surface is the
            // shipped example.
            return (null, new SurfaceResolutionFailure(
                surface.Id,
                $"Surface '{surface.Id}' is served by control channel '{channel.ChannelId}' "
                + $"(query '{channel.Query}'), not by a file on the target, so it has no path to resolve.",
                $"Read this surface over the '{channel.ChannelId}' control channel instead. It is "
                + $"{surface.Role} and is never written through the filesystem."));
        }

        if (surface.Locator is not SurfaceLocator.HostFile hostFile)
        {
            return (null, new SurfaceResolutionFailure(
                surface.Id,
                $"Surface '{surface.Id}' uses locator kind '{surface.Locator.GetType().Name}', which this "
                + "resolver does not understand.",
                "Teach SurfaceResolver how to expand this locator kind, or declare the surface with a "
                + "'host-file' locator."));
        }

        var formatId = FormatIdFor(surface.Format);
        if (!_adaptersByFormatId.ContainsKey(formatId))
        {
            var registered = _adaptersByFormatId.Count == 0
                ? "none"
                : string.Join(", ", _adaptersByFormatId.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"));

            return (null, new SurfaceResolutionFailure(
                surface.Id,
                $"Surface '{surface.Id}' is declared as format '{formatId}', but no IConfigAdapter is "
                + $"registered for that format (registered: {registered}).",
                $"Register an IConfigAdapter whose FormatId is '{formatId}' — AddServyxConfig() is where the "
                + "built-in adapters are wired — before resolving this surface."));
        }

        var root = RootVariable.Match(hostFile.Path);
        if (!root.Success)
        {
            return (null, new SurfaceResolutionFailure(
                surface.Id,
                $"Surface '{surface.Id}' declares path '{hostFile.Path}', which does not begin with "
                + "'${DATA_DIR}' or '${COMPOSE_DIR}'. The root variable must be the very first thing in the "
                + "path — a variable appearing later (e.g. '/prefix/${DATA_DIR}/x') is not a root and is "
                + "refused for the same reason a bare absolute path is.",
                "Re-root the locator at one of the two variables Servyx models, as its leading segment. A "
                + "path that names no root cannot be placed on any specific filesystem, and every shipped "
                + "definition is already written this way."));
        }

        var isDataDir = string.Equals(root.Groups[1].Value, "DATA_DIR", StringComparison.OrdinalIgnoreCase);
        var rootValue = isDataDir ? context.DataDirectory : context.ComposeDirectory;
        if (string.IsNullOrWhiteSpace(rootValue))
        {
            var variable = isDataDir ? "DATA_DIR" : "COMPOSE_DIR";
            return (null, new SurfaceResolutionFailure(
                surface.Id,
                $"Surface '{surface.Id}' is rooted at '${{{variable}}}', but no expansion for that variable "
                + $"is known for server '{serverId}'.",
                isDataDir
                    ? "Populate SurfaceResolutionContext.DataDirectory from the deployment profile's "
                        + "'dataDir' (or the adopted container's reported mount path)."
                    : "Configure the host directory holding this server's compose file and '.env', and "
                        + "surface it as SurfaceResolutionContext.ComposeDirectory. It cannot be discovered "
                        + "from inside a container."));
        }

        var relative = hostFile.Path[root.Value.Length..].TrimStart('/');
        var rootBase = Trim(rootValue!);
        var declared = Join(rootBase, relative);

        if (AnyVariable.Match(declared) is { Success: true } leftover)
        {
            return (null, new SurfaceResolutionFailure(
                surface.Id,
                $"Surface '{surface.Id}' expands to '{declared}', which still contains the unresolved "
                + $"variable '{leftover.Value}'.",
                $"Only '${{DATA_DIR}}' and '${{COMPOSE_DIR}}' are expanded during surface resolution. Remove "
                + $"'{leftover.Value}' from the locator or resolve it before the surface set reaches the "
                + "resolver — writing to a directory literally named after the variable is never intended."));
        }

        // INNER BOUND. The declared remainder is contained against the root variable it claims to be
        // relative to, before it is joined onto anything. The outer SessionRoot check below is not a
        // substitute: on the whole-host SSH/SFTP topology SessionRoot is "/", which every absolute path
        // trivially satisfies, so '${COMPOSE_DIR}/../../../etc/passwd' would sail through it and — on an
        // Authoritative surface — come back carrying FileWrite. Definition YAML is semi-trusted input (an
        // operator can import a definition authored outside this project), so a locator must not be able to
        // name a file outside the directory it names as its root. Both bounds must hold.
        string contained;
        try
        {
            contained = new SandboxedPathResolver(rootBase).Resolve(relative).Value;
        }
        catch (PathEscapesSandboxException ex)
        {
            var variableName = isDataDir ? "DATA_DIR" : "COMPOSE_DIR";
            return (null, new SurfaceResolutionFailure(
                surface.Id,
                $"Surface '{surface.Id}' declares path '{hostFile.Path}', whose remainder "
                + $"'{relative}' escapes the '${{{variableName}}}' root it is declared relative to "
                + $"('{rootBase}'): {ex.Message}",
                $"Keep the locator inside '${{{variableName}}}'. A surface is only ever a file belonging to "
                + "this server's own deployment; a path that climbs out of its declared root names somebody "
                + "else's file, and Servyx will not write one."));
        }
        catch (ArgumentException ex)
        {
            var variableName = isDataDir ? "DATA_DIR" : "COMPOSE_DIR";
            return (null, new SurfaceResolutionFailure(
                surface.Id,
                $"Surface '{surface.Id}' could not be contained against its '${{{variableName}}}' root "
                + $"'{rootBase}': {ex.Message}",
                $"Supply an absolute, well-formed expansion for '${{{variableName}}}' in the "
                + "SurfaceResolutionContext."));
        }

        // Rebuilt from the contained, normalized remainder rather than the declared text, so a redundant but
        // harmless '..' (e.g. 'config/../config/server.properties') is collapsed rather than carried forward.
        var expanded = Join(rootBase, contained);

        // ${COMPOSE_DIR} is a host directory by definition: it is where the compose file and .env sit side
        // by side, which is not a place any container filesystem can serve. Only ${DATA_DIR} can be inside
        // the container, and only when the deployment says so.
        var containerScoped = isDataDir && context.DataDirectoryIsContainerScoped;
        if (containerScoped && !context.Capabilities.HasFlag(TransportCapabilities.ContainerScopedFiles))
        {
            return (null, new SurfaceResolutionFailure(
                surface.Id,
                $"Surface '{surface.Id}' lives inside the container at '{expanded}', but this session's file "
                + "operations reach the host filesystem: its transport does not advertise "
                + "TransportCapabilities.ContainerScopedFiles.",
                "Open the session over a transport whose file channel is rooted in the container (the Docker "
                + "Engine transport is the only one that is). Over a host-scoped channel this path does not "
                + "fail — it succeeds against the wrong filesystem, reading nothing and writing somewhere "
                + "real."));
        }

        // FileWrite is added strictly from ServyxMayWrite, which is computed from Role. A Derived or Runtime
        // surface therefore cannot acquire a write requirement here no matter what else is true of it —
        // including a declared mirror opt-in, which is deliberately NOT folded into this arithmetic; see
        // mirrorWritable below.
        var mayWrite = surface.Role == SurfaceRole.Authoritative;
        var required = TransportCapabilities.FileRead
            | (mayWrite ? TransportCapabilities.FileWrite : TransportCapabilities.None)
            | (containerScoped ? TransportCapabilities.ContainerScopedFiles : TransportCapabilities.None);

        var missing = required & ~context.Capabilities;
        if (missing != TransportCapabilities.None)
        {
            return (null, new SurfaceResolutionFailure(
                surface.Id,
                $"Surface '{surface.Id}' requires {required} from the session, which advertises only "
                + $"{context.Capabilities} — {missing} is missing.",
                mayWrite && missing.HasFlag(TransportCapabilities.FileWrite)
                    ? "Connect through a transport with file access (SFTP for SSH, the Engine API for "
                        + "Docker). An exec-only session cannot write this surface, and shelling out to "
                        + "'cat' would bypass the atomic-write and pre-image-hash guarantees "
                        + "IExecutionTarget.WriteFileAsync exists to provide."
                    : "Connect through a transport with file access (SFTP for SSH, the Engine API for "
                        + "Docker). An exec-only session cannot read this surface."));
        }

        TargetPath path;
        try
        {
            path = new SandboxedPathResolver(context.SessionRoot).Resolve(expanded);
        }
        catch (PathEscapesSandboxException ex)
        {
            return (null, new SurfaceResolutionFailure(
                surface.Id,
                $"Surface '{surface.Id}' expands to '{expanded}', which is not a valid path within this "
                + $"session's root '{context.SessionRoot}': {ex.Message}",
                "Correct the definition's locator, or open the session at a root that contains this path. "
                + "A surface outside the session root is unreachable, not merely awkward to address."));
        }
        catch (ArgumentException ex)
        {
            return (null, new SurfaceResolutionFailure(
                surface.Id,
                $"Surface '{surface.Id}' could not be resolved against session root "
                + $"'{context.SessionRoot}': {ex.Message}",
                "Supply an absolute, non-empty SurfaceResolutionContext.SessionRoot — it is the path every "
                + "TargetPath on this session is relative to."));
        }

        // THE MIRROR-WRITE GATE. Reported as a fact about this resolution, never as a requirement of it, and
        // opened only where all three of the following are simultaneously true:
        //
        //   1. the definition declared 'mirrorWrites: true' on THIS surface — the narrow, reviewable
        //      exception, not a role relaxation (see DeclaredConfigSurface.MirrorWrites);
        //   2. the surface really is Derived — an Authoritative one is already writable through the ordinary
        //      path and must not acquire a second one, and a Runtime one has no file at all; and
        //   3. the session actually advertises FileWrite.
        //
        // Rule 3 is what makes an SSH-connected host degrade honestly rather than silently: an exec-only
        // channel reports false here and a planner blocks the mirror action with a reason, instead of
        // planning a write that would fail confusingly at apply time. In practice a container-scoped surface
        // on an SSH session has already been refused above (a host-scoped file channel cannot serve a
        // container path), so this is the second of two independent refusals, not the only one.
        //
        // Note what is deliberately NOT checked here: whether any SETTING opts in, whether the operator's
        // per-server or per-row toggle is on, and whether the setting is sensitive. Those are per-setting
        // facts a per-surface resolver cannot see, and they are enforced where they are visible — a
        // surface reporting true here still mirrors nothing on its own.
        var mirrorWritable = surface.MirrorWrites
            && surface.Role == SurfaceRole.Derived
            && context.Capabilities.HasFlag(TransportCapabilities.FileWrite);

        return (
            new ConfigSurface(
                surface.Id,
                surface.Role,
                surface.Locator,
                formatId,
                surface.Codec,
                path,
                containerScoped,
                required,
                surface.CodecPath,
                surface.MergePolicy)
            {
                MirrorWritesDeclared = surface.MirrorWrites,
                MirrorWritable = mirrorWritable,
            },
            null);
    }

    /// <summary>
    /// Normalizes a root-variable expansion to a form <see cref="SandboxedPathResolver"/> accepts: no
    /// trailing separator, and never the empty string (a bare <c>"/"</c> root trims to nothing otherwise,
    /// and the resolver rejects a whitespace root).
    /// </summary>
    private static string Trim(string root)
    {
        var trimmed = root.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    /// <summary>Joins an already-trimmed root and a root-relative remainder with exactly one separator.</summary>
    private static string Join(string trimmedRoot, string relative) => relative.Length == 0
        ? trimmedRoot
        : trimmedRoot == "/" ? "/" + relative : $"{trimmedRoot}/{relative}";

    /// <summary>
    /// Maps the closed <see cref="SurfaceFormat"/> enum onto the <see cref="IConfigAdapter.FormatId"/>
    /// string an adapter registers under. Exhaustive by construction: there is no default arm, so a new
    /// <see cref="SurfaceFormat"/> member fails the build here until someone decides its id, rather than
    /// silently resolving to null at runtime.
    /// </summary>
    private static string FormatIdFor(SurfaceFormat format) => format switch
    {
        SurfaceFormat.Dotenv => "dotenv",
        SurfaceFormat.Yaml => "yaml",
        SurfaceFormat.Ini => "ini",
        SurfaceFormat.Json => "json",
        SurfaceFormat.Properties => "properties",
        _ => throw new ArgumentOutOfRangeException(
            nameof(format),
            format,
            "No IConfigAdapter format id is mapped for this SurfaceFormat member."),
    };
}

/// <summary>
/// The <see cref="ISurfaceResolutionContextSource"/> registered when nothing else is: it knows about no
/// server at all.
/// </summary>
/// <remarks>
/// A placeholder that refuses is deliberately preferred to leaving the dependency unregistered. An
/// unregistered dependency fails at container-validation time with a message about a missing service, which
/// tells an operator nothing about game servers; this one lets <see cref="SurfaceResolver"/> answer with one
/// named failure per surface, each pointing at the exact thing a composition root has yet to wire up. It
/// also keeps <see cref="ServiceCollectionExtensions.AddServyxConfig"/> self-contained — the package can be
/// registered and validated on its own — while a real source registered afterwards simply wins.
/// </remarks>
public sealed class UnconfiguredSurfaceResolutionContextSource : ISurfaceResolutionContextSource
{
    /// <inheritdoc />
    public Task<SurfaceResolutionContext?> GetAsync(
        string serverId,
        IExecutionTarget target,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(target);

        return Task.FromResult<SurfaceResolutionContext?>(null);
    }
}
