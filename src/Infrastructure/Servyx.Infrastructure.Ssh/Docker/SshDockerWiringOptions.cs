using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// One remote host Servyx views a Docker container on over SSH, as the operator declared it: where to
/// connect, which credential opens it, how the host key is trusted, and which container to observe.
/// </summary>
/// <param name="Name">The <c>Servyx:Hosts:&lt;name&gt;</c> configuration key — this host's id.</param>
/// <param name="Target">
/// The fully-built <see cref="TargetDescriptor"/> for this host: <see cref="TargetDescriptor.TransportId"/>
/// is always <c>"ssh+docker"</c>, and <see cref="TargetDescriptor.Options"/> carries <c>containerName</c>,
/// <c>declaredChannels</c>, and (when configured) <c>trustPolicy</c>/<c>pinnedFingerprints</c> — the exact
/// option keys <see cref="SshTransport"/>'s connector-descriptor builder reads.
/// </param>
/// <param name="ContainerName">The container this host observes, carried alongside for convenience.</param>
public sealed record SshDockerHost(string Name, TargetDescriptor Target, string ContainerName);

/// <summary>
/// The ssh+docker-hosted remote(s) the operator has configured, read from <c>Servyx:Hosts:&lt;name&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Empty by default, and empty is the whole point.</strong> There is no host Servyx could guess for
/// a stranger's machine, so an operator who configures nothing gets <see cref="None"/> — no SSH transport is
/// constructed, no secret is resolved, no socket is opened, and the process observes only whatever local
/// Docker daemon <c>AddServyxDocker</c> already wired up. That is the same shape <see cref="SshBackupWiringOptions"/>
/// and <see cref="RconWiringOptions"/> take and for the same reason.
/// </para>
/// <para>
/// <strong>Not gated behind <c>Servyx:Provisioning:Enabled</c>.</strong> Unlike backups and RCON, viewing a
/// remote container is a read surface, not a mutating one — the transport this options type feeds is
/// registered write-guarded with zero <see cref="WriteModeGrant"/>s, exactly like the local Docker
/// registration it can replace. A host is reachable the moment it is declared; nothing here can write to it.
/// </para>
/// <para>
/// <strong>Single-host this milestone.</strong> <see cref="Hosts"/> can carry more than one configured entry,
/// but <c>AddServyxSshDocker</c> wires only the first — the composition root has exactly one
/// <see cref="Servyx.Domain.Discovery.IServerDiscovery"/>/<see cref="Servyx.Domain.Observability.ILogStream"/>/
/// <see cref="Servyx.Domain.Observability.IMetricsSource"/> slot to put a session behind, the same
/// single-target shape <c>LiveDashboardDataService</c> already assumes for its probe target. A second
/// configured host is accepted by <see cref="FromConfiguration"/> (so it is visible to a caller that wants to
/// enumerate what was declared) but is not wired to anything by itself.
/// </para>
/// </remarks>
public sealed class SshDockerWiringOptions
{
    /// <summary>The configuration section per-host settings are read from.</summary>
    public const string SectionKey = "Servyx:Hosts";

    /// <summary>The <see cref="TargetDescriptor.TransportId"/> a host must declare (or omit) to be wired here.</summary>
    public const string TransportIdValue = "ssh+docker";

