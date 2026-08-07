using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;
using Servyx.Infrastructure.Azure.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Provisioning;
using Servyx.Infrastructure.Docker.Provisioning;
using Servyx.Infrastructure.Process.Provisioning;
using Servyx.Infrastructure.Ssh.Provisioning;

namespace Servyx.Web.Services;

/// <summary>How a <see cref="ProvisionerFormField"/> is entered, which is the only thing the page needs to
/// know in order to render one.</summary>
public enum ProvisionerFieldKind
{
    /// <summary>A single-line text input.</summary>
    Text,

    /// <summary>A single-line numeric input. Still carried as text — a port is an identifier, not arithmetic.</summary>
    Number,

    /// <summary>A multi-line input, for values that genuinely are multi-line (an SSH public key, cloud-init).</summary>
    Multiline,
}

/// <summary>
/// One editable input on the deploy form, and the <see cref="ProvisioningRequest.Parameters"/> entry it
/// produces.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is data, not code.</strong> There is no delegate here on purpose: a schema is a value a
/// reader can compare against the adapter's <c>BuildSpec</c> line by line, and a test can assert on it
/// without rendering anything. Everything an adapter's parameter dictionary can express is reachable from
/// the three members below.
/// </para>
/// </remarks>
public sealed record ProvisionerFormField
{
    /// <summary>
    /// The field's stable identity within its schema. Emitted verbatim as this input's <c>data-testid</c>
    /// and used as the key the page holds the entered value under, so it must be unique within a schema and
    /// must not change once shipped.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The label shown beside the input, and the name a refusal uses when this field is missing.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// The provisioning parameter key this field writes. A literal key in almost every case; a key
    /// containing the placeholder <c>{0}</c> has the entered value substituted into the key itself, which is
    /// what Docker's <c>port:&lt;host&gt;/tcp</c> shape needs.
    /// </summary>
    public required string ParameterKey { get; init; }

    /// <summary>The value the field starts at when its provisioner is selected.</summary>
    public string DefaultValue { get; init; } = string.Empty;

    /// <summary>How the input is rendered.</summary>
    public ProvisionerFieldKind Kind { get; init; } = ProvisionerFieldKind.Text;

    /// <summary>
    /// Whether the adapter's <c>BuildSpec</c> will throw without this value. Required fields are checked by
    /// the page <em>before</em> a provisioner is asked anything, so a missing one is a named refusal rather
    /// than an exception message.
    /// </summary>
    public bool IsRequired { get; init; } = true;

    /// <summary>An optional one-line explanation rendered under the input.</summary>
    public string? Hint { get; init; }

    /// <summary>
    /// Parameters emitted alongside this one, and only when this field has a value. This exists because some
    /// adapter parameters are meaningless alone: an <c>install:0:appId</c> without an
    /// <c>install:0:verb</c> is an install entry the provisioner will refuse, so the verb travels with the
    /// value that implies it rather than being emitted unconditionally.
    /// </summary>
    public IReadOnlyDictionary<string, string> ImpliedParameters { get; init; } =
        ProvisionerFormSchema.NoParameters;
}

/// <summary>
/// What one provisioner needs from the operator in order to be given a <see cref="ProvisioningRequest"/> it
/// will accept.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this lives here and not on <see cref="IProvisioner"/>.</strong> The obvious alternative is a
/// <c>DescribeParameters()</c> member on the domain interface, so an adapter states its own requirements.
/// It was rejected for three reasons. First, it puts presentation concerns — a label, an input kind, a
/// default value, an ordering — into <c>Servyx.Domain</c>, which is the one project that must stay free of
/// them. Second, it would have to be implemented seven times inside adapters this change is explicitly not
/// allowed to alter, plus by every fake that implements <see cref="IProvisioner"/>. Third, and decisively,
/// it would not actually remove the duplication it promises: an adapter's <c>BuildSpec</c> reads its
/// parameters imperatively, so a <c>DescribeParameters()</c> beside it is a second, independently
/// maintained list that can drift from the first exactly as this one can — but with the drift now spread
/// across seven assemblies instead of visible in one file.
/// </para>
/// <para>
/// <strong>What is bought instead.</strong> The page never names a provisioner id, so a new adapter needs no
/// edit to the UI; it needs a schema added to <see cref="ProvisionerFormCatalog"/>, or nothing at all — an
/// adapter the catalog has never heard of is still deployable through the free-form editor
/// <see cref="AllowsAdditionalParameters"/> turns on. That is the property the requirement is actually
/// after: adding a target must not mean editing a conditional in the UI.
/// </para>
/// </remarks>
public sealed record ProvisionerFormSchema
{
    /// <summary>The shared empty parameter map, so no schema allocates one to say "none".</summary>
    public static readonly IReadOnlyDictionary<string, string> NoParameters =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The <see cref="IProvisioner.ProvisionerId"/> this schema describes.</summary>
    public required string ProvisionerId { get; init; }

