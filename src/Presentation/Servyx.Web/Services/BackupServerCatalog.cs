using Servyx.Composition;
using Servyx.Web.Models;

namespace Servyx.Web.Services;

/// <summary>How Servyx reaches a server it can back up.</summary>
public enum BackupServerKind
{
    /// <summary>Discovered by Docker adoption and reached through the Docker transport.</summary>
    Docker,

    /// <summary>Declared under <c>Servyx:Servers:&lt;key&gt;:Ssh</c> and reached over SSH.</summary>
    Ssh,
}

/// <summary>
/// One entry in the Backups page's server picker: the id a backup call is made with, the name an operator
/// reads, and which kind of server it is.
/// </summary>
/// <param name="Id">
/// The id every <c>IBackupDashboard</c> call carries. For Docker this is the discovery id; for SSH it is the
/// <c>Servyx:Servers:&lt;key&gt;</c> configuration key, which is exactly what
/// <see cref="ServyxBackupProviderRouter.SshServerIds"/> holds — so selecting an entry routes to the provider
/// that owns it without this catalog restating the routing rule.
/// </param>
/// <param name="Name">The name shown to the operator.</param>
/// <param name="Kind">Docker-discovered or SSH-hosted.</param>
/// <param name="Endpoint">
/// The SSH endpoint this server is reached at, or <see langword="null"/> for a Docker-discovered one. Shown
/// beside the name so two entries that share one are still distinguishable.
/// </param>
/// <param name="Collides">
/// Whether another entry in the same catalog claims this entry's name or id. See
/// <see cref="BackupServerCatalog.Collisions"/>.
/// </param>
public sealed record BackupServerChoice(
    string Id,
    string Name,
    BackupServerKind Kind,
    string? Endpoint,
    bool Collides)
{
    /// <summary>A stable machine-readable kind, for <c>data-kind</c> and for tests.</summary>
    public string KindId => Kind == BackupServerKind.Ssh ? "ssh" : "docker";

    /// <summary>The kind, spelled for an operator.</summary>
    public string KindLabel => Kind == BackupServerKind.Ssh ? "SSH-hosted" : "Docker-discovered";
}

/// <summary>
/// A Docker-discovered server and a configured SSH server that answer to the same string.
/// </summary>
/// <param name="Value">The shared name or id — always the SSH server's configuration key.</param>
/// <param name="DockerServerId">The discovery id of the Docker server involved.</param>
/// <param name="DockerServerName">The name of the Docker server involved.</param>
/// <param name="ShadowsRouting">
/// <see langword="true"/> when the clash is on the Docker server's <em>id</em>, not merely its name. That is
/// the case that actually changes behaviour: <see cref="ServyxBackupProviderRouter"/> dispatches on the id, so
/// a backup call for that id reaches the SSH provider and the Docker-discovered server of the same id cannot
/// be acted on from this page at all. A name-only clash routes correctly — the two ids differ — and is only a
/// hazard for the human reading the picker.
/// </param>
public sealed record BackupServerNameCollision(
    string Value,
    string DockerServerId,
    string DockerServerName,
    bool ShadowsRouting);

/// <summary>
/// The servers the Backups page can offer: Docker's discovered list and the operator's configured SSH-hosted
/// servers, merged into one ordered picker.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a catalog rather than another <c>IDashboardDataService</c> member.</strong>
/// <see cref="IDashboardDataService.GetServersAsync"/> answers "what has Docker adopted?", and both its
/// implementations answer it honestly. An SSH-hosted server is not adopted by Docker and has no container,
/// no image, no ports and no health — every field a <see cref="ServerSummary"/> promises. Widening that
/// method would force one of the two services to invent those fields, or force every caller — the dashboard,
/// the server list, the home page — to start filtering out rows that are not really containers. This type
/// instead merges at the one page that needs both, and leaves Docker's list untouched everywhere else.
/// </para>
/// <para>
/// <strong>Docker's list is unchanged when nothing is SSH-hosted.</strong> Docker's servers are emitted
/// first, in discovery order, with the same ids and names; SSH's are appended after. With
/// <see cref="SshBackupWiringOptions.None"/> — the default, and the only possibility while
/// <c>Servyx:Provisioning:Enabled</c> is off — nothing is appended, nothing is flagged, and
/// <see cref="Servers"/> is <see cref="IDashboardDataService.GetServersAsync"/>'s list in order.
/// <see cref="Qualified"/> is then <see langword="false"/> and the page renders bare names exactly as before:
/// a kind label distinguishes one source from another, and with one source there is nothing to distinguish.
/// </para>
/// <para>
/// <strong>A clash is reported, never resolved.</strong> Where a configured SSH key matches a Docker server's
/// id or name, <em>both</em> entries stay in the picker and both are marked
/// <see cref="BackupServerChoice.Collides"/>. Dropping either would be this type quietly deciding which
/// machine an operator's restore overwrites — the same failure <see cref="ServyxBackupProviderRouter"/>
/// refuses to let registration order make. Renaming either would put a name on screen that matches nothing
/// in configuration. So both are shown, each labelled with its kind and the SSH one with its endpoint, and
/// the clash is published through <see cref="Collisions"/> for the page to state outright.
/// </para>
/// </remarks>
public sealed class BackupServerCatalog
{
    /// <summary>No server at all. The state of a host that has adopted nothing and configured nothing.</summary>
    public static readonly BackupServerCatalog Empty = new([], []);

