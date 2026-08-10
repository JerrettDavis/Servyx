using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Configuration;

/// <summary>
/// One live session a server's configuration surfaces can be read through, together with the human-readable
/// name of the filesystem it reaches.
/// </summary>
/// <remarks>
/// A server needs more than one of these. <c>${DATA_DIR}</c> and <c>${COMPOSE_DIR}</c> are two different
/// filesystems for a <c>kind: docker</c> deployment — see <see cref="SurfaceResolutionContext"/>'s own
/// remarks — and <see cref="SurfaceResolutionContext.SessionRoot"/> names exactly one root, so no single
/// session can serve both. <c>ServyxBackupContextSource</c> already splits a definition's backup globs the
/// same way and builds one <c>BackupSource</c> per root.
/// </remarks>
/// <param name="Target">The session itself. Owned by whoever produced it; a consumer must not dispose it.</param>
/// <param name="Description">
/// What this session reaches, phrased for an operator — e.g. "the container's own filesystem" or "the host
/// compose directory". Used only in diagnostics.
/// </param>
public sealed record ConfigSession(IExecutionTarget Target, string Description);

/// <summary>
/// Everything needed to read one server's configuration surfaces: the sessions they are reachable through,
/// and the surface set its governing definition declares.
/// </summary>
/// <param name="Sessions">
/// Every session the server's surfaces may live on. May be empty, which is an honest answer meaning
/// "nothing about this server is reachable" rather than an error.
/// </param>
/// <param name="Surfaces">
/// The governing deployment profile's <c>config.surfaces</c> list, as declared. Supplied here rather than
/// by the caller because the same composition root that knows how to open the sessions is the one that
/// already resolved the server to its definition.
/// </param>
public sealed record ServerConfigSessions(
    IReadOnlyList<ConfigSession> Sessions,
    IReadOnlyList<DeclaredConfigSurface> Surfaces);

/// <summary>
/// Supplies the live sessions and declared surface set an <see cref="ISettingStateResolverFactory"/> reads
/// one server's configuration through.
/// </summary>
/// <remarks>
/// The sibling of <see cref="ISurfaceResolutionContextSource"/>, and for the same reason: the rules for
/// turning a surface into a value are game- and deployment-agnostic, while the facts those rules consume —
/// which container, which host directory, opened over which transport — belong to whichever composition
/// root wired the deployment up. An implementation is expected to own and cache the sessions it hands out,
/// and to dispose them itself; nothing downstream disposes an <see cref="IExecutionTarget"/> it was given.
/// </remarks>
public interface IServerConfigSessionSource
{
    /// <summary>
    /// Returns the sessions and declared surfaces for <paramref name="serverId"/>, or <see langword="null"/>
    /// when nothing is known about that server. Returning <see langword="null"/> is a supported answer, not
    /// an error.
    /// </summary>
    Task<ServerConfigSessions?> GetAsync(string serverId, CancellationToken ct = default);
}

/// <summary>
/// The per-server context an <see cref="ISettingStateResolver"/> is bound to when it is created.
/// </summary>
/// <remarks>
/// <see cref="ISettingStateResolver.ResolveAsync"/> takes only a setting key, which is why a resolver cannot
/// be a stateless singleton: everything else it needs is fixed for the lifetime of one settings view. This
/// record is that everything else.
/// </remarks>
/// <param name="ServerId">
/// The server whose surfaces are read. This is the container id — the same identity
/// <see cref="IServerSettingsService.LoadAsync"/> resolves desired values by and
/// <see cref="ISurfaceResolver.ResolveAsync"/> expands deployment facts against.
/// </param>
/// <param name="Settings">
/// The governing definition's settings catalogue, flattened out of its <see cref="SettingGroup"/>s. A key
/// not in this list is not resolvable, and asking for one is a caller bug rather than a deployment fact.
/// </param>
public sealed record SettingStateScope(string ServerId, IReadOnlyList<SettingDescriptor> Settings);

/// <summary>
/// Mints an <see cref="ISettingStateResolver"/> bound to one server.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a factory rather than a wider <see cref="ISettingStateResolver"/>.</strong> That interface's
/// <see cref="ISettingStateResolver.ResolveAsync"/> takes a setting key and nothing else, which is a
/// deliberate shape, not an oversight: every consumer of one resolver is already talking about one server,
/// so repeating the server id on every call would be noise. Binding the server at construction keeps the
/// declared contract intact and matches how this codebase already hands out per-server things —
/// <c>ServyxRconChannels.GetSessionAsync(serverId, …)</c> and
/// <c>ServyxBackupContextSource.GetAsync(serverId, …)</c> are singletons keyed by server id that return
/// bound objects.
/// </para>
/// <para>
/// <strong><see cref="CreateAsync"/> is the batch point.</strong> Creating a resolver runs surface
/// resolution over the whole declared surface set once and loads the desired-value snapshot once, so
/// per-key resolution afterwards costs at most one file read per surface — not one per setting. That is why
/// there is no batch overload on <see cref="ISettingStateResolver"/>: the cache makes one unnecessary.
/// </para>
/// </remarks>
public interface ISettingStateResolverFactory
{
    /// <summary>
    /// Creates a resolver bound to <paramref name="scope"/>. Never returns <see langword="null"/>: a server
    /// whose surfaces are entirely unreachable still yields a resolver, one that reports every column as
    /// unreadable with a reason rather than pretending it has no settings.
    /// </summary>
    Task<ISettingStateResolver> CreateAsync(SettingStateScope scope, CancellationToken ct = default);
}
