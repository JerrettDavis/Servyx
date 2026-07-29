using System.Globalization;
using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Azure.Provisioning;

namespace Servyx.Infrastructure.Azure.Tests.Provisioning;

/// <summary>
/// The shared setup for the Azure Container Instances suite: one substituted Azure (token service and ARM),
/// one in-memory secret store holding <em>two</em> credentials, and one provisioner wired to both.
/// </summary>
/// <remarks>
/// <para>
/// The second credential is the structural difference from <see cref="AzureScenario"/> and is the whole
/// reason this scenario exists separately. The VM adapter has one secret — the service principal's client
/// secret, resolved inside <c>AzureArmApiClient</c>. This adapter has that one <em>and</em> the Azure Files
/// storage account key, which ACI requires as a literal in the container group's ARM body because it does
/// not support managed identity for SMB mounts. Counting resolutions of each, separately, is how the suite
/// proves the key is held as a locator and materialised exactly once, at the moment of use.
/// </para>
/// <para>
/// Nothing here opens a socket. A test that expects <em>no</em> request proves it by asserting
/// <see cref="AzureArmApiDouble.Requests"/> is empty, which is a stronger claim than "the call failed".
/// </para>
/// </remarks>
internal sealed class AzureContainerInstanceScenario
{
    /// <summary>The Azure Files storage account key. Deliberately distinctive so a leak anywhere is findable.</summary>
    internal const string StorageAccountKey =
        "azstorekey_v1_TESTKEY_must_only_ever_appear_in_the_container_group_put_body";

    /// <summary>The client secret. Must only ever appear in a token-exchange form body.</summary>
    internal const string ClientSecret = AzureScenario.ClientSecret;

    /// <summary>The access token the substituted token service issues.</summary>
    internal const string AccessToken = AzureScenario.AccessToken;

    internal const string TenantId = AzureScenario.TenantId;
    internal const string ClientId = AzureScenario.ClientId;
    internal const string SubscriptionId = AzureScenario.SubscriptionId;

    /// <summary>The resource group the container group lives in. Pre-existing; this adapter never creates one.</summary>
    internal const string ResourceGroup = "rg-servyx-aci";

    internal const string InstanceId = "srv-aci-0001";
    internal const string JobId = "job-aci-7";
    internal const string ConnectorId = "conn-aci-1";

    /// <summary>The container group's name at the provider.</summary>
    internal const string GroupName = "palworld-aci";

    /// <summary>The ARM location the container group is created in.</summary>
    internal const string Region = "eastus";

    /// <summary>The OCI image the container runs. Note: not a four-part Azure image URN.</summary>
    internal const string Image = "docker.io/thijsvanloef/palworld-server-docker:latest";

    /// <summary>The pre-existing storage account holding the share. Never created or destroyed by Servyx.</summary>
    internal const string StorageAccountName = "servyxpalworlddata";

    /// <summary>The pre-existing Azure Files share mounted into the container.</summary>
    internal const string FileShareName = "palworld-saves";

    /// <summary>Where the share is mounted inside the container.</summary>
    internal const string MountPath = "/palworld";

    /// <summary>The public IPv4 the substituted ACI reports for the container group.</summary>
    internal const string PublicIp = "203.0.113.42";

    /// <summary>The vCPU allocation these tests provision.</summary>
    internal const decimal Cpu = 2m;

    /// <summary>The memory allocation, in GB, these tests provision.</summary>
    internal const decimal MemoryInGb = 4m;

    /// <summary>The URN the Azure service principal's client secret lives at.</summary>
    internal static SecretUrn ClientSecretUrn { get; } = AzureScenario.ClientSecretUrn;

    /// <summary>The URN the Azure Files storage account key lives at. A locator, never the key.</summary>
    internal static SecretUrn StorageKeyUrn { get; } =
        SecretUrn.Create("connector", "conn-aci-1", "api", "storage-account-key");

    internal AzureArmApiDouble Api { get; } = new();

    internal RecordingSecretStore Secrets { get; } = new();

    /// <summary>The ARM id of the resource group. Referenced, never written.</summary>
    internal const string ResourceGroupId =
        "/subscriptions/" + SubscriptionId + "/resourceGroups/" + ResourceGroup;