    /// <summary>
    /// The <see cref="ProvisioningRequest.DeploymentProfileId"/> a request for this provisioner carries. No
    /// shipped adapter reads it — it is descriptive, recorded with the request — but it is not nullable, so
    /// every schema states one rather than letting the page invent a value.
    /// </summary>
    public required string DeploymentProfileId { get; init; }

    /// <summary>The fields, in the order they are rendered.</summary>
    public IReadOnlyList<ProvisionerFormField> Fields { get; init; } = [];

    /// <summary>
    /// Parameters this provisioner always receives, which the operator is not asked about because there is
    /// no decision to make. Docker's <c>restartPolicy</c> is the whole of this today.
    /// </summary>
    public IReadOnlyDictionary<string, string> FixedParameters { get; init; } = NoParameters;

    /// <summary>
    /// Whether the form also offers a free-form <c>key=value</c> editor. True only for
    /// <see cref="ProvisionerFormCatalog.Undescribed"/>: a described provisioner's schema <em>is</em> the
    /// description, and bolting an escape hatch onto it would make every described form two forms.
    /// </summary>
    public bool AllowsAdditionalParameters { get; init; }

    /// <summary>An optional sentence explaining what this provisioner creates, shown above its fields.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The starting value of every field, keyed by <see cref="ProvisionerFormField.Id"/>. The page replaces
    /// its whole edit state with this whenever the selected provisioner changes, which is what makes a value
    /// typed for one target unable to reach another.
    /// </summary>
    public Dictionary<string, string> Defaults()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            values[field.Id] = field.DefaultValue;
        }

        return values;
    }

    /// <summary>
    /// Builds the request this schema's provisioner would be handed, or refuses — naming every required
    /// field that is empty — without building anything at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The refusal is the point.</strong> Every adapter's <c>BuildSpec</c> already throws on a
    /// missing parameter, so a half-formed request could not produce a half-formed plan either way. What it
    /// <em>would</em> produce is an exception message naming an internal parameter key, surfaced as "could
    /// not build a plan", after the provisioner had already been called. Checking here means the caller is
    /// told which control on their screen is empty, and the provisioner is never asked.
    /// </para>
    /// <para>
    /// A field that is not required and left empty contributes nothing — not an empty-string parameter.
    /// Several adapters distinguish "absent" from "present and blank" (Lightsail's availability zone falls
    /// back to a computed default only when the key is missing), so emitting an empty value would quietly
    /// mean something different from leaving the field alone.
    /// </para>
    /// </remarks>
    /// <param name="values">The entered values, keyed by <see cref="ProvisionerFormField.Id"/>.</param>
    /// <param name="identity">The per-page identifiers every request carries regardless of provisioner.</param>
    /// <param name="additionalParameters">
    /// The free-form <c>key=value</c> block, used only when <see cref="AllowsAdditionalParameters"/>.
    /// </param>
    /// <param name="request">The built request, or <see langword="null"/> when this method refuses.</param>
    /// <param name="refusal">Why it refused, or <see langword="null"/> on success.</param>
    /// <returns>Whether a request was built.</returns>
    public bool TryBuildRequest(
        IReadOnlyDictionary<string, string> values,
        DeployRequestIdentity identity,
        string? additionalParameters,
        out ProvisioningRequest? request,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(identity);

        request = null;
        refusal = null;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fixedParameter in FixedParameters)
        {
            parameters[fixedParameter.Key] = fixedParameter.Value;
        }

        var missing = new List<string>();

        foreach (var field in Fields)
        {
            var value = values.TryGetValue(field.Id, out var entered) ? entered : string.Empty;

            if (string.IsNullOrWhiteSpace(value))
            {
                if (field.IsRequired)
                {
                    missing.Add(field.Label);
                }

                continue;
            }

            parameters[field.ParameterKey.Replace("{0}", value, StringComparison.Ordinal)] = value;

            foreach (var implied in field.ImpliedParameters)
            {
                parameters[implied.Key] = implied.Value;
            }
        }

        if (missing.Count > 0)
        {
            refusal =
                $"Nothing was sent to '{ProvisionerId}'. "
                + $"{Describe(missing)} required by this provisioner and left empty. "
                + "Fill it in and preview again.";
            return false;
        }

        if (AllowsAdditionalParameters
            && !TryAddAdditionalParameters(additionalParameters, parameters, out refusal))
        {
            return false;
        }

        // Written last, and unconditionally, so nothing a schema or a free-form line says can displace the
        // three identifiers every adapter's tagging convention is built from.
        parameters["instanceId"] = identity.InstanceId;
        parameters["jobId"] = identity.JobId;
        parameters["connectorId"] = identity.ConnectorId;

        request = new ProvisioningRequest(
            GameDefinitionId: identity.GameDefinitionId,
            DeploymentProfileId: DeploymentProfileId,
            ConnectorId: identity.ConnectorId,
            Parameters: parameters);

        return true;
    }

    /// <summary>
    /// Parses the free-form block into parameters, refusing on a line that is not <c>key=value</c> and on any
    /// attempt to write one of the three identifiers the caller owns.
    /// </summary>
    private static bool TryAddAdditionalParameters(
        string? text,
        Dictionary<string, string> parameters,
        out string? refusal)
    {
        refusal = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                refusal =
                    $"Nothing was sent. The line '{line}' is not a 'key=value' pair, and guessing what was "
                    + "meant by it is not something this page will do.";
                return false;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (ReservedKeys.Contains(key))
            {
                refusal =
                    $"Nothing was sent. '{key}' is set by Servyx for every request and cannot be overridden "
                    + "here — it is what ties the created resource back to this page's ledger row.";
                return false;
            }

            parameters[key] = value;
        }

        return true;
    }

    /// <summary>The parameters the page always writes itself, which a free-form line may not displace.</summary>
    private static readonly HashSet<string> ReservedKeys =
        new(StringComparer.Ordinal) { "instanceId", "jobId", "connectorId" };

    /// <summary>Renders one or several missing labels as a sentence fragment that reads correctly either way.</summary>
    private static string Describe(IReadOnlyList<string> labels) => labels.Count == 1
        ? $"'{labels[0]}' is"
        : $"{string.Join(", ", labels.Select(l => $"'{l}'"))} are";
}

