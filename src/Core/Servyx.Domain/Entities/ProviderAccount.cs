namespace Servyx.Domain.Entities;

/// <summary>
/// A configured account with an infrastructure provider, under which resources may be provisioned.
/// Persistence-ignorant: this type carries no storage-specific behavior, and infrastructure layers are
/// responsible for mapping it to and from whatever store is in use.
/// </summary>
public sealed class ProviderAccount
{
    /// <summary>The account's stable identifier.</summary>
    public required string Id { get; set; }

    /// <summary>The provider this account belongs to, e.g. <c>"hetzner"</c> or <c>"digitalocean"</c>.</summary>
    public required string ProviderId { get; set; }

    /// <summary>A human-readable name for the account, shown in the UI.</summary>
    public required string DisplayName { get; set; }

    /// <summary>The region to use by default when provisioning under this account, if any.</summary>
    public string? DefaultRegion { get; set; }

    /// <summary>
    /// Secret-store URNs this account resolves its provider credentials from. Never a literal credential —
    /// matches the convention used by <see cref="Connectors.ConnectorDescriptor.CredentialRefs"/>.
    /// </summary>
    public required IReadOnlyList<string> CredentialUrns { get; set; }

    /// <summary>
    /// A human-readable description of how broad the account's credential permissions are (e.g. "full
    /// account access, including billing and delete" vs. "scoped to a single project"), so the UI can warn
    /// the user when a credential carries account-wide delete rights before it is used for provisioning.
    /// </summary>
    public string? ScopeHint { get; set; }

    /// <summary>When this account record was created.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
