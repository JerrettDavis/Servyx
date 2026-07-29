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

    /// <summary>When this host record was created.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
