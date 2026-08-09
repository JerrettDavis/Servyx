using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Aws;
using Servyx.Infrastructure.Aws.Provisioning;
using Servyx.Infrastructure.Azure;
using Servyx.Infrastructure.Azure.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Provisioning;
using Servyx.Infrastructure.Process.Provisioning;
using Servyx.Infrastructure.Ssh.Provisioning;

namespace Servyx.Composition;

/// <summary>
/// The SSH host <c>SshProcessProvisioner</c> installs onto, as the operator named it.
/// </summary>
/// <param name="Endpoint">
/// The address the provisioner connects to, in <c>SshEndpoint</c>'s <c>[user@]host[:port]</c> form. Carried
/// verbatim, because it is also what this provisioner's <c>WriteModeGrant</c> is scoped to and the two must
/// match exactly for a marker write to be permitted.
/// </param>
/// <param name="CredentialUrn">
/// Where the SSH password or private key lives. A locator only; the value is resolved through
/// <see cref="ISecretStore"/> when a connection is opened and is never held in configuration.
/// </param>
/// <param name="MarkerRoot">
/// Where marker files are written and swept from, or <see langword="null"/> for
/// <see cref="SshProcessProvisioner.DefaultMarkerRoot"/>.
/// </param>
public sealed record SshProvisionerOptions(string Endpoint, SecretUrn CredentialUrn, string? MarkerRoot);

/// <summary>
/// The machine <c>LocalProcessProvisioner</c> installs onto — always the one Servyx itself is running on.
/// </summary>
/// <param name="MachineId">
/// A stable name for this machine, stamped into every descriptor the provisioner produces, or
/// <see langword="null"/> for <see cref="Environment.MachineName"/>.
/// </param>
/// <param name="MarkerRoot">
/// Where marker files are written and swept from, or <see langword="null"/> for
/// <see cref="LocalProcessProvisioner.DefaultMarkerRoot"/>.
/// </param>
public sealed record ProcessProvisionerOptions(string? MachineId, string? MarkerRoot);

/// <summary>
/// The DigitalOcean account <c>DigitalOceanDropletProvisioner</c> creates droplets in.
/// </summary>
/// <param name="ApiTokenUrn">Where the personal access token lives. A locator only, resolved per request.</param>
/// <param name="SshCredentialUrn">
/// The URN stamped onto produced descriptors as their <c>CredentialUrn</c> — the SSH private key matching the
/// account key a droplet boots with. Never the DigitalOcean token.
/// </param>
/// <param name="SshUsername">The username produced endpoints authenticate as.</param>
public sealed record DigitalOceanProvisionerOptions(
    SecretUrn ApiTokenUrn,
    string? SshCredentialUrn,
    string SshUsername);

/// <summary>
/// The Azure subscription <c>AzureVirtualMachineProvisioner</c> creates virtual machines in, and the service
/// principal it authenticates as.
/// </summary>
/// <param name="ServicePrincipal">The tenant, client id, and client-secret URN. Only the URN is held.</param>
/// <param name="SubscriptionId">The subscription every resource is created in.</param>
/// <param name="SshCredentialUrn">
/// The URN stamped onto produced descriptors — the SSH private key matching the public key the VM boots with.
/// Never the Azure client secret.
/// </param>
/// <param name="SshUsername">The VM's <c>adminUsername</c>, and the user produced endpoints authenticate as.</param>
public sealed record AzureProvisionerOptions(
    AzureServicePrincipal ServicePrincipal,
    string SubscriptionId,
    string? SshCredentialUrn,
    string SshUsername);

/// <summary>
/// The AWS region and signing identity <c>AwsEc2Provisioner</c> acts with.
/// </summary>
/// <param name="Identity">The URNs of the access key id, secret access key, and optional session token.</param>
/// <param name="Region">The region this provisioner acts on, e.g. <c>us-east-1</c>.</param>
/// <param name="SshCredentialUrn">
/// The URN stamped onto produced descriptors — the SSH private key matching the EC2 key pair. Never the AWS
/// secret access key.
/// </param>
/// <param name="SshUsername">The username produced endpoints authenticate as.</param>
public sealed record AwsEc2ProvisionerOptions(
    AwsSigningIdentity Identity,
    string Region,
    string? SshCredentialUrn,
    string SshUsername);

