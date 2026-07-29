using Servyx.Domain.Backups;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Backups;

/// <summary>
/// A directory of archives some other mechanism created — a distro cron job, a game's own scheduled save
/// export — that Servyx lists and can inspect but never writes to.
/// </summary>
/// <remarks>
/// This project ships no <see cref="IBackupAdopter"/> of its own. Docker has one only because the Palworld
/// image genuinely ships a cron job whose output shape is knowable in advance; a generic SSH host has no
/// such convention to discover, and inventing one would mean guessing which of a stranger's tarballs are
/// backups. Declaring the directory here is how a composition root <em>tells</em> Servyx where foreign
/// archives live, and a host that also registers an adopter for its own layout gets those artifacts surfaced
/// read-only, never managed.
/// </remarks>
/// <param name="AdapterId">The <see cref="IBackupAdopter.AdapterId"/> that owns this source.</param>
/// <param name="Directory">Absolute host directory the archives live in.</param>
/// <param name="Pattern">Filename glob identifying archives (e.g. <c>*.tar.gz</c>).</param>
public sealed record ForeignSshBackupDirectory(string AdapterId, string Directory, string Pattern);

/// <summary>
/// Everything <see cref="SshBackupProvider"/> needs to act on one server: which host to reach, what to
/// capture, where to put it, and what foreign archives exist alongside it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One target, one root.</strong> Unlike <c>DockerBackupContext</c>, which spans a container
/// filesystem and a host compose directory and therefore needs a list of independently-rooted sources, an
/// SSH-hosted server is one machine reached through one connection. The archive is produced by that
/// machine's own <c>tar</c> with a single <c>--directory</c>, so a second root would need a second archive;
/// modelling one keeps the artifact and its manifest in a one-to-one relationship with what a restore
/// writes back.
/// </para>
/// <para>
/// <strong><see cref="StoreDirectory"/> lives under <see cref="Root"/>.</strong> That is not a convenience:
/// it is what lets <see cref="SshBackupProvider"/> guarantee the archive directory is excluded from every
/// archive it writes, so archives are never re-archived, and it is the containment check the delete barrier
/// re-asserts.
/// </para>
/// </remarks>
/// <param name="ServerId">The server this context describes.</param>
/// <param name="DeploymentKind">The definition's deployment kind, used by adopters' <see cref="IBackupAdopter.Supports"/>.</param>
/// <param name="Target">The execution target the host is reached through. Needs both an exec and a file channel.</param>
/// <param name="Root">The absolute host directory archives are taken relative to.</param>
/// <param name="Include">
/// <see cref="Root"/>-relative literal paths (files or directories) to capture. Wildcards are rejected: the
/// remote <c>tar</c> receives these as argv members with no shell in between, so a <c>*</c> would be taken
/// as a literal filename rather than expanded. Selectivity belongs in <paramref name="Exclude"/>, which
/// <c>tar</c> does glob.
/// </param>
/// <param name="Exclude">Glob patterns handed to <c>tar --exclude</c>, removing paths from the include set.</param>
/// <param name="StoreDirectory"><see cref="Root"/>-relative directory holding Servyx-owned archives.</param>
/// <param name="Foreign">Foreign archive directories to surface read-only.</param>
/// <param name="DefaultRetention">The definition's default retention, applied when a caller supplies none.</param>
public sealed record SshBackupContext(
    string ServerId,
    string DeploymentKind,
    IExecutionTarget Target,
    string Root,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    string StoreDirectory,
    IReadOnlyList<ForeignSshBackupDirectory> Foreign,
    RetentionPolicy DefaultRetention)
{
    /// <summary>The archiver invoked on the host. Overridable for hosts where GNU tar is not on <c>PATH</c> as <c>tar</c>.</summary>
    public string TarExecutable { get; init; } = "tar";

    /// <summary>The hasher invoked on the host to fingerprint a written archive.</summary>
    public string HashExecutable { get; init; } = "sha256sum";

    /// <summary>
    /// How long a single remote command may run. Archiving a large save is minutes of work on the host, so
    /// this defaults far above a normal command timeout.
    /// </summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Supplies the <see cref="SshBackupContext"/> for a server. Implemented by the composition root, which is
/// the only layer that knows how to turn a server id into a connected host, a loaded game definition, and
/// the variable substitutions its backup block is written in terms of.
/// </summary>
/// <remarks>
/// The returned context's <see cref="IExecutionTarget"/> is owned by the implementation, not by
/// <see cref="SshBackupProvider"/>: the provider never disposes it, so one target may back many calls.
/// </remarks>
public interface ISshBackupContextSource
{
    /// <summary>Returns the backup context for <paramref name="serverId"/>.</summary>
    /// <param name="serverId">The server to describe.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SshBackupContext> GetAsync(string serverId, CancellationToken ct = default);
}
