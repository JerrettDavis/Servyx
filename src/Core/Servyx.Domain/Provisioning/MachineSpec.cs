namespace Servyx.Domain.Provisioning;

/// <summary>
/// A single inbound firewall/security-group rule to apply to a provisioned machine.
/// </summary>
/// <param name="Port">The port to allow inbound traffic on.</param>
/// <param name="Protocol">The transport protocol, e.g. <c>"tcp"</c> or <c>"udp"</c>.</param>
/// <param name="SourceCidr">The CIDR block allowed to connect, or <see langword="null"/> to allow any source.</param>
public sealed record FirewallRule(int Port, string Protocol, string? SourceCidr);

/// <summary>
/// The desired shape of a machine to provision, independent of which provider ultimately creates it.
/// </summary>
/// <param name="ImageRef">Provider-specific reference to the OS/base image to boot from.</param>
/// <param name="SizeRef">Provider-specific reference to the machine size/plan (CPU, memory, disk).</param>
/// <param name="Region">The provider region/location to create the machine in.</param>
/// <param name="SshPublicKey">
/// An SSH public key to install for access to the machine. This is a public key only — never a private key
/// or any other secret. The matching private key material lives in the secret store and is addressed by
/// URN elsewhere (see <see cref="Transport.TargetDescriptor.CredentialUrn"/>), never carried here.
/// </param>
/// <param name="CloudInit">Optional cloud-init/user-data script to run on first boot.</param>
/// <param name="Ingress">Firewall rules to apply to the machine.</param>
/// <param name="Tags">Tags/labels to attach to the machine at the provider, used later for <see cref="IProvisioner.ReconcileAsync"/>.</param>
public sealed record MachineSpec(
    string ImageRef,
    string SizeRef,
    string Region,
    string SshPublicKey,
    string? CloudInit,
    IReadOnlyList<FirewallRule> Ingress,
    IReadOnlyDictionary<string, string> Tags);
