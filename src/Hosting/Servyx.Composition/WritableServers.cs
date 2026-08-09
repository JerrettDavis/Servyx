using Servyx.Domain.Transport;

namespace Servyx.Composition;

/// <summary>
/// The set of servers the operator has explicitly granted write access to, so a page can say "this server
/// is read-only" instead of offering a control that would throw
/// <see cref="WritesDisabledException"/> when clicked.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a label, not the enforcement.</strong> The enforcement is
/// <see cref="WriteGuardedExecutionTarget"/>, which refuses every write to a target whose resolved
/// <see cref="WriteMode"/> is not <see cref="WriteMode.Enabled"/>, regardless of what any UI believes.
/// This type exists so that the UI's belief is derived from the same source the guard reads, rather than
/// from an independent assumption that could drift away from it.
/// </para>
/// <para>
/// <strong>It is a live view, not a startup snapshot.</strong> When constructed over a
/// <see cref="WriteGrantCache"/> every read consults that cache, so a grant an operator flipped seconds ago
/// is reflected the next time a page renders. The previous shape — a frozen dictionary built from
/// configuration at process start — meant the READ-ONLY / WRITES ENABLED badge and every
/// <c>GatedButton.Enabled</c> reported the world as of startup. A label is allowed to be a label; it is not
/// allowed to lie.
/// </para>
/// <para>
/// <strong>The live view is keyed on container id.</strong> It answers for the identity the grant was
/// written against and for nothing else, matching <see cref="DbBackedWriteModeResolver"/> exactly — so the
/// control a page renders and the guard that would run agree by construction rather than by coincidence. A
/// caller that knows only a container name gets <see cref="WriteMode.ReadOnly"/>, which is what the guard
/// would do too.
/// </para>
/// <para>
/// The <see cref="None"/> fallback and the <c>Mode(id, name)</c> / <c>IsWritable(id, name)</c> call shape
/// are unchanged, so existing call sites did not have to churn.
/// </para>
/// </remarks>
public sealed class WritableServers
{
    /// <summary>No server is writable. The state of a read-only host, and the safe default.</summary>
    public static readonly WritableServers None = new(Array.Empty<string>());

    private readonly Dictionary<string, WriteMode>? _modes;
    private readonly WriteGrantCache? _grants;

    /// <summary>
    /// Creates a set over the given keys, each granted <see cref="WriteMode.Enabled"/>. A fixed set — see
    /// <see cref="Live"/> for the shape the composition root actually registers.
    /// </summary>
    /// <param name="serverKeys">The server keys granted <see cref="WriteMode.Enabled"/>.</param>
    public WritableServers(IEnumerable<string> serverKeys)
        : this((serverKeys ?? throw new ArgumentNullException(nameof(serverKeys)))
            .Select(key => new KeyValuePair<string, WriteMode>(key, WriteMode.Enabled)))
    {
    }

    /// <summary>Creates a fixed set over the given keys, each holding its own <see cref="WriteMode"/>.</summary>
    /// <param name="serverModes">The server keys granted a non-<see cref="WriteMode.ReadOnly"/> write mode.</param>
    public WritableServers(IEnumerable<KeyValuePair<string, WriteMode>> serverModes)
    {
        ArgumentNullException.ThrowIfNull(serverModes);
        _modes = new Dictionary<string, WriteMode>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, mode) in serverModes)
        {
            _modes[key] = mode;
        }
    }

    private WritableServers(WriteGrantCache grants) => _grants = grants;

    /// <summary>
    /// Creates the live view every host registers: every read goes to <paramref name="grants"/>, so this
    /// label reflects the operator's current grants rather than the ones that existed at startup.
    /// </summary>
    /// <param name="grants">The database-backed grant cache the write guard itself resolves through.</param>
    public static WritableServers Live(WriteGrantCache grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        return new WritableServers(grants);
    }

    /// <summary>The server keys that currently carry a non-read-only write grant.</summary>
    public IReadOnlyCollection<string> Keys => _grants is not null ? _grants.GrantedContainerIds : _modes!.Keys;

    /// <summary>Whether any server at all currently carries a non-read-only write grant in this process.</summary>
    public bool Any => Keys.Count > 0;

    /// <summary>
    /// Whether the named server may actually be written to right now — i.e. its <see cref="Mode"/> is
    /// <see cref="WriteMode.Enabled"/>. A <see cref="WriteMode.PreviewOnly"/> server is deliberately NOT
    /// writable: it may plan, but every apply still throws <see cref="WritesDisabledException"/> at the
    /// transport, so a page offering a live write control for it would be lying.
    /// </summary>
    /// <param name="serverId">The server's discovery id (for Docker, its container id) — the identity a grant is keyed on.</param>
    /// <param name="serverName">The server's container name, if known. Never consulted by the live view; see this type's remarks.</param>
    public bool IsWritable(string? serverId, string? serverName = null) =>
        Mode(serverId, serverName) == WriteMode.Enabled;

    /// <summary>
    /// The write posture granted to the named server, or <see cref="WriteMode.ReadOnly"/> when it carries no
    /// grant. Lets a page distinguish "fully writable" from "preview only" instead of collapsing both into a
    /// single boolean, the way <see cref="IsWritable"/> must.
    /// </summary>
    /// <param name="serverId">The server's discovery id (for Docker, its container id).</param>
    /// <param name="serverName">The server's container name, if known. Never consulted by the live view.</param>
    public WriteMode Mode(string? serverId, string? serverName = null)
    {
        if (_grants is not null)
        {
            return WriteModeMapping.ToTransport(_grants.ModeFor(serverId));
        }

        if (!string.IsNullOrWhiteSpace(serverId) && _modes!.TryGetValue(serverId, out var byId))
        {
            return byId;
        }

        if (!string.IsNullOrWhiteSpace(serverName) && _modes!.TryGetValue(serverName, out var byName))
        {
            return byName;
        }

        return WriteMode.ReadOnly;
    }
}
