using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Configuration;

/// <summary>
/// Everything a <see cref="ISurfaceResolver"/> needs to turn a definition-authored
/// <see cref="DeclaredConfigSurface.Locator"/> into a concrete <see cref="TargetPath"/> on one live session.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because <see cref="IExecutionTarget"/> answers none of these questions.</strong> A
/// session exposes operations, not the facts about the deployment those operations run against:
/// <see cref="TransportCapabilities"/> is declared on <see cref="ITransport"/> and is gone by the time a
/// session has been handed out, and the two root variables every definition's locators are written against —
/// <c>${DATA_DIR}</c> and <c>${COMPOSE_DIR}</c> — are per-server deployment facts that no transport knows.
/// Rather than guess either, the resolver asks an <see cref="ISurfaceResolutionContextSource"/> and refuses
/// the surfaces it cannot answer for.
/// </para>
/// <para>
/// <strong><see cref="DataDirectory"/> and <see cref="ComposeDirectory"/> are two different filesystems, not
/// two spellings of one.</strong> <c>${DATA_DIR}</c> is the deployment's data root — for a
/// <c>kind: docker</c> profile that is a path <em>inside</em> the container (Palworld's <c>/palworld</c>,
/// Minecraft's <c>/data</c>); for a <c>kind: process</c> profile it is an ordinary host directory.
/// <c>${COMPOSE_DIR}</c> is always a host directory: it is where <c>.env</c> and <c>compose.yaml</c> sit
/// next to each other, and no container filesystem can serve it. <c>ServyxBackupContextSource</c> already
/// splits a definition's backup globs along exactly this line and builds a separate
/// <c>BackupSource</c> per root for the same reason.
/// </para>
/// </remarks>
/// <param name="Capabilities">
/// What the transport backing this session can actually do. Sourced from
/// <see cref="ITransport.Capabilities"/> at the point the session was opened.
/// </param>
/// <param name="SessionRoot">
/// The absolute path the session's <see cref="TargetPath"/> values are relative to — the descriptor's
/// <c>rootPath</c>. <c>"/"</c> for a whole-host session (SSH/SFTP, a local process transport rooted at the
/// filesystem root), the container's own root path for a Docker session, or the compose directory itself for
/// a session opened specifically over it. Getting this wrong silently doubles or drops a path prefix, which
/// is why it is a required fact rather than an assumed <c>"/"</c>.
/// </param>
/// <param name="DataDirectory">
/// The absolute expansion of <c>${DATA_DIR}</c> — the deployment profile's <c>dataDir</c>, or the adopted
/// container's reported mount path. <see langword="null"/> when it is not known, in which case every
/// <c>${DATA_DIR}</c>-rooted surface is reported unresolvable rather than resolved against a guess.
/// </param>
/// <param name="ComposeDirectory">
/// The absolute expansion of <c>${COMPOSE_DIR}</c> — the host directory holding this server's compose file
/// and <c>.env</c>. <see langword="null"/> when the operator has not configured one; there is no way to
/// discover it from inside a container, so it is never inferred.
/// </param>
/// <param name="DataDirectoryIsContainerScoped">
/// Whether <see cref="DataDirectory"/> names a path inside the workload's container rather than on the host.
/// True for a <c>kind: docker</c> deployment, false for <c>kind: process</c>. When true, a session whose
/// <see cref="Capabilities"/> lack <see cref="TransportCapabilities.ContainerScopedFiles"/> cannot serve
/// those surfaces at all — see <see cref="ContainerScopedFilesNotSupportedException"/>.
/// </param>
public sealed record SurfaceResolutionContext(
    TransportCapabilities Capabilities,
    string SessionRoot,
    string? DataDirectory,
    string? ComposeDirectory,
    bool DataDirectoryIsContainerScoped);

/// <summary>
/// Supplies the per-server <see cref="SurfaceResolutionContext"/> an <see cref="ISurfaceResolver"/> resolves
/// against.
/// </summary>
/// <remarks>
/// Deliberately a separate abstraction from <see cref="ISurfaceResolver"/>: the resolution <em>rules</em>
/// (root expansion, capability arithmetic, the never-write-a-derived-surface invariant) are game- and
/// deployment-agnostic and belong in one place, while the <em>facts</em> those rules consume differ per
/// deployment kind and are owned by whichever composition root wired the session up.
/// </remarks>
public interface ISurfaceResolutionContextSource
{
    /// <summary>
    /// Returns the resolution context for <paramref name="serverId"/> on <paramref name="target"/>, or
    /// <see langword="null"/> when nothing is known about that server. Returning <see langword="null"/> is a
    /// supported answer, not an error: the resolver turns it into one
    /// <see cref="SurfaceResolutionFailure"/> per surface naming what is missing, rather than throwing.
    /// </summary>
    Task<SurfaceResolutionContext?> GetAsync(string serverId, IExecutionTarget target, CancellationToken ct = default);
}

