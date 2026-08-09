using Servyx.Domain.Entities;

namespace Servyx.Domain.Transport;

/// <summary>
/// The one sanctioned bridge between the persisted <see cref="ServerWriteMode"/> and the transport-facing
/// <see cref="WriteMode"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this type has to exist at all.</strong> <see cref="ServerWriteMode"/> (the entity column) and
/// <see cref="WriteMode"/> (what the write guard enforces) are two <em>different</em> enums that happen to
/// declare identical members in an identical order. A plain cast between them therefore compiles silently
/// and is correct only by coincidence — and it sits directly on the enforcement path, so the day someone
/// adds a member to one and not the other, a cast turns into a silent mis-grant rather than a build failure.
/// Once the database column became the authoritative source of a write grant, that coincidence stopped being
/// acceptable, so every conversion goes through here instead.
/// </para>
/// <para>
/// <strong>No default arm, deliberately.</strong> Both switch expressions below enumerate every named member
/// and nothing else. Adding a member to either enum without updating this file is a compile error
/// (CS8509), which is the entire point: the mapping is the place divergence is supposed to be noticed.
/// <c>CS8524</c> — "some <em>unnamed</em> enum value is not handled", i.e. a value cast in from an integer
/// that names no member — is suppressed rather than answered with a <c>_ =&gt;</c> arm, because a discard arm
/// would also swallow the named-member case CS8509 exists to catch. A value outside the declared set can
/// only arrive from an unchecked cast, which is not a state this codebase creates.
/// </para>
/// <para>
/// The domain enum is authoritative and the transport enum is its projection — see
/// <c>docs/plans/ui-management-surface.md</c> §2 "The two-enum hazard". A parity test asserts the two
/// declare the same members in the same order, so divergence fails the build from two directions.
/// </para>
/// </remarks>
public static class WriteModeMapping
{
    /// <summary>Projects a persisted <paramref name="mode"/> onto the write guard's own enum.</summary>
    /// <param name="mode">The value read from <see cref="Servyx.Domain.Entities.Server.WriteMode"/>.</param>
#pragma warning disable CS8524 // Unnamed enum values cannot arrive here without an unchecked cast; see remarks.
    public static WriteMode ToTransport(ServerWriteMode mode) => mode switch
    {
        ServerWriteMode.ReadOnly => WriteMode.ReadOnly,
        ServerWriteMode.PreviewOnly => WriteMode.PreviewOnly,
        ServerWriteMode.Enabled => WriteMode.Enabled,
    };

    /// <summary>
    /// Projects a transport <paramref name="mode"/> back onto the persisted enum. Used only where an
    /// operator's UI selection (expressed in the transport enum the rest of the UI already speaks) has to be
    /// written to the column; the column, not this direction, remains authoritative.
    /// </summary>
    /// <param name="mode">The transport-facing value to persist.</param>
    public static ServerWriteMode ToDomain(WriteMode mode) => mode switch
    {
        WriteMode.ReadOnly => ServerWriteMode.ReadOnly,
        WriteMode.PreviewOnly => ServerWriteMode.PreviewOnly,
        WriteMode.Enabled => ServerWriteMode.Enabled,
    };
#pragma warning restore CS8524
}