    /// <summary>
    /// The declared connector channels a host observed this way needs: a command channel to run
    /// <c>docker</c> CLI commands, file reads and writes, and directory listing. Deliberately excludes
    /// <c>Stdin</c> — nothing on this transport streams interactive input; the game console is reached
    /// through the cataloged RCON control channel, not a shell — see
    /// <see cref="SshTransport.BuildConnectorDescriptor"/>'s <c>"declaredChannels"</c> option for how this
    /// string is parsed.
    /// </summary>
    /// <remarks>
    /// <strong><c>FileWrite</c> is declared because restoring a backup writes files over this same
    /// connector.</strong> <see cref="Servyx.Infrastructure.Ssh.Backups.SshBackupProvider"/> and the Docker
    /// backup path both write through whichever <see cref="Servyx.Domain.Transport.IExecutionTarget"/> the
    /// composition root wired up for a given server, and for an ssh+docker-observed host that is this
    /// connector. Without <c>FileWrite</c> in the declared set, the connector itself refuses the write
    /// (<c>ConnectorChannel</c> gate) before <see cref="Servyx.Domain.Transport.WriteGuardedExecutionTarget"/>
    /// — the write guard that actually decides whether a server is allowed to be written to — is ever
    /// consulted, which surfaces as a confusing "channel not declared" failure at the wrong layer instead of
    /// the intended, per-server <see cref="Servyx.Domain.Transport.WritesDisabledException"/>. Declaring the
    /// channel does not itself grant writes: a host with no <see cref="Servyx.Domain.Transport.WriteModeGrant"/>
    /// is still refused by the guard exactly as before — see <c>Zero_write_mode_grants_are_registered_and_the_remote_host_stays_read_only</c>.
    /// </remarks>
    public const string DeclaredChannels = "Exec,FileRead,FileWrite,DirectoryList";

    /// <summary>No host is ssh+docker-hosted. The state of a Docker-only process, and the safe default.</summary>
    public static readonly SshDockerWiringOptions None = new([]);

    private readonly IReadOnlyList<SshDockerHost> _hosts;

    /// <summary>Creates options over an explicit set of hosts.</summary>
    /// <param name="hosts">The configured hosts.</param>
    public SshDockerWiringOptions(IEnumerable<SshDockerHost> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        _hosts = [.. hosts];
    }

    /// <summary>The configured hosts.</summary>
    public IReadOnlyList<SshDockerHost> Hosts => _hosts;

    /// <summary>Whether any host is configured.</summary>
    public bool Any => _hosts.Count > 0;

    /// <summary>
    /// Reads the configured ssh+docker hosts from <c>Servyx:Hosts:&lt;name&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A host entry is skipped <em>silently</em> — not reported, not counted as a rejection — only when the
    /// skip is itself a legitimate, intentional piece of configuration: an explicit <c>Enabled: false</c>
    /// (the shipped <c>appsettings.json</c> default), or a <c>Transport</c> naming something other than
    /// <see cref="TransportIdValue"/> (so this section can be shared with a future non-Docker host kind
    /// without this reading its entries as its own).
    /// </para>
    /// <para>
    /// Everything else that keeps a host from being usable IS reported, never silently dropped: a missing or
    /// unparsable <c>Enabled</c>, or — for a host that is enabled and targets this transport — a missing
    /// <c>Endpoint</c> and/or <c>Container</c>, exactly the two values <see cref="SshBackupWiringOptions.FromConfiguration"/>
    /// treats the same way for <c>Host</c> and <c>Root</c>. Each such host is logged at
    /// <see cref="LogLevel.Warning"/>, naming the host key and the specific offending field(s), <em>provided
    /// at least one other configured host is usable</em>. When <c>Servyx:Hosts</c> is present and produces
    /// zero usable hosts, this throws instead of logging: a populated section that yields nothing observable
    /// is almost certainly a typo, not an intentional local-only deployment, and staying silent there is
    /// exactly the operator-deception this method exists to close. An absent (or entirely empty)
    /// <c>Servyx:Hosts</c> section remains a silent, valid no-op — see <see cref="None"/>.
    /// </para>
    /// <para>
    /// Also logs a <see cref="LogLevel.Warning"/> when more than one host ends up usable: <c>AddServyxSshDocker</c>
    /// only ever wires <see cref="Hosts"/>[0] (see this type's remarks), so a second configured host would
    /// otherwise be silently accepted here and then silently ignored there.
    /// </para>
    /// </remarks>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="logger">Where rejected hosts and a multi-host configuration are reported.</param>
    /// <exception cref="InvalidOperationException">
    /// <c>Servyx:Hosts</c> has at least one child entry, but none of them produced a usable host.
    /// </exception>
    public static SshDockerWiringOptions FromConfiguration(IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var hosts = new List<SshDockerHost>();
        var rejected = new List<(string Key, string Reason)>();
        var childCount = 0;

        foreach (var host in configuration.GetSection(SectionKey).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(host.Key))
            {
                continue;
            }

            childCount++;

            var enabledRaw = host["Enabled"];
            if (!bool.TryParse(enabledRaw, out var enabled))
            {
                rejected.Add((host.Key, $"'Enabled' is missing or not a valid boolean (was '{enabledRaw}')"));
                continue;
            }

            if (!enabled)
            {
                // Explicitly turned off by the operator — legitimate, not a misconfiguration to report.
                continue;
            }

            var transport = host["Transport"];
            if (!string.IsNullOrWhiteSpace(transport)
                && !string.Equals(transport, TransportIdValue, StringComparison.OrdinalIgnoreCase))
            {
                // Declared for a different (future) host kind sharing this section — not ours to validate.
                continue;
            }

            var endpoint = host["Endpoint"];
            var container = host["Container"];

            var missingFields = new List<string>(2);
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                missingFields.Add("Endpoint");
            }
            if (string.IsNullOrWhiteSpace(container))
            {
                missingFields.Add("Container");
            }
            if (missingFields.Count > 0)
            {
                var joined = string.Join("' and '", missingFields);
                var verb = missingFields.Count > 1 ? "are" : "is";
                rejected.Add((host.Key, $"'{joined}' {verb} missing"));
                continue;
            }

