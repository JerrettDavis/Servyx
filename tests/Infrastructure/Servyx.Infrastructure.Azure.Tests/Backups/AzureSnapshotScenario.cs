using System.Globalization;
using System.Net;

using Servyx.Domain.Backups;
using Servyx.Domain.Secrets;

using Servyx.Infrastructure.Azure.Backups;
using Servyx.Infrastructure.Azure.Tests.Provisioning;

namespace Servyx.Infrastructure.Azure.Tests.Backups;

/// <summary>A clock the suite controls, so backup set naming and poll pacing are deterministic.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>One managed disk attached to the substituted virtual machine.</summary>
/// <param name="Name">The disk's ARM name.</param>
/// <param name="Lun">Its LUN, or <see langword="null"/> for the OS disk.</param>
/// <param name="SizeGb">Its provisioned size.</param>
internal sealed record FakeManagedDisk(string Name, int? Lun, int SizeGb);

/// <summary>One <c>Microsoft.Compute/snapshots</c> resource as the substituted subscription holds it.</summary>
/// <remarks>
/// <para>
/// <see cref="ProvisioningStates"/> and <see cref="CompletionPercents"/> are queues rather than single values
/// because the two questions this suite has to be able to ask — "was this reported successful on submission
/// alone?" and "was it reported successful while its data was still copying?" — are only answerable if a
/// snapshot can read differently on consecutive GETs. Every read of this snapshot advances both queues by one,
/// stopping on the last entry.
/// </para>
/// <para>
/// The two queues are independent because Azure's two finish lines are independent: a snapshot reaches
/// <c>provisioningState: Succeeded</c> <em>before</em> its incremental background copy has finished, and a
/// fixture that could not express that state could not prove the adapter refuses to report it as a backup.
/// </para>
/// </remarks>
internal sealed class FakeAzureSnapshot
{
    internal required string Name { get; init; }

    internal required string SourceDiskId { get; set; }

    internal DateTimeOffset TimeCreated { get; set; }

    internal int? DiskSizeGb { get; set; } = 30;

    internal bool? Incremental { get; set; } = true;

    internal Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

    /// <summary>The provisioning states successive reads report, in order.</summary>
    internal Queue<string> ProvisioningStates { get; init; } = new(["Succeeded"]);

    /// <summary>The copy percentages successive reads report, in order. A null entry omits the member.</summary>
    internal Queue<double?> CompletionPercents { get; init; } = new([100d]);

    /// <summary>How many times this snapshot has been read back.</summary>
    internal int Reads { get; private set; }

    internal string Json(string resourceId)
    {
        Reads++;

        var state = ProvisioningStates.Count > 1 ? ProvisioningStates.Dequeue() : ProvisioningStates.Peek();
        var percent = CompletionPercents.Count > 1 ? CompletionPercents.Dequeue() : CompletionPercents.Peek();

        return "{\"id\":\"" + resourceId + "\",\"name\":\"" + Name + "\",\"location\":\""
            + AzureSnapshotScenario.Region + "\","
            + "\"tags\":" + AzureSnapshotScenario.TagsJson(Tags) + ","
            + "\"properties\":{"
            + "\"provisioningState\":\"" + state + "\","
            + "\"timeCreated\":\""
            + TimeCreated.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)
            + "\","
            + (DiskSizeGb is { } gb
                ? "\"diskSizeGB\":" + gb.ToString(CultureInfo.InvariantCulture) + ","
                : string.Empty)
            + (Incremental is { } incremental
                ? "\"incremental\":" + (incremental ? "true" : "false") + ","
                : string.Empty)
            + (percent is { } p
                ? "\"completionPercent\":" + p.ToString(CultureInfo.InvariantCulture) + ","
                : string.Empty)
            + "\"diskState\":\"Unattached\","
            + "\"creationData\":{\"createOption\":\"Copy\",\"sourceResourceId\":\"" + SourceDiskId + "\"}"
            + "}}";
    }
}

