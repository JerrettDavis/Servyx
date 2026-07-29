using System.Globalization;
using System.Net;
using System.Text.Json;

using Servyx.Domain.Backups;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.DigitalOcean.Backups;
using Servyx.Infrastructure.DigitalOcean.Tests.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Tests.Backups;

/// <summary>A clock the suite controls, so restore-plan expiry and snapshot naming are deterministic.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>One snapshot as the substituted DigitalOcean account holds it.</summary>
internal sealed class FakeSnapshot
{
    internal required string Id { get; init; }

    internal required string Name { get; set; }

    internal required DateTimeOffset CreatedAt { get; set; }

    internal required string ResourceId { get; set; }

    internal string ResourceType { get; set; } = "droplet";

    internal decimal? SizeGigabytes { get; set; } = 12.5m;

    internal int? MinDiskSize { get; set; } = 80;

    internal List<string> Tags { get; } = [];

    internal string ToJson()
    {
        var tags = string.Join(",", Tags.Select(t => "\"" + t + "\""));
        var size = SizeGigabytes is { } gb
            ? gb.ToString(CultureInfo.InvariantCulture)
            : "null";
        var minDisk = MinDiskSize is { } min
            ? min.ToString(CultureInfo.InvariantCulture)
            : "null";

        return "{\"id\":\"" + Id + "\""
            + ",\"name\":\"" + Name + "\""
            + ",\"created_at\":\"" + CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\""
            + ",\"regions\":[\"nyc3\"]"
            + ",\"resource_id\":\"" + ResourceId + "\""
            + ",\"resource_type\":\"" + ResourceType + "\""
            + ",\"min_disk_size\":" + minDisk
            + ",\"size_gigabytes\":" + size
            + ",\"tags\":[" + tags + "]}";
    }
}

/// <summary>
/// A substituted DigitalOcean account with snapshots in it: the seam that keeps this whole suite offline.
/// </summary>
/// <remarks>
/// <para>
/// It answers the six endpoints the snapshot adapter can reach — list snapshots, delete a snapshot, submit a
/// droplet action, read an action, create a tag, tag a resource — from in-memory state, so a test can assert
/// on what the account looks like <em>afterwards</em> rather than only on what the adapter returned. Every
/// request still passes through <see cref="DigitalOceanApiDouble"/>, so
/// <see cref="DigitalOceanApiDouble.Requests"/> is the record of exactly what the adapter did, and a test
/// claiming "no mutating request was issued" can prove it.
/// </para>
/// <para>
/// Actions are served from a queue of statuses, so "the adapter polled an action that was still in progress
/// and only then saw it complete" is a real sequence here rather than a single canned answer — and the
/// snapshot only appears in the account at the moment the <c>completed</c> status is served, which is what
/// makes "was this reported successful on submission alone?" an answerable question.
/// </para>
/// </remarks>
internal sealed class SnapshotScenario
{
    internal const string ServerId = "srv-0001";
    internal const long DropletId = 3164494L;
    internal const long OtherDropletId = 9999999L;
    internal const string ApiToken = "dop_v1_TESTTOKEN_must_never_appear_anywhere_but_the_authorization_header";

    private long _nextActionId = 100;
    private readonly Dictionary<long, FakeAction> _actions = [];

    internal DigitalOceanApiDouble Api { get; } = new();

    internal RecordingSecretStore Secrets { get; } = new();

    internal List<FakeSnapshot> Snapshots { get; } = [];

