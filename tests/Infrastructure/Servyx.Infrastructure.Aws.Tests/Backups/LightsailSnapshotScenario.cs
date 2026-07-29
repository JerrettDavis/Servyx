using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using Servyx.Domain.Backups;
using Servyx.Infrastructure.Aws.Backups;
using Servyx.Infrastructure.Aws.Tests.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Backups;

/// <summary>One block storage disk an instance snapshot copied, as the substituted account holds it.</summary>
/// <param name="Name">The disk's Lightsail name.</param>
/// <param name="Path">The guest device path it was attached at.</param>
/// <param name="SizeInGb">Its allocated size.</param>
internal sealed record FakeSnapshotDisk(string Name, string Path, int SizeInGb);

/// <summary>One Lightsail instance snapshot as the substituted account holds it.</summary>
/// <remarks>
/// <see cref="States"/> is a queue rather than a single value because "was this reported successful on submission
/// alone?" is only an answerable question if a snapshot can be <c>pending</c> on one read and <c>available</c> on
/// the next. Every read of this snapshot advances the queue by one, stopping on the last entry.
/// </remarks>
internal sealed class FakeInstanceSnapshot
{
    internal required string Name { get; set; }

    internal string? FromInstanceName { get; set; } = LightsailSnapshotScenario.InstanceName;

    internal DateTimeOffset CreatedAt { get; set; }

    internal int? SizeInGb { get; set; } = 40;

    internal string? FromBundleId { get; set; } = "medium_3_0";

    internal string? FromBlueprintId { get; set; } = "amazon_linux_2023";

    internal bool IsFromAutoSnapshot { get; set; }

    internal string AvailabilityZone { get; set; } = LightsailSnapshotScenario.AvailabilityZone;

    internal Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

    internal List<FakeSnapshotDisk> Disks { get; } = [];

    internal Queue<string> States { get; init; } = new(["available"]);

    /// <summary>The state this read reports, advancing the queue unless it is on its last entry.</summary>
    internal string Advance() => States.Count > 1 ? States.Dequeue() : States.Peek();

    internal string Json(string state)
    {
        var disks = string.Join(
            ',',
            Disks.Select(d =>
                $$"""{"name":"{{d.Name}}","path":"{{d.Path}}","sizeInGb":{{d.SizeInGb.ToString(CultureInfo.InvariantCulture)}},"isSystemDisk":false,"resourceType":"Disk"}"""));

        var tags = string.Join(
            ',',
            Tags.OrderBy(t => t.Key, StringComparer.Ordinal)
                .Select(t => $$"""{"key":"{{t.Key}}","value":"{{t.Value}}"}"""));

        var blueprint = FromBlueprintId is null ? string.Empty : $"\"fromBlueprintId\": \"{FromBlueprintId}\",";
        var bundle = FromBundleId is null ? string.Empty : $"\"fromBundleId\": \"{FromBundleId}\",";
        var from = FromInstanceName is null ? string.Empty : $"\"fromInstanceName\": \"{FromInstanceName}\",";
        var size = SizeInGb is { } gb
            ? $"\"sizeInGb\": {gb.ToString(CultureInfo.InvariantCulture)},"
            : string.Empty;
        var createdAt = CreatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        return $$"""
        {
            "name": "{{Name}}",
            "arn": "arn:aws:lightsail:us-east-1:111122223333:InstanceSnapshot/{{Name}}",
            "createdAt": {{createdAt}}.0,
            "fromAttachedDisks": [{{disks}}],
            {{blueprint}}
            {{bundle}}
            {{from}}
            "isFromAutoSnapshot": {{(IsFromAutoSnapshot ? "true" : "false")}},
            "location": { "availabilityZone": "{{AvailabilityZone}}", "regionName": "{{LightsailSnapshotScenario.Region}}" },
            "resourceType": "InstanceSnapshot",
            {{size}}
            "state": "{{state}}",
            "tags": [{{tags}}]
        }
        """;
    }
}

