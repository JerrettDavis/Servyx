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

/// <summary>A single configuration surface as declared by a game definition.</summary>
/// <param name="Id">Surface identifier, referenced from setting bindings.</param>
/// <param name="Role">The surface's role, which determines whether Servyx may write to it.</param>
/// <param name="Locator">Where the surface physically lives.</param>
/// <param name="FormatId">Identifier of the <see cref="IConfigAdapter"/> that parses this surface.</param>
/// <param name="CodecId">Identifier of the <see cref="IConfigValueCodec"/> applied to a structured scalar within this surface, if any.</param>
public sealed record ConfigSurface(string Id, SurfaceRole Role, SurfaceLocator Locator, string FormatId, string? CodecId)
{
    /// <summary>True only when <see cref="Role"/> is <see cref="SurfaceRole.Authoritative"/>.</summary>
    public bool ServyxMayWrite => Role == SurfaceRole.Authoritative;
}
