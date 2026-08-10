using Servyx.Domain.Transport;

namespace Servyx.Domain.Configuration;

/// <summary>Where a configuration surface physically lives.</summary>
public abstract record SurfaceLocator
{
    private SurfaceLocator()
    {
    }

    /// <summary>A file on the target host or container filesystem.</summary>
    /// <param name="Path">Path to the file, relative to the deployment's data root.</param>
    public sealed record HostFile(string Path) : SurfaceLocator;

    /// <summary>A query against a live control channel.</summary>
    /// <param name="ChannelId">Identifier of the control channel to query.</param>
    /// <param name="Query">The channel-specific query expression.</param>
    public sealed record ControlChannel(string ChannelId, string Query) : SurfaceLocator;
}

/// <summary>How a <see cref="SurfaceRole.Derived"/> surface gets regenerated.</summary>
public enum RegenerationKind
{
    /// <summary>The surface regenerates when the owning container restarts.</summary>
    ContainerRestart,

    /// <summary>The surface regenerates when the owning process restarts.</summary>
    ProcessRestart,

    /// <summary>The surface only regenerates via a manual, operator-triggered action.</summary>
    Manual,
}

/// <summary>Describes when and how a derived surface is expected to regenerate.</summary>
/// <param name="Kind">The triggering event.</param>
/// <param name="Description">Human-readable description shown in the UI.</param>
public sealed record RegenerationTrigger(RegenerationKind Kind, string Description);

/// <summary>
/// The engine's resolved view of a single configuration surface: the lighter runtime shape a
/// <see cref="Servyx.Domain.Definitions.Model.DeclaredConfigSurface"/> is projected down to once its
/// locator has been expanded against a real deployment.
/// </summary>
/// <remarks>
/// <para>
/// The trailing five members are populated by <see cref="ISurfaceResolver"/> and are what make this type
/// <em>resolved</em> rather than merely declared. They are optional positional parameters rather than
/// required ones because the type predates the resolver and is still constructed directly — in drift tests
/// and anywhere a surface's identity, role and format are all that matter. A <see cref="ConfigSurface"/>
/// built that way has a <see langword="null"/> <see cref="Path"/>, which is the honest answer for "this
/// surface was never bound to a session": code that needs a reachable path must check, and cannot be handed
/// a plausible-looking default that points nowhere.
/// </para>
/// <para>
/// <see cref="ServyxMayWrite"/> is deliberately computed from <see cref="Role"/> rather than stored. There
/// is no constructor argument, object initializer, or <c>with</c> expression that can mark a
/// <see cref="SurfaceRole.Derived"/> surface writable — the invariant is enforced by the shape of the type,
/// not by the discipline of whoever builds one.
/// </para>
/// </remarks>
/// <param name="Id">Surface identifier, referenced from setting bindings.</param>
/// <param name="Role">The surface's role, which determines whether Servyx may write to it.</param>
/// <param name="Locator">Where the surface physically lives, as declared (root variables unexpanded).</param>
/// <param name="FormatId">Identifier of the <see cref="IConfigAdapter"/> that parses this surface.</param>
/// <param name="CodecId">Identifier of the <see cref="IConfigValueCodec"/> applied to a structured scalar within this surface, if any.</param>
/// <param name="Path">
/// The concrete, session-relative path this surface resolved to, or <see langword="null"/> when the surface
/// has not been resolved against a session. Only ever set by <see cref="ISurfaceResolver"/>.
/// </param>
/// <param name="ContainerScoped">
/// Whether <see cref="Path"/> is a path inside the workload's container rather than on the host. A session
/// serving a container-scoped surface must advertise
/// <see cref="TransportCapabilities.ContainerScopedFiles"/>; the resolver refuses the surface outright
/// otherwise, because a host-scoped file channel handed a container path succeeds against the wrong
/// filesystem instead of failing.
/// </param>
/// <param name="RequiredCapabilities">
/// What a session must advertise to serve this surface: <see cref="TransportCapabilities.FileRead"/> for
/// every file surface, plus <see cref="TransportCapabilities.FileWrite"/> only when
/// <see cref="ServyxMayWrite"/>, plus <see cref="TransportCapabilities.ContainerScopedFiles"/> when
/// <see cref="ContainerScoped"/>.
/// </param>
/// <param name="CodecPath">
/// The path within the parsed document where <see cref="CodecId"/> applies, e.g.
/// <c>["/Script/Pal.PalGameWorldSettings"].OptionSettings</c>. Null when <see cref="CodecId"/> is null.
/// </param>
/// <param name="MergePolicy">How unmanaged content in this surface is treated on write.</param>
public sealed record ConfigSurface(
    string Id,
    SurfaceRole Role,
    SurfaceLocator Locator,
    string FormatId,
    string? CodecId,
    TargetPath? Path = null,
    bool ContainerScoped = false,
    TransportCapabilities RequiredCapabilities = TransportCapabilities.None,
    string? CodecPath = null,
    MergePolicy MergePolicy = MergePolicy.PreserveUnknown)
{
    /// <summary>True only when <see cref="Role"/> is <see cref="SurfaceRole.Authoritative"/>.</summary>
    public bool ServyxMayWrite => Role == SurfaceRole.Authoritative;
}
