using Servyx.Domain.Transport;

namespace Servyx.Web.Services;

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
/// This type exists so that the UI's belief is derived from the same configuration the guard reads —
/// <see cref="ServerWriteModes.SectionKey"/> — rather than from an independent assumption that could drift
/// away from it.
/// </para>
/// <para>
/// It is empty whenever <see cref="ProvisioningGate"/> is closed, for exactly the reason
/// <see cref="ServerWriteModes.ReadGrants"/> returns nothing then: with the flag off there are no grants
/// in the container at all, so every server genuinely is read-only and saying otherwise would be a lie the
/// operator only discovers by clicking.
/// </para>
/// </remarks>
public sealed class WritableServers
{
    /// <summary>No server is writable. The state of a read-only host, and the safe default.</summary>
    public static readonly WritableServers None = new(Array.Empty<string>());

    private readonly Dictionary<string, WriteMode> _modes;

    /// <summary>
    /// Creates a set over the given configuration keys (container names), each granted
    /// <see cref="WriteMode.Enabled"/>.
    /// </summary>
    /// <param name="serverKeys">The <c>Servyx:Servers:&lt;key&gt;</c> keys granted <see cref="WriteMode.Enabled"/>.</param>
    public WritableServers(IEnumerable<string> serverKeys)
        : this((serverKeys ?? throw new ArgumentNullException(nameof(serverKeys)))
            .Select(key => new KeyValuePair<string, WriteMode>(key, WriteMode.Enabled)))
    {
    }

    /// <summary>Creates a set over the given configuration keys, each holding its own <see cref="WriteMode"/>.</summary>
    /// <param name="serverModes">
    /// The <c>Servyx:Servers:&lt;key&gt;</c> keys granted a non-<see cref="WriteMode.ReadOnly"/> write mode.
    /// </param>
    public WritableServers(IEnumerable<KeyValuePair<string, WriteMode>> serverModes)
    {
        ArgumentNullException.ThrowIfNull(serverModes);
        _modes = new Dictionary<string, WriteMode>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, mode) in serverModes)
        {
            _modes[key] = mode;
        }
    }

    /// <summary>The configuration keys that carry a non-read-only write grant.</summary>
    public IReadOnlyCollection<string> Keys => _modes.Keys;

    /// <summary>Whether any server at all carries a non-read-only write grant in this process.</summary>
    public bool Any => _modes.Count > 0;

    /// <summary>
    /// Whether the named server may actually be written to right now — i.e. its <see cref="Mode"/> is
    /// <see cref="WriteMode.Enabled"/>. A <see cref="WriteMode.PreviewOnly"/> server is deliberately NOT
    /// writable: it may plan, but every apply still throws <see cref="WritesDisabledException"/> at the
    /// transport, so a page offering a live write control for it would be lying. Both the discovery id and
    /// the container name are checked, because <c>IServerQueryService</c> itself resolves a server by either
    /// and an operator writes whichever one they see in the UI into configuration.
    /// </summary>
    /// <param name="serverId">The server's discovery id.</param>
    /// <param name="serverName">The server's container name, if known.</param>
    public bool IsWritable(string? serverId, string? serverName = null) =>
        Mode(serverId, serverName) == WriteMode.Enabled;

    /// <summary>
    /// The write posture granted to the named server, or <see cref="WriteMode.ReadOnly"/> when neither the
    /// discovery id nor the container name carries a grant. Lets a page distinguish "fully writable" from
    /// "preview only" instead of collapsing both into a single boolean, the way <see cref="IsWritable"/> must.
    /// </summary>
    /// <param name="serverId">The server's discovery id.</param>
    /// <param name="serverName">The server's container name, if known.</param>
    public WriteMode Mode(string? serverId, string? serverName = null)
    {
        if (!string.IsNullOrWhiteSpace(serverId) && _modes.TryGetValue(serverId, out var byId))
        {
            return byId;
        }

        if (!string.IsNullOrWhiteSpace(serverName) && _modes.TryGetValue(serverName, out var byName))
        {
            return byName;
        }

        return WriteMode.ReadOnly;
    }

    /// <summary>
    /// Reads the writable set from configuration, or returns <see cref="None"/> when
    /// <paramref name="gate"/> is closed.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="gate">The provisioning gate; a closed gate yields no writable servers at all.</param>
    public static WritableServers FromConfiguration(IConfiguration configuration, ProvisioningGate gate)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(gate);

        if (!gate.Enabled)
        {
            return None;
        }

        var modes = new List<KeyValuePair<string, WriteMode>>();
        foreach (var server in configuration.GetSection(ServerWriteModes.SectionKey).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(server.Key))
            {
                continue;
            }

            // Same fail-closed parse as ServerWriteModes.ReadGrants: absent, misspelled, or explicitly
            // read-only all mean read-only, and are all spelled the same way here.
            if (Enum.TryParse<WriteMode>(server[ServerWriteModes.WriteModeKey], ignoreCase: true, out var mode) &&
                mode != WriteMode.ReadOnly)
            {
                modes.Add(new KeyValuePair<string, WriteMode>(server.Key, mode));
            }
        }

        return modes.Count == 0 ? None : new WritableServers(modes);
    }
}
