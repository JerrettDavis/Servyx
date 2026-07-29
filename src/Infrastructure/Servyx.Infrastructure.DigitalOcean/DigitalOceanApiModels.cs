using System.Text.Json.Serialization;

namespace Servyx.Infrastructure.DigitalOcean;

/// <summary>The <c>{ "droplet": { ... } }</c> envelope returned by droplet create and droplet get.</summary>
internal sealed class DropletEnvelope
{
    [JsonPropertyName("droplet")]
    public DropletResource? Droplet { get; init; }
}

/// <summary>The <c>{ "droplets": [ ... ], "links": { ... } }</c> envelope returned by droplet list.</summary>
internal sealed class DropletListEnvelope
{
    [JsonPropertyName("droplets")]
    public IReadOnlyList<DropletResource>? Droplets { get; init; }

    [JsonPropertyName("links")]
    public DropletLinks? Links { get; init; }
}

/// <summary>DigitalOcean's pagination links. Only <c>pages.next</c> is read.</summary>
internal sealed class DropletLinks
{
    [JsonPropertyName("pages")]
    public DropletPages? Pages { get; init; }
}

/// <summary>The page cursor. A sweep that stopped at page one would under-report orphans, so <c>next</c> is followed.</summary>
internal sealed class DropletPages
{
    [JsonPropertyName("next")]
    public string? Next { get; init; }
}

/// <summary>A droplet as the DigitalOcean API reports it. Only the fields Servyx actually reads are modelled.</summary>
internal sealed class DropletResource
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("size_slug")]
    public string? SizeSlug { get; init; }

    /// <summary>
    /// The droplet's boot-disk size in gigabytes, as DigitalOcean reports it.
    /// </summary>
    /// <remarks>
    /// Read only by update planning, and load-bearing there: a resize this adapter plans is always the
    /// CPU/RAM-only form, so the plan states the live disk size and states that the operation leaves it
    /// alone. That is the evidence behind a <c>DataImpact.Preserved</c> on a resize, rather than an
    /// assumption about what a resize does.
    /// </remarks>
    [JsonPropertyName("disk")]
    public int? Disk { get; init; }

    /// <summary>
    /// The image the droplet is currently running, as DigitalOcean reports it.
    /// </summary>
    /// <remarks>
    /// Absent from the model until update planning needed it: a drift check that could not read the live
    /// image could not report an out-of-band rebuild, and an update plan that could not read it could not
    /// tell whether the plan it is about to describe is a disk-erasing rebuild or nothing at all.
    /// </remarks>
    [JsonPropertyName("image")]
    public DropletImage? Image { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    [JsonPropertyName("region")]
    public DropletRegion? Region { get; init; }

    [JsonPropertyName("networks")]
    public DropletNetworks? Networks { get; init; }
}

/// <summary>
/// A droplet's image. Only the two fields that can name it in a provisioning request are modelled.
/// </summary>
/// <remarks>
/// DigitalOcean accepts either form in <c>POST /v2/droplets</c>'s <c>image</c> member: a public image's slug
/// (<c>ubuntu-24-04-x64</c>) or a numeric id (any custom image or snapshot, which have no slug at all). Both
/// are read here so a live droplet can be compared against whichever form the request used, rather than only
/// against the one a stock image happens to have.
/// </remarks>
internal sealed class DropletImage
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("slug")]
    public string? Slug { get; init; }
}

/// <summary>A droplet's region. Only the slug is read; it is what <c>ResourceHandle.Region</c> carries.</summary>
internal sealed class DropletRegion
{
    [JsonPropertyName("slug")]
    public string? Slug { get; init; }
}

/// <summary>A droplet's network assignments, split by address family.</summary>
internal sealed class DropletNetworks
{
    [JsonPropertyName("v4")]
    public IReadOnlyList<DropletNetworkV4>? V4 { get; init; }
}

/// <summary>One IPv4 assignment. <c>type</c> is <c>"public"</c> or <c>"private"</c>.</summary>
internal sealed class DropletNetworkV4
{
    [JsonPropertyName("ip_address")]
    public string? IpAddress { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

/// <summary>The body sent to <c>POST /v2/droplets</c>.</summary>
/// <remarks>
/// <c>user_data</c> is null unless the caller supplied cloud-init of their own — nothing in this assembly
/// authors one. Fields Servyx does not use (<c>volumes</c>, <c>backups</c>, <c>with_droplet_agent</c>, …) are
/// absent rather than sent as defaults, so the request body says only what Servyx actually means.
/// </remarks>
internal sealed class CreateDropletRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("region")]
    public required string Region { get; init; }

    [JsonPropertyName("size")]
    public required string Size { get; init; }

    [JsonPropertyName("image")]
    public required string Image { get; init; }

    [JsonPropertyName("ssh_keys")]
    public required IReadOnlyList<string> SshKeys { get; init; }

    [JsonPropertyName("tags")]
    public required IReadOnlyList<string> Tags { get; init; }

    [JsonPropertyName("user_data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserData { get; init; }
}
