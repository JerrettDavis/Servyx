using Servyx.Domain.Backups;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process.Backups;

/// <summary>
/// A directory of archives some other mechanism created — a scheduled task, a distro cron job, a game's own
/// save export — that Servyx lists and can inspect but never writes to.
/// </summary>
/// <remarks>
/// This project ships no <see cref="IBackupAdopter"/> of its own, for the same reason the SSH project does
/// not: Docker has one only because the Palworld image genuinely ships a cron job whose output shape is
/// knowable in advance, and a bare machine has no such convention to discover. Inventing one would mean
/// guessing which of the operator's tarballs are backups, on the machine the panel itself runs on. Declaring
/// the directory here is how a composition root <em>tells</em> Servyx where foreign archives live; a host
/// that also registers an adopter for its own layout gets those artifacts surfaced read-only, never managed.
/// </remarks>
/// <param name="AdapterId">The <see cref="IBackupAdopter.AdapterId"/> that owns this source.</param>
/// <param name="Directory">Absolute local directory the archives live in.</param>
/// <param name="Pattern">Filename glob identifying archives (e.g. <c>*.tar.gz</c>).</param>
public sealed record ForeignLocalBackupDirectory(string AdapterId, string Directory, string Pattern);

/// <summary>
/// Everything <see cref="LocalProcessBackupProvider"/> needs to act on one server: which directory holds its
/// data, what to capture, where to put the archive, and what foreign archives exist alongside it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One target, one root.</strong> A locally-installed server is one directory tree on one machine,
/// which is the shape <c>LocalProcessSpec.DataDirectory</c> already produces. Modelling a single root keeps
/// the artifact and its manifest in a one-to-one relationship with what a restore writes back, and lets
/// every entry name in the archive be a plain root-relative path with no source prefix to decode.
/// </para>
/// <para>
/// <strong><see cref="StoreDirectory"/> lives under <see cref="Root"/>.</strong> That is not a convenience:
/// it is what lets the provider guarantee the archive directory is excluded from every archive it writes, so
/// archives are never re-archived, and it is the containment check the delete barrier re-asserts. A context
/// that names no store directory is refused outright rather than defaulting to the root.
/// </para>
/// <para>
/// <strong><see cref="Include"/> and <see cref="Exclude"/> may both use globs.</strong> The SSH provider
/// rejects a wildcard include because its includes become argv members of a remote <c>tar</c> with no shell
/// to expand them. This provider walks the tree itself, so a pattern is matched by
/// <see cref="BackupGlob"/> rather than handed to anything — <c>saves/**/*.db</c> means what its author
/// meant.
/// </para>
/// </remarks>
/// <param name="ServerId">The server this context describes.</param>
/// <param name="DeploymentKind">The definition's deployment kind, used by adopters' <see cref="IBackupAdopter.Supports"/>.</param>
/// <param name="Target">
/// The execution target the files are reached through. Normally a <see cref="WriteGuardedExecutionTarget"/>
/// over a <c>LocalExecutionTarget</c> rooted at <paramref name="Root"/>: every archive byte and every delete
/// travels through it, so the guard is structural rather than advisory for those steps.
/// </param>
/// <param name="Root">The absolute local directory archives are taken relative to.</param>
/// <param name="Include">
/// <paramref name="Root"/>-relative paths or globs to capture. A literal path may name a file or a
/// directory; naming a directory captures it whole, subject to <paramref name="Exclude"/>.
/// </param>
/// <param name="Exclude">Glob patterns removing paths from the include set. Whole subtrees are pruned, not filtered per file.</param>
/// <param name="StoreDirectory"><paramref name="Root"/>-relative directory holding Servyx-owned archives.</param>
/// <param name="Foreign">Foreign archive directories to surface read-only.</param>
/// <param name="DefaultRetention">The definition's default retention, applied when a caller supplies none.</param>
public sealed record LocalBackupContext(
    string ServerId,
    string DeploymentKind,
    IExecutionTarget Target,
    string Root,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    string StoreDirectory,
    IReadOnlyList<ForeignLocalBackupDirectory> Foreign,
    RetentionPolicy DefaultRetention)
{
    /// <summary>
    /// How deep the include walk descends before it stops. A cycle through a directory link would otherwise
    /// walk forever; the sandbox already refuses a link whose target leaves the root, but a link pointing
    /// back <em>inside</em> it is contained and still circular.
    /// </summary>
    public int MaxTraversalDepth { get; init; } = 64;
}

/// <summary>
/// Supplies the <see cref="LocalBackupContext"/> for a server. Implemented by the composition root, which is
/// the only layer that knows how to turn a server id into an installed data directory, a loaded game
/// definition, and the variable substitutions its backup block is written in terms of.
/// </summary>
/// <remarks>
/// The returned context's <see cref="IExecutionTarget"/> is owned by the implementation, not by
/// <see cref="LocalProcessBackupProvider"/>: the provider never disposes it, so one target may back many
/// calls.
/// </remarks>
public interface ILocalBackupContextSource
{
    /// <summary>Returns the backup context for <paramref name="serverId"/>.</summary>
    /// <param name="serverId">The server to describe.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<LocalBackupContext> GetAsync(string serverId, CancellationToken ct = default);
}
