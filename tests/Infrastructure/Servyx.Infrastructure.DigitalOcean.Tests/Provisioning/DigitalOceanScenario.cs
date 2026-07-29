using System.Globalization;
using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.DigitalOcean.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Tests.Provisioning;

/// <summary>
/// The shared setup every test in this assembly builds on: one substituted DigitalOcean API, one in-memory
/// secret store holding a token, and one provisioner wired to both.
/// </summary>
/// <remarks>
/// Kept as a scenario object rather than a base class so a test can reach the API double and the secret store
/// directly and assert on what the adapter actually did — which requests it made, which secrets it resolved —
/// rather than only on what it returned.
/// </remarks>
internal sealed class DigitalOceanScenario
{
    /// <summary>The token value stored in <see cref="Secrets"/>. Deliberately distinctive so a leak is findable.</summary>
    internal const string ApiToken = "dop_v1_TESTTOKEN_must_never_appear_anywhere_but_the_authorization_header";

    /// <summary>The Servyx identity every droplet in these tests carries.</summary>
    internal const string InstanceId = "srv-0001";

    /// <summary>The provisioning job every droplet in these tests carries.</summary>
    internal const string JobId = "job-42";

    /// <summary>The connector every droplet in these tests carries.</summary>
    internal const string ConnectorId = "conn-1";

    /// <summary>The droplet id the substituted API hands back from a create.</summary>
    internal const long DropletId = 3164494;

    /// <summary>The public IPv4 the substituted API reports for the created droplet.</summary>
    internal const string PublicIp = "203.0.113.7";

    /// <summary>The private IPv4 the substituted API reports for the created droplet.</summary>
    internal const string PrivateIp = "10.128.0.5";

    /// <summary>The URN the DigitalOcean personal access token lives at.</summary>
    internal static SecretUrn TokenUrn { get; } = SecretUrn.Create("global", "digitalocean", "api", "token");

    /// <summary>The URN of the SSH private key produced descriptors point at. Never the DigitalOcean token.</summary>
    internal const string SshCredentialUrn = "secret://connector/conn-1/ssh/private-key";

    internal DigitalOceanApiDouble Api { get; } = new();

    internal RecordingSecretStore Secrets { get; } = new();

    /// <summary>Transport options a caller hands through to the SSH transport, untouched by this adapter.</summary>
    internal Dictionary<string, string> TransportOptions { get; } = new(StringComparer.Ordinal)
    {
        ["trustPolicy"] = "trustOnFirstUse",
        ["declaredChannels"] = "Exec,FileRead,FileWrite",
    };

    /// <summary>Builds a provisioner over this scenario's API double and secret store.</summary>
    /// <param name="withToken">Whether the API token is present in the secret store.</param>
    /// <param name="sshUsername">The username produced descriptors authenticate as.</param>
    /// <param name="actionPollAttempts">
    /// How many times an approved resize re-reads its DigitalOcean action before reporting it as still in
    /// progress. Three by default so the "still running when the polls are spent" case is reachable in a
    /// test; the interval is always zero here, so the suite never actually waits.
    /// </param>
    internal DigitalOceanDropletProvisioner Provisioner(
        bool withToken = true,
        string sshUsername = "root",
        int actionPollAttempts = 3)
    {
        if (withToken)
        {
            Secrets.Put(TokenUrn, ApiToken);
        }

        return new DigitalOceanDropletProvisioner(
            Api.Client(),
            Secrets,
            TokenUrn,
            sshCredentialUrn: SshCredentialUrn,
            transportOptions: TransportOptions,
            sshUsername: sshUsername,
            timeProvider: TimeProvider.System,
            addressPollInterval: TimeSpan.Zero,
            addressPollAttempts: 3,
            actionPollInterval: TimeSpan.Zero,
            actionPollAttempts: actionPollAttempts);
    }