/// <summary>
/// The AWS region and signing identity <c>AwsLightsailProvisioner</c> acts with.
/// </summary>
/// <remarks>
/// Carries no SSH username, and that is the adapter's shape rather than an omission here:
/// <see cref="AwsLightsailProvisioner"/> has no <c>sshUsername</c> parameter, because the username a Lightsail
/// blueprint gives SSH access to is fixed by the blueprint.
/// </remarks>
/// <param name="Identity">The URNs of the access key id, secret access key, and optional session token.</param>
/// <param name="Region">The region this provisioner acts on, e.g. <c>us-east-1</c>.</param>
/// <param name="SshCredentialUrn">
/// The URN stamped onto produced descriptors — the SSH private key matching the Lightsail key pair. Never the
/// AWS secret access key.
/// </param>
public sealed record AwsLightsailProvisionerOptions(
    AwsSigningIdentity Identity,
    string Region,
    string? SshCredentialUrn);

/// <summary>
/// The provisioners the operator has switched on, read from <c>Servyx:Provisioners:&lt;name&gt;:*</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Empty by default, and empty is the whole point.</strong> Every provisioner here is individually
/// opt-in on top of <c>Servyx:Provisioning:Enabled</c>. With the gate closed this returns <see cref="None"/>
/// outright; with it open, a provisioner still has to name itself under
/// <c>Servyx:Provisioners:&lt;name&gt;:Enabled</c>. An operator who configures nothing therefore gets exactly
/// the composition Servyx had before this type existed: the Docker provisioner and nothing else. That is the
/// same double opt-in <see cref="RconWiringOptions"/> and <see cref="SshBackupWiringOptions"/> use, for the
/// same reason — a provisioner is a mutating, money-spending capability, and no key other than its own may
/// bring one into existence.
/// </para>
/// <para>
/// <strong>Docker deliberately has no key here.</strong> <c>AddServyxDockerProvisioning()</c> stays
/// unconditional inside the gate, so "the gate is open and nothing else is configured" keeps meaning exactly
/// what it meant before. Adding a <c>Servyx:Provisioners:Docker:Enabled</c> key would change the behaviour of
/// every host that already sets the gate.
/// </para>
/// <para>
/// <strong>Credentials are locators, never values.</strong> Every credential this type reads is a
/// <see cref="SecretUrn"/> resolved through <see cref="ISecretStore"/> at the point of use. Non-secret
/// identifiers that appear in every portal URL and audit log — an Azure tenant/client/subscription id, an AWS
/// region — are plain configuration, exactly as <see cref="AzureServicePrincipal"/> argues.
/// </para>
/// <para>
/// <strong>A provisioner enabled without its credentials fails the process at startup.</strong> Not skipped
/// with a warning, and not registered to throw later. See <see cref="FromConfiguration"/>.
/// </para>
/// </remarks>
public sealed class ProvisionerWiringOptions
{
    /// <summary>The configuration section per-provisioner settings are read from.</summary>
    public const string SectionKey = "Servyx:Provisioners";

    /// <summary>The per-provisioner child key that switches it on. Absent, empty or unparseable means off.</summary>
    public const string EnabledKey = "Enabled";

    /// <summary>The section key for the SSH-host provisioner.</summary>
    public const string SshKey = "Ssh";

    /// <summary>The section key for the local-process provisioner.</summary>
    public const string ProcessKey = "Process";

    /// <summary>The section key for the DigitalOcean droplet provisioner.</summary>
    public const string DigitalOceanKey = "DigitalOcean";

    /// <summary>The section key for the Azure virtual-machine provisioner.</summary>
    public const string AzureKey = "Azure";

    /// <summary>The section key for the AWS EC2 provisioner.</summary>
    public const string AwsEc2Key = "AwsEc2";

    /// <summary>The section key for the AWS Lightsail provisioner.</summary>
    public const string AwsLightsailKey = "AwsLightsail";

    /// <summary>No provisioner beyond Docker. The state of a host that configured none, and the safe default.</summary>
    public static readonly ProvisionerWiringOptions None = new(null, null, null, null, null, null);

