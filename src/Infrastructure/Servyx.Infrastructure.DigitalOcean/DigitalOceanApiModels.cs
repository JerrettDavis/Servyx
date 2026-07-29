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

/// <summary>The <c>{ "action": { ... } }</c> envelope returned by droplet actions and by action reads.</summary>
internal sealed class DropletActionEnvelope
{
    [JsonPropertyName("action")]
    public DropletActionResource? Action { get; init; }
}

/// <summary>
/// A DigitalOcean action — the asynchronous receipt for a mutation, returned by
/// <c>POST /v2/droplets/{id}/actions</c> and re-read from <c>GET /v2/actions/{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Status"/> is the whole point of this type.</strong> DigitalOcean answers the mutating
/// POST immediately, with an action whose status is almost always <c>in-progress</c>: the resize has not
/// happened yet, and treating that response as success would report a droplet as resized while it was still
/// powered off mid-operation. The three statuses are <c>in-progress</c>, <c>completed</c> and <c>errored</c>,
/// and only the second is success.
/// </para>
/// <para>
/// <see cref="Message"/> is not part of DigitalOcean's documented action schema, but the API does attach a
/// human-readable message to some error payloads. It is read so that when the provider explains why an
/// action errored, that explanation reaches the operator verbatim instead of being replaced by this
/// adapter's guess.
/// </para>
/// </remarks>
internal sealed class DropletActionResource
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("resource_id")]
    public long? ResourceId { get; init; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// The body sent to <c>POST /v2/droplets/{id}/actions</c> to resize a droplet's CPU and memory allocation.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>disk</c> is a property with no setter that returns <see langword="false"/>, and that is the
/// whole design of this type.</strong> DigitalOcean's resize action takes a <c>disk</c> boolean.
/// <c>disk: false</c> changes the CPU and RAM allocation only: the boot disk is untouched and the operation
/// can be reversed later. <c>disk: true</c> additionally grows the boot disk, is irreversible, and
/// permanently prevents the droplet from ever being resized down again. A constructor parameter that
/// happened to default to <see langword="false"/> would leave "Servyx never grows a droplet's disk" as a
/// property of every call site; a get-only <see langword="false"/> leaves it as a property of the program.
/// There is no expression in this assembly that produces a resize body with <c>disk</c> set to
/// <see langword="true"/>, because there is no member that could be assigned.
/// </para>
/// <para>
/// <c>type</c> is fixed for the same reason and it is not cosmetic: the <em>only</em> difference between the
/// request that changes a droplet's CPU allocation and the request that erases its boot disk is that string.
/// A <c>rebuild</c> is not reachable by supplying a different value to this type, because this type has no
/// value to supply.
/// </para>
/// </remarks>
internal sealed class ResizeDropletActionRequest
{
    /// <summary>Always <c>resize</c>. Not settable, so no other action type can be issued through this body.</summary>
    [JsonPropertyName("type")]
    public string Type => "resize";

    /// <summary>The size slug to move the droplet to.</summary>
    [JsonPropertyName("size")]
    public required string Size { get; init; }

    /// <summary>
    /// Always <see langword="false"/>: the CPU-and-memory-only form. Not settable, and deliberately never
    /// omitted — DigitalOcean's default for an absent <c>disk</c> member is not something this adapter is
    /// willing to depend on when the irreversible operation is on the other side of it.
    /// </summary>
    [JsonPropertyName("disk")]
    public bool Disk => false;
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