/// <summary>
/// A substituted Azure subscription with one multi-disk virtual machine and some managed-disk snapshots in it:
/// the seam that keeps this whole suite offline.
/// </summary>
/// <remarks>
/// <para>
/// It answers the five ARM operations the snapshot adapter can reach — read the VM, list the resource group's
/// snapshots, PUT a snapshot, GET a snapshot, DELETE a snapshot — from in-memory state, so a test can assert on
/// what the subscription looks like <em>afterwards</em> rather than only on what the adapter returned. Every
/// request still passes through <see cref="AzureArmApiDouble"/>, so a test claiming "no mutating request was
/// issued" can prove it rather than infer it.
/// </para>
/// <para>
/// The machine carries <em>two</em> managed disks by default — an OS disk and one data disk — because the two
/// central claims of this adapter are that a backup covers every attached managed disk and that a multi-disk
/// set is not a consistent point in time. A one-disk fixture could not tell a correct implementation from one
/// that silently captures only the OS disk, and could not exercise the consistency caveat at all.
/// </para>
/// </remarks>
internal sealed class AzureSnapshotScenario
{
    internal const string ServerId = "srv-0001";
    internal const string JobId = "job-42";
    internal const string ConnectorId = "conn-1";
    internal const string SubscriptionId = AzureScenario.SubscriptionId;
    internal const string ResourceGroup = "rg-servyx-palworld";
    internal const string VmName = "palworld-01";
    internal const string OtherVmName = "palworld-02";
    internal const string Region = "eastus";
    internal const string OsDiskName = "palworld-01-osdisk";
    internal const string DataDiskName = "palworld-01-data0";

    internal const string ResourceGroupId = "/subscriptions/" + SubscriptionId + "/resourceGroups/" + ResourceGroup;
    internal const string VmId = ResourceGroupId + "/providers/Microsoft.Compute/virtualMachines/" + VmName;

    private int _nextCreated = 1;

    internal AzureArmApiDouble Api { get; } = new();

    internal RecordingSecretStore Secrets { get; } = new();

