namespace Servyx.Domain.Provisioning;

/// <summary>
/// A row Servyx commits <em>before</em> it asks a provider to create anything, recording the intent and
/// the tags that will identify the resulting resource.
/// </summary>
/// <param name="LedgerRowId">The ledger row's own identifier, assigned by Servyx before the provider is contacted.</param>
/// <param name="ProvisionerId">The <see cref="IProvisioner.ProvisionerId"/> that will own the resource.</param>
/// <param name="Region">The provider region/location the resource will live in, if the provider is region-scoped.</param>
/// <param name="Tags">
/// The tags/labels Servyx will ask the provider to attach. Recorded at intent time so an orphan sweep can
/// find the resource by tag even if its provider-assigned id is never learned.
/// </param>
/// <param name="JobId">The provisioning job that authored this row, if any.</param>
/// <param name="RecordedAt">When the intent was committed — i.e. immediately before the provider call.</param>
public sealed record ProvisioningIntent(
    Guid LedgerRowId,
    string ProvisionerId,
    string? Region,
    IReadOnlyDictionary<string, string> Tags,
    string? JobId,
    DateTimeOffset RecordedAt);

/// <summary>
/// A row the provider has confirmed: the same ledger row a <see cref="ProvisioningIntent"/> started as,
/// once <see cref="IProvisioningLedger.MarkCreatedAsync"/> has stamped it with the provider-assigned id.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The <see cref="ResourceHandle"/> is the point of this type.</strong> A row in
/// <see cref="ResourceLifecycleState.Created"/> is exactly the row whose provider-assigned id Servyx
/// <em>has</em> learned, so it can hand back a complete, non-nullable handle rather than the parts a caller
/// would otherwise have to null-check and reassemble. That is what lets a drift check or an update plan run
/// against the real resource instead of against whatever identifier could be scavenged from the row's tags.
/// </para>
/// <para>
/// It is a separate type from <see cref="ProvisioningIntent"/> for the same reason: an intent row provably
/// has no provider id, a created row provably has one, and collapsing the two into a shape with a nullable
/// id would push that distinction back onto every reader as a runtime check.
/// </para>
/// </remarks>
/// <param name="LedgerRowId">The ledger row's own identifier, unchanged since intent was recorded.</param>
/// <param name="Handle">
/// The complete provider-specific reference to the confirmed resource — provisioner, provider-assigned id,
/// region, and the tags the row recorded.
/// </param>
/// <param name="JobId">The provisioning job that authored this row, if any.</param>
/// <param name="RecordedAt">When the intent was first committed — i.e. immediately before the provider call.</param>
/// <param name="ConfirmedAt">When the provider's confirmation was recorded against this row.</param>
public sealed record ProvisionedResourceRow(
    Guid LedgerRowId,
    ResourceHandle Handle,
    string? JobId,
    DateTimeOffset RecordedAt,
    DateTimeOffset ConfirmedAt);

/// <summary>
/// The durable write-ahead ledger of provider resources Servyx has intended to create.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The ordering is the whole point.</strong> <see cref="RecordIntentAsync"/> must be committed to
/// durable storage before any billable/mutating provider call is issued, and
/// <see cref="MarkCreatedAsync"/> only afterwards. A crash anywhere in between then still leaves a row in
/// <see cref="ResourceLifecycleState.Intended"/> on disk, which is what lets a later
/// <see cref="IProvisioner.ReconcileAsync"/> sweep discover a resource that was created but never
/// acknowledged. Without the write-ahead row such a resource has no local trace at all and bills forever.
/// </para>
/// <para>
/// <strong>Why this lives in <c>Servyx.Domain</c>.</strong> The only implementation that can honour the
/// word "durable" is one backed by a store, and every store lives in an infrastructure project — each of
/// which references <c>Servyx.Domain</c> and nothing else, by design (see the defending comments in those
/// projects' csproj files). An abstraction that infrastructure must <em>implement</em> therefore has to be
/// declared here, alongside <see cref="IProvisioner"/> and <see cref="IProvisioningOperation"/>, which are
/// here for exactly the same reason. <c>Servyx.Infrastructure.Persistence</c> supplies the durable
/// implementation (<c>EfProvisioningLedger</c>, over the <c>ProvisionedResources</c> table);
/// <c>Servyx.Application</c> keeps a deliberately non-durable in-memory one for tests only.
/// </para>
/// </remarks>
public interface IProvisioningLedger
{
    /// <summary>
    /// Durably records <paramref name="intent"/> in state <see cref="ResourceLifecycleState.Intended"/>.
    /// Must not return until the row is committed.
    /// </summary>
    Task RecordIntentAsync(ProvisioningIntent intent, CancellationToken ct = default);

    /// <summary>
    /// Advances the row identified by <paramref name="ledgerRowId"/> to
    /// <see cref="ResourceLifecycleState.Created"/>, stamping it with the provider-assigned
    /// <paramref name="providerResourceId"/>.
    /// </summary>
    Task MarkCreatedAsync(Guid ledgerRowId, string providerResourceId, DateTimeOffset observedAt, CancellationToken ct = default);

    /// <summary>
    /// Returns every row for <paramref name="provisionerId"/> still in
    /// <see cref="ResourceLifecycleState.Intended"/> — i.e. the rows an orphan sweep must resolve against
    /// <see cref="IProvisioner.ReconcileAsync"/>.
    /// </summary>
    Task<IReadOnlyList<ProvisioningIntent>> ListIntendedAsync(string provisionerId, CancellationToken ct = default);

    /// <summary>
    /// Returns every row for <paramref name="provisionerId"/> the provider has confirmed — i.e. those in
    /// <see cref="ResourceLifecycleState.Created"/> — each carrying the complete
    /// <see cref="ResourceHandle"/> its provider-assigned id makes possible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the member that makes the ledger a system of record rather than a crash log.</strong>
    /// <see cref="ListIntendedAsync"/> answers "what might be leaking"; this answers "what do I own, and what
    /// is it called at the provider". Without it the only rows anything could enumerate were the ones written
    /// before the provider was contacted, so a caller wanting to inspect a live resource had to derive an
    /// identifier from the row's tags and hope the provider agreed — a guess that silently checks the wrong
    /// resource whenever the tag is absent or stale.
    /// </para>
    /// <para>
    /// <strong>Why this is a second method rather than a state filter on the first.</strong> The two
    /// enumerations do not return the same shape and must not pretend to. An
    /// <see cref="ResourceLifecycleState.Intended"/> row provably has no provider-assigned id — that is the
    /// definition of the state — while a <see cref="ResourceLifecycleState.Created"/> row provably has one.
    /// A single <c>ListAsync(state)</c> could only return the weaker of the two shapes, with a nullable id,
    /// which hands every caller back the exact runtime check this member exists to eliminate. A state
    /// parameter would also advertise <see cref="ResourceLifecycleState.Destroying"/> and
    /// <see cref="ResourceLifecycleState.Destroyed"/> as answerable queries when no implementation serves
    /// them, and those states want different shapes again: a destroyed row's handle names a resource that is
    /// gone, so offering one would invite a drift check against nothing. When they are needed they get their
    /// own members, on the same reasoning as this one.
    /// </para>
    /// </remarks>
    /// <param name="provisionerId">The provisioner whose confirmed resources to list.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ProvisionedResourceRow>> ListCreatedAsync(string provisionerId, CancellationToken ct = default);
}
