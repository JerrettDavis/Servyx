using Servyx.Domain.Common;

namespace Servyx.Domain.Configuration;

/// <summary>A single desired setting value as read back for display — Servyx's recorded intent, never an applied value.</summary>
/// <param name="Key">The setting's catalogue key.</param>
/// <param name="Value">
/// The recorded desired value, as raw text. Matches <see cref="Servyx.Domain.Entities.ServerSettingValue.Value"/>:
/// "no value recorded" is the absence of a <see cref="DesiredSettingValue"/> entry entirely (see
/// <see cref="ServerSettingsSnapshot"/>), never a null or otherwise distinguished value here.
/// </param>
/// <param name="UpdatedBy">Who recorded it. See <see cref="Servyx.Domain.Entities.ServerSettingValue.UpdatedBy"/>'s remarks — one constant value in practice.</param>
/// <param name="UpdatedAt">When it was recorded.</param>
/// <param name="MirrorToDerived">
/// This row's own answer to "should this setting's write also be mirrored onto the eligible derived
/// surface?", or <see langword="null"/> to inherit <see cref="ServerSettingsSnapshot.MirrorDerivedSurfaces"/>.
/// Three-valued on purpose — see <see cref="Servyx.Domain.Entities.ServerSettingValue.MirrorToDerived"/>.
/// Trailing with a default so every existing four-argument construction keeps compiling and keeps meaning
/// "inherit the server default".
/// </param>
public sealed record DesiredSettingValue(
    string Key,
    string Value,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    bool? MirrorToDerived = null)
{
    /// <summary>
    /// Whether this setting's write should be mirrored, given <paramref name="serverDefault"/> — the row's
    /// own override when it has one, the server default otherwise.
    /// </summary>
    /// <remarks>
    /// The single place inheritance is resolved, so "null means inherit" cannot be spelled two subtly
    /// different ways by two callers. Note both directions really are supported and both matter: an override
    /// of <see langword="false"/> suppresses mirroring on a server that has it on by default, and an
    /// override of <see langword="true"/> enables it for one row on a server that has it off.
    /// </remarks>
    public bool MirrorsToDerived(bool serverDefault) => MirrorToDerived ?? serverDefault;
}

/// <summary>
/// Every desired value currently recorded for a tracked server, plus the resolved <see cref="ServerId"/> a
/// caller needs to pass back to <see cref="IServerSettingsService.SaveDesiredValueAsync"/>.
/// </summary>
/// <remarks>
/// Resolving the container id to a <see cref="ServerId"/> once here — rather than on every save — mirrors
/// <c>IWriteGrantService</c>'s own split: <c>DescribeAsync(containerId)</c> resolves the id once, and
/// <c>SetWriteModeAsync(ServerId, ...)</c> reuses it. A page with several settings to save (one row each)
/// would otherwise re-resolve the same container id on every single click.
/// </remarks>
/// <param name="ServerId">The tracked server's own row id.</param>
/// <param name="Values">Every desired value recorded for it, keyed by setting key.</param>
/// <param name="MirrorDerivedSurfaces">
/// This server's default answer to "should an eligible setting's write also be mirrored onto the derived
/// surface?" — <see cref="Servyx.Domain.Entities.Server.MirrorDerivedSurfaces"/>, carried here so
/// <see cref="IPlanExecutor.PreviewAsync"/> can read it off the snapshot it already loads rather than
/// growing a parameter for it. Defaults to <see langword="false"/>, which is both the seeded database
/// default and the honest answer for a caller that constructs a snapshot without one: mirroring is opt-in.
/// </param>
public sealed record ServerSettingsSnapshot(
    ServerId ServerId,
    IReadOnlyDictionary<string, DesiredSettingValue> Values,
    bool MirrorDerivedSurfaces = false);

/// <summary>
/// Whether a <see cref="IServerSettingsService.SaveDesiredValueAsync"/> call recorded a value, or why not.
/// </summary>
/// <remarks>
/// Deliberately named <see cref="Recorded"/> rather than "Applied": that word is reserved for a value that
/// has reached the running server, which nothing in this codebase can do yet (see
/// <c>docs/plans/ui-management-surface.md</c>, Phase 4b). Using it here, even as an enum member name, would
/// be exactly the kind of quiet overstatement this phase exists to avoid.
/// </remarks>
public enum SaveDesiredValueOutcome
{
    /// <summary>The row was written to Servyx's own database. This says nothing about the running server.</summary>
    Recorded,

    /// <summary>No <c>Server</c> row matches the supplied <see cref="ServerId"/>, so there was nothing to record against.</summary>
    ServerNotFound,

    /// <summary>
    /// The server is tracked, but no desired value is recorded for the key — so there is no row whose
    /// mirror override could be set. Only ever returned by
    /// <see cref="IServerSettingsService.SetMirrorToDerivedAsync"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not silently creating an empty-valued row to hang the flag off. A mirror override says
    /// "when this setting is written, also write the derived copy"; with no desired value there is nothing to
    /// write, so the row would record a preference about an event that cannot happen and would then be
    /// indistinguishable from an operator who had genuinely blanked the field.
    /// </remarks>
    NoDesiredValueRecorded,
}