            var options = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["containerName"] = container!.Trim(),
                ["declaredChannels"] = DeclaredChannels,
            };

            var trustPolicy = host["TrustPolicy"];
            if (!string.IsNullOrWhiteSpace(trustPolicy))
            {
                options["trustPolicy"] = trustPolicy.Trim();
            }

            var pinnedFingerprints = host["PinnedFingerprints"];
            if (!string.IsNullOrWhiteSpace(pinnedFingerprints))
            {
                options["pinnedFingerprints"] = pinnedFingerprints.Trim();
            }

            var credentialUrn = host["CredentialUrn"];

            var target = new TargetDescriptor(
                TransportIdValue,
                endpoint!.Trim(),
                string.IsNullOrWhiteSpace(credentialUrn) ? null : credentialUrn.Trim(),
                null,
                options);

            hosts.Add(new SshDockerHost(host.Key, target, container.Trim()));
        }

        if (childCount > 0 && rejected.Count > 0)
        {
            if (hosts.Count == 0)
            {
                var detail = string.Join("; ", rejected.Select(r => $"'{r.Key}' — {r.Reason}"));
                throw new InvalidOperationException(
                    $"'{SectionKey}' declares {childCount} host(s), but none are usable: {detail}. Fix the "
                    + "offending field(s), or remove the section entirely to run local-only.");
            }

            foreach (var (key, reason) in rejected)
            {
                logger.LogWarning(
                    "'{Section}:{HostKey}' was rejected and will not be observed: {Reason}.",
                    SectionKey, key, reason);
            }

            logger.LogWarning(
                "'{Section}' found {Found} host(s) configured, {Accepted} accepted, {Rejected} rejected.",
                SectionKey, childCount, hosts.Count, rejected.Count);
        }

        if (hosts.Count > 1)
        {
            logger.LogWarning(
                "'{Section}' configures {Count} hosts, but only the first ('{FirstHost}') is wired for "
                + "observation — the rest are accepted but not connected to anything.",
                SectionKey, hosts.Count, hosts[0].Name);
        }

        return hosts.Count == 0 ? None : new SshDockerWiringOptions(hosts);
    }
}