/// <summary>
/// A substituted AWS Lightsail account with one instance and some instance snapshots in it: the seam that keeps
/// this whole suite offline.
/// </summary>
/// <remarks>
/// <para>
/// It answers the five Lightsail actions the snapshot adapter can reach — <c>GetInstance</c>,
/// <c>GetInstanceSnapshots</c>, <c>GetInstanceSnapshot</c>, <c>CreateInstanceSnapshot</c> and
/// <c>DeleteInstanceSnapshot</c> — from in-memory state, so a test can assert on what the account looks like
/// <em>afterwards</em> rather than only on what the adapter returned. Every request still passes through
/// <see cref="AwsApiDouble"/>, so <see cref="AwsApiDouble.Requests"/> is the record of exactly what the adapter
/// did, and a test claiming "no mutating request was issued" can prove it.
/// </para>
/// <para>
/// <strong><see cref="MutatingRequests"/> is an allow-list of reads, not a check on the HTTP verb.</strong> Every
/// Lightsail action is a <c>POST</c>, including the reads — the AWS JSON 1.1 protocol has no query-string
/// parameter shape at all — so the <c>Method != Get</c> test the EBS scenario uses would classify a plain listing
/// as a mutation. Naming the four read actions and treating everything else as mutating is the honest version of
/// the same assertion, and it fails closed: a new action nobody added here counts as a mutation.
/// </para>
/// <para>
/// The instance carries an attached block storage disk by default, because the central claim about this backup
/// shape is that an instance snapshot copies attached disks as well as the system disk, and a fixture with no
/// attached disk could not tell a correct description from one that quietly ignores them.
/// </para>
/// </remarks>
internal sealed class LightsailSnapshotScenario
{
    internal const string ServerId = "srv-0001";
    internal const string JobId = "job-42";
    internal const string ConnectorId = "conn-1";
    internal const string InstanceName = "palworld-01";
    internal const string OtherInstanceName = "someone-elses-box";
    internal const string Region = "us-east-1";
    internal const string AvailabilityZone = "us-east-1a";
    internal const string DataDiskName = "palworld-01-saves";
    internal const string DataDiskPath = "/dev/xvdf";
    internal const int SystemDiskGb = 40;
    internal const int DataDiskGb = 80;

    /// <summary>The Lightsail actions that read and cannot change anything.</summary>
    private static readonly HashSet<string> ReadActions = new(StringComparer.Ordinal)
    {
        "GetInstance",
        "GetInstances",
        "GetInstanceSnapshot",
        "GetInstanceSnapshots",
    };

    internal AwsApiDouble Api { get; } = new();

    internal RecordingSecretStore Secrets { get; } = new();

