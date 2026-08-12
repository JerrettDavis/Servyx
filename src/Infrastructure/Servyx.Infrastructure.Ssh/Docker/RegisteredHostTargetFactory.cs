using Servyx.Domain.Entities;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// Builds the <see cref="TargetDescriptor"/> a database-registered <see cref="Host"/> row is reached through,
/// using the same transport id and option keys <see cref="SshDockerWiringOptions.FromConfiguration"/> emits
/// for a configuration-declared host (see that type's remarks), so downstream code — the ssh+docker transport,
/// <see cref="SshDockerServerDiscovery"/> — treats a database-registered host identically to a configured one.
/// </summary>
/// <remarks>
/// <strong>No <c>containerName</c> option.</strong> A configured host's <c>containerName</c> option exists only
/// for descriptive/refusal-message purposes (<see cref="SshDockerTransport"/>'s private <c>DescribeContainer</c>
/// helper) — <see cref="SshDockerServerDiscovery"/> never reads it; it lists every container the connected host
/// runs and filters by image/mount, not by name. <see cref="Host"/> carries no container identity at all: a
/// registered host names a MACHINE, and discovery is exactly what finds the containers running on it. This
/// factory therefore omits the option entirely rather than fabricate a value the row does not carry.
/// </remarks>
public static class RegisteredHostTargetFactory
{
    /// <summary>Builds the <see cref="TargetDescriptor"/> for <paramref name="host"/>.</summary>
    public static TargetDescriptor Build(Host host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["declaredChannels"] = SshDockerWiringOptions.DeclaredChannels,
        };

        if (!string.IsNullOrWhiteSpace(host.TrustPolicy))
        {
            options["trustPolicy"] = host.TrustPolicy.Trim();
        }

        if (!string.IsNullOrWhiteSpace(host.PinnedFingerprints))
        {
            options["pinnedFingerprints"] = host.PinnedFingerprints.Trim();
        }

        return new TargetDescriptor(
            SshDockerWiringOptions.TransportIdValue,
            host.Endpoint,
            string.IsNullOrWhiteSpace(host.CredentialUrn) ? null : host.CredentialUrn.Trim(),
            null,
            options);
    }
}