/// <summary>The result of attempting to record a desired setting value.</summary>
/// <param name="Outcome">Whether the value was recorded, and if not, why not.</param>
/// <param name="Value">The value now on record, when <paramref name="Outcome"/> is <see cref="SaveDesiredValueOutcome.Recorded"/>; otherwise <see langword="null"/>.</param>
public sealed record SaveDesiredValueResult(SaveDesiredValueOutcome Outcome, DesiredSettingValue? Value)
{
    /// <summary>Whether a row was actually written.</summary>
    public bool Recorded => Outcome == SaveDesiredValueOutcome.Recorded;
}

/// <summary>
/// Loads and persists an operator's desired setting values for a server.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately narrow.</strong> This type touches ONLY Servyx's own database — implementations must
/// never hold or call an <c>IExecutionTarget</c>, an <c>IRconSession</c>, or any other transport, and must
/// never call <c>IPlanExecutor</c> either. It records what an operator WANTS; it does not issue a command,
/// open a session, or otherwise touch the running server. See <c>docs/plans/ui-management-surface.md</c>,
/// "Phase 4a — Desired-value persistence" for why "load and save intent" is the entire contract. Turning
/// recorded intent into a plan is <c>IPlanExecutor.PreviewAsync</c>'s job, not this type's: that funnel reads
/// recorded values back out through <see cref="LoadAsync"/>, the same read any other caller gets — this
/// interface has no special relationship with it.
/// </para>
/// <para>
/// <strong>Storage keys on <see cref="ServerId"/>, never on the container id.</strong> A container id is the
/// durable identity for the WRITE GRANT cache (<c>WriteGrantCache</c>/<c>DbBackedWriteModeResolver</c>)
/// because that is what a <c>TargetDescriptor</c> presents at the transport seam — but a desired setting
/// value is operator intent about a specific TRACKED server, not about whatever container currently answers
/// to an id. Keying storage on <see cref="ServerId"/>, combined with the cascade delete
/// <c>ServerSettingValueConfiguration</c> declares from <c>Server</c>, means forgetting a server discards its
/// desired values outright: a later re-adopt of the same container mints a brand new <see cref="ServerId"/>
/// (see <c>ServerAdoptionService.AdoptAsync</c>) and starts with none. Keying on the container id instead
/// would let an operator forget a server, re-adopt the same container later, and silently inherit
/// configuration intent they never re-entered — the same class of bug Phase 2's security review found in an
/// earlier, container-id-keyed write grant. <see cref="LoadAsync"/> still takes the container id, matching
/// <c>IWriteGrantService.DescribeAsync</c>: a page route and a discovery listing both carry the container id,
/// not the internal row id, so lookup has to start there — see <see cref="ServerSettingsSnapshot"/>'s remarks
/// for why the resolved <see cref="ServerId"/> is then handed back for every subsequent save.
/// </para>
/// </remarks>
public interface IServerSettingsService
{
    /// <summary>
    /// Every desired value currently recorded for the server identified by <paramref name="containerId"/>,
    /// plus its resolved <see cref="ServerId"/> — or <see langword="null"/> when Servyx tracks no server for
    /// that container id at all (distinct from tracking one but having recorded nothing yet, which is a
    /// non-null snapshot with an empty <see cref="ServerSettingsSnapshot.Values"/>).
    /// </summary>
    Task<ServerSettingsSnapshot?> LoadAsync(string containerId, CancellationToken ct = default);

    /// <summary>
    /// Records <paramref name="value"/> as the desired value for <paramref name="key"/> on the server
    /// identified by <paramref name="serverId"/> (the id <see cref="LoadAsync"/> resolved), attributed to
    /// <paramref name="actor"/>. A second call for the same (server, key) pair overwrites the prior desired
    /// value; this table holds current intent, not a history of it.
    /// </summary>
    Task<SaveDesiredValueResult> SaveDesiredValueAsync(
        ServerId serverId, string key, string? value, string actor, CancellationToken ct = default);

    /// <summary>
    /// Records this row's own answer to "mirror this setting's write onto the eligible derived surface?" —
    /// <see langword="true"/> to force it on, <see langword="false"/> to force it off, or
    /// <see langword="null"/> to go back to inheriting the server default
    /// (<see cref="ServerSettingsSnapshot.MirrorDerivedSurfaces"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Separate from <see cref="SaveDesiredValueAsync"/> rather than a parameter on it.</strong>
    /// Folding it in would mean every ordinary value edit silently rewrites the override too — an operator
    /// who set a per-row override and later corrected a typo in the value would have their override reset
    /// with nothing saying so. The two are independent facts about the row and are recorded independently.
    /// </para>
    /// <para>
    /// <strong>This grants nothing.</strong> Turning the flag on for a setting the governing definition
    /// never declared mirror-eligible, or for a sensitive one, changes nothing at all: eligibility is a
    /// definition fact checked at plan time (see <c>SettingDescriptor.MirroredBindings</c>), and this table
    /// only records an operator preference that eligibility is then consulted against.
    /// </para>
    /// </remarks>
    Task<SaveDesiredValueResult> SetMirrorToDerivedAsync(
        ServerId serverId, string key, bool? mirrorToDerived, string actor, CancellationToken ct = default);
}