    internal FixedTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero));

    internal List<FakeAzureSnapshot> Snapshots { get; } = [];

    /// <summary>The managed disks the substituted machine's storage profile attaches.</summary>
    internal List<FakeManagedDisk> Disks { get; } =
    [
        new(OsDiskName, null, 30),
        new(DataDiskName, 0, 128),
    ];

    /// <summary>Whether ARM still knows the virtual machine at all.</summary>
    internal bool MachineExists { get; set; } = true;

    /// <summary>Whether the machine's OS disk is reported as a managed disk. False reproduces an unmanaged VHD.</summary>
    internal bool OsDiskIsManaged { get; set; } = true;

    /// <summary>The provisioning states each read of a freshly created snapshot serves, in order.</summary>
    internal List<string> CreatedProvisioningStates { get; set; } = ["Succeeded"];

    /// <summary>The copy percentages each read of a freshly created snapshot serves, in order.</summary>
    internal List<double?> CreatedCompletionPercents { get; set; } = [100d];

    /// <summary>Whether the tags a snapshot PUT carried actually show up on the created resource.</summary>
    internal bool TagsStick { get; set; } = true;

    /// <summary>How many of the machine's disks a create is allowed to write before ARM refuses.</summary>
    /// <remarks><see langword="null"/> means all of them, which is what Azure does.</remarks>
    internal int? DisksCoveredByCreate { get; set; }

    /// <summary>Whether a created snapshot vanishes from the subscription before it can be read back.</summary>
    internal bool SnapshotVanishesAfterCreate { get; set; }

    /// <summary>The ARM names the adapter asked Azure to delete, in order.</summary>
    internal List<string> Deleted { get; } = [];

    /// <summary>The status a snapshot DELETE answers with.</summary>
    internal HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>Every ARM request that was not a <c>GET</c> — every request that could have changed anything.</summary>
    internal IReadOnlyList<RecordedRequest> MutatingRequests =>
        Api.ArmRequests.Where(r => r.Method != HttpMethod.Get).ToList();

    internal AzureSnapshotScenario()
    {
        Secrets.Put(AzureScenario.ClientSecretUrn, AzureScenario.ClientSecret);
        Api.Responder = Route;
    }

    /// <summary>Builds a provider over this substituted subscription.</summary>
    internal AzureSnapshotBackupProvider Provider(
        RetentionPolicy? retention = null,
        int snapshotPollAttempts = 3,
        string serverId = ServerId,
        string resourceGroup = ResourceGroup,
        string vmName = VmName) =>
        new(
            Api.Client(),
            Secrets,
            new AzureServicePrincipal(AzureScenario.TenantId, AzureScenario.ClientId, AzureScenario.ClientSecretUrn),
            SubscriptionId,
            new StubContextSource(new AzureSnapshotContext(
                serverId,
                resourceGroup,
                vmName,
                JobId,
                ConnectorId,
                retention ?? new RetentionPolicy(0, 3, 0))),
            Clock,
            snapshotPollInterval: TimeSpan.Zero,
            snapshotPollAttempts: snapshotPollAttempts);

    /// <summary>The ARM id of one of the machine's managed disks.</summary>
    internal static string DiskId(string diskName) =>
        ResourceGroupId + "/providers/Microsoft.Compute/disks/" + diskName;

    /// <summary>The ARM id of a snapshot in this scenario's resource group.</summary>
    internal static string SnapshotId(string name) =>
        ResourceGroupId + "/providers/Microsoft.Compute/snapshots/" + name;

    /// <summary>The backup id of a Servyx set taken at a given instant.</summary>
    internal static string SetBackupId(DateTimeOffset takenAt, string serverId = ServerId) =>
        AzureSnapshotBackupId.FormatSet(serverId, AzureSnapshotOwnership.FormatSetName(serverId, takenAt));

    /// <summary>Adds one complete Servyx-owned backup set: one snapshot per attached disk, all four marks.</summary>
    internal IReadOnlyList<FakeAzureSnapshot> AddServyxSet(
        DateTimeOffset takenAt,
        string serverId = ServerId,
        string resourceGroup = ResourceGroup,
        string vmName = VmName)
    {
        var setName = AzureSnapshotOwnership.FormatSetName(serverId, takenAt);
        var created = new List<FakeAzureSnapshot>();

        for (var index = 0; index < Disks.Count; index++)
        {
            var disk = Disks[index];
            var snapshot = new FakeAzureSnapshot
            {
                Name = AzureSnapshotOwnership.FormatMemberName(setName, index),
                SourceDiskId = DiskId(disk.Name),
                TimeCreated = takenAt,
                DiskSizeGb = disk.SizeGb,
            };

            foreach (var tag in ServyxSnapshotTags(setName, disk.Name, serverId, resourceGroup, vmName))
            {
                snapshot.Tags[tag.Key] = tag.Value;
            }

            Snapshots.Add(snapshot);
            created.Add(snapshot);
        }

        return created;
    }

    /// <summary>The exact tag set a Servyx-owned snapshot carries.</summary>
    internal static IReadOnlyDictionary<string, string> ServyxSnapshotTags(
        string setName,
        string diskName,
        string serverId = ServerId,
        string resourceGroup = ResourceGroup,
        string vmName = VmName) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = serverId,
            ["servyx.job-id"] = JobId,
            ["servyx.connector-id"] = ConnectorId,
            ["servyx.role"] = AzureSnapshotOwnership.RoleDiskSnapshot,
            ["servyx.azure-resource-group"] = resourceGroup,
            [AzureSnapshotOwnership.SourceVirtualMachineTag] =
                AzureSnapshotOwnership.FormatSourceVirtualMachine(resourceGroup, vmName),
            [AzureSnapshotOwnership.SourceDiskTag] = diskName,
            [AzureSnapshotOwnership.SetTag] = setName,
        };

    /// <summary>Adds a snapshot Servyx did not create — the kind that must never be deleted.</summary>
    internal FakeAzureSnapshot AddForeignSnapshot(
        string name,
        DateTimeOffset takenAt,
        string? sourceDiskName = OsDiskName,
        params KeyValuePair<string, string>[] tags)
    {
        var snapshot = new FakeAzureSnapshot
        {
            Name = name,
            SourceDiskId = DiskId(sourceDiskName ?? OsDiskName),
            TimeCreated = takenAt,
        };

        foreach (var tag in tags)
        {
            snapshot.Tags[tag.Key] = tag.Value;
        }

        Snapshots.Add(snapshot);
        return snapshot;
    }

    /// <summary>Renders a tag dictionary as an ARM <c>tags</c> object.</summary>
    internal static string TagsJson(IReadOnlyDictionary<string, string> tags) =>
        "{" + string.Join(",", tags.Select(t => $"\"{t.Key}\":\"{t.Value}\"")) + "}";

    // ---------------------------------------------------------------------------------------------------
    // Routing
    // ---------------------------------------------------------------------------------------------------

    private HttpResponseMessage Route(RecordedRequest request)
    {
        if (request.IsTokenExchange)
        {
            return AzureArmApiDouble.Json(HttpStatusCode.OK, AzureScenario.TokenJson());
        }

        var path = request.Uri.AbsolutePath;

        if (path.Contains("/virtualMachines/", StringComparison.Ordinal))
        {
            return VirtualMachine(path);
        }

        if (path.EndsWith("/providers/Microsoft.Compute/snapshots", StringComparison.Ordinal))
        {
            return ListSnapshots(path);
        }

        if (path.Contains("/providers/Microsoft.Compute/snapshots/", StringComparison.Ordinal))
        {
            var name = path[(path.LastIndexOf('/') + 1)..];

            return request.Method.Method switch
            {
                "PUT" => CreateSnapshot(name, request),
                "GET" => ReadSnapshot(name),
                "DELETE" => DeleteSnapshot(name),
                var method => throw new InvalidOperationException(
                    $"The snapshot adapter made an unexpected {method} request to '{request.Uri}'."),
            };
        }

        throw new InvalidOperationException(
            $"The snapshot adapter made an unexpected {request.Method} request to '{request.Uri}'.");
    }

    private HttpResponseMessage VirtualMachine(string path)
    {
        if (!MachineExists || !path.EndsWith("/" + VmName, StringComparison.Ordinal))
        {
            return AzureArmApiDouble.Json(
                HttpStatusCode.NotFound,
                "{\"error\":{\"code\":\"ResourceNotFound\",\"message\":\"The Resource was not found.\"}}");
        }

        var osDisk = Disks.FirstOrDefault(d => d.Lun is null);
        var dataDisks = Disks.Where(d => d.Lun is not null).ToList();

        var osJson = osDisk is null
            ? string.Empty
            : "\"osDisk\":{\"name\":\"" + osDisk.Name + "\",\"diskSizeGB\":"
              + osDisk.SizeGb.ToString(CultureInfo.InvariantCulture) + ",\"deleteOption\":\"Delete\""
              + (OsDiskIsManaged
                  ? ",\"managedDisk\":{\"id\":\"" + DiskId(osDisk.Name)
                    + "\",\"storageAccountType\":\"Premium_LRS\"}"
                  : ",\"vhd\":{\"uri\":\"https://legacy.blob.core.windows.net/vhds/" + osDisk.Name + ".vhd\"}")
              + "},";

        var dataJson = "\"dataDisks\":["
            + string.Join(
                ",",
                dataDisks.Select(d =>
                    "{\"name\":\"" + d.Name + "\",\"lun\":" + (d.Lun ?? 0).ToString(CultureInfo.InvariantCulture)
                    + ",\"diskSizeGB\":" + d.SizeGb.ToString(CultureInfo.InvariantCulture)
                    + ",\"managedDisk\":{\"id\":\"" + DiskId(d.Name)
                    + "\",\"storageAccountType\":\"Premium_LRS\"}}"))
            + "]";

        return AzureArmApiDouble.Json(
            HttpStatusCode.OK,
            "{\"id\":\"" + VmId + "\",\"name\":\"" + VmName + "\",\"location\":\"" + Region + "\","
            + "\"tags\":" + TagsJson(AzureScenario.CanonicalVmTags) + ","
            + "\"properties\":{\"provisioningState\":\"Succeeded\",\"storageProfile\":{"
            + osJson + dataJson + "}}}");
    }

    private HttpResponseMessage ListSnapshots(string path)
    {
        if (!path.Contains("/resourceGroups/" + ResourceGroup + "/", StringComparison.Ordinal))
        {
            // A resource group this scenario does not have holds no snapshots.
            return AzureArmApiDouble.Json(HttpStatusCode.OK, "{\"value\":[]}");
        }

        return AzureArmApiDouble.Json(
            HttpStatusCode.OK,
            "{\"value\":[" + string.Join(",", Snapshots.Select(s => s.Json(SnapshotId(s.Name)))) + "]}");
    }

    private HttpResponseMessage CreateSnapshot(string name, RecordedRequest request)
    {
        if (DisksCoveredByCreate is { } covered && _nextCreated > covered)
        {
            return AzureArmApiDouble.Json(
                HttpStatusCode.Conflict,
                "{\"error\":{\"code\":\"OperationNotAllowed\",\"message\":\"Snapshot quota exceeded.\"}}");
        }

        var body = request.Body ?? string.Empty;

        if (!body.Contains("\"incremental\":true", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The adapter wrote a snapshot without incremental=true. A FULL Azure snapshot is billed against "
                + "the disk's stored contents on every single capture rather than only the delta, which would make "
                + "a nightly backup cost many times what it should. Body: " + body);
        }

        var sourceDiskId = ExtractJsonString(body, "sourceResourceId")
            ?? throw new InvalidOperationException("A snapshot write named no sourceResourceId. Body: " + body);

        var diskName = sourceDiskId[(sourceDiskId.LastIndexOf('/') + 1)..];
        var disk = Disks.FirstOrDefault(d => string.Equals(d.Name, diskName, StringComparison.Ordinal));

        _nextCreated++;

        var snapshot = new FakeAzureSnapshot
        {
            Name = name,
            SourceDiskId = sourceDiskId,
            TimeCreated = Clock.Now,
            DiskSizeGb = disk?.SizeGb ?? 30,
            ProvisioningStates = new Queue<string>(CreatedProvisioningStates),
            CompletionPercents = new Queue<double?>(CreatedCompletionPercents),
        };

        if (TagsStick)
        {
            foreach (var tag in ExtractTags(body))
            {
                snapshot.Tags[tag.Key] = tag.Value;
            }
        }

        if (!SnapshotVanishesAfterCreate)
        {
            Snapshots.Add(snapshot);
        }

        // ARM answers a snapshot PUT with 201 and a non-terminal provisioning state: accepted, not finished.
        return AzureArmApiDouble.Json(
            HttpStatusCode.Created,
            "{\"id\":\"" + SnapshotId(name) + "\",\"name\":\"" + name
            + "\",\"properties\":{\"provisioningState\":\"Updating\"}}");
    }

    private HttpResponseMessage ReadSnapshot(string name)
    {
        var snapshot = Snapshots.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

        return snapshot is null
            ? AzureArmApiDouble.Json(
                HttpStatusCode.NotFound,
                "{\"error\":{\"code\":\"ResourceNotFound\",\"message\":\"The Resource was not found.\"}}")
            : AzureArmApiDouble.Json(HttpStatusCode.OK, snapshot.Json(SnapshotId(name)));
    }

    private HttpResponseMessage DeleteSnapshot(string name)
    {
        Deleted.Add(name);

        if (DeleteStatus == HttpStatusCode.NotFound)
        {
            return AzureArmApiDouble.Empty(HttpStatusCode.NotFound);
        }

        Snapshots.RemoveAll(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        return AzureArmApiDouble.Empty(DeleteStatus);
    }

    // ---------------------------------------------------------------------------------------------------
    // Tiny JSON readers, deliberately not a serializer: the fixture must read what the adapter really sent
    // ---------------------------------------------------------------------------------------------------

    private static string? ExtractJsonString(string body, string member)
    {
        var marker = "\"" + member + "\":\"";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = body.IndexOf('"', start);
        return end < 0 ? null : body[start..end];
    }

    private static IReadOnlyDictionary<string, string> ExtractTags(string body)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        var start = body.IndexOf("\"tags\":{", StringComparison.Ordinal);
        if (start < 0)
        {
            return tags;
        }

        start += "\"tags\":{".Length;
        var end = body.IndexOf('}', start);
        if (end < 0)
        {
            return tags;
        }

        foreach (var pair in body[start..end].Split(','))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2)
            {
                tags[parts[0].Trim().Trim('"')] = parts[1].Trim().Trim('"');
            }
        }

        return tags;
    }

    /// <summary>The smallest honest context source: one server, one virtual machine.</summary>
    internal sealed class StubContextSource(AzureSnapshotContext context) : IAzureSnapshotContextSource
    {
        private readonly AzureSnapshotContext _context = context;

        public Task<AzureSnapshotContext?> GetAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(serverId, _context.ServerId, StringComparison.Ordinal)
                ? _context
                : null);
    }
}
