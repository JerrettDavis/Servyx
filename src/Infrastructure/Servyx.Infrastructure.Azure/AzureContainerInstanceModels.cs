using System.Text.Json.Serialization;

namespace Servyx.Infrastructure.Azure;

// ---------------------------------------------------------------------------------------------------------
// Requests — the body of PUT .../Microsoft.ContainerInstance/containerGroups/{name}
// ---------------------------------------------------------------------------------------------------------

/// <summary>The body sent to <c>PUT .../containerGroups/{name}</c>.</summary>
/// <remarks>
/// <para>
/// One PUT creates the whole thing: the container, its public address, and its mounted Azure Files volume.
/// That is the structural contrast with the virtual-machine adapter's five-write sequence, and it removes
/// most of the orphan surface with it — there is no window between two writes in which one billable resource
/// exists and another does not, because there is only one write.
/// </para>
/// <para>
/// It does <em>not</em> remove the orphan question, it relocates it: the storage account backing
/// <see cref="ArmAzureFileVolumeRequest"/> is a separate billable resource with an independent lifetime,
/// which this adapter neither creates nor destroys. See <c>AzureContainerInstanceProvisioner</c>'s remarks.
/// </para>
/// </remarks>
internal sealed class ArmContainerGroupRequest
{
    [JsonPropertyName("location")]
    public required string Location { get; init; }

    [JsonPropertyName("tags")]
    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    [JsonPropertyName("properties")]
    public required ArmContainerGroupRequestProperties Properties { get; init; }
}

/// <summary>The <c>properties</c> of <see cref="ArmContainerGroupRequest"/>.</summary>
internal sealed class ArmContainerGroupRequestProperties
{
    [JsonPropertyName("osType")]
    public required string OsType { get; init; }

    [JsonPropertyName("restartPolicy")]
    public required string RestartPolicy { get; init; }

    [JsonPropertyName("containers")]
    public required IReadOnlyList<ArmContainerRequest> Containers { get; init; }

    /// <summary>
    /// The group's volumes. Never empty in this adapter: an Azure Files mount is mandatory, and
    /// <c>AzureFileShareMount</c> makes an unmounted deployment unrepresentable in the spec type.
    /// </summary>
    [JsonPropertyName("volumes")]
    public required IReadOnlyList<ArmVolumeRequest> Volumes { get; init; }

    [JsonPropertyName("ipAddress")]
    public required ArmContainerGroupIpAddressRequest IpAddress { get; init; }
}

/// <summary>One container within a container group.</summary>
internal sealed class ArmContainerRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("properties")]
    public required ArmContainerRequestProperties Properties { get; init; }
}

/// <summary>The <c>properties</c> of <see cref="ArmContainerRequest"/>.</summary>
internal sealed class ArmContainerRequestProperties
{
    [JsonPropertyName("image")]
    public required string Image { get; init; }

    [JsonPropertyName("resources")]
    public required ArmContainerResourcesRequest Resources { get; init; }

    [JsonPropertyName("ports")]
    public required IReadOnlyList<ArmContainerPort> Ports { get; init; }

    [JsonPropertyName("environmentVariables")]
    public required IReadOnlyList<ArmEnvironmentVariable> EnvironmentVariables { get; init; }

    [JsonPropertyName("volumeMounts")]
    public required IReadOnlyList<ArmVolumeMount> VolumeMounts { get; init; }
}

/// <summary>A container's requested compute allocation. ACI bills on exactly these two numbers.</summary>
internal sealed class ArmContainerResourcesRequest
{
    [JsonPropertyName("requests")]
    public required ArmResourceRequests Requests { get; init; }
}

/// <summary>The vCPU and memory a container group reserves — and the two meters it is billed on, per second.</summary>
internal sealed class ArmResourceRequests
{
    [JsonPropertyName("cpu")]
    public required double Cpu { get; init; }

    [JsonPropertyName("memoryInGB")]
    public required double MemoryInGb { get; init; }
}

/// <summary>A published port. ACI has no source-address filter, so there is nowhere for a CIDR to go.</summary>
internal sealed class ArmContainerPort
{
    [JsonPropertyName("port")]
    public required int Port { get; init; }

    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }
}

/// <summary>One environment variable handed to the container. Never used to carry a secret by this adapter.</summary>
internal sealed class ArmEnvironmentVariable
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

/// <summary>Where a volume is mounted inside the container.</summary>
internal sealed class ArmVolumeMount
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("mountPath")]
    public required string MountPath { get; init; }

    [JsonPropertyName("readOnly")]
    public required bool ReadOnly { get; init; }
}

/// <summary>A volume attached to the container group.</summary>
internal sealed class ArmVolumeRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("azureFile")]
    public required ArmAzureFileVolumeRequest AzureFile { get; init; }
}

