using System.Globalization;
using System.Net;

using Servyx.Domain.Backups;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Aws.Backups;
using Servyx.Infrastructure.Aws.Tests.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Backups;

/// <summary>A clock the suite controls, so backup set naming and poll pacing are deterministic.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>One EBS snapshot as the substituted AWS account holds it.</summary>
/// <remarks>
/// <see cref="States"/> is a queue rather than a single value because "was this reported successful on
/// submission alone?" is only an answerable question if a snapshot can be <c>pending</c> on one read and
/// <c>completed</c> on the next. Every <c>DescribeSnapshots</c> that returns this snapshot advances the queue
/// by one, stopping on the last entry.
/// </remarks>
internal sealed class FakeSnapshot
{
    internal required string Id { get; init; }

    internal required string VolumeId { get; set; }

    internal DateTimeOffset StartTime { get; set; }

    internal int? VolumeSizeGib { get; set; } = 30;

    internal string? Description { get; set; }

    internal string? StateMessage { get; set; }

    internal Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

    internal Queue<string> States { get; init; } = new(["completed"]);

    /// <summary>The state this read reports, advancing the queue unless it is on its last entry.</summary>
    internal string Advance() => States.Count > 1 ? States.Dequeue() : States.Peek();

    internal string Xml(string state, string stateElement) =>
        "<item>"
        + $"<snapshotId>{Id}</snapshotId>"
        + $"<volumeId>{VolumeId}</volumeId>"
        + $"<{stateElement}>{state}</{stateElement}>"
        + (StateMessage is null ? string.Empty : $"<statusMessage>{StateMessage}</statusMessage>")
        + $"<startTime>{StartTime.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}</startTime>"
        + "<progress>50%</progress>"
        + "<ownerId>123456789012</ownerId>"
        + (VolumeSizeGib is { } gib ? $"<volumeSize>{gib.ToString(CultureInfo.InvariantCulture)}</volumeSize>" : string.Empty)
        + (Description is null ? string.Empty : $"<description>{Description}</description>")
        + "<tagSet>"
        + string.Join(
            string.Empty,
            Tags.OrderBy(t => t.Key, StringComparer.Ordinal)
                .Select(t => $"<item><key>{t.Key}</key><value>{t.Value}</value></item>"))
        + "</tagSet>"
        + "</item>";
}

/// <summary>One EBS volume attached to the substituted instance.</summary>
/// <param name="VolumeId">The volume's provider id.</param>
/// <param name="Device">The guest device it is attached at.</param>
/// <param name="SizeGib">Its allocated size.</param>
internal sealed record FakeAttachedVolume(string VolumeId, string Device, int SizeGib);

/// <summary>
/// A substituted AWS account with one multi-volume EC2 instance and some EBS snapshots in it: the seam that
/// keeps this whole suite offline.
/// </summary>
/// <remarks>
/// <para>
/// It answers the four EC2 actions the snapshot adapter can reach — <c>DescribeInstances</c>,
/// <c>CreateSnapshots</c>, <c>DescribeSnapshots</c> and <c>DeleteSnapshot</c> — from in-memory state, so a test
/// can assert on what the account looks like <em>afterwards</em> rather than only on what the adapter returned.
/// Every request still passes through <see cref="AwsApiDouble"/>, so <see cref="AwsApiDouble.Requests"/> is the
/// record of exactly what the adapter did, and a test claiming "no mutating request was issued" can prove it.
/// </para>
/// <para>
/// The instance carries <em>two</em> volumes by default — a root and a data volume — because the central claim
/// of this adapter is that a backup covers every attached volume, and a one-volume fixture could not tell a
/// correct implementation from one that silently captures only the root.
/// </para>
/// </remarks>
internal sealed class EbsSnapshotScenario
{
    internal const string ServerId = "srv-0001";
    internal const string JobId = "job-42";
    internal const string ConnectorId = "conn-1";
    internal const string Ec2InstanceId = "i-0abcdef1234567890";
    internal const string OtherEc2InstanceId = "i-0999888877776666";
    internal const string RootVolumeId = "vol-0root000000000a";
    internal const string DataVolumeId = "vol-0data000000000b";
    internal const string RootDevice = "/dev/xvda";
    internal const string DataDevice = "/dev/xvdf";
    internal const string Region = "us-east-1";
    internal const string AvailabilityZone = "us-east-1a";

    private int _nextCreatedSnapshot = 1;

    internal AwsApiDouble Api { get; } = new();

    internal RecordingSecretStore Secrets { get; } = new();

