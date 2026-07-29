using System.Globalization;
using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Azure.Provisioning;

namespace Servyx.Infrastructure.Azure.Tests.Provisioning;

/// <summary>
/// The shared setup every test in this assembly builds on: one substituted Azure (token service and ARM), one
/// in-memory secret store holding a client secret, and one provisioner wired to both.
/// </summary>
/// <remarks>
/// Kept as a scenario object rather than a base class so a test can reach the API double and the secret store
/// directly and assert on what the adapter actually did — which requests it made, in which order, to which
/// host, and which secrets it resolved — rather than only on what it returned. That ordering matters far more
/// here than it does in the DigitalOcean suite, because a create is a five-resource sequence rather than one
/// call.
/// </remarks>
internal sealed class AzureScenario
{
    /// <summary>The client secret stored in <see cref="Secrets"/>. Deliberately distinctive so a leak is findable.</summary>
    internal const string ClientSecret = "azsec_v1_TESTSECRET_must_never_appear_anywhere_but_the_token_request_body";

    /// <summary>The access token the substituted token service issues. Must only ever appear in an ARM Authorization header.</summary>
    internal const string AccessToken = "eyJTESTACCESSTOKEN.only.valid.in.an.authorization.header";

    /// <summary>The lifetime, in seconds, the substituted token service states for <see cref="AccessToken"/>.</summary>
    internal const int AccessTokenLifetimeSeconds = 3599;

    /// <summary>The Entra ID tenant the service principal lives in. An identifier, not a secret.</summary>
    internal const string TenantId = "72f988bf-1111-41af-91ab-222222222222";

    /// <summary>The application (client) id of the service principal. An identifier, not a secret.</summary>
    internal const string ClientId = "11111111-2222-3333-4444-555555555555";

    /// <summary>The subscription every resource in these tests is created in.</summary>
    internal const string SubscriptionId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    /// <summary>The resource group the host's five resources live in.</summary>
    internal const string ResourceGroup = "rg-servyx-palworld";

    /// <summary>The Servyx identity every resource in these tests carries.</summary>
    internal const string InstanceId = "srv-0001";

    /// <summary>The provisioning job every resource in these tests carries.</summary>
    internal const string JobId = "job-42";

    /// <summary>The connector every resource in these tests carries.</summary>
    internal const string ConnectorId = "conn-1";

    /// <summary>The virtual machine's name, and the stem its four siblings are named from.</summary>
    internal const string VmName = "palworld-01";

    /// <summary>The ARM location every resource is created in.</summary>
    internal const string Region = "eastus";

    /// <summary>The VM size these tests provision. Present in the price snapshot.</summary>
    internal const string VmSize = "Standard_B2s";

    /// <summary>The four-part Azure image URN these tests provision from.</summary>
    internal const string ImageUrn = "Canonical:ubuntu-24_04-lts:server:latest";

    /// <summary>The public IPv4 the substituted ARM reports for the created address resource.</summary>
    internal const string PublicIp = "203.0.113.7";

    /// <summary>The private IPv4 the substituted ARM reports on the network interface.</summary>
    internal const string PrivateIp = "10.20.0.4";

    /// <summary>The operator's declared SSH public key. ARM consumes this raw, unlike DigitalOcean.</summary>
    internal const string SshPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleKeyMaterial servyx";

    /// <summary>The URN of the SSH private key produced descriptors point at. Never the Azure client secret.</summary>
    internal const string SshCredentialUrn = "secret://connector/conn-1/ssh/private-key";

    /// <summary>The URN the Azure service principal's client secret lives at.</summary>
    internal static SecretUrn ClientSecretUrn { get; } = SecretUrn.Create("global", "azure", "api", "client-secret");

    internal AzureArmApiDouble Api { get; } = new();

    internal RecordingSecretStore Secrets { get; } = new();

    /// <summary>Transport options a caller hands through to the SSH transport, untouched by this adapter.</summary>
    internal Dictionary<string, string> TransportOptions { get; } = new(StringComparer.Ordinal)
    {
        ["trustPolicy"] = "trustOnFirstUse",
        ["declaredChannels"] = "Exec,FileRead,FileWrite",
    };