    /// <summary>The ARM id of the container group — the only resource this adapter ever creates.</summary>
    internal const string GroupId =
        ResourceGroupId + "/providers/Microsoft.ContainerInstance/containerGroups/" + GroupName;

    /// <summary>The ARM id of a virtual machine in the same subscription, used to prove the sweep is narrow.</summary>
    internal const string ForeignVmId =
        ResourceGroupId + "/providers/Microsoft.Compute/virtualMachines/some-other-host";

    /// <summary>The ARM id of the storage account. Present only so tests can assert it is never requested.</summary>
    internal const string StorageAccountId =
        ResourceGroupId + "/providers/Microsoft.Storage/storageAccounts/" + StorageAccountName;

    /// <summary>Builds a provisioner over this scenario's API double and secret store.</summary>
    /// <param name="withClientSecret">Whether the service principal's client secret is present in the store.</param>
    /// <param name="withStorageKey">Whether the Azure Files storage account key is present in the store.</param>
    /// <param name="pollAttempts">How many address/provisioning/deletion polls before giving up.</param>
    internal AzureContainerInstanceProvisioner Provisioner(
        bool withClientSecret = true,
        bool withStorageKey = true,
        int pollAttempts = 3)
    {
        if (withClientSecret)
        {
            Secrets.Put(ClientSecretUrn, ClientSecret);
        }

        if (withStorageKey)
        {
            Secrets.Put(StorageKeyUrn, StorageAccountKey);
        }

        return new AzureContainerInstanceProvisioner(
            Api.Client(),
            Secrets,
            new AzureServicePrincipal(TenantId, ClientId, ClientSecretUrn),
            SubscriptionId,
            timeProvider: TimeProvider.System,
            pollInterval: TimeSpan.Zero,
            pollAttempts: pollAttempts);
    }