    internal FixedTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero));

    internal static SecretUrn TokenUrn { get; } = SecretUrn.Create("global", "digitalocean", "api", "token");

    /// <summary>The statuses <c>GET /v2/actions/{id}</c> serves for a snapshot action, in order.</summary>
    internal List<string> SnapshotActionStatuses { get; set; } = ["completed"];

    /// <summary>The statuses <c>GET /v2/actions/{id}</c> serves for a restore action, in order.</summary>
    internal List<string> RestoreActionStatuses { get; set; } = ["completed"];

    /// <summary>Whether a completed snapshot action actually produces a snapshot in the account.</summary>
    internal bool SnapshotAppearsOnCompletion { get; set; } = true;

    /// <summary>The size DigitalOcean reports for a snapshot the adapter creates.</summary>
    internal decimal? CreatedSnapshotSizeGigabytes { get; set; } = 20m;

    /// <summary>The status <c>POST /v2/tags/{name}/resources</c> answers with.</summary>
    internal HttpStatusCode TagApplyStatus { get; set; } = HttpStatusCode.NoContent;

    /// <summary>Whether an applied tag actually shows up on the snapshot afterwards.</summary>
    internal bool TagsStick { get; set; } = true;

    /// <summary>The status <c>DELETE /v2/snapshots/{id}</c> answers with.</summary>
    internal HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.NoContent;

    /// <summary>Snapshot ids the adapter asked DigitalOcean to delete, in order.</summary>
    internal List<string> Deleted { get; } = [];

    /// <summary>Every request that was not a <c>GET</c>.</summary>
    internal IReadOnlyList<RecordedRequest> MutatingRequests =>
        Api.Requests.Where(r => r.Method != HttpMethod.Get).ToList();

    internal SnapshotScenario()
    {
        Secrets.Put(TokenUrn, ApiToken);
        Api.Responder = Route;
    }

    /// <summary>Builds a provider over this substituted account.</summary>
    internal DigitalOceanSnapshotBackupProvider Provider(
        RetentionPolicy? retention = null,
        int actionPollAttempts = 3,
        TimeSpan? restorePlanTtl = null,
        long dropletId = DropletId,
        string serverId = ServerId) =>
        new(
            Api.Client(),
            Secrets,
            TokenUrn,
            new StubContextSource(new DigitalOceanSnapshotContext(
                serverId,
                dropletId,
                retention ?? new RetentionPolicy(0, 3, 0))),
            Clock,
            actionPollInterval: TimeSpan.Zero,
            actionPollAttempts: actionPollAttempts,
            restorePlanTtl: restorePlanTtl);

    /// <summary>Adds a snapshot carrying every one of Servyx's four ownership marks.</summary>
    internal FakeSnapshot AddServyxSnapshot(string id, DateTimeOffset takenAt, string serverId = ServerId, long dropletId = DropletId)
    {
        var snapshot = new FakeSnapshot
        {
            Id = id,
            Name = SnapshotOwnership.FormatName(serverId, takenAt),
            CreatedAt = takenAt,
            ResourceId = dropletId.ToString(CultureInfo.InvariantCulture),
        };

        snapshot.Tags.Add(SnapshotOwnership.ManagedTag);
        snapshot.Tags.Add(SnapshotOwnership.InstanceTag(serverId));
        Snapshots.Add(snapshot);
        return snapshot;
    }

    /// <summary>Adds a snapshot Servyx did not create — the kind that must never be deleted.</summary>
    internal FakeSnapshot AddForeignSnapshot(
        string id,
        DateTimeOffset takenAt,
        string name = "taken-by-hand-before-the-update",
        long dropletId = DropletId,
        params string[] tags)
    {
        var snapshot = new FakeSnapshot
        {
            Id = id,
            Name = name,
            CreatedAt = takenAt,
            ResourceId = dropletId.ToString(CultureInfo.InvariantCulture),
        };

        snapshot.Tags.AddRange(tags);
        Snapshots.Add(snapshot);
        return snapshot;
    }

    private HttpResponseMessage Route(RecordedRequest request)
    {
        var path = request.Uri.AbsolutePath;

        if (request.Method == HttpMethod.Get && path == "/v2/snapshots")
        {
            return DigitalOceanApiDouble.Json(
                HttpStatusCode.OK,
                "{\"snapshots\":[" + string.Join(",", Snapshots.Select(s => s.ToJson())) + "],\"links\":{}}");
        }

        if (request.Method == HttpMethod.Delete && path.StartsWith("/v2/snapshots/", StringComparison.Ordinal))
        {
            var id = path["/v2/snapshots/".Length..];
            Deleted.Add(id);

            if (DeleteStatus == HttpStatusCode.NoContent)
            {
                Snapshots.RemoveAll(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            }

            return DigitalOceanApiDouble.Empty(DeleteStatus);
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/actions", StringComparison.Ordinal))
        {
            return SubmitAction(request);
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/v2/actions/", StringComparison.Ordinal))
        {
            return ReadAction(long.Parse(path["/v2/actions/".Length..], CultureInfo.InvariantCulture));
        }

        if (request.Method == HttpMethod.Post && path == "/v2/tags")
        {
            return DigitalOceanApiDouble.Json(HttpStatusCode.Created, "{\"tag\":{\"name\":\"servyx\"}}");
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/resources", StringComparison.Ordinal))
        {
            return ApplyTag(request, path);
        }

        throw new InvalidOperationException(
            $"The snapshot adapter made an unexpected {request.Method} request to '{request.Uri}'.");
    }

    private HttpResponseMessage SubmitAction(RecordedRequest request)
    {
        using var document = JsonDocument.Parse(request.Body ?? "{}");
        var type = document.RootElement.GetProperty("type").GetString() ?? string.Empty;

        var id = _nextActionId++;
        var statuses = type switch
        {
            "snapshot" => new Queue<string>(SnapshotActionStatuses),
            "restore" => new Queue<string>(RestoreActionStatuses),
            _ => throw new InvalidOperationException($"The snapshot adapter submitted an unexpected action type '{type}'."),
        };

        var name = type == "snapshot" ? document.RootElement.GetProperty("name").GetString() : null;
        _actions[id] = new FakeAction(type, statuses, name);

        return DigitalOceanApiDouble.Json(
            HttpStatusCode.Created,
            "{\"action\":{\"id\":" + id.ToString(CultureInfo.InvariantCulture)
            + ",\"status\":\"in-progress\",\"type\":\"" + type + "\"}}");
    }

    private HttpResponseMessage ReadAction(long actionId)
    {
        var action = _actions[actionId];
        var status = action.Statuses.Count > 1 ? action.Statuses.Dequeue() : action.Statuses.Peek();

        if (string.Equals(status, "completed", StringComparison.Ordinal)
            && !action.EffectApplied
            && string.Equals(action.Type, "snapshot", StringComparison.Ordinal))
        {
            action.EffectApplied = true;

            if (SnapshotAppearsOnCompletion)
            {
                Snapshots.Add(new FakeSnapshot
                {
                    Id = (800000000 + actionId).ToString(CultureInfo.InvariantCulture),
                    Name = action.SnapshotName ?? "unnamed",
                    CreatedAt = Clock.Now,
                    ResourceId = DropletId.ToString(CultureInfo.InvariantCulture),
                    SizeGigabytes = CreatedSnapshotSizeGigabytes,
                });
            }
        }

        return DigitalOceanApiDouble.Json(
            HttpStatusCode.OK,
            "{\"action\":{\"id\":" + actionId.ToString(CultureInfo.InvariantCulture)
            + ",\"status\":\"" + status + "\",\"type\":\"" + action.Type + "\""
            + (string.Equals(status, "errored", StringComparison.Ordinal)
                ? ",\"message\":\"the droplet was not in a snapshottable state\""
                : string.Empty)
            + "}}");
    }

    private HttpResponseMessage ApplyTag(RecordedRequest request, string path)
    {
        if (TagApplyStatus != HttpStatusCode.NoContent)
        {
            return DigitalOceanApiDouble.Json(
                TagApplyStatus,
                "{\"id\":\"not_found\",\"message\":\"The resource you were accessing could not be found.\"}");
        }

        var tagName = Uri.UnescapeDataString(path["/v2/tags/".Length..^"/resources".Length]);

        using var document = JsonDocument.Parse(request.Body ?? "{}");
        foreach (var resource in document.RootElement.GetProperty("resources").EnumerateArray())
        {
            var resourceId = resource.GetProperty("resource_id").GetString();
            var snapshot = Snapshots.FirstOrDefault(s => string.Equals(s.Id, resourceId, StringComparison.Ordinal));

            if (snapshot is not null && TagsStick && !snapshot.Tags.Contains(tagName, StringComparer.Ordinal))
            {
                snapshot.Tags.Add(tagName);
            }
        }

        return DigitalOceanApiDouble.Empty(HttpStatusCode.NoContent);
    }

    private sealed class FakeAction(string type, Queue<string> statuses, string? snapshotName)
    {
        internal string Type { get; } = type;

        internal Queue<string> Statuses { get; } = statuses;

        internal string? SnapshotName { get; } = snapshotName;

        internal bool EffectApplied { get; set; }
    }

    /// <summary>The smallest honest context source: one server, one droplet.</summary>
    internal sealed class StubContextSource(DigitalOceanSnapshotContext context) : IDigitalOceanSnapshotContextSource
    {
        private readonly DigitalOceanSnapshotContext _context = context;

        public Task<DigitalOceanSnapshotContext?> GetAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(serverId, _context.ServerId, StringComparison.Ordinal)
                ? _context
                : null);
    }
}