/// <summary>
/// The identifiers every request carries whichever provisioner is selected: what is being deployed, and the
/// three names that let a created resource be found again.
/// </summary>
/// <param name="GameDefinitionId">The game definition the resulting server will run.</param>
/// <param name="InstanceId">
/// The instance this deployment is for, fixed once per page instance so repeated previews of unchanged
/// inputs produce the same plan hash.
/// </param>
/// <param name="JobId">The job this deployment belongs to, likewise fixed per page instance.</param>
/// <param name="ConnectorId">The connector the provisioned resource is attached to.</param>
public sealed record DeployRequestIdentity(
    string GameDefinitionId,
    string InstanceId,
    string JobId,
    string ConnectorId);

/// <summary>
/// The schemas this host knows, keyed by provisioner id, with an honest answer for a provisioner it does
/// not know.
/// </summary>
/// <remarks>
/// Registered in the container so a host can supply its own set; the page falls back to
/// <see cref="CreateDefault"/> when none is registered, which is what keeps a test — and a host that
/// composes nothing — working without a registration.
/// </remarks>
public sealed class ProvisionerFormCatalog
{
    /// <summary>
    /// The container image Docker's <c>image</c> field starts at when no bundled game definition supplies
    /// one. The same literal <c>DeployPage</c> carried before this catalog existed.
    /// </summary>
    public const string FallbackContainerImage = "ghcr.io/thijsvanloef/palworld-server-docker:latest";