    // ---------------------------------------------------------------------------------------------------
    // ARM resource ids, spelled out so tests assert against literals rather than against the code under test
    // ---------------------------------------------------------------------------------------------------

    /// <summary>The ARM id of the resource group.</summary>
    internal const string ResourceGroupId =
        "/subscriptions/" + SubscriptionId + "/resourceGroups/" + ResourceGroup;

    /// <summary>The ARM id of the virtual machine.</summary>
    internal const string VmId = ResourceGroupId + "/providers/Microsoft.Compute/virtualMachines/" + VmName;

    /// <summary>The ARM id of the network interface.</summary>
    internal const string NicId =
        ResourceGroupId + "/providers/Microsoft.Network/networkInterfaces/" + VmName + "-nic";

    /// <summary>The ARM id of the public IP address.</summary>
    internal const string PublicIpId =
        ResourceGroupId + "/providers/Microsoft.Network/publicIPAddresses/" + VmName + "-ip";

    /// <summary>The ARM id of the virtual network.</summary>
    internal const string VirtualNetworkId =
        ResourceGroupId + "/providers/Microsoft.Network/virtualNetworks/" + VmName + "-vnet";

    /// <summary>Builds a provisioner over this scenario's API double and secret store.</summary>
    /// <param name="withSecret">Whether the client secret is present in the store.</param>
    /// <param name="sshUsername">The admin username produced descriptors authenticate as.</param>
    /// <param name="pollAttempts">
    /// How many polls the adapter makes before giving up — on a provisioning wait, on a deletion wait, and on
    /// the long-running operation a resize creates. Defaulted so every existing test is unaffected.
    /// </param>
    internal AzureVirtualMachineProvisioner Provisioner(
        bool withSecret = true,
        string sshUsername = "azureuser",
        int pollAttempts = 3)
    {
        if (withSecret)
        {
            Secrets.Put(ClientSecretUrn, ClientSecret);
        }

        return new AzureVirtualMachineProvisioner(
            Api.Client(),
            Secrets,
            new AzureServicePrincipal(TenantId, ClientId, ClientSecretUrn),
            SubscriptionId,
            sshCredentialUrn: SshCredentialUrn,
            transportOptions: TransportOptions,
            sshUsername: sshUsername,
            timeProvider: TimeProvider.System,
            pollInterval: TimeSpan.Zero,
            pollAttempts: pollAttempts);
    }

