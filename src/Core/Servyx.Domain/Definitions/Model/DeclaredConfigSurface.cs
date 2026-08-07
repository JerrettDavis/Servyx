using Servyx.Domain.Configuration;

namespace Servyx.Domain.Definitions.Model;

/// <summary>The configuration format a <see cref="DeclaredConfigSurface"/> is parsed as.</summary>
/// <remarks>
/// <para>
/// Closed, unlike <see cref="ControlChannelDefinition.Protocol"/>, even though both fields exist for the
/// same surface-level reason <see cref="IConfigAdapter.FormatId"/> does. The distinguishing principle: a
/// format names the parser Servyx itself must ship to read a surface — there is no such thing as "an
/// unrecognized format that still works", because nothing in this codebase can parse it. That closes the set
/// to what <see cref="IConfigAdapter"/> implementations actually exist for. A protocol, by contrast, is
/// resolved by adapter key against machinery that lives entirely outside a single surface parse (readiness
/// probes, RCON reachability strategies, control-channel sessions) and a new game is expected to register a
/// new protocol id without any change to this codebase's closed types — see the remarks on
/// <see cref="ControlChannelDefinition.Protocol"/>.
/// </para>
/// <para>
/// <c>docs/schema.md</c> describes <c>format</c> as an open set ("dotenv, yaml, ini, json, etc."), but this
/// model tightens it to the four formats actually used by <c>definitions/palworld-docker.yaml</c>. A
/// definition needing a fifth format will need a new member here alongside its own
/// <see cref="IConfigAdapter"/> — a deliberate, reviewed addition rather than an open string that a typo
/// could silently fail to match against any registered adapter.
/// </para>
/// </remarks>
public enum SurfaceFormat
{
    /// <summary><c>KEY=value</c> files, e.g. Docker Compose's <c>.env</c>.</summary>
    Dotenv,

    /// <summary>YAML documents, e.g. a Compose file.</summary>
    Yaml,

    /// <summary>INI files.</summary>
    Ini,

    /// <summary>JSON documents or control-channel responses.</summary>
    Json,

    /// <summary>
    /// Java <c>.properties</c> files: flat <c>key=value</c> lines, <c>#</c>/<c>!</c> comments, no section
    /// headers and no quoting — e.g. Minecraft's <c>server.properties</c>. Added for
    /// <c>definitions/minecraft-itzg.yaml</c>: this format is close to <see cref="Dotenv"/> in shape (both
    /// are flat key/value text) but distinct enough in convention (dotted keys such as <c>rcon.password</c>,
    /// no quoting, no <c>export</c> prefix, <c>!</c> as an additional comment marker) to earn its own format
    /// id and its own <see cref="Servyx.Domain.Configuration.IConfigAdapter"/> rather than being folded into
    /// <see cref="Dotenv"/> — see <c>Servyx.Config.PropertiesConfigAdapter</c>.
    /// </summary>
    Properties,
}

/// <summary>
/// One entry of a <see cref="DeploymentProfile"/>'s <c>config.surfaces</c> list: describes one place
/// configuration lives, as a definition author declared it.
/// </summary>
/// <remarks>
/// <para>
/// Reuses <see cref="SurfaceRole"/>, <see cref="SurfaceLocator"/>, <see cref="MergePolicy"/>, and
/// <see cref="RegenerationTrigger"/> from <see cref="Servyx.Domain.Configuration"/> — the runtime config
/// engine's own vocabulary for these exact concepts — rather than duplicating them under this namespace.
/// </para>
/// <para>
/// Deliberately named <see cref="DeclaredConfigSurface"/> rather than <c>ConfigSurface</c>, even though it
/// lives in a different namespace from <see cref="Servyx.Domain.Configuration.ConfigSurface"/> and would not
/// collide at compile time. Two identically-named types across <c>Servyx.Domain</c> namespaces is a real
/// hazard on its own: no <c>using</c> disambiguates a bare reference, IntelliSense collapses the two in
/// search, and a log line or stack trace reading <c>ConfigSurface.Id</c> gives no signal which stage broke.
/// The two types are a genuine two-stage split, not a coincidence: <see cref="Servyx.Domain.Configuration.ConfigSurface"/>
/// is the engine's lighter, resolved runtime shape (id, role, locator, and a string format/codec id) that
/// <c>IPlanExecutor</c>/<c>ISettingStateResolver</c> operate on, with no <see cref="ManagedSubtree"/>,
/// <see cref="DerivedFrom"/>, or <see cref="Regeneration"/> — those are parse-time concerns the engine has
/// no use for once a surface is resolved. This type is the fuller, as-declared shape a definition author
/// writes; a future parser is expected to project it down to <see cref="Servyx.Domain.Configuration.ConfigSurface"/>
/// for the engine to consume.
/// </para>
/// </remarks>
/// <param name="Id">Surface identifier, referenced from <see cref="SettingBinding.SurfaceId"/> and <see cref="DerivedFrom"/>.</param>
/// <param name="Role">Whether Servyx may write to this surface.</param>
/// <param name="Format">The parser to use for this surface.</param>
/// <param name="Codec">Identifier of a value codec applied to a structured payload embedded in a single scalar within this surface, e.g. <c>unreal-option-settings</c>. Null when the surface needs no codec.</param>
/// <param name="CodecPath">The path within the parsed document where <see cref="Codec"/> applies, e.g. <c>["/Script/Pal.PalGameWorldSettings"].OptionSettings</c>. Null when <see cref="Codec"/> is null.</param>
/// <param name="Locator">Where the surface physically lives.</param>
/// <param name="ManagedSubtree">For structured formats, restricts writes to a specific subtree (e.g. <c>services.palworld</c>) rather than the whole document. Null when the whole document is in scope.</param>
/// <param name="MergePolicy">How unmanaged content is treated on write.</param>
/// <param name="DerivedFrom">
/// For a <see cref="SurfaceRole.Derived"/> or <see cref="SurfaceRole.Runtime"/> surface, the upstream
/// surface id(s) this one is generated from — what drift detection compares against. Empty for an
/// <see cref="SurfaceRole.Authoritative"/> surface.
/// </param>
/// <param name="Regeneration">For a <see cref="SurfaceRole.Derived"/> surface, how and when it regenerates. Null otherwise.</param>
public sealed record DeclaredConfigSurface(
    string Id,
    SurfaceRole Role,
    SurfaceFormat Format,
    string? Codec,
    string? CodecPath,
    SurfaceLocator Locator,
    string? ManagedSubtree,
    MergePolicy MergePolicy,
    IReadOnlyList<string> DerivedFrom,
    RegenerationTrigger? Regeneration);