    /// <summary>
    /// The host port Docker's <c>host-port</c> field starts at when no selected game declares a published
    /// <c>purpose: game</c> network port with a literal value. The same literal <c>DeployPage</c> and this
    /// catalog carried before a game's own capabilities could drive it.
    /// </summary>
    public const string FallbackHostPort = "8211";

    /// <summary>
    /// How the SSH adapter spells an "install a Steam app" step. Taken from that adapter's own constant
    /// rather than written out here, so renaming the verb breaks this file at compile time instead of
    /// producing a form that silently emits a verb the provisioner will refuse.
    /// </summary>
    private const string SshSteamCmdVerb = Servyx.Infrastructure.Ssh.Provisioning.SteamCmdInstallStep.VerbName;

    /// <summary>The same, for the local-process adapter, which declares its own step type. Kept separate
    /// rather than assumed equal for exactly the reason above.</summary>
    private const string LocalSteamCmdVerb = Servyx.Infrastructure.Process.Provisioning.SteamCmdInstallStep.VerbName;

    private readonly Dictionary<string, ProvisionerFormSchema> _schemas;

    /// <summary>Creates a catalog over <paramref name="schemas"/>. A later schema wins over an earlier one
    /// with the same id, so a host can override a shipped default.</summary>
    /// <param name="schemas">The schemas this catalog answers with.</param>
    public ProvisionerFormCatalog(IEnumerable<ProvisionerFormSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        _schemas = new Dictionary<string, ProvisionerFormSchema>(StringComparer.Ordinal);
        foreach (var schema in schemas)
        {
            _schemas[schema.ProvisionerId] = schema;
        }
    }

    /// <summary>The provisioner ids this catalog describes, in no particular order.</summary>
    public IReadOnlyCollection<string> DescribedProvisionerIds => _schemas.Keys;

    /// <summary>
    /// The schema for <paramref name="provisionerId"/>, or <see cref="Undescribed"/> when this catalog has
    /// never heard of it.
    /// </summary>
    /// <remarks>
    /// Never returns <see langword="null"/> and never throws. An unknown provisioner is a real situation —
    /// a host may register an adapter this build has no schema for — and the answer to it is a usable form,
    /// not a blank page or an exception.
    /// </remarks>
    /// <param name="provisionerId">The provisioner to describe.</param>
    public ProvisionerFormSchema For(string provisionerId) =>
        provisionerId is not null && _schemas.TryGetValue(provisionerId, out var schema)
            ? schema
            : Undescribed(provisionerId ?? string.Empty);

    /// <summary>
    /// The schema for a provisioner nobody described: no declared fields, and a free-form
    /// <c>key=value</c> editor instead.
    /// </summary>
    /// <remarks>
    /// This is the difference between "the page cannot drive this target" and "the page cannot guess this
    /// target's fields for you". Only the second is true, and only the second is worth saying.
    /// </remarks>
    /// <param name="provisionerId">The provisioner with no schema.</param>
    public static ProvisionerFormSchema Undescribed(string provisionerId) => new()
    {
        ProvisionerId = provisionerId,
        DeploymentProfileId = "custom",
        AllowsAdditionalParameters = true,
        Description =
            "This build ships no field list for this provisioner, so its parameters are entered directly. "
            + "One 'key=value' per line; the request still carries instanceId, jobId and connectorId on its own.",
    };

