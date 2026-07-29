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
    public required string Name { get; set; }

    /// <summary>The game definition this server runs.</summary>
    public required string GameDefinitionId { get; set; }

    /// <summary>
    /// A content hash of the game definition as it was when this server was created or last matched, used
    /// to detect when the definition has since changed underneath the server.
    /// </summary>
    public required string DefinitionContentHash { get; set; }

    /// <summary>The host this server runs on.</summary>
    public required HostId HostId { get; set; }

    /// <summary>Whether this server was adopted from an existing workload or provisioned by Servyx.</summary>
    public required AdoptionMode AdoptionMode { get; set; }

    /// <summary>The current write-access posture Servyx holds for this server.</summary>
    public required ServerWriteMode WriteMode { get; set; }

    /// <summary>Who last changed <see cref="WriteMode"/>, if it has ever been changed.</summary>
    public string? WriteModeChangedBy { get; set; }

    /// <summary>When <see cref="WriteMode"/> was last changed, if it has ever been changed.</summary>
    public DateTimeOffset? WriteModeChangedAt { get; set; }

    /// <summary>When this server record was created.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
