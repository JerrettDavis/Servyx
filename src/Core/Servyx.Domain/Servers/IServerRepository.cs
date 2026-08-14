using Servyx.Domain.Common;
using Servyx.Domain.Entities;

namespace Servyx.Domain.Servers;

/// <summary>
/// Durable storage for the <see cref="Server"/> rows Servyx's own adoption/forget path reads and writes —
/// the read/write surface behind "ADOPT an already-running container", "VIEW it", and "FORGET it".
/// </summary>
/// <remarks>
/// <strong>Why this lives in <c>Servyx.Domain</c>.</strong> The only implementation that can honour the
/// word "durable" is one backed by a store, and every infrastructure project references
/// <c>Servyx.Domain</c> and nothing else, by design (see the defending comments in those projects' csproj
/// files). An abstraction infrastructure must <em>implement</em> therefore has to be declared here, exactly
/// the same reasoning <c>IProvisioningLedger</c> and <c>IServerDefinitionBindingStore</c> already follow.
/// <c>Servyx.Infrastructure.Persistence</c> supplies the real, EF-backed implementation
/// (<c>EfServerRepository</c>, over the <c>Servers</c> table).
/// </remarks>
public interface IServerRepository
{
    /// <summary>Every currently-tracked <see cref="Server"/> row, in no particular order.</summary>
    Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct = default);

    /// <summary>The tracked row for <paramref name="id"/>, or <see langword="null"/> if none exists.</summary>
    Task<Server?> TryGetAsync(ServerId id, CancellationToken ct = default);

    /// <summary>Persists a newly-adopted (or, later, newly-provisioned) <see cref="Server"/> row.</summary>
    Task AddAsync(Server server, CancellationToken ct = default);

    /// <summary>
    /// Records a new write-access posture for <paramref name="id"/>, stamping
    /// <see cref="Server.WriteModeChangedBy"/> and <see cref="Server.WriteModeChangedAt"/> alongside it.
    /// Returns the updated row, or <see langword="null"/> when no row exists for <paramref name="id"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the only sanctioned way a write grant is created or revoked.</strong> It is a single
    /// unit of work on purpose: the posture and its attribution are written together, so a row can never
    /// carry a grant nobody is recorded as having made. Like every other member here it touches ONLY
    /// Servyx's own storage — granting write access does not, by itself, contact the workload at all.
    /// </para>
    /// <para>
    /// Callers must invalidate whatever in-memory view of the grants they hold immediately after this
    /// returns, before reporting success. A revocation that is durable but not yet visible to the write
    /// guard is a revocation the operator believes happened and that has not.
    /// </para>
    /// </remarks>
    /// <param name="id">The server whose posture changes.</param>
    /// <param name="mode">The posture to record.</param>
    /// <param name="changedBy">Who made the change, as the host understands identity.</param>
    /// <param name="changedAt">When the change was made.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Server?> SetWriteModeAsync(
        ServerId id,
        ServerWriteMode mode,
        string changedBy,
        DateTimeOffset changedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Records a new default for <see cref="Server.MirrorDerivedSurfaces"/> on <paramref name="id"/>,
    /// stamping <see cref="Server.MirrorDerivedSurfacesChangedBy"/> and
    /// <see cref="Server.MirrorDerivedSurfacesChangedAt"/> alongside it. Returns the updated row, or
    /// <see langword="null"/> when no row exists for <paramref name="id"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shaped exactly like <see cref="SetWriteModeAsync"/> — one unit of work writing the flag and its
    /// attribution together, touching only Servyx's own storage — because it is the same kind of fact: an
    /// operator-recorded posture about how much Servyx may change, carrying who recorded it.
    /// </para>
    /// <para>
    /// <strong>It is not a second write grant and does not act as one.</strong> A server whose
    /// <see cref="Server.WriteMode"/> is not <see cref="ServerWriteMode.Enabled"/> writes nothing whatever
    /// this says; a setting the governing definition never declared mirror-eligible is not mirrored whatever
    /// this says; and a sensitive setting is never mirrored at all. This only decides what an
    /// already-eligible setting does when its own row expresses no opinion.
    /// </para>
    /// </remarks>
    /// <param name="id">The server whose default changes.</param>
    /// <param name="mirrorDerivedSurfaces">The default to record.</param>
    /// <param name="changedBy">Who made the change, as the host understands identity.</param>
    /// <param name="changedAt">When the change was made.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Server?> SetMirrorDerivedSurfacesAsync(
        ServerId id,
        bool mirrorDerivedSurfaces,
        string changedBy,
        DateTimeOffset changedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the tracked row for <paramref name="id"/>, if one exists. Returns <see langword="true"/> when
    /// a row was actually removed, <see langword="false"/> when none existed to remove. This method touches
    /// only Servyx's own storage — it has no way to reach, and must never be asked to reach, the workload
    /// itself.
    /// </summary>
    Task<bool> RemoveAsync(ServerId id, CancellationToken ct = default);
}