/// <summary>
/// An Azure Files share mounted onto the container group.
/// </summary>
/// <remarks>
/// <strong><see cref="StorageAccountKey"/> is the one literal credential this assembly puts in an ARM body,
/// and it is unavoidable.</strong> ACI does not support managed identity for SMB mounts, so the key is the
/// only authentication the platform accepts here. Servyx's rule is not "a credential never travels" — the
/// Azure client secret travels to the token service on every exchange — it is that a credential is
/// <em>held</em> only as a <c>SecretUrn</c> and resolved through <c>ISecretStore</c> at the moment of use.
/// That is exactly what happens: the spec carries a URN, this property is populated inside the create call
/// from a <c>SecretLease</c> that is disposed immediately afterwards, and the value reaches no tag, handle,
/// plan, plan hash, ledger row, or log. See <c>AzureContainerInstanceProvisioner</c>'s remarks.
/// </remarks>
internal sealed class ArmAzureFileVolumeRequest
{
    [JsonPropertyName("shareName")]
    public required string ShareName { get; init; }

    [JsonPropertyName("storageAccountName")]
    public required string StorageAccountName { get; init; }

    [JsonPropertyName("storageAccountKey")]
    public required string StorageAccountKey { get; init; }

    [JsonPropertyName("readOnly")]
    public required bool ReadOnly { get; init; }
}

/// <summary>The group's public address request.</summary>
/// <remarks>
/// ACI's own documentation warns that a container group's public IP may change when the group restarts, so
/// this is emphatically not a static address — see the provisioner's capability remarks, which is why
/// <c>ProvisioningCapabilities.StaticAddress</c> is absent.
/// </remarks>
internal sealed class ArmContainerGroupIpAddressRequest
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("ports")]
    public required IReadOnlyList<ArmContainerPort> Ports { get; init; }

    [JsonPropertyName("dnsNameLabel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DnsNameLabel { get; init; }
}

// ---------------------------------------------------------------------------------------------------------
// Responses
// ---------------------------------------------------------------------------------------------------------

/// <summary>A container group as ARM reports it. Only the fields Servyx actually reads are modelled.</summary>
internal sealed class ArmContainerGroup
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("location")]
    public string? Location { get; init; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; init; }

    [JsonPropertyName("properties")]
    public ArmContainerGroupProperties? Properties { get; init; }
}

/// <summary>The subset of a container group's <c>properties</c> Servyx reads.</summary>
/// <remarks>
/// Note what is absent and cannot be added: ACI reports no creation timestamp for a container group. A
/// container's <c>instanceView.currentState.startTime</c> is the time of its <em>current</em> start and moves
/// every time the group restarts, so it is not a creation time and is deliberately not read as one.
/// </remarks>
internal sealed class ArmContainerGroupProperties
{
    [JsonPropertyName("provisioningState")]
    public string? ProvisioningState { get; init; }

    [JsonPropertyName("ipAddress")]
    public ArmContainerGroupIpAddress? IpAddress { get; init; }

    [JsonPropertyName("containers")]
    public IReadOnlyList<ArmContainer>? Containers { get; init; }

    [JsonPropertyName("volumes")]
    public IReadOnlyList<ArmVolume>? Volumes { get; init; }
}

/// <summary>The group's assigned public address, once ACI has allocated one.</summary>
internal sealed class ArmContainerGroupIpAddress
{
    [JsonPropertyName("ip")]
    public string? Ip { get; init; }

    [JsonPropertyName("fqdn")]
    public string? Fqdn { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

/// <summary>One container as ARM reports it.</summary>
internal sealed class ArmContainer
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("properties")]
    public ArmContainerProperties? Properties { get; init; }
}

/// <summary>The subset of a container's <c>properties</c> Servyx reads.</summary>
internal sealed class ArmContainerProperties
{
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("resources")]
    public ArmContainerResources? Resources { get; init; }
}

/// <summary>A container's allocation as ARM reports it — the input to the compute-only cost figure.</summary>
internal sealed class ArmContainerResources
{
    [JsonPropertyName("requests")]
    public ArmResourceRequestsInfo? Requests { get; init; }
}

/// <summary>The reported vCPU/memory allocation.</summary>
internal sealed class ArmResourceRequestsInfo
{
    [JsonPropertyName("cpu")]
    public double? Cpu { get; init; }

    [JsonPropertyName("memoryInGB")]
    public double? MemoryInGb { get; init; }
}

/// <summary>A volume as ARM reports it. The storage account key is never echoed back, and is not modelled.</summary>
internal sealed class ArmVolume
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("azureFile")]
    public ArmAzureFileVolume? AzureFile { get; init; }
}

/// <summary>The share a reported volume is backed by.</summary>
internal sealed class ArmAzureFileVolume
{
    [JsonPropertyName("shareName")]
    public string? ShareName { get; init; }

    [JsonPropertyName("storageAccountName")]
    public string? StorageAccountName { get; init; }
}