    /// <summary>A provisioning request for a Palworld-sized VM, mirroring a cloud deployment profile.</summary>
    internal static ProvisioningRequest PalworldVmRequest(
        IReadOnlyDictionary<string, string>? overrides = null,
        string? size = VmSize)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instanceId"] = InstanceId,
            ["jobId"] = JobId,
            ["connectorId"] = ConnectorId,
            ["name"] = VmName,
            ["resourceGroup"] = ResourceGroup,
            ["image"] = ImageUrn,
            ["region"] = Region,
            ["sshPublicKey"] = SshPublicKey,
        };

        if (size is not null)
        {
            parameters["size"] = size;
        }

        foreach (var pair in overrides ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            parameters[pair.Key] = pair.Value;
        }

        return new ProvisioningRequest("palworld", "azure-vm", ConnectorId, parameters);
    }

    // ---------------------------------------------------------------------------------------------------
    // Substituted ARM payloads
    // ---------------------------------------------------------------------------------------------------

    /// <summary>The token service's successful client-credentials response.</summary>
    internal static string TokenJson(string accessToken = AccessToken, int expiresIn = AccessTokenLifetimeSeconds) =>
        "{\"token_type\":\"Bearer\",\"expires_in\":" + expiresIn.ToString(CultureInfo.InvariantCulture)
        + ",\"ext_expires_in\":" + expiresIn.ToString(CultureInfo.InvariantCulture)
        + ",\"access_token\":\"" + accessToken + "\"}";

    /// <summary>The canonical Servyx tags stamped on the VM, including the sibling bookkeeping keys.</summary>
    internal static IReadOnlyDictionary<string, string> CanonicalVmTags { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = InstanceId,
            ["servyx.job-id"] = JobId,
            ["servyx.connector-id"] = ConnectorId,
            ["servyx.role"] = "virtual-machine",
            ["servyx.azure-resource-group"] = ResourceGroup,
            ["servyx.azure-virtual-network"] = VmName + "-vnet",
            ["servyx.azure-subnet"] = VmName + "-subnet",
            ["servyx.azure-public-ip"] = VmName + "-ip",
            ["servyx.azure-network-interface"] = VmName + "-nic",
        };

    /// <summary>The canonical Servyx tags stamped on one of the VM's subsidiary resources.</summary>
    internal static IReadOnlyDictionary<string, string> SiblingTags(string role) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = InstanceId,
            ["servyx.job-id"] = JobId,
            ["servyx.connector-id"] = ConnectorId,
            ["servyx.role"] = role,
            ["servyx.azure-resource-group"] = ResourceGroup,
        };

    /// <summary>Renders a tag dictionary as an ARM <c>tags</c> object.</summary>
    internal static string TagsJson(IReadOnlyDictionary<string, string>? tags) =>
        "{" + string.Join(",", (tags ?? CanonicalVmTags).Select(t => $"\"{t.Key}\":\"{t.Value}\"")) + "}";

    // Assembled by concatenation rather than with raw interpolated string literals: ARM payloads are deeply
    // nested, so they end in runs of three or four '}' characters, which a $$"""...""" literal reads as an
    // interpolation terminator. Concatenation keeps the JSON readable as JSON.

    /// <summary>
    /// A bare provisioned resource, enough for the resource group and the virtual network. Tagged, because ARM
    /// returns a resource's tags on every read and compensation reads a resource back before deleting it.
    /// </summary>
    internal static string ProvisionedJson(
        string id,
        IReadOnlyDictionary<string, string> tags,
        string provisioningState = "Succeeded") =>
        "{\"id\":\"" + id + "\",\"location\":\"" + Region + "\","
        + "\"tags\":" + TagsJson(tags) + ","
        + "\"properties\":{\"provisioningState\":\"" + provisioningState + "\"}}";

    /// <summary>The virtual machine as ARM reports it.</summary>
    internal static string VirtualMachineJson(
        string id = VmId,
        string provisioningState = "Succeeded",
        string vmSize = VmSize,
        IReadOnlyDictionary<string, string>? tags = null,
        string? imageUrn = ImageUrn,
        string? osDiskDeleteOption = "Delete",
        string location = Region)
    {
        var parts = imageUrn?.Split(':');
        var imageJson = parts is { Length: 4 }
            ? "\"imageReference\":{\"publisher\":\"" + parts[0] + "\",\"offer\":\"" + parts[1]
              + "\",\"sku\":\"" + parts[2] + "\",\"version\":\"" + parts[3] + "\"},"
            : string.Empty;

        var osDiskJson = "\"osDisk\":{\"name\":\"" + VmName + "_OsDisk\",\"createOption\":\"FromImage\""
            + (osDiskDeleteOption is null ? string.Empty : ",\"deleteOption\":\"" + osDiskDeleteOption + "\"")
            + ",\"managedDisk\":{\"storageAccountType\":\"Premium_LRS\"}}";

        return "{\"id\":\"" + id + "\",\"name\":\"" + VmName + "\",\"type\":\"Microsoft.Compute/virtualMachines\","
            + "\"location\":\"" + location + "\","
            + "\"tags\":" + TagsJson(tags) + ","
            + "\"properties\":{"
            + "\"provisioningState\":\"" + provisioningState + "\","
            + "\"timeCreated\":\"2026-07-27T10:00:00Z\","
            + "\"hardwareProfile\":{\"vmSize\":\"" + vmSize + "\"},"
            + "\"storageProfile\":{" + imageJson + osDiskJson + "},"
            + "\"osProfile\":{\"adminUsername\":\"azureuser\",\"computerName\":\"" + VmName + "\"},"
            + "\"networkProfile\":{\"networkInterfaces\":[{\"id\":\"" + NicId + "\"}]}"
            + "}}";
    }

    /// <summary>
    /// The handle Servyx would have recorded for the machine, optionally carrying the two descriptive
    /// expectations a drift check needs in order to be able to prove a match.
    /// </summary>
    /// <remarks>
    /// The size and image expectations are ordinary Servyx tags a caller opts into with <c>tag:</c>
    /// provisioning parameters — the adapter does not stamp them itself — so a handle built without them is
    /// just as realistic, and is what the "cannot prove a match" tests use.
    /// </remarks>
    internal static ResourceHandle RecordedHandle(
        string region = Region,
        string? size = VmSize,
        string? image = ImageUrn,
        string resourceId = VmId,
        string provisionerId = AzureVirtualMachineProvisioner.Id)
    {
        var tags = new Dictionary<string, string>(CanonicalVmTags, StringComparer.Ordinal);

        if (size is not null)
        {
            tags[ServyxTagKeys.Size] = size;
        }

        if (image is not null)
        {
            tags[ServyxTagKeys.Image] = image;
        }

        return new ResourceHandle(provisionerId, resourceId, region, tags);
    }

    /// <summary>
    /// Answers the token exchange and every ARM GET with one VM payload, and fails loudly on any ARM request
    /// that is not a GET.
    /// </summary>
    /// <remarks>
    /// The "anything else" branch is the assertion, not the convenience: planning and drift detection must
    /// issue reads and nothing else, so a PUT, PATCH or DELETE from either path fails the test where it
    /// happens rather than being silently answered.
    /// </remarks>
    internal void RouteReadOnly(string? virtualMachineJson = null) =>
        Api.Responder = request =>
            RouteTokenExchange(request)
            ?? (request.Method == HttpMethod.Get
                ? AzureArmApiDouble.Json(HttpStatusCode.OK, virtualMachineJson ?? VirtualMachineJson())
                : throw new InvalidOperationException(
                    $"A read-only path issued a mutating {request.Method} request to '{request.Uri}'."));

    /// <summary>Answers every ARM GET with a 404, as ARM does for a machine that no longer exists.</summary>
    internal void RouteMissingVirtualMachine() =>
        Api.Responder = request =>
            RouteTokenExchange(request)
            ?? (request.Method == HttpMethod.Get
                ? AzureArmApiDouble.Empty(HttpStatusCode.NotFound)
                : throw new InvalidOperationException(
                    $"A read-only path issued a mutating {request.Method} request to '{request.Uri}'."));

    /// <summary>The network interface as ARM reports it.</summary>
    internal static string NetworkInterfaceJson(
        string provisioningState = "Succeeded",
        bool withPublicIp = true) =>
        "{\"id\":\"" + NicId + "\",\"name\":\"" + VmName + "-nic\","
        + "\"type\":\"Microsoft.Network/networkInterfaces\",\"location\":\"" + Region + "\","
        + "\"tags\":" + TagsJson(SiblingTags("network-interface")) + ","
        + "\"properties\":{"
        + "\"provisioningState\":\"" + provisioningState + "\","
        + "\"ipConfigurations\":[{\"name\":\"ipconfig1\",\"properties\":{"
        + "\"privateIPAddress\":\"" + PrivateIp + "\""
        + (withPublicIp ? ",\"publicIPAddress\":{\"id\":\"" + PublicIpId + "\"}" : string.Empty)
        + "}}]"
        + "}}";

    /// <summary>The public IP address as ARM reports it.</summary>
    internal static string PublicIpJson(string? ipAddress = PublicIp, string provisioningState = "Succeeded") =>
        "{\"id\":\"" + PublicIpId + "\",\"name\":\"" + VmName + "-ip\","
        + "\"type\":\"Microsoft.Network/publicIPAddresses\",\"location\":\"" + Region + "\","
        + "\"tags\":" + TagsJson(SiblingTags("public-ip")) + ","
        + "\"properties\":{"
        + "\"provisioningState\":\"" + provisioningState + "\""
        + (ipAddress is null ? string.Empty : ",\"ipAddress\":\"" + ipAddress + "\"")
        + "}}";

    /// <summary>One row of ARM's resource listing.</summary>
    internal static string ResourceSummaryJson(
        string id,
        string type,
        string name,
        string location = Region,
        IReadOnlyDictionary<string, string>? tags = null) =>
        "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"type\":\"" + type + "\","
        + "\"location\":\"" + location + "\",\"tags\":" + TagsJson(tags) + "}";

    /// <summary>The <c>{ "value": [ ... ] }</c> envelope ARM's resource listing answers with.</summary>
    internal static string ResourceListJson(string? nextLink = null, params string[] resources) =>
        "{\"value\":[" + string.Join(",", resources) + "]"
        + (nextLink is null ? string.Empty : ",\"nextLink\":\"" + nextLink + "\"")
        + "}";

    /// <summary>The four resource rows a fully-created Servyx host contributes to a sweep.</summary>
    internal static string[] SweptHostResources() =>
    [
        ResourceSummaryJson(VmId, "Microsoft.Compute/virtualMachines", VmName),
        ResourceSummaryJson(NicId, "Microsoft.Network/networkInterfaces", VmName + "-nic", tags: SiblingTags("network-interface")),
        ResourceSummaryJson(PublicIpId, "Microsoft.Network/publicIPAddresses", VmName + "-ip", tags: SiblingTags("public-ip")),
        ResourceSummaryJson(VirtualNetworkId, "Microsoft.Network/virtualNetworks", VmName + "-vnet", tags: SiblingTags("virtual-network")),
    ];

    // ---------------------------------------------------------------------------------------------------
    // Routing
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Answers a token exchange, or <see langword="null"/> if the request was not one.</summary>
    internal static HttpResponseMessage? RouteTokenExchange(RecordedRequest request) =>
        request.IsTokenExchange
            ? AzureArmApiDouble.Json(HttpStatusCode.OK, TokenJson())
            : null;

    /// <summary>
    /// Routes the whole create sequence: one token exchange, five ARM writes, then address reads.
    /// </summary>
    /// <remarks>
    /// Routed by ARM resource type rather than by call ordinal, so a test that changes the sequence's shape
    /// fails on its own assertions rather than by falling off the end of a script.
    /// </remarks>
    internal void RouteSuccessfulCreate()
    {
        Api.Responder = request =>
            RouteTokenExchange(request)
            ?? (request.Method == HttpMethod.Put
                ? AzureArmApiDouble.Json(HttpStatusCode.Created, PayloadFor(request))
                : AzureArmApiDouble.Json(HttpStatusCode.OK, PayloadFor(request)));
    }

    /// <summary>The substituted ARM object for whichever resource a request names.</summary>
    internal static string PayloadFor(RecordedRequest request)
    {
        var path = request.Uri.AbsolutePath;

        return path.Contains("/Microsoft.Compute/virtualMachines/", StringComparison.OrdinalIgnoreCase)
            ? VirtualMachineJson()
            : path.Contains("/Microsoft.Network/networkInterfaces/", StringComparison.OrdinalIgnoreCase)
                ? NetworkInterfaceJson()
                : path.Contains("/Microsoft.Network/publicIPAddresses/", StringComparison.OrdinalIgnoreCase)
                    ? PublicIpJson()
                    : path.Contains("/Microsoft.Network/virtualNetworks/", StringComparison.OrdinalIgnoreCase)
                        ? ProvisionedJson(VirtualNetworkId, SiblingTags("virtual-network"))
                        : ProvisionedJson(ResourceGroupId, SiblingTags("resource-group"));
    }

    /// <summary>Creates a host through the full operation path and hands back the resource it produced.</summary>
    internal async Task<ProvisionedResource> CreateAsync(ProvisioningRequest? request = null)
    {
        RouteSuccessfulCreate();

        var provisioner = Provisioner();
        var spec = AzureVirtualMachineProvisioner.BuildSpec(request ?? PalworldVmRequest());

        return await provisioner.CreateOperation(spec).CreateAsync();
    }
}