    /// <summary>A provisioning request for a Palworld-sized droplet, mirroring a cloud deployment profile.</summary>
    internal static ProvisioningRequest PalworldDropletRequest(
        IReadOnlyDictionary<string, string>? overrides = null,
        string? size = "s-2vcpu-4gb")
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instanceId"] = InstanceId,
            ["jobId"] = JobId,
            ["connectorId"] = ConnectorId,
            ["name"] = "palworld-01",
            ["image"] = "ubuntu-24-04-x64",
            ["region"] = "nyc3",
            ["sshKey:0"] = "3b:16:bf:e4:8b:00:8b:b8:59:8c:a9:d3:f0:19:45:fa",
            ["sshPublicKey"] = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleKeyMaterial servyx",
        };

        if (size is not null)
        {
            parameters["size"] = size;
        }

        foreach (var pair in overrides ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            parameters[pair.Key] = pair.Value;
        }

        return new ProvisioningRequest("palworld", "digitalocean-droplet", ConnectorId, parameters);
    }

    /// <summary>The canonical Servyx tag array as the adapter's encoding writes it.</summary>
    internal static IReadOnlyList<string> CanonicalDropletTags { get; } =
    [
        "servyx_connector-id:" + ConnectorId,
        "servyx_instance-id:" + InstanceId,
        "servyx_job-id:" + JobId,
        "servyx_managed:true",
    ];

    /// <summary>The image slug the substituted API reports the droplet is running.</summary>
    internal const string ImageSlug = "ubuntu-24-04-x64";

    /// <summary>The boot-disk size, in gigabytes, the substituted API reports for the droplet.</summary>
    internal const int DiskGigabytes = 80;

    /// <summary>A droplet object as the DigitalOcean API reports it.</summary>
    internal static string DropletJson(
        long id = DropletId,
        string status = "active",
        string region = "nyc3",
        string sizeSlug = "s-2vcpu-4gb",
        IReadOnlyList<string>? tags = null,
        bool withNetworks = true,
        string? imageSlug = ImageSlug,
        long imageId = 178432517,
        int disk = DiskGigabytes)
    {
        var tagJson = string.Join(",", (tags ?? CanonicalDropletTags).Select(t => "\"" + t + "\""));
        var networks = withNetworks
            ? "{\"v4\":[{\"ip_address\":\"" + PrivateIp + "\",\"netmask\":\"255.255.240.0\",\"gateway\":\"\",\"type\":\"private\"},"
              + "{\"ip_address\":\"" + PublicIp + "\",\"netmask\":\"255.255.240.0\",\"gateway\":\"203.0.113.1\",\"type\":\"public\"}],\"v6\":[]}"
            : "{\"v4\":[],\"v6\":[]}";

        // A custom image or a snapshot has no slug at all, so the slug member is omitted rather than emptied
        // when a test asks for that shape - which is exactly how DigitalOcean reports one.
        var image = "{\"id\":" + imageId.ToString(CultureInfo.InvariantCulture)
            + (imageSlug is null ? string.Empty : ",\"slug\":\"" + imageSlug + "\"")
            + ",\"distribution\":\"Ubuntu\"}";

        return "{\"id\":" + id.ToString(CultureInfo.InvariantCulture)
            + ",\"name\":\"palworld-01\""
            + ",\"status\":\"" + status + "\""
            + ",\"created_at\":\"2026-07-27T10:00:00Z\""
            + ",\"size_slug\":\"" + sizeSlug + "\""
            + ",\"disk\":" + disk.ToString(CultureInfo.InvariantCulture)
            + ",\"image\":" + image
            + ",\"tags\":[" + tagJson + "]"
            + ",\"region\":{\"slug\":\"" + region + "\"}"
            + ",\"networks\":" + networks
            + "}";
    }

    /// <summary>The <c>{ "droplet": ... }</c> envelope a create or get answers with.</summary>
    internal static string DropletEnvelopeJson(
        long id = DropletId,
        string status = "active",
        string region = "nyc3",
        string sizeSlug = "s-2vcpu-4gb",
        IReadOnlyList<string>? tags = null,
        bool withNetworks = true,
        string? imageSlug = ImageSlug,
        long imageId = 178432517,
        int disk = DiskGigabytes) =>
        "{\"droplet\":" + DropletJson(id, status, region, sizeSlug, tags, withNetworks, imageSlug, imageId, disk) + "}";

    /// <summary>
    /// The handle Servyx would have recorded for the droplet, optionally carrying the two descriptive
    /// expectations a drift check needs in order to be able to prove a match.
    /// </summary>
    /// <remarks>
    /// The size and image expectations are ordinary Servyx tags a caller opts into with <c>tag:</c>
    /// provisioning parameters — the adapter does not stamp them itself — so a handle built without them is
    /// just as realistic, and is what the "cannot prove a match" tests use.
    /// </remarks>
    internal static ResourceHandle RecordedHandle(
        string region = "nyc3",
        string? size = "s-2vcpu-4gb",
        string? image = ImageSlug,
        long id = DropletId,
        string provisionerId = DigitalOceanDropletProvisioner.Id)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxTagKeys.Managed] = ServyxTagKeys.ManagedValue,
            [ServyxTagKeys.InstanceId] = InstanceId,
            [ServyxTagKeys.JobId] = JobId,
            [ServyxTagKeys.ConnectorId] = ConnectorId,
        };

        if (size is not null)
        {
            tags[ServyxTagKeys.Size] = size;
        }

        if (image is not null)
        {
            tags[ServyxTagKeys.Image] = image;
        }

        return new ResourceHandle(provisionerId, id.ToString(CultureInfo.InvariantCulture), region, tags);
    }

    /// <summary>Answers every GET with one droplet payload, and fails loudly on anything that is not a GET.</summary>
    /// <remarks>
    /// The "anything else" branch is the assertion, not the convenience: planning and drift detection must
    /// issue reads and nothing else, so a POST, PUT or DELETE from either path fails the test where it
    /// happens rather than being silently answered.
    /// </remarks>
    internal void RouteReadOnly(string? envelope = null)
    {
        Api.Responder = request => request.Method == HttpMethod.Get
            ? DigitalOceanApiDouble.Json(HttpStatusCode.OK, envelope ?? DropletEnvelopeJson())
            : throw new InvalidOperationException(
                $"A read-only path issued a mutating {request.Method} request to '{request.Uri}'.");
    }

    /// <summary>Answers every GET with a 404, as DigitalOcean does for a droplet that no longer exists.</summary>
    internal void RouteMissingDroplet() =>
        Api.Responder = request => request.Method == HttpMethod.Get
            ? DigitalOceanApiDouble.Empty(HttpStatusCode.NotFound)
            : throw new InvalidOperationException(
                $"A read-only path issued a mutating {request.Method} request to '{request.Uri}'.");

    /// <summary>The <c>{ "droplets": [ ... ] }</c> envelope a list answers with.</summary>
    internal static string DropletListJson(string? nextPage = null, params string[] droplets)
    {
        var links = nextPage is null ? "{}" : "{\"pages\":{\"next\":\"" + nextPage + "\"}}";
        return "{\"droplets\":[" + string.Join(",", droplets) + "],\"links\":" + links + "}";
    }

    /// <summary>Routes the standard create-then-observe exchange: one POST, then GETs answering with the droplet.</summary>
    internal void RouteSuccessfulCreate(string? createEnvelope = null, string? getEnvelope = null)
    {
        Api.Responder = request => request.Method == HttpMethod.Post
            ? DigitalOceanApiDouble.Json(HttpStatusCode.Accepted, createEnvelope ?? DropletEnvelopeJson())
            : DigitalOceanApiDouble.Json(HttpStatusCode.OK, getEnvelope ?? DropletEnvelopeJson());
    }

    /// <summary>Creates a droplet through the full operation path and hands back the resource it produced.</summary>
    internal async Task<ProvisionedResource> CreateAsync(ProvisioningRequest? request = null)
    {
        RouteSuccessfulCreate();

        var provisioner = Provisioner();
        var spec = DigitalOceanDropletProvisioner.BuildSpec(request ?? PalworldDropletRequest());

        return await provisioner.CreateOperation(spec).CreateAsync();
    }
}