    /// <summary>Creates options over an explicit set of provisioners.</summary>
    /// <param name="ssh">The SSH host provisioner, or null.</param>
    /// <param name="process">The local-process provisioner, or null.</param>
    /// <param name="digitalOcean">The DigitalOcean droplet provisioner, or null.</param>
    /// <param name="azure">The Azure virtual-machine provisioner, or null.</param>
    /// <param name="awsEc2">The AWS EC2 provisioner, or null.</param>
    /// <param name="awsLightsail">The AWS Lightsail provisioner, or null.</param>
    public ProvisionerWiringOptions(
        SshProvisionerOptions? ssh,
        ProcessProvisionerOptions? process,
        DigitalOceanProvisionerOptions? digitalOcean,
        AzureProvisionerOptions? azure,
        AwsEc2ProvisionerOptions? awsEc2,
        AwsLightsailProvisionerOptions? awsLightsail)
    {
        Ssh = ssh;
        Process = process;
        DigitalOcean = digitalOcean;
        Azure = azure;
        AwsEc2 = awsEc2;
        AwsLightsail = awsLightsail;
    }

    /// <summary>The configured SSH host provisioner, or null when the operator enabled none.</summary>
    public SshProvisionerOptions? Ssh { get; }

    /// <summary>The configured local-process provisioner, or null when the operator enabled none.</summary>
    public ProcessProvisionerOptions? Process { get; }

    /// <summary>The configured DigitalOcean provisioner, or null when the operator enabled none.</summary>
    public DigitalOceanProvisionerOptions? DigitalOcean { get; }

    /// <summary>The configured Azure provisioner, or null when the operator enabled none.</summary>
    public AzureProvisionerOptions? Azure { get; }

    /// <summary>The configured AWS EC2 provisioner, or null when the operator enabled none.</summary>
    public AwsEc2ProvisionerOptions? AwsEc2 { get; }

    /// <summary>The configured AWS Lightsail provisioner, or null when the operator enabled none.</summary>
    public AwsLightsailProvisionerOptions? AwsLightsail { get; }

    /// <summary>Whether the operator enabled any provisioner beyond the unconditional Docker one.</summary>
    public bool Any => ProvisionerIds.Count > 0;

    /// <summary>
    /// The <c>IProvisioner.ProvisionerId</c>s this configuration will add to the container, in a stable
    /// order. Exposed so a host — or a test — can state what /deploy will offer without building a container.
    /// </summary>
    public IReadOnlyList<string> ProvisionerIds
    {
        get
        {
            var ids = new List<string>(6);

            if (Ssh is not null)
            {
                ids.Add(SshProcessProvisioner.Id);
            }

            if (Process is not null)
            {
                ids.Add(LocalProcessProvisioner.Id);
            }

            if (DigitalOcean is not null)
            {
                ids.Add(DigitalOceanDropletProvisioner.Id);
            }

            if (Azure is not null)
            {
                ids.Add(AzureVirtualMachineProvisioner.Id);
            }

            if (AwsEc2 is not null)
            {
                ids.Add(AwsEc2Provisioner.Id);
            }

            if (AwsLightsail is not null)
            {
                ids.Add(AwsLightsailProvisioner.Id);
            }

            return ids;
        }
    }

    /// <summary>
    /// Reads the enabled provisioners, or returns <see cref="None"/> when <paramref name="gate"/> is closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why a missing credential throws instead of being skipped.</strong> The three plausible
    /// behaviours are: register anyway, decline quietly, or refuse to start. Registering anyway is
    /// unacceptable — a provisioner with no token cannot make a single provider call, so it would appear on
    /// /deploy as a selectable target whose first click is guaranteed to fail, after the operator has already
    /// approved a plan. Declining quietly is better but still wrong here: the operator wrote
    /// <c>Enabled = true</c>, which is an unambiguous statement of intent, and the only symptom of the
    /// decline would be a target silently missing from a list the operator has never seen complete. Refusing
    /// to start names the exact key that is missing, at the only moment when the fix is obvious and nothing
    /// has been provisioned yet — the same choice <see cref="ServyxRconChannels"/> already makes for a
    /// channel configured against an empty command catalogue.
    /// </para>
    /// <para>
    /// <strong>What is checked, and what deliberately is not.</strong> Every required value must be present,
    /// and every credential locator must be a well-formed <see cref="SecretUrn"/>. Whether the secret store
    /// currently <em>holds</em> a value at that URN is not checked: that would be I/O at composition time
    /// against a store an operator may legitimately populate after first start, and an absent value is
    /// reported by the adapter, per call, with the URN in the message. The line drawn here is the one that
    /// can be decided from configuration alone.
    /// </para>
    /// </remarks>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="gate">The provisioning gate; a closed gate yields no provisioners at all.</param>
    /// <exception cref="InvalidOperationException">
    /// A provisioner is enabled but a value it cannot be constructed without is absent or malformed. The
    /// message names the full configuration key.
    /// </exception>
    public static ProvisionerWiringOptions FromConfiguration(IConfiguration configuration, ProvisioningGate gate)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(gate);

