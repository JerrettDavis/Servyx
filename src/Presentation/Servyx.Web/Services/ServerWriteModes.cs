using Servyx.Domain.Transport;

namespace Servyx.Web.Services;

/// <summary>
/// Turns the operator's per-server write-mode configuration into the <see cref="WriteModeGrant"/>s the
/// write guard consults.
/// </summary>
/// <remarks>
/// <para>
/// The configuration shape is one entry per server, never one switch for the process:
/// </para>
/// <code>
/// Servyx:Servers:palworld-server:WriteMode = Enabled
/// </code>
/// <para>
/// A server with no entry, an empty entry, or an unparseable one is <see cref="WriteMode.ReadOnly"/>. That
/// is the same fail-closed rule <see cref="ProvisioningGate"/> follows and for the same reason: a
/// misconfiguration must never widen what Servyx may change.
/// </para>
/// <para>
/// <b>Nothing here is read unless <see cref="ProvisioningGate"/> is open.</b> With
/// <c>Servyx:Provisioning:Enabled</c> absent or false — the default — this returns an empty list no matter
/// what the configuration says, so a read-only host cannot be talked into a write grant by an edit to a
/// different key. Turning writes on takes two deliberate decisions, not one.
/// </para>
/// <para>
/// Each configured server yields one grant per container-option spelling a
/// <see cref="TargetDescriptor"/> may use (<c>containerId</c>, <c>containerName</c>, <c>container</c> —
/// the same three keys <c>DockerTransport.ResolveContainerRef</c> reads, in that order). They all name the
/// same single container; emitting all three is what stops a grant from silently failing to apply because
/// the caller spelled the descriptor differently than the operator spelled the configuration key.
/// </para>
/// </remarks>
public static class ServerWriteModes
{
    /// <summary>The configuration section holding per-server settings.</summary>
    public const string SectionKey = "Servyx:Servers";

    /// <summary>The key, within a server's section, holding its write mode.</summary>
    public const string WriteModeKey = "WriteMode";

    /// <summary>The transport these grants apply to.</summary>
    public const string DockerTransportId = "docker";

    /// <summary>The descriptor option keys that can name a container, mirroring the Docker transport's own order.</summary>
    private static readonly string[] ContainerOptionKeys = ["containerId", "containerName", "container"];

    /// <summary>
    /// Reads every per-server write grant the configuration declares, or an empty list when
    /// <paramref name="gate"/> is closed.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="gate">The provisioning gate; a closed gate yields no grants at all.</param>
    /// <param name="logger">
    /// Where a misspelled <see cref="WriteModeKey"/> value is reported. Naming the key and the offending
    /// value turns a silent fail-closed into a diagnosable one — the server is still read-only either way,
    /// but the operator finds out why without guessing.
    /// </param>
    public static IReadOnlyList<WriteModeGrant> ReadGrants(
        IConfiguration configuration, ProvisioningGate gate, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(logger);

        if (!gate.Enabled)
        {
            return [];
        }

        var grants = new List<WriteModeGrant>();

        foreach (var server in configuration.GetSection(SectionKey).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(server.Key))
            {
                continue;
            }

            var rawMode = server[WriteModeKey];
            if (!Enum.TryParse<WriteMode>(rawMode, ignoreCase: true, out var mode))
            {
                // Absent or empty is the ordinary, silent shape of "not writable"; anything else present is
                // a typo the operator deserves to be told about, even though the outcome — read-only — is
                // identical either way.
                if (!string.IsNullOrWhiteSpace(rawMode))
                {
                    logger.LogWarning(
                        "'{SectionKey}:{Server}:{WriteModeKey}' is not a recognized WriteMode (was '{Value}'); " +
                        "'{Server}' stays ReadOnly.",
                        SectionKey, server.Key, WriteModeKey, rawMode, server.Key);
                }

                continue;
            }

            if (mode == WriteMode.ReadOnly)
            {
                // Explicitly read-only — a legitimate, intentional value, not a misconfiguration to warn about.
                continue;
            }

            foreach (var optionKey in ContainerOptionKeys)
            {
                grants.Add(new WriteModeGrant(
                    mode,
                    DockerTransportId,
                    endpoint: null,
                    requiredOptions: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [optionKey] = server.Key,
                    }));
            }
        }

        return grants;
    }
}
