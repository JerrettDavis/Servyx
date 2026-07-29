using System.Globalization;

using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;

using Servyx.Infrastructure.Azure.Provisioning;

namespace Servyx.Infrastructure.Azure.Backups;

/// <summary>
/// The one place that decides whether a managed-disk snapshot is Servyx's or somebody else's.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before widening anything here.</strong> An Azure resource group contains snapshots Servyx
/// did not create: taken by hand in the portal, taken by Azure Backup or a partner tool, taken by a
/// <c>az snapshot create</c> in somebody's script, or left over from a disk that no longer exists. Everything
/// this class does not positively recognise is <see cref="BackupOwnership.Foreign"/>, and a foreign snapshot is
/// listed and inspectable but is never deleted — under any value of <c>dryRun</c>.
/// </para>
/// <para>
/// <strong>Ownership is a conjunction of four independent marks, and every one must hold.</strong> A snapshot
/// is Servyx's only if its ARM <em>tags</em> carry all four of:
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="ManagedTag"/> = <c>true</c>, matched byte-for-byte through
/// <see cref="ServyxTagKeys.IsManaged"/> — the same key, the same value and the same exact-match rule the VM
/// orphan sweep uses, so a snapshot and a virtual machine agree on what "Servyx-managed" spells;
/// </description></item>
/// <item><description>
/// <see cref="InstanceIdTag"/> = the Servyx server being asked about — a snapshot belonging to a
/// <em>different</em> Servyx server is foreign to this one, which is what stops one server's retention deleting
/// another server's backups;
/// </description></item>
/// <item><description>
/// <see cref="SourceVirtualMachineTag"/> = <see cref="FormatSourceVirtualMachine"/> of the resource group and
/// ARM name of the machine the snapshot was taken from — the analogue of the EBS classifier's source-instance
/// mark, and the mark that binds a snapshot to a machine rather than only to a name;
/// </description></item>
/// <item><description>
/// <see cref="SetTag"/> parsing as <see cref="FormatSetName"/> produced it, for this exact server id — the mark
/// that makes a multi-disk capture one artifact rather than several.
/// </description></item>
/// </list>
/// <para>
/// <strong>Why the source machine is a resource group and a name, not an ARM resource id.</strong> An ARM tag
/// value is capped at 256 characters. A virtual machine's resource id is
/// <c>/subscriptions/{36}/resourceGroups/{up to 90}/providers/Microsoft.Compute/virtualMachines/{up to 64}</c>,
/// which reaches 265 in the worst case — so a mark built from it would be silently unrecordable for exactly the
/// machines with the longest names, and a snapshot Servyx could not mark is a snapshot Servyx can never prune.
/// The subscription is adapter state and is not in doubt, so the group and the name are sufficient and always
/// fit. This is the same reasoning <see cref="ServyxAzureTags.ResourceGroupTag"/> already applies to the VM's
/// sibling resources.
/// </para>
/// <para>
/// <strong>Why the source-machine mark is a tag and not a live attachment check.</strong> The obvious
/// alternative is "the snapshot's <c>creationData.sourceResourceId</c> is one of the disks currently attached
/// to the VM". That is wrong in the expensive direction: a data disk detached last week would flip every
/// snapshot ever taken of it to foreign, and Servyx never prunes what it cannot prove it owns, so those
/// snapshots would bill forever. A tag recorded at creation does not change when the machine's disks do. The
/// source disk id <em>is</em> read, but only to decide whether a snapshot Servyx did not create is nonetheless
/// a backup of this server's data and should be surfaced.
/// </para>
/// <para>
/// <strong>The failure directions are deliberately asymmetric.</strong> A snapshot Servyx took whose tags are
/// missing reads as foreign, and the consequence is that it is never pruned and bills per GB-month until a
/// human removes it. That is a cost bug. The opposite mistake — a hand-taken snapshot that happened to look
/// Servyx-shaped — would be an irreversible deletion of somebody's only copy. Four marks that must all hold
/// makes the second mistake require four coincidences at once and pushes every partial match into the first,
/// survivable direction. <see cref="AzureSnapshotBackupProvider.CreateAsync"/> refuses to report success when
/// it cannot verify all four afterwards, so the cost bug is loud rather than silent.
/// </para>
/// </remarks>
public static class AzureSnapshotOwnership
{
    /// <summary>The prefix every backup set name Servyx writes begins with.</summary>
    public const string SetNamePrefix = "servyx-snapshot-";

