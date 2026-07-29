namespace Servyx.Domain.Provisioning;

/// <summary>
/// The lifecycle state of a provider resource tracked by Servyx, as recorded locally rather than as
/// observed live from the provider (see <see cref="IProvisioner.RefreshAsync"/> for the live view).
/// </summary>
public enum ResourceLifecycleState
{
    /// <summary>
    /// Servyx has decided to create this resource and has committed a record of that intent to storage,
    /// but the provider API call that actually creates it may not have happened yet — or may have happened
    /// but the confirmation was never received. This state is the critical one: it exists specifically so
    /// the intent is durable <em>before</em> any billable API call is made. If Servyx crashes between
    /// issuing the create call and recording the result, the <see cref="Intended"/> row already on disk is
    /// what lets a later <see cref="IProvisioner.ReconcileAsync"/> sweep find and reconcile the orphaned
    /// resource, instead of it silently billing forever with no local trace.
    /// </summary>
    Intended,

    /// <summary>The provider confirmed the resource exists.</summary>
    Created,

    /// <summary>Servyx has requested destruction and is waiting for the provider to confirm it.</summary>
    Destroying,

    /// <summary>The provider confirmed the resource no longer exists.</summary>
    Destroyed,
}

/// <summary>
/// A stable reference to a specific resource at a specific infrastructure provider. This is the
/// provider-specific counterpart to a Servyx <c>Host</c>/<c>Server</c> entity id — it carries whatever a
/// provisioner needs to look the resource up again, independent of anything Servyx-internal.
/// </summary>
/// <param name="ProvisionerId">The <see cref="IProvisioner.ProvisionerId"/> that owns this resource.</param>
/// <param name="ProviderResourceId">The resource's identifier as assigned by the provider (e.g. a VM instance id).</param>
/// <param name="Region">The provider region/location the resource lives in, if the provider is region-scoped.</param>
/// <param name="Tags">
/// Tags/labels attached to the resource at the provider, used by <see cref="IProvisioner.ReconcileAsync"/>
/// to find resources Servyx created without relying on local state alone.
/// </param>
public sealed record ResourceHandle(
    string ProvisionerId,
    string ProviderResourceId,
    string? Region,
    IReadOnlyDictionary<string, string> Tags);
