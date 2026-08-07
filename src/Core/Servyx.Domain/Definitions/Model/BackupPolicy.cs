using Servyx.Domain.Backups;

namespace Servyx.Domain.Definitions.Model;

/// <summary>
/// The parsed shape of a definition's <c>backup</c> block: what a Servyx-created backup archives, steps to
/// run before archiving, foreign backup sources to adopt, and default retention.
/// </summary>
/// <param name="Include">Glob patterns defining what a Servyx-created backup archives.</param>
/// <param name="Exclude">Glob patterns excluded from the archive — always removes the image's own backup directory, if any, to prevent re-archiving archives.</param>
/// <param name="Quiesce">Steps run before archiving, e.g. an RCON <c>save</c> command.</param>
/// <param name="Adopt">Foreign backup sources to list and make restorable without Servyx ever managing their lifecycle.</param>
/// <param name="DefaultRetention">
/// Default keep-counts applied only to <see cref="Servyx.Domain.Backups.BackupOwnership.Servyx"/>-owned
/// backups. Reuses the existing <see cref="Servyx.Domain.Backups.RetentionPolicy"/> record rather than a
/// second shape for the same three fields. Null when the definition declares no default.
/// </param>
public sealed record BackupPolicy(
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    IReadOnlyList<QuiesceStep> Quiesce,
    IReadOnlyList<BackupAdoptSource> Adopt,
    RetentionPolicy? DefaultRetention)
{
    /// <summary>
    /// Steps run after capture finishes, whatever the outcome — the undo half of <see cref="Quiesce"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this exists.</strong> The canonical safe-backup sequence for a live game server is
    /// "stop writing to disk, flush, copy the files, start writing again". <see cref="Quiesce"/> alone can
    /// only express the first half; a definition that used it to turn saving <em>off</em> had no way to
    /// turn it back on, so the first backup would leave saving disabled for the lifetime of the process.
    /// Every player action after that point would be lost at the next restart, and nothing would report it.
    /// </para>
    /// <para>
    /// <strong>These steps are guaranteed to run.</strong> A backup provider must issue them on every exit
    /// path out of capture — success, a mid-list quiesce failure, a capture failure, and cancellation
    /// alike — and must not bind them to the caller's cancellation token, since an operator cancelling a
    /// backup is asking to stop copying files, never to leave the server unable to save. A failure here is
    /// reported loudly rather than swallowed: see the provider's own resume documentation.
    /// </para>
    /// <para>
    /// Optional and empty by default, so a definition that declares no <c>backup.resume</c> block — every
    /// definition written before this key existed — parses and behaves exactly as it did before.
    /// </para>
    /// </remarks>
    public IReadOnlyList<QuiesceStep> Resume { get; init; } = [];
}

/// <summary>
/// One entry of <see cref="BackupPolicy.Quiesce"/> or <see cref="BackupPolicy.Resume"/>: an action taken
/// to bring the workload to a backup-consistent state before archiving, or to undo that action after.
/// Both phases take the same shape, so both are modelled by this one type; which phase a step belongs to
/// is carried by the list it is in, never by a discriminator on the step itself.
/// </summary>
public abstract record QuiesceStep
{
    private QuiesceStep()
    {
    }

    /// <summary>Invoke a control-channel command, e.g. RCON's <c>save</c>, waiting up to <paramref name="Timeout"/>.</summary>
    /// <param name="Channel">The control channel id to invoke on, e.g. <c>rcon</c>.</param>
    /// <param name="CommandId">The declared command id to invoke.</param>
    /// <param name="Timeout">Maximum time to wait for the command to complete.</param>
    public sealed record Control(string Channel, string CommandId, TimeSpan Timeout) : QuiesceStep;
}

/// <summary>
/// One entry of <see cref="BackupPolicy.Adopt"/>: a foreign backup source Servyx discovers and surfaces as
/// read-only, without ever managing its lifecycle.
/// </summary>
/// <param name="Adapter">The <see cref="Servyx.Domain.Backups.IBackupAdopter.AdapterId"/> that knows how to discover this source, e.g. <c>palworld-docker-cron</c>.</param>
/// <param name="Path">Where the foreign artifacts live.</param>
/// <param name="Pattern">A glob matching the foreign artifact filenames, e.g. <c>*.tar.gz</c>.</param>
/// <param name="Ownership">
/// Always <see cref="Servyx.Domain.Backups.BackupOwnership.Foreign"/> for an adopted source by construction
/// of this block — carried explicitly, using the existing enum, rather than assumed, so a definition that
/// somehow declares otherwise fails validation instead of being silently reinterpreted.
/// </param>
/// <param name="Note">A human-readable note shown in the UI, e.g. the foreign source's own retention policy.</param>
public sealed record BackupAdoptSource(string Adapter, string Path, string Pattern, BackupOwnership Ownership, string? Note);