    /// <summary>
    /// The schemas for the seven adapters Servyx ships, each one a transcription of that adapter's
    /// <c>BuildSpec</c>.
    /// </summary>
    /// <param name="containerImage">
    /// The image Docker's <c>image</c> field defaults to — the selected game definition's, when one is
    /// selected. Passed in rather than read here so this type stays free of game-definition loading, and so
    /// the default the page shows is the same one it showed before this catalog existed.
    /// </param>
    /// <param name="hostPort">
    /// The port Docker's <c>host-port</c> field defaults to — the selected game definition's own published
    /// <c>purpose: game</c> network port, when one is declared as a literal. Passed in for the same reason
    /// <paramref name="containerImage"/> is: this type stays free of game-definition loading, and callers
    /// that never had a game to ask keep the literal this catalog always defaulted to.
    /// </param>
    /// <param name="stopGracePeriodSeconds">
    /// The whole-seconds value Docker's <c>stop-grace-period-seconds</c> field defaults to — the selected
    /// game definition's Docker deployment profile's own declared <c>stopGracePeriodSeconds</c>, when one is
    /// declared, as a plain decimal string. Passed in for the same reason <paramref name="containerImage"/>
    /// is. Unlike the image and port, there is no non-empty fallback: a definition that declares no grace
    /// period leaves the field blank, which is the honest "let Docker's own 10-second default apply" state —
    /// inventing a fallback value here would be exactly the silent substitution this field exists to prevent.
    /// See <see cref="Servyx.Infrastructure.Docker.Provisioning.DockerContainerProvisioner.BuildSpec"/> for
    /// where an out-of-range or malformed value (never produced by this catalog itself, since it only ever
    /// carries a value copied verbatim from a validated definition) is refused loudly rather than silently
    /// discarded.
    /// </param>
    public static ProvisionerFormCatalog CreateDefault(
        string? containerImage = null, string? hostPort = null, string? stopGracePeriodSeconds = null)
    {
        var image = string.IsNullOrWhiteSpace(containerImage) ? FallbackContainerImage : containerImage;
        var port = string.IsNullOrWhiteSpace(hostPort) ? FallbackHostPort : hostPort;
        var gracePeriod = string.IsNullOrWhiteSpace(stopGracePeriodSeconds) ? null : stopGracePeriodSeconds;

        return new ProvisionerFormCatalog(
        [
            Docker(image, port, gracePeriod),
            SshProcess(),
            LocalProcess(),
            DigitalOcean(),
            Azure(),
            AwsEc2(),
            AwsLightsail(),
        ]);
    }