    private readonly IReadOnlyList<BackupServerChoice> _servers;
    private readonly IReadOnlyList<BackupServerNameCollision> _collisions;

    private BackupServerCatalog(
        IReadOnlyList<BackupServerChoice> servers,
        IReadOnlyList<BackupServerNameCollision> collisions)
    {
        _servers = servers;
        _collisions = collisions;
    }

    /// <summary>Every selectable server: Docker's, in discovery order, then the configured SSH ones.</summary>
    public IReadOnlyList<BackupServerChoice> Servers => _servers;

    /// <summary>Every name or id claimed by both a Docker-discovered and a configured SSH server.</summary>
    public IReadOnlyList<BackupServerNameCollision> Collisions => _collisions;

    /// <summary>Whether any name or id is claimed twice.</summary>
    public bool HasCollisions => _collisions.Count > 0;

    /// <summary>Whether any entry is SSH-hosted.</summary>
    public bool HasSsh => _servers.Any(s => s.Kind == BackupServerKind.Ssh);

    /// <summary>
    /// Whether entries should carry a kind label. False for a Docker-only catalog, which is the default and
    /// which therefore reads exactly as it did before SSH servers could be selected.
    /// </summary>
    public bool Qualified => HasSsh;

    /// <summary>Finds an entry by the id a backup call carries, or returns <see langword="null"/>.</summary>
    /// <param name="serverId">The server id.</param>
    public BackupServerChoice? Find(string? serverId) =>
        string.IsNullOrWhiteSpace(serverId)
            ? null
            : _servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.Ordinal));

    /// <summary>The text shown for one entry in the picker.</summary>
    /// <param name="choice">The entry.</param>
    public string OptionLabel(BackupServerChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);

        if (!Qualified)
        {
            return choice.Name;
        }

        return choice.Endpoint is null
            ? $"{choice.Name} — {choice.KindLabel}"
            : $"{choice.Name} — {choice.KindLabel} ({choice.Endpoint})";
    }

    /// <summary>
    /// Merges Docker's discovered servers with the operator's configured SSH-hosted ones.
    /// </summary>
    /// <param name="dockerServers">
    /// <see cref="IDashboardDataService.GetServersAsync"/>'s result, carried through in order.
    /// </param>
    /// <param name="ssh">
    /// The configured SSH-hosted servers. <see langword="null"/> and <see cref="SshBackupWiringOptions.None"/>
    /// both mean "none", because a closed provisioning gate registers no options object at all.
    /// </param>
    public static BackupServerCatalog Merge(
        IReadOnlyList<ServerSummary>? dockerServers,
        SshBackupWiringOptions? ssh)
    {
        var docker = dockerServers ?? [];
        var sshServers = (ssh ?? SshBackupWiringOptions.None).Servers;

        var choices = new List<BackupServerChoice>(docker.Count + sshServers.Count);
        foreach (var server in docker)
        {
            choices.Add(new BackupServerChoice(server.Id, server.Name, BackupServerKind.Docker, null, Collides: false));
        }

        // Nothing SSH-hosted: the list above is Docker's, verbatim, and no entry was touched.
        if (sshServers.Count == 0)
        {
            return choices.Count == 0 ? Empty : new BackupServerCatalog(choices, []);
        }

        var dockerCount = choices.Count;
        var collisions = new List<BackupServerNameCollision>();

        foreach (var server in sshServers)
        {
            var collides = false;

            for (var i = 0; i < dockerCount; i++)
            {
                var candidate = choices[i];

                // Ordinal-ignore-case on both, matching how SshBackupWiringOptions.Find and
                // ServyxBackupProviderRouter compare ids: a clash that routing would honour must be reported,
                // and a clash only a reader would trip over must be too.
                var byId = string.Equals(candidate.Id, server.ServerKey, StringComparison.OrdinalIgnoreCase);
                var byName = string.Equals(candidate.Name, server.ServerKey, StringComparison.OrdinalIgnoreCase);

                if (!byId && !byName)
                {
                    continue;
                }

                collides = true;
                choices[i] = candidate with { Collides = true };
                collisions.Add(new BackupServerNameCollision(
                    server.ServerKey,
                    candidate.Id,
                    candidate.Name,
                    ShadowsRouting: byId));
            }

            choices.Add(new BackupServerChoice(
                server.ServerKey,
                server.ServerKey,
                BackupServerKind.Ssh,
                server.Endpoint,
                collides));
        }

        return new BackupServerCatalog(choices, collisions);
    }
}