/// <summary>
/// One declared surface that could <em>not</em> be turned into a concrete, safely reachable path, and why.
/// </summary>
/// <remarks>
/// A failure here is a first-class result, never an exception. Resolution runs over a whole surface set at
/// once, and the useful answer for an operator is "these four are reachable, this one is not and here is
/// what to change" — an exception collapses that into "nothing worked". It is also the barrier that keeps
/// a correct set of bytes from being written to the wrong filesystem: refusing loudly beats resolving
/// optimistically.
/// </remarks>
/// <param name="SurfaceId">The <see cref="DeclaredConfigSurface.Id"/> that could not be resolved.</param>
/// <param name="Reason">What specifically was wrong, phrased for an operator rather than a stack trace.</param>
/// <param name="RemediationHint">
/// The concrete next action that would make this surface resolvable — a transport to use, a value to
/// configure, an adapter to register. Never empty: a failure an operator cannot act on is a dead end.
/// </param>
public sealed record SurfaceResolutionFailure(string SurfaceId, string Reason, string RemediationHint);

/// <summary>
/// The outcome of resolving a whole declared surface set against one session: what is reachable, and what
/// is not.
/// </summary>
/// <remarks>
/// Partial success is the normal case, not a degraded one. A ssh+docker deployment can reach its
/// <c>${COMPOSE_DIR}</c> surfaces over SFTP and genuinely cannot reach its in-container
/// <c>${DATA_DIR}</c> ones; a definition can declare a YAML surface before a YAML
/// <see cref="IConfigAdapter"/> exists. Both lists are always present and either may be empty.
/// </remarks>
/// <param name="Resolved">
/// The surfaces that resolved, each carrying a concrete <see cref="ConfigSurface.Path"/> and the
/// <see cref="ConfigSurface.RequiredCapabilities"/> the session was checked against.
/// </param>
/// <param name="Unresolvable">The surfaces that did not, each with a reason and a remediation hint.</param>
public sealed record SurfaceResolution(
    IReadOnlyList<ConfigSurface> Resolved,
    IReadOnlyList<SurfaceResolutionFailure> Unresolvable);

/// <summary>
/// Projects definition-authored <see cref="DeclaredConfigSurface"/> declarations down to the engine's
/// resolved <see cref="ConfigSurface"/> shape, binding each one to a concrete <see cref="TargetPath"/> on a
/// live session — or refusing it with a reason.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the second half of the split <see cref="DeclaredConfigSurface"/>'s own remarks describe.</strong>
/// A definition author writes where a surface lives as a template (<c>${DATA_DIR}/server.properties</c>);
/// the engine needs a path it can hand to <see cref="IExecutionTarget.OpenReadAsync"/>. Nothing performed
/// that projection before this interface existed, which is why Servyx could record desired settings and
/// never apply them.
/// </para>
/// <para>
/// <strong>Resolution is read-only and performs no I/O.</strong> It does not stat, list, or open anything on
/// the target: it answers "where would this surface be, and may this session touch it", not "is it there".
/// Existence is a later question, and asking it here would make the cheap, safe operation an operator
/// triggers on every settings-page load into a round trip that fails differently on every transport.
/// </para>
/// <para>
/// <strong>Two invariants are structural, not conventional.</strong> A
/// <see cref="SurfaceRole.Derived"/> surface never resolves with
/// <see cref="TransportCapabilities.FileWrite"/> in its
/// <see cref="ConfigSurface.RequiredCapabilities"/>, because
/// <see cref="ConfigSurface.ServyxMayWrite"/> is derived from <see cref="SurfaceRole"/> and cannot be set
/// independently. And a surface that lives inside a container never resolves at all on a session whose file
/// channel reaches the host — the failure mode there is not an error but a successful write to the wrong
/// filesystem, so the only safe answer is to refuse.
/// </para>
/// </remarks>
public interface ISurfaceResolver
{
    /// <summary>
    /// Resolves <paramref name="surfaces"/> against <paramref name="target"/>, returning the ones that
    /// produced a concrete reachable path and the ones that did not.
    /// </summary>
    /// <remarks>
    /// Never throws for an unresolvable surface — that is what
    /// <see cref="SurfaceResolution.Unresolvable"/> is for. Argument validation (a null
    /// <paramref name="target"/>, an empty <paramref name="serverId"/>) still throws, because those are
    /// caller bugs rather than facts about a deployment.
    /// </remarks>
    /// <param name="serverId">The server whose deployment facts the locators are expanded against.</param>
    /// <param name="target">The live session the resolved paths will be used on.</param>
    /// <param name="surfaces">The declared surface set, typically one deployment profile's <c>config.surfaces</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SurfaceResolution> ResolveAsync(
        string serverId,
        IExecutionTarget target,
        IReadOnlyList<DeclaredConfigSurface> surfaces,
        CancellationToken ct = default);
}
