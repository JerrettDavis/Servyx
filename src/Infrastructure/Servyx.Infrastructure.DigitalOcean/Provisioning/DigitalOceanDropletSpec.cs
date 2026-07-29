using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Provisioning;

/// <summary>
/// Everything needed to create one droplet: the provider-independent <see cref="MachineSpec"/> the domain
/// already defines for shape I, plus the two things DigitalOcean's API needs that <see cref="MachineSpec"/>
/// does not carry.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this wraps <see cref="MachineSpec"/> rather than replacing it.</strong>
/// <see cref="MachineSpec"/> is the domain's statement that shape I's four clouds "differ trivially", and
/// almost all of it maps one-to-one onto <c>POST /v2/droplets</c>: <see cref="MachineSpec.ImageRef"/> is
/// <c>image</c>, <see cref="MachineSpec.SizeRef"/> is <c>size</c>, <see cref="MachineSpec.Region"/> is
/// <c>region</c>, <see cref="MachineSpec.CloudInit"/> is <c>user_data</c>, <see cref="MachineSpec.Tags"/>
/// feeds <c>tags</c>. Keeping it as a member rather than flattening it means that correspondence stays
/// visible.
/// </para>
/// <para>
/// <strong>Where it does not fit, honestly.</strong> <see cref="MachineSpec.SshPublicKey"/> holds raw public
/// key material, and DigitalOcean's droplet-create API cannot consume that: its <c>ssh_keys</c> array takes
/// only the ids or MD5 fingerprints of keys already registered on the account via <c>/v2/account/keys</c>.
/// So the raw key travels here unused-by-the-wire (it is still part of the plan hash, because changing which
/// key an operator intends to install must invalidate a plan), and <see cref="SshKeyFingerprints"/> carries
/// what the API actually accepts. Registering a key on the account is a separate account-level mutation this
/// adapter deliberately does not perform.
/// </para>
/// <para>
/// <strong><see cref="MachineSpec.CloudInit"/> is forwarded, never authored.</strong> Nothing in this
/// assembly generates user-data. If a caller supplies none, none is sent — no bootstrap script, no package
/// install, no game payload. That is what makes "shape I contains no install logic" checkable rather than
/// merely claimed, and it is pinned by a test.
/// </para>
/// </remarks>
public sealed record DigitalOceanDropletSpec
{
    /// <summary>Creates a spec.</summary>
    /// <param name="dropletName">The droplet's name at the provider.</param>
    /// <param name="machine">The provider-independent machine shape.</param>
    /// <param name="tags">The mandatory Servyx identity, which cannot be constructed incompletely.</param>
    /// <exception cref="ArgumentException"><paramref name="dropletName"/> is blank.</exception>
    public DigitalOceanDropletSpec(string dropletName, MachineSpec machine, ServyxDropletTags tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dropletName);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(tags);

        DropletName = dropletName;
        Machine = machine;
        Tags = tags;
    }

    /// <summary>The droplet's name at the provider.</summary>
    public string DropletName { get; }

    /// <summary>The provider-independent machine shape this droplet realises.</summary>
    public MachineSpec Machine { get; }

    /// <summary>The mandatory Servyx identity stamped onto the droplet.</summary>
    public ServyxDropletTags Tags { get; }

    /// <summary>
    /// Ids or MD5 fingerprints of SSH keys <em>already registered on the DigitalOcean account</em>, which is
    /// the only form <c>POST /v2/droplets</c> accepts — see the type remarks.
    /// </summary>
    public IReadOnlyList<string> SshKeyFingerprints { get; init; } = [];

    /// <summary>Extra Servyx tags to stamp alongside the canonical ones. Can never shadow a canonical key.</summary>
    public IReadOnlyDictionary<string, string> AdditionalTags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