    /// <summary>The UTC timestamp format a Servyx set name ends with. Exactly 16 characters.</summary>
    public const string SetNameTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>The rendered length of <see cref="SetNameTimestampFormat"/>, e.g. <c>20260727T100000Z</c>.</summary>
    public const int SetNameTimestampLength = 16;

    /// <summary>The longest ARM name a <c>Microsoft.Compute/snapshots</c> resource may have.</summary>
    public const int MaxSnapshotNameLength = 80;

    /// <summary>
    /// The longest server id that can be carried in a set name and still leave room for a member suffix inside
    /// <see cref="MaxSnapshotNameLength"/>.
    /// </summary>
    /// <remarks>
    /// The arithmetic, because it is load-bearing rather than a round number: a member's ARM name is
    /// <see cref="SetNamePrefix"/> (16) + the server id + <c>-</c> (1) + the timestamp (16) + <c>-</c> (1) + a
    /// two-digit member index (2) = 36 + the server id. Forty leaves the longest possible name at 76 of the 80
    /// characters ARM allows. Shorter than the EBS adapter's 128 because that limit came from a tag value and
    /// this one comes from a resource name.
    /// </remarks>
    public const int MaxServerIdLength = 40;

    /// <summary>Mark 1: the key marking a snapshot as created and owned by Servyx — <c>servyx.managed</c>.</summary>
    public const string ManagedTag = ServyxAzureTags.ManagedTag;

    /// <summary>The only value <see cref="ManagedTag"/> is ever set to.</summary>
    public const string ManagedTagValue = ServyxAzureTags.ManagedTagValue;

    /// <summary>Mark 2: the key binding a snapshot to one Servyx server — <c>servyx.instance-id</c>.</summary>
    public const string InstanceIdTag = ServyxAzureTags.InstanceIdTag;

    /// <summary>Mark 3: the key recording which virtual machine a snapshot was taken from.</summary>
    /// <remarks>
    /// Distinct from <see cref="InstanceIdTag"/>, and the two are easy to confuse, so the spelling is
    /// deliberately unambiguous: <c>servyx.instance-id</c> is the <em>Servyx</em> server id (e.g.
    /// <c>srv-0001</c>) and <c>servyx.source-vm</c> is the <em>Azure</em> machine, as
    /// <c>{resourceGroup}/{vmName}</c>. Both must match, because one Servyx server can be re-provisioned onto a
    /// new virtual machine and the old machine's snapshots are not backups of the new one.
    /// </remarks>
    public const string SourceVirtualMachineTag = ServyxTagKeys.Prefix + "source-vm";

    /// <summary>Mark 4: the key carrying the backup set a snapshot belongs to.</summary>
    /// <remarks>
    /// This is what makes a multi-disk backup one artifact rather than several. Every snapshot written for one
    /// capture carries the same value here, so the set can be reassembled from a listing without relying on
    /// timestamps being close together — which matters far more here than it does on EBS, because Azure's
    /// snapshots are created by <em>separate</em> ARM operations at genuinely different instants.
    /// </remarks>
    public const string SetTag = ServyxTagKeys.Prefix + "snapshot-set";

    /// <summary>Descriptive: the ARM name of the managed disk a snapshot was taken from.</summary>
    /// <remarks>
    /// Not one of the marks, and never consulted by <see cref="Classify"/>. It exists so a human auditing the
    /// resource group can see what a snapshot is of without opening it, in the same spirit as
    /// <see cref="ServyxAzureTags.RoleTag"/>. The authoritative source-disk link is ARM's own
    /// <c>creationData.sourceResourceId</c>, which no tag can forge.
    /// </remarks>
    public const string SourceDiskTag = ServyxTagKeys.Prefix + "source-disk";

    /// <summary>The <see cref="ServyxAzureTags.RoleTag"/> value stamped on a snapshot.</summary>
    public const string RoleDiskSnapshot = "disk-snapshot";

