using System.Globalization;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;

namespace Servyx.Composition;

/// <summary>
/// One server's RCON control channel, as the host understands it: where the port is and where the
/// credential lives.
/// </summary>
/// <param name="ServerKey">The <c>Servyx:Servers:&lt;key&gt;</c> configuration key — the container name.</param>
/// <param name="Endpoint">The address <c>direct-tcp</c> would connect to.</param>
/// <param name="PasswordUrn">
/// Where the admin/RCON password lives. A locator only; the value is resolved through
/// <see cref="ISecretStore"/> at the moment a packet is built and is never held anywhere in between.
/// </param>
public sealed record RconChannel(string ServerKey, RconEndpoint Endpoint, SecretUrn PasswordUrn);

/// <summary>
/// The RCON control channels the operator has configured, read from
/// <c>Servyx:Servers:&lt;container&gt;:Rcon:*</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Opt-in per server, on top of the provisioning gate.</strong> With
/// <c>Servyx:Provisioning:Enabled</c> off this returns <see cref="Disabled"/> and nothing in the process
/// holds an RCON endpoint at all. With it on, a server still has to name itself under
/// <c>Servyx:Servers:&lt;container&gt;:Rcon:Enabled</c>. That double opt-in is what keeps flag-off behaviour
/// byte-for-byte identical: no configuration file read, no secret resolved, no socket.
/// </para>
/// <para>
/// <strong>Enabling RCON changes what a failed backup does, and that is the point.</strong> When a server
/// has a control channel, <see cref="ServyxBackupContextSource"/> attaches the definition's quiesce step to
/// its backup context, and <c>DockerBackupProvider</c> then refuses to write an archive if the flush does
/// not succeed. A server without a channel keeps the previous behaviour — an archive of on-disk state,
/// recorded in the manifest as having had no quiesce. Both are honest; only one of them is a backup you can
/// trust a running server to.
/// </para>
/// </remarks>
public sealed class RconWiringOptions
{
    /// <summary>The configuration section per-server RCON settings are read from.</summary>
    public const string SectionKey = ServerWriteModes.SectionKey;

    /// <summary>The per-server child key holding the RCON block.</summary>
    public const string RconKey = "Rcon";

    /// <summary>
    /// The definition's <c>backup.quiesce</c> command id — <c>{ kind: control, channel: rcon, command:
    /// save, timeout: 30s }</c> in <c>definitions/palworld-docker.yaml</c>.
    /// </summary>
    public const string QuiesceCommandId = "save";

    /// <summary>The definition's declared quiesce timeout: <c>30s</c>.</summary>
    public static readonly TimeSpan QuiesceTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The RCON port the definition defaults <c>RCON_PORT</c> to.</summary>
    public const int DefaultPort = 25575;

    /// <summary>
    /// The host <c>direct-tcp</c> connects to when configuration names none. Loopback, because the only way
    /// a container's RCON port is reachable by TCP at all is if it has been published to the host.
    /// </summary>
    public const string DefaultHost = "127.0.0.1";

    /// <summary>The <see cref="SecretUrn"/> scope RCON credentials live under.</summary>
    public const string SecretScope = "server";

    /// <summary>The <see cref="SecretUrn"/> category RCON credentials live under.</summary>
    public const string SecretCategory = "rcon";

    /// <summary>The <see cref="SecretUrn"/> name an RCON credential is stored as.</summary>
    public const string SecretName = "password";

    /// <summary>No server has an RCON control channel. The state of a read-only host, and the safe default.</summary>
    public static readonly RconWiringOptions Disabled = new([]);

    private readonly IReadOnlyList<RconChannel> _channels;

    /// <summary>Creates options over an explicit set of channels.</summary>
    /// <param name="channels">The configured channels.</param>
    public RconWiringOptions(IEnumerable<RconChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _channels = [.. channels];
    }

    /// <summary>The configured channels.</summary>
    public IReadOnlyList<RconChannel> Channels => _channels;

    /// <summary>Whether any server in this process has an RCON control channel.</summary>
    public bool Any => _channels.Count > 0;

    /// <summary>Finds the channel for a server, by discovery id or container name.</summary>
    /// <param name="serverId">The server's discovery id.</param>
    /// <param name="serverName">The server's container name, if known.</param>
    public RconChannel? Find(string? serverId, string? serverName = null)
    {
        foreach (var channel in _channels)
        {
            if ((!string.IsNullOrWhiteSpace(serverId) && string.Equals(channel.ServerKey, serverId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(serverName) && string.Equals(channel.ServerKey, serverName, StringComparison.OrdinalIgnoreCase)))
            {
                return channel;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the configured channels, or returns <see cref="Disabled"/> when <paramref name="gate"/> is
    /// closed.
    /// </summary>
    /// <remarks>
    /// A server whose configuration key is not a legal <see cref="SecretUrn"/> segment is skipped rather
    /// than coerced: the credential's location is derived from that key, and a key that cannot address a
    /// secret cannot have a control channel.
    /// </remarks>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="gate">The provisioning gate; a closed gate yields no channels at all.</param>
    public static RconWiringOptions FromConfiguration(IConfiguration configuration, ProvisioningGate gate)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(gate);

        if (!gate.Enabled)
        {
            return Disabled;
        }

        var channels = new List<RconChannel>();

        foreach (var server in configuration.GetSection(SectionKey).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(server.Key) || !SecretUrn.IsValidSegment(server.Key))
            {
                continue;
            }

            var rcon = server.GetSection(RconKey);

            // Fail-closed, exactly like SshDockerWriteModes.ReadGrants: absent, misspelled and explicitly
            // false all mean "no control channel", and are all spelled the same way here.
            if (!bool.TryParse(rcon["Enabled"], out var enabled) || !enabled)
            {
                continue;
            }

            var host = string.IsNullOrWhiteSpace(rcon["Host"]) ? DefaultHost : rcon["Host"]!.Trim();
            var port = int.TryParse(rcon["Port"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed is > 0 and <= 65535
                    ? parsed
                    : DefaultPort;

            channels.Add(new RconChannel(
                server.Key,
                new RconEndpoint(host, port),
                SecretUrn.Create(SecretScope, server.Key, SecretCategory, SecretName)));
        }

        return new RconWiringOptions(channels);
    }
}