    internal FixedTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero));

    internal List<FakeInstanceSnapshot> Snapshots { get; } = [];

    /// <summary>Whether Lightsail still knows the instance at all.</summary>
    internal bool InstanceExists { get; set; } = true;

    /// <summary>The block storage disks the substituted instance has attached.</summary>
    internal List<FakeSnapshotDisk> AttachedDisks { get; } =
    [
        new(DataDiskName, DataDiskPath, DataDiskGb),
    ];

    /// <summary>The states a created snapshot's reads serve, in order.</summary>
    internal List<string> CreatedSnapshotStates { get; set; } = ["available"];

    /// <summary>Whether the tags <c>CreateInstanceSnapshot</c> was given actually show up on the snapshot.</summary>
    internal bool TagsStick { get; set; } = true;

    /// <summary>Whether a created snapshot ever becomes readable at all, or answers NotFoundException forever.</summary>
    internal bool CreatedSnapshotEverAppears { get; set; } = true;

    /// <summary>The status the <c>CreateInstanceSnapshot</c> operation record reports.</summary>
    internal string CreateOperationStatus { get; set; } = "Started";

    /// <summary>Whether <c>DeleteInstanceSnapshot</c> answers as Lightsail does for a snapshot that has vanished.</summary>
    internal bool DeleteAnswersNotFound { get; set; }

    /// <summary>How many snapshots one <c>GetInstanceSnapshots</c> page carries, so pagination is exercised.</summary>
    internal int PageSize { get; set; } = 100;

    /// <summary>Snapshot names the adapter asked Lightsail to delete, in order.</summary>
    internal List<string> Deleted { get; } = [];

    /// <summary>Every request that was not one of the four read actions — every request that could have changed the account.</summary>
    internal IReadOnlyList<RecordedRequest> MutatingRequests =>
        Api.Requests.Where(r => r.LightsailAction is not { } action || !ReadActions.Contains(action)).ToList();

    /// <summary>Every read of a single snapshot the adapter made, which is how a poll is counted.</summary>
    internal int SnapshotReads =>
        Api.Requests.Count(r => string.Equals(r.LightsailAction, "GetInstanceSnapshot", StringComparison.Ordinal));

    internal LightsailSnapshotScenario()
    {
        Secrets.Put(AwsScenario.AccessKeyIdUrn, AwsScenario.AccessKeyId);
        Secrets.Put(AwsScenario.SecretAccessKeyUrn, AwsScenario.SecretAccessKey);
        Api.Responder = Route;
    }

    /// <summary>Builds a provider over this substituted account.</summary>
    internal LightsailSnapshotBackupProvider Provider(
        RetentionPolicy? retention = null,
        int snapshotPollAttempts = 3,
        string serverId = ServerId,
        string instanceName = InstanceName) =>
        new(
            Api.Client(),
            Secrets,
            new AwsSigningIdentity(AwsScenario.AccessKeyIdUrn, AwsScenario.SecretAccessKeyUrn),
            Region,
            new StubContextSource(new LightsailSnapshotContext(
                serverId,
                instanceName,
                JobId,
                ConnectorId,
                retention ?? new RetentionPolicy(0, 3, 0))),
            Clock,
            snapshotPollInterval: TimeSpan.Zero,
            snapshotPollAttempts: snapshotPollAttempts);

    /// <summary>Adds one Servyx-owned snapshot carrying all four ownership marks.</summary>
    internal FakeInstanceSnapshot AddServyxSnapshot(
        DateTimeOffset takenAt,
        string serverId = ServerId,
        string instanceName = InstanceName)
    {
        var snapshot = new FakeInstanceSnapshot
        {
            Name = LightsailSnapshotOwnership.FormatSnapshotName(serverId, takenAt),
            FromInstanceName = instanceName,
            CreatedAt = takenAt,
            SizeInGb = SystemDiskGb,
        };

        snapshot.Tags["servyx.managed"] = "true";
        snapshot.Tags["servyx.instance-id"] = serverId;
        snapshot.Tags["servyx.job-id"] = JobId;
        snapshot.Tags["servyx.connector-id"] = ConnectorId;
        snapshot.Disks.AddRange(AttachedDisks);

        Snapshots.Add(snapshot);
        return snapshot;
    }

    /// <summary>Adds a snapshot Servyx did not create — the kind that must never be deleted.</summary>
    internal FakeInstanceSnapshot AddForeignSnapshot(
        string name,
        DateTimeOffset takenAt,
        string? fromInstanceName = InstanceName,
        bool isFromAutoSnapshot = false,
        params KeyValuePair<string, string>[] tags)
    {
        var snapshot = new FakeInstanceSnapshot
        {
            Name = name,
            FromInstanceName = fromInstanceName,
            CreatedAt = takenAt,
            SizeInGb = SystemDiskGb,
            IsFromAutoSnapshot = isFromAutoSnapshot,
        };

        foreach (var tag in tags)
        {
            snapshot.Tags[tag.Key] = tag.Value;
        }

        snapshot.Disks.AddRange(AttachedDisks);
        Snapshots.Add(snapshot);
        return snapshot;
    }

    /// <summary>The backup id of a Servyx snapshot taken at a given instant.</summary>
    internal static string BackupIdOf(DateTimeOffset takenAt, string serverId = ServerId) =>
        LightsailSnapshotBackupId.Format(serverId, LightsailSnapshotOwnership.FormatSnapshotName(serverId, takenAt));

    private HttpResponseMessage Route(RecordedRequest request)
    {
        var body = (request.Body is { Length: > 0 } text ? JsonNode.Parse(text) as JsonObject : null)
            ?? new JsonObject();

        return request.LightsailAction switch
        {
            "GetInstance" => GetInstance(),
            "GetInstanceSnapshots" => GetInstanceSnapshots(body),
            "GetInstanceSnapshot" => GetInstanceSnapshot(body),
            "CreateInstanceSnapshot" => CreateInstanceSnapshot(body),
            "DeleteInstanceSnapshot" => DeleteInstanceSnapshot(body),
            var action => throw new InvalidOperationException(
                $"The snapshot adapter made an unexpected Lightsail request with Target='{request.Target}' "
                + $"(action '{action}') to '{request.Uri}'."),
        };
    }

    private HttpResponseMessage GetInstance() =>
        InstanceExists
            ? AwsApiDouble.Json(
                HttpStatusCode.OK,
                $$"""
                {
                    "instance": {
                        "name": "{{InstanceName}}",
                        "blueprintId": "amazon_linux_2023",
                        "bundleId": "medium_3_0",
                        "createdAt": 1785000000.0,
                        "location": { "availabilityZone": "{{AvailabilityZone}}", "regionName": "{{Region}}" },
                        "state": { "code": 16, "name": "running" },
                        "username": "ec2-user",
                        "resourceType": "Instance",
                        "tags": []
                    }
                }
                """)
            : AwsApiDouble.Json(
                HttpStatusCode.BadRequest,
                LightsailScenario.ErrorJson(
                    LightsailScenario.NotFoundErrorType,
                    $"The instance name '{InstanceName}' does not exist"));

    private HttpResponseMessage GetInstanceSnapshots(JsonObject body)
    {
        var skip = body["pageToken"] is { } token
            ? int.Parse(token.GetValue<string>(), CultureInfo.InvariantCulture)
            : 0;

        var page = Snapshots.Skip(skip).Take(PageSize).ToList();
        var next = skip + page.Count < Snapshots.Count
            ? $""", "nextPageToken": "{skip + page.Count}" """
            : string.Empty;

        return AwsApiDouble.Json(
            HttpStatusCode.OK,
            $$"""
            { "instanceSnapshots": [{{string.Join(',', page.Select(s => s.Json(s.Advance())))}}]{{next}} }
            """);
    }

    private HttpResponseMessage GetInstanceSnapshot(JsonObject body)
    {
        var name = body["instanceSnapshotName"]?.GetValue<string>() ?? string.Empty;
        var snapshot = Snapshots.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

        return snapshot is null
            ? AwsApiDouble.Json(
                HttpStatusCode.BadRequest,
                LightsailScenario.ErrorJson(
                    LightsailScenario.NotFoundErrorType,
                    $"The instance snapshot name '{name}' does not exist"))
            : AwsApiDouble.Json(
                HttpStatusCode.OK,
                $$"""{ "instanceSnapshot": {{snapshot.Json(snapshot.Advance())}} }""");
    }

    private HttpResponseMessage CreateInstanceSnapshot(JsonObject body)
    {
        var name = body["instanceSnapshotName"]?.GetValue<string>()
            ?? throw new InvalidOperationException("CreateInstanceSnapshot was submitted with no snapshot name.");

        if (body["instanceName"]?.GetValue<string>() is not { Length: > 0 })
        {
            throw new InvalidOperationException("CreateInstanceSnapshot was submitted with no instance name.");
        }

        if (CreatedSnapshotEverAppears)
        {
            var snapshot = new FakeInstanceSnapshot
            {
                Name = name,
                FromInstanceName = InstanceName,
                CreatedAt = Clock.Now,
                SizeInGb = SystemDiskGb,
                States = new Queue<string>(CreatedSnapshotStates),
            };

            if (TagsStick && body["tags"] is JsonArray tags)
            {
                foreach (var node in tags)
                {
                    if (node is JsonObject tag && tag["key"]?.GetValue<string>() is { } key)
                    {
                        snapshot.Tags[key] = tag["value"]?.GetValue<string>() ?? string.Empty;
                    }
                }
            }

            snapshot.Disks.AddRange(AttachedDisks);
            Snapshots.Add(snapshot);
        }

        return AwsApiDouble.Json(
            HttpStatusCode.OK,
            $$"""
            {
                "operations": [
                    {
                        "id": "11111111-2222-3333-4444-555555555555",
                        "operationType": "CreateInstanceSnapshot",
                        "resourceName": "{{name}}",
                        "resourceType": "InstanceSnapshot",
                        "status": "{{CreateOperationStatus}}",
                        {{(string.Equals(CreateOperationStatus, "Failed", StringComparison.Ordinal)
                            ? "\"errorCode\": \"OperationFailure\", \"errorDetails\": \"the instance was not in a snapshottable state\","
                            : string.Empty)}}
                        "isTerminal": false
                    }
                ]
            }
            """);
    }

    private HttpResponseMessage DeleteInstanceSnapshot(JsonObject body)
    {
        var name = body["instanceSnapshotName"]?.GetValue<string>() ?? string.Empty;
        Deleted.Add(name);

        if (DeleteAnswersNotFound)
        {
            return AwsApiDouble.Json(
                HttpStatusCode.BadRequest,
                LightsailScenario.ErrorJson(
                    LightsailScenario.NotFoundErrorType,
                    $"The instance snapshot name '{name}' does not exist"));
        }

        Snapshots.RemoveAll(s => string.Equals(s.Name, name, StringComparison.Ordinal));

        return AwsApiDouble.Json(
            HttpStatusCode.OK,
            $$"""
            {
                "operations": [
                    {
                        "id": "66666666-7777-8888-9999-000000000000",
                        "operationType": "DeleteInstanceSnapshot",
                        "resourceName": "{{name}}",
                        "resourceType": "InstanceSnapshot",
                        "status": "Started",
                        "isTerminal": false
                    }
                ]
            }
            """);
    }

    /// <summary>The smallest honest context source: one server, one Lightsail instance.</summary>
    internal sealed class StubContextSource(LightsailSnapshotContext context) : ILightsailSnapshotContextSource
    {
        private readonly LightsailSnapshotContext _context = context;

        public Task<LightsailSnapshotContext?> GetAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(serverId, _context.ServerId, StringComparison.Ordinal)
                ? _context
                : null);
    }
}
