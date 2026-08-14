using Servyx.Domain.Common;

namespace Servyx.Domain.Entities;

/// <summary>
/// How a <see cref="Server"/> came to be known to Servyx.
/// </summary>
public enum AdoptionMode
{
    /// <summary>
    /// Servyx discovered this server as a pre-existing workload (see <c>IServerDiscovery</c>) and the user
    /// chose to bring it under management. Servyx did not author the workload's identity — it matched an
    /// existing one against a deployment profile's detection rules.
    /// </summary>
    Adopted,

    /// <summary>
    /// Servyx created this server itself, and therefore authored its own detection identity (image,
    /// labels, naming) rather than having to infer one from an existing workload.
    /// </summary>
    Provisioned,
}

/// <summary>
/// Governs how much Servyx is currently allowed to change about a server.
/// </summary>
public enum ServerWriteMode
{
    /// <summary>Servyx may only observe the server; no write operation may be attempted.</summary>
    ReadOnly,

    /// <summary>Servyx may compute and show write operations (e.g. config diffs) but not apply them.</summary>
    PreviewOnly,

    /// <summary>Servyx may apply write operations the server's held capabilities allow.</summary>
    Enabled,
}

/// <summary>
/// A game server Servyx knows about, whether adopted from an existing workload or provisioned by Servyx
/// itself. Persistence-ignorant: this type carries no storage-specific behavior, and infrastructure layers
/// are responsible for mapping it to and from whatever store is in use.
/// </summary>
public sealed class Server
{
    /// <summary>The server's stable identifier.</summary>
    public required ServerId Id { get; set; }

    /// <summary>A human-readable name for the server.</summary>
    /// <remarks>
    /// Display only. A container's name can be reassigned to a different workload outside Servyx at any
    /// time, and is not unique across hosts, so nothing may correlate or authorise on it — see
    /// <see cref="ContainerId"/>.
    /// </remarks>
    public required string Name { get; set; }

    /// <summary>
    /// The discovery-native identifier of the workload this row tracks — a Docker container id for an
    /// adopted container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the durable identity everything about this row keys on</strong>: adoption's
    /// "already tracked?" check, the per-server write grant, and the definition binding all use it, and the
    /// database enforces it unique. A container id is assigned once by its own daemon and never changes for
    /// that workload's lifetime; <see cref="Name"/> is neither of those things.
    /// </para>
    /// <para>
    /// The write grant's key semantics follow directly: renaming a container keeps its grant, and recreating
    /// one returns it to read-only, because the new workload has a new id and therefore no row. That is the
    /// fail-closed direction and it is the reason this column exists rather than the grant being keyed on a
    /// name.
    /// </para>
    /// </remarks>
    public required string ContainerId { get; set; }

    /// <summary>The game definition this server runs.</summary>
    public required string GameDefinitionId { get; set; }

    /// <summary>
    /// A content hash of the game definition as it was when this server was created or last matched, used
    /// to detect when the definition has since changed underneath the server.
    /// </summary>
    public required string DefinitionContentHash { get; set; }

    /// <summary>
    /// The host this server runs on, or <see langword="null"/> when Servyx does not model one for it.
    /// </summary>
    /// <remarks>
    /// Set on adoption (see <c>ServerAdoptionService.AdoptAsync</c>) ONLY when the discovered container's
    /// host resolves to an actual, durable <c>Host</c> row — i.e. it was discovered on a database-registered
    /// host (see <c>IHostRepository</c>/<c>IHostRegistrationService</c>). It stays <see langword="null"/> for
    /// a container adopted from the local/non-SSH discovery source, or one discovered on a
    /// configuration-declared host (<c>Servyx:Hosts</c>) that has no corresponding row — configuration hosts
    /// are authoritative for connecting but are never themselves persisted as a <c>Host</c> row. Null is the
    /// honest "not modeled" state in both cases; a fabricated <c>HostId.New()</c> would be a foreign key to a
    /// row that does not exist, and would make host-scoped queries look answerable when they are not. Note
    /// the consequence for grant matching: a (<c>HostId</c>, <c>ContainerId</c>) pair would compare null to
    /// null for every server left unresolved this way and contribute nothing while appearing to check more,
    /// which is why <see cref="ContainerId"/> alone is the grant key.
    /// </remarks>
    public HostId? HostId { get; set; }

    /// <summary>Whether this server was adopted from an existing workload or provisioned by Servyx.</summary>
    public required AdoptionMode AdoptionMode { get; set; }

    /// <summary>The current write-access posture Servyx holds for this server.</summary>
    public required ServerWriteMode WriteMode { get; set; }

    /// <summary>Who last changed <see cref="WriteMode"/>, if it has ever been changed.</summary>
    public string? WriteModeChangedBy { get; set; }

    /// <summary>When <see cref="WriteMode"/> was last changed, if it has ever been changed.</summary>
    public DateTimeOffset? WriteModeChangedAt { get; set; }

    /// <summary>
    /// This server's default answer to "when a setting is written to its authoritative surface, should the
    /// change ALSO be mirrored onto the derived in-container copy the workload regenerates?"
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Opt-in, and seeded false.</strong> Not <c>required</c>, and defaulting to <see langword="false"/>
    /// rather than <see langword="true"/>, for the same reason <see cref="WriteMode"/> starts read-only: a
    /// mirrored write puts bytes into a file the workload owns, and an operator who has not asked for that
    /// has not consented to it. Every server adopted before this column existed reads as false, which is the
    /// correct posture for all of them.
    /// </para>
    /// <para>
    /// <strong>A default, not an authorisation.</strong> Turning this on grants nothing by itself: the
    /// governing definition must declare the individual setting mirror-eligible, the surface must declare it
    /// accepts mirrored writes, the setting must not be sensitive, the transport must be able to write into
    /// the container, and the container must be running. This flag only decides what an eligible setting
    /// does when its own row expresses no opinion — see
    /// <c>ServerSettingValue.MirrorToDerived</c> for the per-row override that can point either way.
    /// </para>
    /// <para>
    /// Attribution mirrors <see cref="WriteModeChangedBy"/>/<see cref="WriteModeChangedAt"/> exactly,
    /// including its honesty caveat: Servyx has one shared operator password and no per-operator accounts,
    /// so "who" is a constant in practice — recorded anyway so a future identity system does not need a
    /// schema change to become meaningful.
    /// </para>
    /// </remarks>
    public bool MirrorDerivedSurfaces { get; set; }

    /// <summary>Who last changed <see cref="MirrorDerivedSurfaces"/>, if it has ever been changed.</summary>
    public string? MirrorDerivedSurfacesChangedBy { get; set; }

    /// <summary>When <see cref="MirrorDerivedSurfaces"/> was last changed, if it has ever been changed.</summary>
    public DateTimeOffset? MirrorDerivedSurfacesChangedAt { get; set; }

    /// <summary>When this server record was created.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