    /// <summary>A provisioning request for a Palworld container group with the mandatory mount configured.</summary>
    internal static ProvisioningRequest PalworldRequest(IReadOnlyDictionary<string, string>? overrides = null)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instanceId"] = InstanceId,
            ["jobId"] = JobId,
            ["connectorId"] = ConnectorId,
            ["name"] = GroupName,
            ["resourceGroup"] = ResourceGroup,
            ["region"] = Region,
            ["image"] = Image,
            ["storageAccount"] = StorageAccountName,
            ["fileShare"] = FileShareName,
            ["storageAccountKeyUrn"] = StorageKeyUrn.Value,
            ["mountPath"] = MountPath,
            ["cpu"] = Cpu.ToString(CultureInfo.InvariantCulture),
            ["memory"] = MemoryInGb.ToString(CultureInfo.InvariantCulture),
            ["ingress:8211/udp"] = string.Empty,
            ["ingress:25575/tcp"] = string.Empty,
        };

        foreach (var pair in overrides ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (pair.Value.Length == 0 && parameters.ContainsKey(pair.Key) && !pair.Key.StartsWith("ingress:", StringComparison.Ordinal))
            {
                parameters.Remove(pair.Key);
                continue;
            }

            parameters[pair.Key] = pair.Value;
        }

        return new ProvisioningRequest("palworld", "azure-container-instance", ConnectorId, parameters);
    }

    /// <summary>The spec the request above produces, for tests that want the typed overloads.</summary>
    internal static AzureContainerGroupSpec Spec() => AzureContainerInstanceProvisioner.BuildSpec(PalworldRequest());

    /// <summary>The canonical Servyx tags stamped on a container group, including the storage pointers.</summary>
    internal static IReadOnlyDictionary<string, string> CanonicalTags { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = InstanceId,
            ["servyx.job-id"] = JobId,
            ["servyx.connector-id"] = ConnectorId,
            ["servyx.role"] = "container-group",
            ["servyx.azure-resource-group"] = ResourceGroup,
            ["servyx.azure-storage-account"] = StorageAccountName,
            ["servyx.azure-file-share"] = FileShareName,
        };

    /// <summary>Renders a tag dictionary as an ARM <c>tags</c> object.</summary>
    internal static string TagsJson(IReadOnlyDictionary<string, string>? tags) =>
        "{" + string.Join(",", (tags ?? CanonicalTags).Select(t => $"\"{t.Key}\":\"{t.Value}\"")) + "}";

    // Assembled by concatenation rather than with raw interpolated literals, for the same reason
    // AzureScenario does it: ARM payloads end in runs of '}' that a $$"""...""" literal misreads.

    /// <summary>
    /// The FQDN ARM reports for a group that was provisioned with a <c>dnsNameLabel</c>. The address a
    /// control channel is pinned to, and the only one that survives the restart that moves the IP.
    /// </summary>
    internal const string Fqdn = "palworld.eastus.azurecontainer.io";

    /// <summary>A container group as ARM reports it.</summary>
    /// <param name="ip">The public IP, or null for a group ARM has not allocated an address for yet.</param>
    /// <param name="tags">The group's tags; defaults to <see cref="CanonicalTags"/>.</param>
    /// <param name="provisioningState">ARM's <c>provisioningState</c>.</param>
    /// <param name="id">The ARM resource id.</param>
    /// <param name="fqdn">
    /// The DNS name ARM assigned from the group's <c>dnsNameLabel</c>. Null models a group provisioned
    /// without one — which has an IP that works right now and moves on the next restart, and is therefore
    /// the case that separates a durable control address from an ephemeral one.
    /// </param>
    internal static string GroupJson(
        string? ip = PublicIp,
        IReadOnlyDictionary<string, string>? tags = null,
        string provisioningState = "Succeeded",
        string id = GroupId,
        string? fqdn = Fqdn) =>
        "{\"id\":\"" + id + "\",\"name\":\"" + GroupName + "\",\"location\":\"" + Region + "\","
        + "\"tags\":" + TagsJson(tags) + ","
        + "\"properties\":{"
        + "\"provisioningState\":\"" + provisioningState + "\","
        + (ip is null
            ? "\"ipAddress\":{\"type\":\"Public\"},"
            : "\"ipAddress\":{\"type\":\"Public\",\"ip\":\"" + ip + "\""
                + (fqdn is null ? string.Empty : ",\"fqdn\":\"" + fqdn + "\"") + "},")
        + "\"volumes\":[{\"name\":\"servyx-data\",\"azureFile\":{\"shareName\":\"" + FileShareName
        + "\",\"storageAccountName\":\"" + StorageAccountName + "\"}}],"
        + "\"containers\":[{\"name\":\"" + GroupName + "\",\"properties\":{"
        + "\"image\":\"" + Image + "\","
        + "\"resources\":{\"requests\":{\"cpu\":" + Cpu.ToString(CultureInfo.InvariantCulture)
        + ",\"memoryInGB\":" + MemoryInGb.ToString(CultureInfo.InvariantCulture) + "}}}}]"
        + "}}";

    /// <summary>An ARM tag-sweep page.</summary>
    internal static string SweepJson(params string[] resources) =>
        "{\"value\":[" + string.Join(",", resources) + "]}";

    /// <summary>One row of an ARM tag sweep.</summary>
    internal static string SweepRow(
        string id,
        string type,
        string location = Region,
        IReadOnlyDictionary<string, string>? tags = null) =>
        "{\"id\":\"" + id + "\",\"name\":\"n\",\"type\":\"" + type + "\",\"location\":\"" + location + "\","
        + "\"tags\":" + TagsJson(tags) + "}";

    /// <summary>
    /// The default responder: answer the token exchange, then answer any container-group call with
    /// <paramref name="groupJson"/>. Anything else fails the test, which is how "the adapter made a request
    /// it should not have" is caught rather than tolerated.
    /// </summary>
    internal void RespondWithGroup(string? groupJson = null, HttpStatusCode putStatus = HttpStatusCode.Created)
    {
        var payload = groupJson ?? GroupJson();

        Api.Responder = request =>
        {
            if (request.IsTokenExchange)
            {
                return AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.TokenJson());
            }

            if (request.Uri.AbsolutePath.Contains("/containerGroups/", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Method == HttpMethod.Delete)
                {
                    return AzureArmApiDouble.Empty(HttpStatusCode.OK);
                }

                return AzureArmApiDouble.Json(
                    request.Method == HttpMethod.Put ? putStatus : HttpStatusCode.OK,
                    payload);
            }

            throw new InvalidOperationException(
                $"The container-instance adapter made an unexpected {request.Method} request to '{request.Uri}'. "
                + "It is only ever supposed to touch Microsoft.ContainerInstance/containerGroups - never "
                + "Microsoft.Storage, and never a resource group write.");
        };
    }
}
