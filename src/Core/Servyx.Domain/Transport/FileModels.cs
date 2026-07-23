using Servyx.Domain.Control;

namespace Servyx.Domain.Transport;

/// <summary>An entry returned by <see cref="IExecutionTarget.ListDirectoryAsync"/>.</summary>
/// <param name="Name">The entry's file or directory name (not a full path).</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
/// <param name="SizeBytes">The entry's size in bytes, if known and applicable (typically null for directories).</param>
/// <param name="ModifiedAt">Last-modified timestamp, if known.</param>
public sealed record FileEntry(string Name, bool IsDirectory, long? SizeBytes, DateTimeOffset? ModifiedAt);

/// <summary>Metadata about a single file or directory on a target.</summary>
/// <param name="Exists">Whether the path exists.</param>
/// <param name="IsDirectory">Whether the path is a directory.</param>
/// <param name="SizeBytes">Size in bytes, if applicable.</param>
/// <param name="ModifiedAt">Last-modified timestamp, if known.</param>
/// <param name="Sha256">Content hash, if computed.</param>
public sealed record FileStat(bool Exists, bool IsDirectory, long? SizeBytes, DateTimeOffset? ModifiedAt, string? Sha256)
{
    /// <summary>
    /// POSIX permission-and-type mode bits (the low 9 bits carry <c>rwxrwxrwx</c>), or
    /// <see langword="null"/> on platforms/probes that don't report them — notably Windows targets, and
    /// any capability probe that only had access to a coarse stat header without ownership data.
    /// </summary>
    public int? Mode { get; init; }

    /// <summary>The owning user's name, if known.</summary>
    public string? Owner { get; init; }

    /// <summary>The owning group's name, if known.</summary>
    public string? Group { get; init; }

    /// <summary>The owning user's numeric id, if known (POSIX targets).</summary>
    public int? Uid { get; init; }

    /// <summary>The owning group's numeric id, if known (POSIX targets).</summary>
    public int? Gid { get; init; }

    /// <summary>
    /// Whether the file lives on a mount the host reports as read-only (e.g. a read-only Docker bind
    /// mount). When true, no identity can write here regardless of what <see cref="Mode"/> says.
    /// </summary>
    public bool IsReadOnlyMount { get; init; }

    /// <summary>Whether the path itself is a symbolic link, as opposed to the file/directory it resolves to.</summary>
    public bool IsSymlink { get; init; }

    /// <summary>
    /// Evaluates whether <paramref name="identity"/> may write to this file using POSIX permission
    /// semantics: an owner match (by <see cref="Uid"/> or, failing that, by <see cref="Owner"/> name)
    /// checks the user write bit (<c>0200</c>); otherwise a group match (by <see cref="Gid"/> — including
    /// <paramref name="identity"/>'s <see cref="TargetIdentity.SupplementaryGids"/> — or by
    /// <see cref="Group"/> name) checks the group write bit (<c>0020</c>); otherwise the other/world write
    /// bit (<c>0002</c>) is checked.
    /// </summary>
    /// <remarks>
    /// Always <see langword="false"/> when <see cref="IsReadOnlyMount"/> is set, regardless of mode bits.
    /// <para>
    /// <b>Deliberate asymmetry when <see cref="Mode"/> is <see langword="null"/>:</b> on Windows this
    /// returns <see langword="true"/>, because POSIX mode bits are meaningless there (NTFS ACLs are a
    /// different model entirely, and a null <see cref="Mode"/> is simply how every Windows target reports
    /// itself — it is not evidence of anything). On every other platform a null <see cref="Mode"/> means
    /// the probe that produced this <see cref="FileStat"/> genuinely could not determine permissions, and
    /// this returns <see langword="false"/>: claiming writability without POSIX evidence is how a panel
    /// tells a user a write will succeed and then watches it fail with <c>EACCES</c>.
    /// </para>
    /// </remarks>
    public bool PermitsWriteBy(TargetIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (IsReadOnlyMount)
        {
            return false;
        }

        if (Mode is null)
        {
            return OperatingSystem.IsWindows();
        }

        const int OwnerWriteBit = 0x80; // 0200 octal
        const int GroupWriteBit = 0x10; // 0020 octal
        const int OtherWriteBit = 0x02; // 0002 octal

        var mode = Mode.Value;

        var isOwner = (Uid.HasValue && identity.Uid.HasValue && Uid.Value == identity.Uid.Value)
            || (Owner is not null && identity.UserName is not null && string.Equals(Owner, identity.UserName, StringComparison.Ordinal));
        if (isOwner)
        {
            return (mode & OwnerWriteBit) != 0;
        }

        var isGroupMember = (Gid.HasValue && identity.Gid.HasValue && Gid.Value == identity.Gid.Value)
            || (Gid.HasValue && identity.SupplementaryGids.Contains(Gid.Value));
        if (isGroupMember)
        {
            return (mode & GroupWriteBit) != 0;
        }

        return (mode & OtherWriteBit) != 0;
    }
}

/// <summary>Options controlling an atomic file write.</summary>
/// <param name="ExpectedPreImageHash">
/// SHA-256 of the content the caller last observed. If the file's current content does not match, the
/// write is refused with <see cref="TargetDriftException"/>. Null means "no expectation" and should only
/// be used for files known not to previously exist.
/// </param>
public sealed record FileWriteOptions(string? ExpectedPreImageHash);

/// <summary>Receipt returned after a successful atomic file write.</summary>
/// <param name="PreImageSha256">Hash of the file's content before this write, or null if it did not exist.</param>
/// <param name="PostImageSha256">Hash of the file's content after this write.</param>
/// <param name="WrittenAt">When the write completed.</param>
public sealed record FileWriteReceipt(string? PreImageSha256, string PostImageSha256, DateTimeOffset WrittenAt);

/// <summary>The write posture of a server, checked by the write-guard decorator over <see cref="IExecutionTarget"/>.</summary>
public enum WriteMode
{
    /// <summary>No mutating operation is permitted; only reads and read-only control commands.</summary>
    ReadOnly,

    /// <summary>Plans may be previewed but never applied.</summary>
    PreviewOnly,

    /// <summary>Writes are permitted, subject to per-plan approval.</summary>
    Enabled,
}