    internal FixedTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero));

    internal List<FakeSnapshot> Snapshots { get; } = [];

    /// <summary>The EBS volumes the substituted instance's block device mapping attaches.</summary>
    internal List<FakeAttachedVolume> AttachedVolumes { get; } =
    [
        new(RootVolumeId, RootDevice, 30),
        new(DataVolumeId, DataDevice, 100),
    ];

    /// <summary>Whether EC2 still knows the instance at all.</summary>
    internal bool InstanceExists { get; set; } = true;

    /// <summary>The lifecycle state EC2 reports for the instance.</summary>
    internal string InstanceState { get; set; } = "running";

    /// <summary>The states <c>DescribeSnapshots</c> serves for each snapshot a create produces, in order.</summary>
    internal List<string> CreatedSnapshotStates { get; set; } = ["completed"];

    /// <summary>How many of the instance's volumes <c>CreateSnapshots</c> actually covers.</summary>
    /// <remarks>
    /// <see langword="null"/> means all of them, which is what AWS does. Setting it lower reproduces a partial
    /// capture — the failure this adapter must refuse to report as a backup.
    /// </remarks>
    internal int? VolumesCoveredByCreate { get; set; }

    /// <summary>Whether the tags <c>CreateSnapshots</c> was given actually show up on the snapshots.</summary>
    internal bool TagsStick { get; set; } = true;

    /// <summary>Whether a poll of a created snapshot loses one of them from the account.</summary>
    internal bool SnapshotVanishesDuringPoll { get; set; }

    /// <summary>Whether <c>DescribeSnapshots</c> by id answers with EC2's not-found error.</summary>
    internal bool DescribeByIdAnswersNotFound { get; set; }

    /// <summary>Snapshot ids the adapter asked AWS to delete, in order.</summary>
    internal List<string> Deleted { get; } = [];

    /// <summary>The status <c>DeleteSnapshot</c> answers with.</summary>
    internal HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>Every request that was not a <c>GET</c> — every request that could have changed the account.</summary>
    internal IReadOnlyList<RecordedRequest> MutatingRequests =>
        Api.Requests.Where(r => r.Method != HttpMethod.Get).ToList();

    internal EbsSnapshotScenario()
    {
        Secrets.Put(AwsScenario.AccessKeyIdUrn, AwsScenario.AccessKeyId);
        Secrets.Put(AwsScenario.SecretAccessKeyUrn, AwsScenario.SecretAccessKey);
        Api.Responder = Route;
    }

    /// <summary>Builds a provider over this substituted account.</summary>
    internal EbsSnapshotBackupProvider Provider(
        RetentionPolicy? retention = null,
        int snapshotPollAttempts = 3,
        string serverId = ServerId,
        string ec2InstanceId = Ec2InstanceId) =>
        new(
            Api.Client(),
            Secrets,
            new AwsSigningIdentity(AwsScenario.AccessKeyIdUrn, AwsScenario.SecretAccessKeyUrn),
            Region,
            new StubContextSource(new EbsSnapshotContext(
                serverId,
                ec2InstanceId,
                JobId,
                ConnectorId,
                retention ?? new RetentionPolicy(0, 3, 0))),
            Clock,
            snapshotPollInterval: TimeSpan.Zero,
            snapshotPollAttempts: snapshotPollAttempts);

    /// <summary>Adds one complete Servyx-owned backup set: one snapshot per attached volume, all four marks.</summary>
    internal IReadOnlyList<FakeSnapshot> AddServyxSet(
        string idPrefix,
        DateTimeOffset takenAt,
        string serverId = ServerId,
        string ec2InstanceId = Ec2InstanceId)
    {
        var setName = EbsSnapshotOwnership.FormatSetName(serverId, takenAt);
        var created = new List<FakeSnapshot>();

        foreach (var volume in AttachedVolumes)
        {
            var snapshot = new FakeSnapshot
            {
                Id = idPrefix + "-" + volume.VolumeId[^1],
                VolumeId = volume.VolumeId,
                StartTime = takenAt,
                VolumeSizeGib = volume.SizeGib,
                Description = setName,
            };

            snapshot.Tags["servyx.managed"] = "true";
            snapshot.Tags["servyx.instance-id"] = serverId;
            snapshot.Tags["servyx.job-id"] = JobId;
            snapshot.Tags["servyx.connector-id"] = ConnectorId;
            snapshot.Tags[EbsSnapshotOwnership.SourceInstanceTag] = ec2InstanceId;
            snapshot.Tags[EbsSnapshotOwnership.SetTag] = setName;
            snapshot.Tags["Name"] = setName;

            Snapshots.Add(snapshot);
            created.Add(snapshot);
        }

        return created;
    }

    /// <summary>Adds a snapshot Servyx did not create — the kind that must never be deleted.</summary>
    internal FakeSnapshot AddForeignSnapshot(
        string id,
        DateTimeOffset takenAt,
        string volumeId = RootVolumeId,
        string? description = "taken by hand before the update",
        params KeyValuePair<string, string>[] tags)
    {
        var snapshot = new FakeSnapshot
        {
            Id = id,
            VolumeId = volumeId,
            StartTime = takenAt,
            Description = description,
        };

        foreach (var tag in tags)
        {
            snapshot.Tags[tag.Key] = tag.Value;
        }

        Snapshots.Add(snapshot);
        return snapshot;
    }

    /// <summary>The backup id of a Servyx set taken at a given instant.</summary>
    internal static string SetBackupId(DateTimeOffset takenAt, string serverId = ServerId) =>
        EbsSnapshotBackupId.Format(serverId, EbsSnapshotOwnership.FormatSetName(serverId, takenAt));

    private HttpResponseMessage Route(RecordedRequest request)
    {
        var parameters = Parameters(request);

        return parameters.GetValueOrDefault("Action") switch
        {
            "DescribeInstances" => DescribeInstances(),
            "DescribeSnapshots" => DescribeSnapshots(parameters),
            "CreateSnapshots" => CreateSnapshots(parameters),
            "DeleteSnapshot" => DeleteSnapshot(parameters),
            var action => throw new InvalidOperationException(
                $"The snapshot adapter made an unexpected {request.Method} request with Action='{action}' to "
                + $"'{request.Uri}'."),
        };
    }

    private HttpResponseMessage DescribeInstances()
    {
        if (!InstanceExists)
        {
            return AwsApiDouble.Xml(
                HttpStatusCode.BadRequest,
                AwsScenario.ErrorXml("InvalidInstanceID.NotFound", $"The instance ID '{Ec2InstanceId}' does not exist"));
        }

        var blockDevices = string.Join(
            string.Empty,
            AttachedVolumes.Select(v =>
                $"<item><deviceName>{v.Device}</deviceName>"
                + $"<ebs><volumeId>{v.VolumeId}</volumeId><status>attached</status>"
                + "<deleteOnTermination>true</deleteOnTermination></ebs></item>"));

        return AwsApiDouble.Xml(
            HttpStatusCode.OK,
            Envelope(
                "DescribeInstancesResponse",
                "<reservationSet><item><reservationId>r-0123456789abcdef0</reservationId><instancesSet><item>"
                + $"<instanceId>{Ec2InstanceId}</instanceId>"
                + "<imageId>ami-0abcdef1234567890</imageId>"
                + $"<instanceState><code>16</code><name>{InstanceState}</name></instanceState>"
                + "<instanceType>t3.medium</instanceType>"
                + "<launchTime>2026-07-01T10:00:00.000Z</launchTime>"
                + $"<placement><availabilityZone>{AvailabilityZone}</availabilityZone></placement>"
                + $"<blockDeviceMapping>{blockDevices}</blockDeviceMapping>"
                + "<tagSet></tagSet>"
                + "</item></instancesSet></item></reservationSet>"));
    }

    private HttpResponseMessage DescribeSnapshots(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.GetValueOrDefault("Owner.1") != "self")
        {
            throw new InvalidOperationException(
                "A snapshot listing was issued without Owner.1=self, which would return every public snapshot in "
                + "the region.");
        }

        var requestedIds = Indexed(parameters, "SnapshotId.").ToList();

        if (requestedIds.Count > 0 && DescribeByIdAnswersNotFound)
        {
            return AwsApiDouble.Xml(
                HttpStatusCode.BadRequest,
                AwsScenario.ErrorXml(
                    "InvalidSnapshot.NotFound",
                    $"The snapshot '{requestedIds[0]}' does not exist"));
        }

        List<FakeSnapshot> matched;

        if (requestedIds.Count > 0)
        {
            matched = Snapshots.Where(s => requestedIds.Contains(s.Id, StringComparer.Ordinal)).ToList();

            if (SnapshotVanishesDuringPoll && matched.Count > 1)
            {
                var vanished = matched[^1];
                Snapshots.Remove(vanished);
                matched.Remove(vanished);
            }
        }
        else
        {
            var filterName = parameters.GetValueOrDefault("Filter.1.Name");
            var filterValues = Indexed(parameters, "Filter.1.Value.").ToList();

            matched = filterName switch
            {
                "volume-id" => Snapshots
                    .Where(s => filterValues.Contains(s.VolumeId, StringComparer.Ordinal))
                    .ToList(),
                { } tagFilter when tagFilter.StartsWith("tag:", StringComparison.Ordinal) => Snapshots
                    .Where(s => s.Tags.TryGetValue(tagFilter["tag:".Length..], out var value)
                        && filterValues.Contains(value, StringComparer.Ordinal))
                    .ToList(),
                _ => throw new InvalidOperationException(
                    $"A snapshot listing was issued with an unexpected filter '{filterName}'."),
            };
        }

        return AwsApiDouble.Xml(
            HttpStatusCode.OK,
            Envelope(
                "DescribeSnapshotsResponse",
                "<snapshotSet>"
                + string.Join(string.Empty, matched.Select(s => s.Xml(s.Advance(), "status")))
                + "</snapshotSet>"));
    }

    private HttpResponseMessage CreateSnapshots(IReadOnlyDictionary<string, string> parameters)
    {
        var setName = parameters.GetValueOrDefault("Description")
            ?? throw new InvalidOperationException("CreateSnapshots was submitted with no Description.");

        if (parameters.GetValueOrDefault("TagSpecification.1.ResourceType") != "snapshot")
        {
            throw new InvalidOperationException("CreateSnapshots was submitted without a snapshot TagSpecification.");
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; ; i++)
        {
            var prefix = "TagSpecification.1.Tag." + i.ToString(CultureInfo.InvariantCulture) + ".";
            if (!parameters.TryGetValue(prefix + "Key", out var key))
            {
                break;
            }

            tags[key] = parameters.GetValueOrDefault(prefix + "Value") ?? string.Empty;
        }

        var covered = AttachedVolumes.Take(VolumesCoveredByCreate ?? AttachedVolumes.Count).ToList();
        var created = new List<FakeSnapshot>();

        foreach (var volume in covered)
        {
            var snapshot = new FakeSnapshot
            {
                Id = "snap-0created" + (_nextCreatedSnapshot++).ToString("0000", CultureInfo.InvariantCulture),
                VolumeId = volume.VolumeId,
                StartTime = Clock.Now,
                VolumeSizeGib = volume.SizeGib,
                Description = setName,
                States = new Queue<string>(CreatedSnapshotStates),
            };

            if (TagsStick)
            {
                foreach (var tag in tags)
                {
                    snapshot.Tags[tag.Key] = tag.Value;
                }
            }

            Snapshots.Add(snapshot);
            created.Add(snapshot);
        }

        return AwsApiDouble.Xml(
            HttpStatusCode.OK,
            Envelope(
                "CreateSnapshotsResponse",
                "<snapshotSet>"
                + string.Join(string.Empty, created.Select(s => s.Xml("pending", "state")))
                + "</snapshotSet>"));
    }

    private HttpResponseMessage DeleteSnapshot(IReadOnlyDictionary<string, string> parameters)
    {
        var id = parameters.GetValueOrDefault("SnapshotId") ?? string.Empty;
        Deleted.Add(id);

        if (DeleteStatus != HttpStatusCode.OK)
        {
            return AwsApiDouble.Xml(
                DeleteStatus,
                AwsScenario.ErrorXml("InvalidSnapshot.NotFound", $"The snapshot '{id}' does not exist"));
        }

        Snapshots.RemoveAll(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        return AwsApiDouble.Xml(HttpStatusCode.OK, Envelope("DeleteSnapshotResponse", "<return>true</return>"));
    }

    /// <summary>Every EC2 Query parameter on a request, wherever the client happened to put them.</summary>
    private static IReadOnlyDictionary<string, string> Parameters(RecordedRequest request)
    {
        var source = request.Method == HttpMethod.Get ? request.Uri.Query.TrimStart('?') : request.Body;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(source))
        {
            return parameters;
        }

        foreach (var part in source.Split('&'))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                parameters[Uri.UnescapeDataString(part[..separator])] =
                    Uri.UnescapeDataString(part[(separator + 1)..]);
            }
        }

        return parameters;
    }

    private static IEnumerable<string> Indexed(IReadOnlyDictionary<string, string> parameters, string prefix)
    {
        for (var i = 1; ; i++)
        {
            if (!parameters.TryGetValue(prefix + i.ToString(CultureInfo.InvariantCulture), out var value))
            {
                yield break;
            }

            yield return value;
        }
    }

    private static string Envelope(string root, string inner) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
        + $"<{root} xmlns=\"http://ec2.amazonaws.com/doc/2016-11-15/\">"
        + "<requestId>abcd1234-0000-0000-0000-000000000000</requestId>"
        + inner
        + $"</{root}>";

    /// <summary>The smallest honest context source: one server, one EC2 instance.</summary>
    internal sealed class StubContextSource(EbsSnapshotContext context) : IEbsSnapshotContextSource
    {
        private readonly EbsSnapshotContext _context = context;

        public Task<EbsSnapshotContext?> GetAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(serverId, _context.ServerId, StringComparison.Ordinal)
                ? _context
                : null);
    }
}