        if (!gate.Enabled)
        {
            return None;
        }

        var root = configuration.GetSection(SectionKey);

        return new ProvisionerWiringOptions(
            ReadSsh(root.GetSection(SshKey)),
            ReadProcess(root.GetSection(ProcessKey)),
            ReadDigitalOcean(root.GetSection(DigitalOceanKey)),
            ReadAzure(root.GetSection(AzureKey)),
            ReadAwsEc2(root.GetSection(AwsEc2Key)),
            ReadAwsLightsail(root.GetSection(AwsLightsailKey)));
    }

    private static SshProvisionerOptions? ReadSsh(IConfigurationSection section)
    {
        if (!IsEnabled(section))
        {
            return null;
        }

        return new SshProvisionerOptions(
            RequireValue(section, "Endpoint", "the [user@]host[:port] address the provisioner installs onto"),
            // Required rather than optional, unlike the adapter's own nullable parameter. SshTransport builds
            // its connector descriptor's credential set from this URN alone, so a provisioner configured
            // without one holds a connection that can present no credential at all — the guaranteed-to-fail
            // state this whole check exists to make unreachable.
            RequireUrn(section, "CredentialUrn", "the SSH password or private key the provisioner authenticates with"),
            Trimmed(section["MarkerRoot"]));
    }

    private static ProcessProvisionerOptions? ReadProcess(IConfigurationSection section) =>
        IsEnabled(section)
            // No required values at all, and that is honest rather than lax: this provisioner installs onto
            // the machine Servyx is already running on, over a transport that needs no credential, so there
            // is nothing an operator could omit that would leave it unable to make its first call.
            ? new ProcessProvisionerOptions(Trimmed(section["MachineId"]), Trimmed(section["MarkerRoot"]))
            : null;

    private static DigitalOceanProvisionerOptions? ReadDigitalOcean(IConfigurationSection section)
    {
        if (!IsEnabled(section))
        {
            return null;
        }

        return new DigitalOceanProvisionerOptions(
            RequireUrn(section, "ApiTokenUrn", "the DigitalOcean personal access token every API call is made with"),
            OptionalUrnValue(section, "SshCredentialUrn"),
            Trimmed(section["SshUsername"]) ?? DigitalOceanDropletProvisioner.DefaultSshUsername);
    }

    private static AzureProvisionerOptions? ReadAzure(IConfigurationSection section)
    {
        if (!IsEnabled(section))
        {
            return null;
        }

        return new AzureProvisionerOptions(
            new AzureServicePrincipal(
                RequireValue(section, "TenantId", "the Entra ID tenant the service principal lives in"),
                RequireValue(section, "ClientId", "the application (client) id of the service principal"),
                RequireUrn(section, "ClientSecretUrn", "the service principal's client secret")),
            RequireValue(section, "SubscriptionId", "the subscription every resource is created in"),
            OptionalUrnValue(section, "SshCredentialUrn"),
            Trimmed(section["SshUsername"]) ?? AzureVirtualMachineProvisioner.DefaultSshUsername);
    }

    private static AwsEc2ProvisionerOptions? ReadAwsEc2(IConfigurationSection section)
    {
        if (!IsEnabled(section))
        {
            return null;
        }

        return new AwsEc2ProvisionerOptions(
            ReadSigningIdentity(section),
            RequireValue(section, "Region", "the AWS region this provisioner acts on, e.g. us-east-1"),
            OptionalUrnValue(section, "SshCredentialUrn"),
            Trimmed(section["SshUsername"]) ?? AwsEc2Provisioner.DefaultSshUsername);
    }

    private static AwsLightsailProvisionerOptions? ReadAwsLightsail(IConfigurationSection section)
    {
        if (!IsEnabled(section))
        {
            return null;
        }

        return new AwsLightsailProvisionerOptions(
            ReadSigningIdentity(section),
            RequireValue(section, "Region", "the AWS region this provisioner acts on, e.g. us-east-1"),
            OptionalUrnValue(section, "SshCredentialUrn"));
    }

    /// <summary>
    /// Reads the AWS key pair both AWS adapters sign with. The session token is genuinely optional — it is
    /// present for temporary STS credentials and absent for a long-lived IAM user key — so a missing one is
    /// not an error, but a malformed one is.
    /// </summary>
    private static AwsSigningIdentity ReadSigningIdentity(IConfigurationSection section) => new(
        RequireUrn(section, "AccessKeyIdUrn", "the AWS access key id every request is signed with"),
        RequireUrn(section, "SecretAccessKeyUrn", "the AWS secret access key every request is signed with"),
        OptionalUrn(section, "SessionTokenUrn"));

    /// <summary>
    /// Fail-closed, exactly like <see cref="SshDockerWriteModes.ReadGrants"/>, <see cref="RconWiringOptions"/>
    /// and <see cref="SshBackupWiringOptions"/>: absent, empty, misspelled and explicitly false all mean "not
    /// enabled", and are all spelled the same way here.
    /// </summary>
    private static bool IsEnabled(IConfigurationSection section) =>
        bool.TryParse(section[EnabledKey], out var enabled) && enabled;

    private static string? Trimmed(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static string RequireValue(IConfigurationSection section, string key, string what) =>
        Trimmed(section[key])
        ?? throw new InvalidOperationException(
            $"'{section.Path}:{EnabledKey}' is true, so '{section.Path}:{key}' is required — it names {what}. "
            + "Refusing to start rather than registering a provisioner whose first provider call could only "
            + $"fail, or dropping it silently from /deploy after '{section.Path}:{EnabledKey}' asked for it.");

    private static SecretUrn RequireUrn(IConfigurationSection section, string key, string what)
    {
        var configured = RequireValue(section, key, $"where {what} is stored");

        if (!SecretUrn.TryParse(configured, out var urn))
        {
            throw new InvalidOperationException(
                $"'{section.Path}:{key}' is not a well-formed secret URN. It must be a locator of the form "
                + "secret://{scope}/{scopeId}/{category}/{name}, for example "
                + $"secret://global/{section.Key.ToLowerInvariant()}/api/token — never {what} itself. "
                + "Servyx resolves it through ISecretStore at the point of use, so a credential written "
                + "inline here would be a credential in a configuration file.");
        }

        return urn;
    }

    /// <summary>
    /// Reads an optional credential locator. Absent is fine; present-but-malformed is not, because an
    /// operator who named a locator meant a specific one and quietly substituting nothing would produce a
    /// descriptor that authenticates with no key at all.
    /// </summary>
    private static SecretUrn? OptionalUrn(IConfigurationSection section, string key)
    {
        var configured = Trimmed(section[key]);
        if (configured is null)
        {
            return null;
        }

        if (!SecretUrn.TryParse(configured, out var urn))
        {
            throw new InvalidOperationException(
                $"'{section.Path}:{key}' is set but is not a well-formed secret URN. Remove the key or correct "
                + "it to a locator of the form secret://{scope}/{scopeId}/{category}/{name}; it is never the "
                + "credential itself.");
        }

        return urn;
    }

    /// <summary>
    /// The same read as <see cref="OptionalUrn"/>, flattened to the <c>string?</c> the cloud adapters take for
    /// <c>TargetDescriptor.CredentialUrn</c> — validated here so a malformed locator is caught at startup
    /// rather than stamped onto every descriptor the provisioner produces.
    /// </summary>
    private static string? OptionalUrnValue(IConfigurationSection section, string key) =>
        OptionalUrn(section, key)?.Value;
}
