using Servyx.Domain.Common;

namespace Servyx.Domain.Entities;

/// <summary>
/// A physical or virtual machine reachable via a transport, on which one or more <see cref="Server"/>
/// instances run. Persistence-ignorant: this type carries no storage-specific behavior, and infrastructure
/// layers are responsible for mapping it to and from whatever store is in use.
/// </summary>
public sealed class Host
{
    /// <summary>The host's stable identifier.</summary>
    public required HostId Id { get; set; }

    /// <summary>A human-readable name for the host.</summary>
    public required string Name { get; set; }

    /// <summary>The connector used to reach this host.</summary>
    public required string ConnectorId { get; set; }

    /// <summary>
    /// The job that provisioned this host, if it was created by Servyx rather than adopted.
    /// </summary>
    public string? ProvisionedByJobId { get; set; }

    /// <summary>The provider-assigned identifier for this host's underlying resource, if it was provisioned.</summary>
    public string? ProviderResourceId { get; set; }

    /// <summary>The provider account this host's resource belongs to, if it was provisioned.</summary>
    public string? ProviderAccountId { get; set; }

    /// <summary>The network address (host, or host:port) Servyx reaches this host at.</summary>
    public required string Endpoint { get; set; }

    /// <summary>The URN of the credential (e.g. an SSH key) used to authenticate to this host, if any.</summary>
    public string? CredentialUrn { get; set; }

    /// <summary>
    /// How Servyx verifies this host's identity on connect — e.g. <c>"requirePinned"</c> or
    /// <c>"trustOnFirstUse"</c>.
    /// </summary>
    public required string TrustPolicy { get; set; }

    /// <summary>The host key fingerprint(s) pinned for this host, if any have been recorded.</summary>
    public string? PinnedFingerprints { get; set; }

    /// <summary>Whether this host is currently eligible for use.</summary>
    public required bool Enabled { get; set; }

    /// <summary>Who registered this host, as the host understands identity, if known.</summary>
    public string? RegisteredBy { get; set; }

    /// <summary>When this host record was created.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