    /// <summary>Whether <paramref name="serverId"/> can carry a Servyx managed-disk snapshot at all.</summary>
    /// <remarks>
    /// Narrower than <see cref="ServyxAzureTags.IsTaggableValue"/>, and for a reason that has nothing to do with
    /// tags: ARM tag values accept almost anything, but the snapshot's own <em>resource name</em> carries the
    /// server id too, and an ARM disk-provider resource name admits only letters, digits, <c>-</c>, <c>_</c> and
    /// <c>.</c>. So the resource name is the binding constraint, exactly the inverse of the DigitalOcean
    /// adapter, where the tag encoding was.
    /// </remarks>
    public static bool IsSupportedServerId(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId) || serverId.Length > MaxServerIdLength)
        {
            return false;
        }

        foreach (var c in serverId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds the backup set name Servyx gives one multi-disk capture of a machine.</summary>
    /// <param name="serverId">The Servyx server the set backs.</param>
    /// <param name="takenAt">When the capture was requested, in UTC.</param>
    /// <exception cref="ArgumentException"><paramref name="serverId"/> is not a supported server id.</exception>
    public static string FormatSetName(string serverId, DateTimeOffset takenAt)
    {
        if (!IsSupportedServerId(serverId))
        {
            throw new ArgumentException(
                $"Server id '{serverId}' cannot be carried in an Azure snapshot set name, so snapshots taken for it "
                + "could never be recognised as Servyx's afterwards — and unrecognisable snapshots bill per "
                + $"GB-month forever and are never pruned. Ids may be at most {MaxServerIdLength} characters of "
                + "letters, digits, '-', '_' and '.'.",
                nameof(serverId));
        }

        return SetNamePrefix
            + serverId
            + "-"
            + takenAt.UtcDateTime.ToString(SetNameTimestampFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads back a set name <see cref="FormatSetName"/> produced.</summary>
    /// <param name="setName">The value of the <see cref="SetTag"/> tag, as ARM reports it.</param>
    /// <param name="serverId">The server id encoded in the name.</param>
    /// <param name="takenAt">The UTC instant encoded in the name.</param>
    /// <returns><see langword="false"/> for any value this encoding did not produce.</returns>
    public static bool TryParseSetName(string? setName, out string serverId, out DateTimeOffset takenAt)
    {
        serverId = string.Empty;
        takenAt = default;

        if (setName is null || !setName.StartsWith(SetNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = setName[SetNamePrefix.Length..];
        if (remainder.Length < SetNameTimestampLength + 2 || remainder[^(SetNameTimestampLength + 1)] != '-')
        {
            return false;
        }

        var candidateServerId = remainder[..^(SetNameTimestampLength + 1)];
        if (!IsSupportedServerId(candidateServerId))
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                remainder[^SetNameTimestampLength..],
                SetNameTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        serverId = candidateServerId;
        takenAt = parsed;
        return true;
    }

    /// <summary>The ARM name of one member of a set.</summary>
    /// <param name="setName">The set name from <see cref="FormatSetName"/>.</param>
    /// <param name="index">The member's position in the capture, from zero.</param>
    /// <remarks>
    /// Zero-padded to two digits so the members of a set sort in capture order in the portal, and capped at
    /// ninety-nine because a virtual machine cannot carry that many data disks at any Azure size.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or exceeds 99.</exception>
    public static string FormatMemberName(string setName, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setName);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 99);

        return setName + "-" + index.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>Builds the <see cref="SourceVirtualMachineTag"/> value for one machine.</summary>
    /// <param name="resourceGroup">The resource group the machine lives in.</param>
    /// <param name="virtualMachineName">The machine's ARM name.</param>
    public static string FormatSourceVirtualMachine(string resourceGroup, string virtualMachineName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualMachineName);

        return resourceGroup + "/" + virtualMachineName;
    }

    /// <summary>
    /// Classifies one snapshot for one server. Returns <see cref="BackupOwnership.Servyx"/> only when all four
    /// marks described in the type remarks hold; everything else is <see cref="BackupOwnership.Foreign"/>.
    /// </summary>
    /// <param name="tags">The snapshot's ARM tags, exactly as ARM reports them.</param>
    /// <param name="serverId">The Servyx server the classification is being made for.</param>
    /// <param name="resourceGroup">The resource group the server's machine lives in.</param>
    /// <param name="virtualMachineName">The ARM name of the machine that server runs on.</param>
    public static BackupOwnership Classify(
        IReadOnlyDictionary<string, string>? tags,
        string serverId,
        string resourceGroup,
        string virtualMachineName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualMachineName);

        if (!IsSupportedServerId(serverId) || tags is null || tags.Count == 0)
        {
            return BackupOwnership.Foreign;
        }

        // Mark 1 - the managed marker, matched exactly. ServyxTagKeys.IsManaged is an ordinal comparison and not
        // a truthiness test, for the same reason it is one in the VM orphan sweep: the output here feeds a
        // delete list, and a classifier that guesses wrong deletes somebody else's only copy.
        if (!ServyxTagKeys.IsManaged(tags))
        {
            return BackupOwnership.Foreign;
        }

        // Mark 2 - it must belong to *this* Servyx server.
        if (!Matches(tags, InstanceIdTag, serverId))
        {
            return BackupOwnership.Foreign;
        }

        // Mark 3 - it must have been taken from *this* machine. A snapshot of the VM this server used to run on
        // is not a backup of the VM it runs on now.
        if (!Matches(tags, SourceVirtualMachineTag, FormatSourceVirtualMachine(resourceGroup, virtualMachineName)))
        {
            return BackupOwnership.Foreign;
        }

        // Mark 4 - the set tag must be a name this adapter wrote, for this exact server.
        return tags.TryGetValue(SetTag, out var setName)
            && TryParseSetName(setName, out var namedServer, out _)
            && string.Equals(namedServer, serverId, StringComparison.Ordinal)
            ? BackupOwnership.Servyx
            : BackupOwnership.Foreign;
    }

    /// <summary>
    /// Reads the backup set a Servyx-owned snapshot belongs to, or <see langword="null"/> if its tags do not
    /// carry a set name this adapter wrote.
    /// </summary>
    public static string? ReadSetName(IReadOnlyDictionary<string, string>? tags) =>
        tags is not null
        && tags.TryGetValue(SetTag, out var setName)
        && TryParseSetName(setName, out _, out _)
            ? setName
            : null;

    /// <summary>
    /// Builds the exact tag dictionary every snapshot in one set is written with.
    /// </summary>
    /// <remarks>
    /// Routed through <see cref="ServyxTagKeys.Build"/> so the descriptive extras are written first and can
    /// never shadow a canonical key, then validated against ARM's own tag rules through
    /// <see cref="ServyxAzureTags.Validate"/> before any call is made. A snapshot ARM refuses to create is far
    /// better than a snapshot ARM creates untagged: the first costs nothing, the second bills forever and can
    /// never be pruned.
    /// </remarks>
    /// <param name="serverId">The Servyx server the set backs.</param>
    /// <param name="resourceGroup">The resource group the machine and its snapshots live in.</param>
    /// <param name="virtualMachineName">The ARM name of the machine the disks are attached to.</param>
    /// <param name="jobId">The provisioning job identity to carry through, for sweep parity with the machine.</param>
    /// <param name="connectorId">The connector identity to carry through, for the same reason.</param>
    /// <param name="setName">The set name from <see cref="FormatSetName"/>.</param>
    /// <param name="sourceDiskName">The ARM name of the managed disk this member is a snapshot of.</param>
    /// <exception cref="ArgumentException">Any value would be rejected by ARM, or the set name is not one this adapter wrote.</exception>
    public static IReadOnlyDictionary<string, string> BuildTags(
        string serverId,
        string resourceGroup,
        string virtualMachineName,
        string jobId,
        string connectorId,
        string setName,
        string sourceDiskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDiskName);

        if (!TryParseSetName(setName, out var namedServer, out _)
            || !string.Equals(namedServer, serverId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Set name '{setName}' is not one this adapter produced for server '{serverId}', so snapshots "
                + "tagged with it could never be classified as Servyx's afterwards.",
                nameof(setName));
        }

        var extras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServyxAzureTags.RoleTag] = RoleDiskSnapshot,
            [ServyxAzureTags.ResourceGroupTag] = resourceGroup,
            [SourceVirtualMachineTag] = FormatSourceVirtualMachine(resourceGroup, virtualMachineName),
            [SourceDiskTag] = sourceDiskName,
            [SetTag] = setName,
        };

        return ServyxAzureTags.Validate(ServyxTagKeys.Build(serverId, jobId, connectorId, extras));
    }

    private static bool Matches(IReadOnlyDictionary<string, string> tags, string key, string expected) =>
        tags.TryGetValue(key, out var value) && string.Equals(value, expected, StringComparison.Ordinal);
}
