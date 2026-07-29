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
    public static readonly WritableServers None = new([]);

    private readonly HashSet<string> _keys;

    /// <summary>Creates a set over the given configuration keys (container names).</summary>
    /// <param name="serverKeys">The <c>Servyx:Servers:&lt;key&gt;</c> keys granted a non-read-only write mode.</param>
    public WritableServers(IEnumerable<string> serverKeys)
    {
        ArgumentNullException.ThrowIfNull(serverKeys);
        _keys = new HashSet<string>(serverKeys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The configuration keys that carry a write grant.</summary>
    public IReadOnlyCollection<string> Keys => _keys;

    /// <summary>Whether any server at all is writable in this process.</summary>
    public bool Any => _keys.Count > 0;

    /// <summary>
    /// Whether the named server may be written to. Both the discovery id and the container name are
    /// checked, because <c>IServerQueryService</c> itself resolves a server by either and an operator
    /// writes whichever one they see in the UI into configuration.
    /// </summary>
    /// <param name="serverId">The server's discovery id.</param>
    /// <param name="serverName">The server's container name, if known.</param>
    public bool IsWritable(string? serverId, string? serverName = null) =>
        (!string.IsNullOrWhiteSpace(serverId) && _keys.Contains(serverId))
        || (!string.IsNullOrWhiteSpace(serverName) && _keys.Contains(serverName));

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

        var keys = new List<string>();
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
                keys.Add(server.Key);
            }
        }

        return new WritableServers(keys);
    }
}