    /// <summary>
    /// <c>DockerContainerProvisioner</c>: image, container name, one published port, an optional stop grace
    /// period, and a fixed restart policy.
    /// </summary>
    /// <remarks>
    /// <strong>The first three field ids are a compatibility surface.</strong> They are the
    /// <c>data-testid</c>s the Docker form has always emitted, and the parameters they produce are
    /// character-for-character the ones <c>DeployPage.BuildRequest</c> hardcoded before it existed —
    /// including <c>restartPolicy=unless-stopped</c> and the <c>port:&lt;n&gt;/tcp</c> key whose value is
    /// the port again. <c>ProvisionerFormCatalogTests</c> pins all of it. The fourth field,
    /// <c>stop-grace-period-seconds</c>, is newer: it closes the gap where a game definition's
    /// <c>deployments[].stopGracePeriodSeconds</c> was parsed and validated but never actually reached a
    /// provisioned container — see <see cref="CreateDefault"/>'s remarks on its own
    /// <paramref name="stopGracePeriodSeconds"/> parameter for why it has no non-empty fallback.
    /// </remarks>
    private static ProvisionerFormSchema Docker(string image, string hostPort, string? stopGracePeriodSeconds) => new()
    {
        ProvisionerId = DockerContainerProvisioner.Id,
        DeploymentProfileId = "docker",
        Description = "Creates a container on the Docker daemon this process is configured against.",
        Fields =
        [
            new()
            {
                Id = "container-name",
                Label = "Container name",
                ParameterKey = "containerName",
                DefaultValue = "servyx-preview",
            },
            new()
            {
                Id = "image",
                Label = "Image",
                ParameterKey = "image",
                DefaultValue = image,
            },
            new()
            {
                Id = "host-port",
                Label = "Port (host → container, tcp)",
                ParameterKey = "port:{0}/tcp",
                DefaultValue = hostPort,
                Kind = ProvisionerFieldKind.Number,
            },
            new()
            {
                Id = "stop-grace-period-seconds",
                Label = "Stop grace period (seconds)",
                ParameterKey = "stopGracePeriodSeconds",
                DefaultValue = stopGracePeriodSeconds ?? string.Empty,
                Kind = ProvisionerFieldKind.Number,
                IsRequired = false,
                Hint = "How long Docker waits after asking the workload to stop before force-killing it. "
                    + "Leave blank to accept Docker's own 10-second default, which truncates a slow save.",
            },
        ],
        FixedParameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["restartPolicy"] = "unless-stopped",
        },
    };

    /// <summary><c>SshProcessProvisioner</c>: a data directory and an executable on an already-existing host.</summary>
    private static ProvisionerFormSchema SshProcess() => new()
    {
        ProvisionerId = SshProcessProvisioner.Id,
        DeploymentProfileId = "native-steamcmd",
        Description =
            "Installs onto the SSH host this provisioner was configured with. The host itself is fixed at "
            + "startup — there is no endpoint or credential field here, and there is not meant to be.",
        Fields = ProcessFields(SshSteamCmdVerb),
    };

    /// <summary><c>LocalProcessProvisioner</c>: the same two values, on the machine Servyx is already running on.</summary>
    private static ProvisionerFormSchema LocalProcess() => new()
    {
        ProvisionerId = LocalProcessProvisioner.Id,
        DeploymentProfileId = "native-steamcmd",
        Description = "Installs onto the machine Servyx itself is running on.",
        Fields = ProcessFields(LocalSteamCmdVerb),
    };

    /// <summary>
    /// The fields both process adapters read, which are the same fields because both adapters parse the same
    /// parameter names — see their <c>BuildSpec</c> methods, which differ only in the spec type they return.
    /// </summary>
    /// <param name="steamCmdVerb">That adapter's own spelling of the SteamCMD install verb.</param>
    private static IReadOnlyList<ProvisionerFormField> ProcessFields(string steamCmdVerb) =>
    [
        new()
        {
            Id = "data-dir",
            Label = "Data directory",
            ParameterKey = "dataDir",
            Hint = "An absolute path on the target machine, as that machine spells paths.",
        },
        new()
        {
            Id = "executable",
            Label = "Executable",
            ParameterKey = "executable",
            Hint = "Resolved relative to the data directory unless absolute.",
        },
        new()
        {
            Id = "steam-app-id",
            Label = "SteamCMD app id (optional)",
            ParameterKey = "install:0:appId",
            IsRequired = false,
            Hint = "Left empty, no install step runs and the executable must already be present.",
            ImpliedParameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["install:0:verb"] = steamCmdVerb,
                ["install:0:validate"] = "true",
            },
        },
    ];

    /// <summary><c>DigitalOceanDropletProvisioner</c>: name, size, region, image.</summary>
    private static ProvisionerFormSchema DigitalOcean() => new()
    {
        ProvisionerId = DigitalOceanDropletProvisioner.Id,
        DeploymentProfileId = "machine",
        Description = "Creates a droplet. This spends real money the moment the plan is applied.",
        Fields =
        [
            Name("Droplet name"),
            new() { Id = "size", Label = "Size", ParameterKey = "size", DefaultValue = "s-2vcpu-4gb" },
            new() { Id = "region", Label = "Region", ParameterKey = "region", DefaultValue = "nyc3" },
            new() { Id = "image", Label = "Image", ParameterKey = "image", DefaultValue = "ubuntu-24-04-x64" },
            SshPublicKey(required: false),
        ],
    };

    /// <summary><c>AzureVirtualMachineProvisioner</c>: name, resource group, size, region, image URN, SSH key.</summary>
    /// <remarks>
    /// The admin username is <em>not</em> here, and that is the adapter's shape rather than an omission: it
    /// is fixed at construction from <c>Servyx:Provisioners:Azure:SshUsername</c>, because
    /// <c>RefreshAsync</c> has to rebuild an identical descriptor from a VM that records no such thing.
    /// </remarks>
    private static ProvisionerFormSchema Azure() => new()
    {
        ProvisionerId = AzureVirtualMachineProvisioner.Id,
        DeploymentProfileId = "machine",
        Description = "Creates a virtual machine and the four resources it needs. This spends real money.",
        Fields =
        [
            Name("VM name"),
            new()
            {
                Id = "resource-group",
                Label = "Resource group",
                ParameterKey = "resourceGroup",
                Hint = "Created if it does not exist; every resource in this plan goes into it.",
            },
            new() { Id = "size", Label = "Size", ParameterKey = "size", DefaultValue = "Standard_D2as_v5" },
            new() { Id = "region", Label = "Region", ParameterKey = "region", DefaultValue = "eastus" },
            new()
            {
                Id = "image",
                Label = "Image URN",
                ParameterKey = "image",
                DefaultValue = "Canonical:ubuntu-24_04-lts:server:latest",
                Hint = "publisher:offer:sku:version. Parsed before anything is created.",
            },
            SshPublicKey(required: true),
        ],
    };

    /// <summary><c>AwsEc2Provisioner</c>: name, instance type, AMI. The region is fixed at construction.</summary>
    private static ProvisionerFormSchema AwsEc2() => new()
    {
        ProvisionerId = AwsEc2Provisioner.Id,
        DeploymentProfileId = "machine",
        Description =
            "Creates an EC2 instance in the region this provisioner was configured with — there is no region "
            + "field, because a request that disagreed with the signing region could only ever be wrong. "
            + "This spends real money.",
        Fields =
        [
            Name("Instance name"),
            new() { Id = "size", Label = "Instance type", ParameterKey = "size", DefaultValue = "t3.large" },
            new()
            {
                Id = "image",
                Label = "AMI id",
                ParameterKey = "image",
                Hint = "An ami-… id valid in this provisioner's configured region.",
            },
            new()
            {
                Id = "key-pair",
                Label = "Key pair (optional)",
                ParameterKey = "keyPair",
                IsRequired = false,
            },
            SshPublicKey(required: false),
        ],
    };

    /// <summary><c>AwsLightsailProvisioner</c>: name, bundle, blueprint, availability zone.</summary>
    /// <remarks>
    /// Lightsail's bundle and blueprint are carried by the same <c>size</c> and <c>image</c> parameter names
    /// every other machine adapter uses — only the labels differ, because only the provider's vocabulary
    /// does.
    /// </remarks>
    private static ProvisionerFormSchema AwsLightsail() => new()
    {
        ProvisionerId = AwsLightsailProvisioner.Id,
        DeploymentProfileId = "machine",
        Description =
            "Creates a Lightsail instance in the region this provisioner was configured with. This spends "
            + "real money.",
        Fields =
        [
            Name("Instance name"),
            new() { Id = "size", Label = "Bundle", ParameterKey = "size", DefaultValue = "large_3_0" },
            new() { Id = "image", Label = "Blueprint", ParameterKey = "image", DefaultValue = "ubuntu_24_04" },
            new()
            {
                Id = "availability-zone",
                Label = "Availability zone (optional)",
                ParameterKey = "availabilityZone",
                IsRequired = false,
                Hint = "Left empty, the provisioner picks the configured region's first zone.",
            },
            SshPublicKey(required: false),
        ],
    };

    private static ProvisionerFormField Name(string label) => new()
    {
        Id = "name",
        Label = label,
        ParameterKey = "name",
        DefaultValue = "servyx-preview",
    };

    /// <summary>
    /// The public half of an SSH key pair. A public key is not a credential and does not belong in the
    /// secret store — the matching <em>private</em> key is, and it is referenced by URN in configuration and
    /// never typed on this page.
    /// </summary>
    private static ProvisionerFormField SshPublicKey(bool required) => new()
    {
        Id = "ssh-public-key",
        Label = required ? "SSH public key" : "SSH public key (optional)",
        ParameterKey = "sshPublicKey",
        Kind = ProvisionerFieldKind.Multiline,
        IsRequired = required,
        Hint = "The public half only. The private key is resolved from the secret store by URN.",
    };
}
