using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Web.Services;

/// <summary>
/// Turns the operator's per-server write-mode configuration into the <see cref="WriteModeGrant"/>s the write
/// guard consults for a container observed over the <c>ssh+docker</c> transport.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ServerWriteModes"/> emits grants for the <c>docker</c> transport only — correctly so, since
/// its grants are keyed on container-option spellings no <c>ssh+docker</c> descriptor carries (that
/// transport's <see cref="TargetDescriptor.Options"/> only ever has <c>containerName</c>, never
/// <c>containerId</c> or <c>container</c> — see <see cref="SshDockerWiringOptions"/>). This is the
/// <c>ssh+docker</c> half, read from the exact same <c>Servyx:Servers:&lt;key&gt;:WriteMode</c> key so one
/// server cannot be writable over one transport and read-only over the other by accident.
/// </para>
/// <para>
/// <b>Resolved against the OUTER descriptor.</b> <see cref="WriteGuardedTransport.ConnectAsync"/> resolves a
/// target's <see cref="WriteMode"/> before delegating to the inner transport, and the only
/// <see cref="ITransport"/> <c>AddServyxSshDocker</c> registers is a <see cref="WriteGuardedTransport"/>
/// wrapping <see cref="SshDockerTransport"/> directly (never <see cref="SshTransport"/> unwrapped). The
/// descriptor the guard resolves against therefore still carries
/// <see cref="TargetDescriptor.TransportId"/> == <c>"ssh+docker"</c> — <see cref="SshDockerTransport.ConnectAsync"/>
/// only rewrites it to <c>"ssh"</c> when it forwards to its own inner SSH transport, one layer further in,
/// after the guard has already decided. A grant scoped to transport id <c>"ssh"</c> would therefore never
/// match anything and would silently leave every <c>ssh+docker</c> server read-only — which is exactly why
/// every grant this type emits names <see cref="TransportId"/> == <c>"ssh+docker"</c>.
/// </para>
/// <para>
/// <b>Nothing here is read unless <see cref="ProvisioningGate"/> is open</b>, for the same reason
/// <see cref="ServerWriteModes.ReadGrants"/> gates on it: with <c>Servyx:Provisioning:Enabled</c> off, this
/// returns an empty list no matter what configuration says.
/// </para>
/// </remarks>
public static class SshDockerWriteModes
{
    /// <summary>The transport these grants apply to.</summary>
    public const string TransportId = SshDockerWiringOptions.TransportIdValue;

    /// <summary>
    /// The descriptor option key that names a container on the <c>ssh+docker</c> transport — the only one
    /// <see cref="SshDockerWiringOptions.FromConfiguration"/> ever puts in <see cref="TargetDescriptor.Options"/>.
    /// </summary>
    public const string ContainerNameOptionKey = "containerName";

    /// <summary>
    /// Reads one <see cref="WriteModeGrant"/> per configured <c>ssh+docker</c> host whose container carries a
    /// non-<see cref="WriteMode.ReadOnly"/> <c>Servyx:Servers:&lt;key&gt;:WriteMode</c>, or an empty list when
    /// <paramref name="gate"/> is closed or no host is configured at all.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="gate">The provisioning gate; a closed gate yields no grants at all.</param>
    /// <param name="hosts">The configured <c>ssh+docker</c> hosts to match write-mode entries against.</param>
    /// <param name="logger">
    /// Where an unparseable <see cref="ServerWriteModes.WriteModeKey"/> value and a writable server with no
    /// matching host are reported — both fail closed regardless, but silently is not how either is handled.
    /// </param>
    public static IReadOnlyList<WriteModeGrant> ReadGrants(
        IConfiguration configuration, ProvisioningGate gate, SshDockerWiringOptions hosts, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(logger);

        if (!gate.Enabled || !hosts.Any)
        {
            return [];
        }

        var grants = new List<WriteModeGrant>();

        foreach (var server in configuration.GetSection(ServerWriteModes.SectionKey).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(server.Key))
            {
                continue;
            }

            var rawMode = server[ServerWriteModes.WriteModeKey];
            if (!Enum.TryParse<WriteMode>(rawMode, ignoreCase: true, out var mode))
            {
                if (!string.IsNullOrWhiteSpace(rawMode))
                {
                    logger.LogWarning(
                        "'{SectionKey}:{Server}:{WriteModeKey}' is not a recognized WriteMode (was '{Value}'); " +
                        "'{Server}' stays ReadOnly on the ssh+docker transport.",
                        ServerWriteModes.SectionKey, server.Key, ServerWriteModes.WriteModeKey, rawMode, server.Key);
                }

                continue;
            }

            if (mode == WriteMode.ReadOnly)
            {
                continue;
            }

            var host = FindHost(hosts, server.Key);
            if (host is null)
            {
                // A writable server that names no configured ssh+docker host at all is very likely a typo —
                // the operator believes they granted writes to a remote container and, on this transport,
                // did not. (It may legitimately be a plain local `docker` container instead; ServerWriteModes
                // covers that case on its own, and this warning does not contradict it.)
                logger.LogWarning(
                    "'{SectionKey}:{Server}:{WriteModeKey}' grants {Mode}, but no configured " +
                    "'{HostsSection}' entry has a matching container — this grant can never apply to the " +
                    "ssh+docker transport. Check for a typo in the container name.",
                    ServerWriteModes.SectionKey, server.Key, ServerWriteModes.WriteModeKey, mode,
                    SshDockerWiringOptions.SectionKey);
                continue;
            }

            grants.Add(new WriteModeGrant(
                mode,
                TransportId,
                endpoint: host.Target.Endpoint,
                requiredOptions: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ContainerNameOptionKey] = host.ContainerName,
                }));
        }

        return grants;
    }

    private static SshDockerHost? FindHost(SshDockerWiringOptions hosts, string containerKey)
    {
        foreach (var host in hosts.Hosts)
        {
            if (string.Equals(host.ContainerName, containerKey, StringComparison.Ordinal))
            {
                return host;
            }
        }

        return null;
    }
}
