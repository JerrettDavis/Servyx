using System.Text.Json.Serialization;

namespace Servyx.Infrastructure.DigitalOcean;

/// <summary>The <c>{ "snapshots": [ ... ], "links": { ... } }</c> envelope returned by <c>GET /v2/snapshots</c>.</summary>
/// <remarks>
/// Pagination reuses <see cref="DropletLinks"/>/<see cref="DropletPages"/> rather than declaring a second
/// pair: DigitalOcean's <c>links.pages.next</c> shape is one shape across the whole API, and a snapshot sweep
/// that stopped at page one would under-report exactly as a droplet sweep would — except that here the
/// under-report is "you have no other snapshots", said to someone deciding what their account is costing.
/// </remarks>
internal sealed class SnapshotListEnvelope
{
    [JsonPropertyName("snapshots")]
    public IReadOnlyList<SnapshotResource>? Snapshots { get; init; }

    [JsonPropertyName("links")]
    public DropletLinks? Links { get; init; }
}

/// <summary>
/// A snapshot as <c>GET /v2/snapshots</c> reports it — the only DigitalOcean listing that carries both the
/// snapshot's <c>tags</c> and the id of the resource it was taken from.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this endpoint and not <c>GET /v2/droplets/{id}/snapshots</c>.</strong> The per-droplet
/// endpoint answers with <em>image</em> objects, which carry no <c>tags</c> member at all. Servyx's ownership
/// mark is partly a tag (see <c>SnapshotOwnership</c>), so a listing that cannot report tags cannot tell a
/// Servyx snapshot from a hand-taken one — and an adapter that could not tell them apart would either prune
/// someone else's snapshot or prune nothing. <c>/v2/snapshots</c> reports <c>tags</c>, <c>resource_id</c> and
/// <c>resource_type</c>, which is precisely the evidence the classification needs.
/// </para>
/// <para>
/// <strong>Ids are strings here.</strong> A droplet snapshot's id is a decimal string
/// (<c>"6372321"</c>) because it is also an image id; a volume snapshot's is a UUID. They are read as
/// <see cref="string"/> and parsed to a number only where DigitalOcean requires a number — the restore
/// action's <c>image</c> member — so a snapshot whose id is not numeric is refused there rather than
/// coerced into some other resource's id.
/// </para>
/// </remarks>
internal sealed class SnapshotResource
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>The id of the resource the snapshot was taken from — for a droplet snapshot, the droplet id.</summary>
    [JsonPropertyName("resource_id")]
    public string? ResourceId { get; init; }

    /// <summary><c>droplet</c> or <c>volume</c>. A volume snapshot is never a droplet backup and is never claimed as one.</summary>
    [JsonPropertyName("resource_type")]
    public string? ResourceType { get; init; }

    /// <summary>
    /// The billed size in gigabytes — the number Servyx multiplies by DigitalOcean's per-GB-month rate.
    /// </summary>
    /// <remarks>
    /// Read as <see cref="decimal"/> rather than <see cref="double"/> because it is a money input: it is
    /// multiplied by a published rate and shown to somebody deciding whether to keep paying for the snapshot.
    /// </remarks>
    [JsonPropertyName("size_gigabytes")]
    public decimal? SizeGigabytes { get; init; }

    /// <summary>The smallest droplet disk this snapshot can be restored onto, in gigabytes.</summary>
    [JsonPropertyName("min_disk_size")]
    public int? MinDiskSize { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>
/// The body sent to <c>POST /v2/droplets/{id}/actions</c> to take a snapshot of a droplet.
/// </summary>
/// <remarks>
/// <c>type</c> is a property with no setter for the same reason
/// <see cref="ResizeDropletActionRequest"/>'s and <see cref="RebuildDropletActionRequest"/>'s are: every
/// droplet action shares one endpoint, and the only thing separating "copy this disk" from "erase this disk"
/// is that string. This body carries no <c>image</c> and no <c>size</c> member, so it cannot express a
/// restore, a rebuild or a resize even if the type string were somehow wrong.
/// </remarks>
internal sealed class SnapshotDropletActionRequest
{
    /// <summary>Always <c>snapshot</c>. Not settable, so no other action type can be issued through this body.</summary>
    [JsonPropertyName("type")]
    public string Type => "snapshot";

    /// <summary>The name DigitalOcean records for the snapshot. Servyx's first ownership mark.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>
/// The body sent to <c>POST /v2/droplets/{id}/actions</c> to <em>restore</em> a droplet from one of its
/// snapshots — the action that replaces the droplet's boot disk with the snapshot's contents.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This destroys the droplet's current disk.</strong> A restore is the same class of operation as a
/// rebuild: everything written since the snapshot was taken is gone and cannot be recovered from the droplet
/// afterwards. The droplet keeps its id and its address; nothing else about it survives. Nothing in this
/// assembly builds this body without a previewed restore plan and a separately-supplied acknowledgement of
/// <c>DataImpact.Destroyed</c> having both been checked first — see
/// <c>DigitalOceanSnapshotBackupProvider.RestoreAsync</c>.
/// </para>
/// <para>
/// <c>image</c> is a <see cref="long"/>, not a string: DigitalOcean's restore action names the snapshot by
/// its numeric image id. A snapshot whose id will not parse as one is refused before this body can be built,
/// rather than being coerced into a number that would name a different image.
/// </para>
/// </remarks>
internal sealed class RestoreDropletActionRequest
{
    /// <summary>Always <c>restore</c>. Not settable, so no other action type can be issued through this body.</summary>
    [JsonPropertyName("type")]
    public string Type => "restore";

    /// <summary>The numeric id of the snapshot to restore the droplet from.</summary>
    [JsonPropertyName("image")]
    public required long Image { get; init; }
}

/// <summary>The body sent to <c>POST /v2/tags</c> to create a tag before anything can be tagged with it.</summary>
internal sealed class CreateTagRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>The body sent to <c>POST /v2/tags/{tag_name}/resources</c> to apply a tag to a snapshot.</summary>
internal sealed class TagResourcesRequest
{
    [JsonPropertyName("resources")]
    public required IReadOnlyList<TagResourceRef> Resources { get; init; }
}

/// <summary>One resource in a <see cref="TagResourcesRequest"/>.</summary>
/// <remarks>
/// <c>resource_type</c> is a get-only <c>image</c>. A droplet snapshot <em>is</em> an image as far as
/// DigitalOcean's tag API is concerned, and the alternatives the same API accepts — <c>droplet</c>,
/// <c>volume</c>, <c>database</c> — all name live, billable resources that a mistaken value would tag
/// instead. Since the id namespaces overlap (a snapshot id and a droplet id are both decimal strings), a
/// settable resource type would make "tag the snapshot" one typo away from "tag whichever droplet shares
/// that number". There is no member to set, so that expression does not exist in this assembly.
/// </remarks>
internal sealed class TagResourceRef
{
    [JsonPropertyName("resource_id")]
    public required string ResourceId { get; init; }

    /// <summary>Always <c>image</c>. Not settable.</summary>
    [JsonPropertyName("resource_type")]
    public string ResourceType => "image";
}
