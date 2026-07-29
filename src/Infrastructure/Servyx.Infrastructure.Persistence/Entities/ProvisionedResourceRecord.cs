using Servyx.Domain.Common;
using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable ledger of every provider resource Servyx has ever <em>intended</em> to create, whether or not
/// the creation succeeded, was observed to succeed, or was ever linked to a <c>Host</c>/<c>Server</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Invariant — write-ahead intent.</strong> A row is written with
/// <see cref="ResourceLifecycleState.Intended"/> and committed <em>before</em> any billable provider API call
/// is issued. Only after the provider confirms the resource does the same row get updated to
/// <see cref="ResourceLifecycleState.Created"/> with the provider-assigned
/// <see cref="ProviderResourceId"/>. The order is not negotiable and is not an optimization: it is the entire
/// reason this table exists.
/// </para>
/// <para>
/// A crash, a lost response, a timeout, or a process kill anywhere between issuing the create call and
/// recording its result therefore still leaves an <see cref="ResourceLifecycleState.Intended"/> row on disk.
/// An orphan-sweep process (see <c>IProvisioner.ReconcileAsync</c>) can find every such row, ask the provider
/// what actually exists for the recorded <see cref="ProvisionerId"/>/<see cref="Region"/>/<see cref="Tags"/>,
/// and either promote the row to <see cref="ResourceLifecycleState.Created"/> or destroy the orphan. Without
/// this write-ahead row, a VM that was created but never acknowledged has no local trace at all: it bills
/// forever, and nothing in Servyx knows it exists or that it should be reclaimed.
/// </para>
/// <para>
/// <strong>No foreign keys to Server or Host, deliberately.</strong> The row is written before the resource —
/// and therefore before any <c>Host</c> row — exists, and it must survive the deletion of whatever it
/// eventually got linked to, because a leaked provider resource outlives the Servyx entity that wanted it.
/// <see cref="ServerId"/> and <see cref="HostId"/> are recorded as plain nullable values with no referential
/// constraint so that neither ordering nor cascade delete can ever erase the evidence of a billable resource.
/// </para>
/// <para>
/// This type lives in the persistence project rather than in <c>Servyx.Domain</c> because it is a record of a
/// storage-level guarantee, not a domain concept — the domain expresses the same resource through
/// <see cref="ResourceHandle"/>, which this row can reconstruct in full.
/// </para>
/// </remarks>
public sealed class ProvisionedResourceRecord
{
    /// <summary>The ledger row's own identifier, assigned by Servyx before the provider is contacted.</summary>
    public required Guid Id { get; set; }

    /// <summary>The <c>IProvisioner.ProvisionerId</c> that owns (or was asked to own) this resource.</summary>
    public required string ProvisionerId { get; set; }

    /// <summary>
    /// The provider-assigned identifier for the resource. Null while the row is still
    /// <see cref="ResourceLifecycleState.Intended"/>, because the provider has not assigned one yet — that is
    /// the whole point of the state. Populated when the row transitions to
    /// <see cref="ResourceLifecycleState.Created"/>.
    /// </summary>
    public string? ProviderResourceId { get; set; }

    /// <summary>The provider region/location the resource lives in, if the provider is region-scoped.</summary>
    public string? Region { get; set; }

    /// <summary>
    /// Tags/labels Servyx asked the provider to attach to the resource. Recorded at intent time so an orphan
    /// sweep can find the resource by tag even when <see cref="ProviderResourceId"/> was never learned.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Tags { get; set; }

    /// <summary>The locally recorded lifecycle state of the resource.</summary>
    public required ResourceLifecycleState State { get; set; }

    /// <summary>The Servyx server this resource backs, once one exists.</summary>
    public ServerId? ServerId { get; set; }

    /// <summary>The Servyx host this resource became, once one exists.</summary>
    public HostId? HostId { get; set; }

    /// <summary>
    /// The provisioning job that authored this row, if any. Typed as a string rather than a
    /// <see cref="Guid"/> to match <c>Host.ProvisionedByJobId</c>, which is already a string; job ids are
    /// produced by the job scheduler, not by this layer, and forcing them through a Guid here would make the
    /// ledger unable to record a job whose id is not GUID-shaped.
    /// </summary>
    public string? JobId { get; set; }

    /// <summary>When the intent row was first committed — i.e. immediately before the provider call.</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the row last changed state. Equal to <see cref="CreatedAt"/> for a fresh intent row.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }
}
