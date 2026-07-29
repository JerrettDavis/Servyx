using Servyx.Domain.Backups;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker.Backups;

/// <summary>
/// One filesystem region a Servyx-owned backup captures, together with the target it is read from.
/// </summary>
/// <remarks>
/// A single server's backup normally spans more than one filesystem: Palworld's saves and rendered INI
/// live inside the container, while <c>.env</c> and <c>compose.yaml</c> live on the host next to the
/// compose file. Modelling that as a list of independently-rooted sources — rather than as one root with
/// clever globs — is what lets the archive carry both without the provider inventing a path relationship
/// between two filesystems that has none.
/// </remarks>
/// <param name="Id">
/// Short, stable identifier for this source (e.g. <c>data</c>, <c>compose</c>). It becomes the first path
/// segment of every archive entry drawn from this source, which is how a restore knows which filesystem
/// an entry came from without guessing.
/// </param>
/// <param name="Target">The execution target this source is read from and restored to.</param>
/// <param name="Root">The absolute root <paramref name="Target"/>'s <see cref="TargetPath"/>s are relative to.</param>
/// <param name="Include">Root-relative glob patterns selecting what to capture.</param>
/// <param name="Exclude">Root-relative glob patterns removing paths from the include set.</param>
public sealed record BackupSource(
    string Id,
    IExecutionTarget Target,
    string Root,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude);

/// <summary>Where Servyx writes and reads its own backup artifacts for a server.</summary>
/// <param name="Target">The execution target holding the artifact directory.</param>
/// <param name="Root">The absolute root <paramref name="Target"/>'s <see cref="TargetPath"/>s are relative to.</param>
/// <param name="Directory">
/// Root-relative directory holding Servyx-owned archives. It must not be a directory any workload writes
/// backups into itself — this directory is the one place <see cref="DockerBackupProvider"/> is ever
/// permitted to delete from.
/// </param>
public sealed record BackupStore(IExecutionTarget Target, string Root, string Directory);

/// <summary>
/// A directory of archives some other mechanism created — a container image's own cron job, typically —
/// that Servyx lists and can restore from but never writes to.
/// </summary>
/// <param name="AdapterId">The <see cref="IBackupAdopter.AdapterId"/> that owns this source.</param>
/// <param name="Target">The execution target holding the archives.</param>
/// <param name="Root">The absolute root <paramref name="Target"/>'s <see cref="TargetPath"/>s are relative to.</param>
/// <param name="Directory">Root-relative directory the archives live in.</param>
/// <param name="Pattern">Filename glob identifying archives (e.g. <c>*.tar.gz</c>).</param>
/// <param name="RestoreSourceId">
/// The <see cref="BackupSource.Id"/> whose root these archives' entries are relative to, or
/// <see langword="null"/> when the mapping is unknown. A null value makes the archives listable and
/// inspectable but not restorable: writing a foreign archive's entries into a guessed location is worse
/// than refusing, so <see cref="DockerBackupProvider.PlanRestoreAsync"/> refuses instead.
/// </param>
public sealed record ForeignBackupSource(
    string AdapterId,
    IExecutionTarget Target,
    string Root,
    string Directory,
    string Pattern,
    string? RestoreSourceId = null);

/// <summary>
/// A control-channel command issued to flush in-memory state to disk immediately before archiving, e.g.
/// Palworld's RCON <c>Save</c>.
/// </summary>
/// <param name="CommandId">The definition-declared control command id (never a raw command string).</param>
/// <param name="Arguments">Arguments for the command, if any.</param>
/// <param name="Timeout">How long to wait before treating the quiesce as failed.</param>
public sealed record QuiesceStep(string CommandId, IReadOnlyDictionary<string, string>? Arguments, TimeSpan Timeout);

/// <summary>
/// Everything <see cref="DockerBackupProvider"/> needs to act on one server: what to capture, where to
/// put it, what foreign archives exist alongside it, and how to quiesce first.
/// </summary>
/// <param name="ServerId">The server this context describes.</param>
/// <param name="DeploymentKind">The definition's deployment kind (e.g. <c>docker</c>), used by adopters' <see cref="IBackupAdopter.Supports"/>.</param>
/// <param name="Sources">The filesystem regions a Servyx-owned backup captures.</param>
/// <param name="Store">Where Servyx-owned artifacts are written.</param>
/// <param name="Foreign">Foreign archive directories to surface read-only.</param>
/// <param name="DefaultRetention">The definition's <c>defaultRetention</c>, applied when a caller supplies none.</param>
/// <param name="Quiesce">The pre-archive quiesce step, or <see langword="null"/> when the definition declares none.</param>
/// <param name="Control">
/// The control session <paramref name="Quiesce"/> is issued through. May be <see langword="null"/> only
/// when <paramref name="Quiesce"/> is also null: a context that asks for a quiesce it has no channel to
/// perform is a misconfiguration, and <see cref="DockerBackupProvider.CreateAsync"/> fails loudly rather
/// than writing an archive of un-flushed state.
/// </param>
public sealed record DockerBackupContext(
    string ServerId,
    string DeploymentKind,
    IReadOnlyList<BackupSource> Sources,
    BackupStore Store,
    IReadOnlyList<ForeignBackupSource> Foreign,
    RetentionPolicy DefaultRetention,
    QuiesceStep? Quiesce = null,
    IRconSession? Control = null);

/// <summary>
/// Supplies the <see cref="DockerBackupContext"/> for a server. Implemented by the composition root,
/// which is the only layer that knows how to turn a server id into an adopted container, a loaded game
/// definition, and the variable substitutions (<c>${DATA_DIR}</c>, <c>${COMPOSE_DIR}</c>) its backup
/// block is written in terms of.
/// </summary>
/// <remarks>
/// The returned context's <see cref="IExecutionTarget"/>s are owned by the implementation, not by
/// <see cref="DockerBackupProvider"/>: the provider never disposes them, so one target may back many
/// calls and many sources.
/// </remarks>
public interface IDockerBackupContextSource
{
    /// <summary>Returns the backup context for <paramref name="serverId"/>.</summary>
    /// <param name="serverId">The server to describe.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DockerBackupContext> GetAsync(string serverId, CancellationToken ct = default);
}
