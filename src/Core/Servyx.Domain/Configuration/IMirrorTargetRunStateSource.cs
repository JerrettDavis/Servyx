namespace Servyx.Domain.Configuration;

/// <summary>
/// What a mirror-write target's workload is doing right now, as far as anything below the settings pipeline
/// can tell.
/// </summary>
/// <param name="Running">
/// Whether the workload is running. <see langword="false"/> is a definite "it is not"; a source that cannot
/// tell returns <see langword="null"/> from <see cref="IMirrorTargetRunStateSource.GetAsync"/> rather than
/// guessing <see langword="false"/> here.
/// </param>
/// <param name="State">
/// The transport-reported state string (e.g. <c>"running"</c>, <c>"exited"</c>, <c>"created"</c>) when one
/// is available, so a refusal can quote what was actually observed instead of paraphrasing it.
/// </param>
public sealed record MirrorTargetRunState(bool Running, string? State = null);

/// <summary>
/// Reads whether the workload behind a server's container-scoped surfaces is currently running, without
/// mutating anything and without routing through <c>IServerQueryService</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the settings pipeline needs this at all.</strong> A mirrored write lands on a file inside a
/// container, and the only atomic way to place it there is a temporary sibling finalized by a rename issued
/// through <c>docker exec</c> — which requires a running container. Against a stopped one the rename fails,
/// the transport removes its sibling and the target file is untouched: safe, but the operator finds out at
/// apply time, after approving a diff, with an error about a rename. Asking the question at PREVIEW time
/// turns that into a named <see cref="BlockedChange"/> with a remediation hint, which is the difference
/// between an honest refusal and a confusing one. Note the asymmetry this exists to preserve: the
/// authoritative <c>.env</c> write is a host file and works whether or not the workload is running, so
/// blocking the whole plan would be wrong — only the mirror action is blocked.
/// </para>
/// <para>
/// <strong>Why it is a separate port rather than <c>IContainerStateProbe</c>.</strong> That port lives in
/// <c>Servyx.Application</c>, which <c>Servyx.Config</c> does not (and must not) reference, and its only
/// composed implementation adapts <c>IServerQueryService</c> — the exact dependency
/// <see cref="IServerPlanCatalogSource"/>'s remarks forbid the plan pipeline from taking, because the query
/// service optionally consumes the settings pipeline and all three are singletons. An implementation of this
/// interface must be a leaf at or below the layer <c>ISurfaceResolutionContextSource</c> already sits on
/// (server discovery, or a cache fed by it), never a reach back up into the query service.
/// </para>
/// <para>
/// <strong>Read-only, and never a write gate.</strong> Nothing here authorises anything: the write grant,
/// the transport's own guard and every capability check are untouched and still run. This answers one
/// question — "would the finalizing rename have somewhere to run?" — and a wrong answer can only ever cost a
/// mirror action that would have worked, never allow one that should not have.
/// </para>
/// </remarks>
public interface IMirrorTargetRunStateSource
{
    /// <summary>
    /// Returns the run state of the workload behind <paramref name="serverId"/> (a container id, the same
    /// identity <see cref="IServerSettingsService.LoadAsync"/> and <see cref="ISurfaceResolver.ResolveAsync"/>
    /// are keyed by), or <see langword="null"/> when it cannot be determined.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> means "unknown", and a caller must treat it as a reason to refuse a mirrored
    /// write rather than as permission to attempt one. Failing closed here costs an operator one blocked
    /// action they can act on; failing open costs them an approved diff that fails halfway through.
    /// </remarks>
    Task<MirrorTargetRunState?> GetAsync(string serverId, CancellationToken ct = default);
}
