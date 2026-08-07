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

/// <summary>How a write is finalized onto the target path.</summary>
/// <remarks>
/// <para>
/// This is a caller declaration, never a transport's runtime decision. A transport that quietly picked
/// <see cref="DirectPlacement"/> because <see cref="AtomicRename"/> happened not to work right now would
/// be silently downgrading a durability guarantee the caller asked for — which is exactly the failure this
/// enum exists to make impossible to express.
/// </para>
/// </remarks>
public enum FileWriteStrategy
{
    /// <summary>
    /// Stage the content beside the target and rename it over it, so a concurrent reader observes either the
    /// whole old file or the whole new one and never a half-written one. The default, and the only correct
    /// choice against a workload that is running. On a container transport the rename is the one step that
    /// cannot be served by the daemon's archive endpoint, so it costs an in-container process — which means
    /// this strategy requires the container to be <em>running</em>.
    /// </summary>
    AtomicRename,

    /// <summary>
    /// Place the bytes straight at the target path, with no staging and no rename. Not atomic: a reader
    /// racing this write can observe a partial file. Correct only when nothing can be reading — the case it
    /// exists for is a container that has been created but never started, where an
    /// <see cref="AtomicRename"/> is not merely slower but impossible, because there is no process to run
    /// the rename in.
    /// </summary>
    DirectPlacement,
}

/// <summary>Options controlling a file write.</summary>
/// <param name="ExpectedPreImageHash">
/// SHA-256 of the content the caller last observed. If the file's current content does not match, the
/// write is refused with <see cref="TargetDriftException"/>. Null means "no expectation" and should only
/// be used for files known not to previously exist.
/// </param>
public sealed record FileWriteOptions(string? ExpectedPreImageHash)
{
    private readonly int? _mode;

    /// <summary>
    /// How the write is finalized onto the target path. Defaults to <see cref="FileWriteStrategy.AtomicRename"/>,
    /// so every caller that does not think about this gets the durable behaviour.
    /// </summary>
    public FileWriteStrategy Strategy { get; init; } = FileWriteStrategy.AtomicRename;

    /// <summary>
    /// POSIX permission bits (the low 9, <c>rwxrwxrwx</c>) the file must end up with, or
    /// <see langword="null"/> to preserve an existing file's mode and let the transport pick a default for
    /// a file it creates.
    /// </summary>
    /// <remarks>
    /// Carried on the write rather than applied afterwards on purpose. A separate "now chmod it" step is a
    /// second operation that can be refused, skipped, or — on a container that is not running — impossible,
    /// which would leave a file whose whole point is to be readable by exactly one identity sitting at the
    /// transport's default until someone noticed.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative or sets a bit outside the low 9. Set-user-id, set-group-id and the sticky bit
    /// are deliberately not expressible here.
    /// </exception>
    public int? Mode
    {
        get => _mode;
        init
        {
            if (value is { } mode && mode is < 0 or > 0x1FF)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    mode,
                    "A file mode carried on a write must be POSIX permission bits only (0 to 0777 octal). "
                    + "Set-user-id, set-group-id and the sticky bit are not expressible through this seam.");
            }

            _mode = value;
        }
    }

    /// <summary>
    /// Throws when this instance asks for anything beyond a plain stage-and-rename write — the only shape a
    /// transport calling this implements.
    /// </summary>
    /// <param name="transportDescription">
    /// How the refusing transport names itself in the message, e.g. <c>"LocalExecutionTarget"</c>.
    /// </param>
    /// <remarks>
    /// The alternative to calling this is ignoring <see cref="Strategy"/> and <see cref="Mode"/>, which would
    /// hand the caller a receipt for a write that did not do what was asked. A loud refusal is the only
    /// honest answer a transport that cannot honour them can give.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// <see cref="Strategy"/> is not <see cref="FileWriteStrategy.AtomicRename"/>, or <see cref="Mode"/> is set.
    /// </exception>
    public void ThrowIfBeyondPlainAtomicRename(string transportDescription)
    {
        if (Strategy != FileWriteStrategy.AtomicRename)
        {
            throw new NotSupportedException(
                $"{transportDescription} implements {nameof(FileWriteStrategy)}.{nameof(FileWriteStrategy.AtomicRename)} "
                + $"only; it cannot honour {nameof(FileWriteStrategy)}.{Strategy}. Refusing rather than writing "
                + "with a durability guarantee the caller did not ask for.");
        }

        if (Mode is not null)
        {
            throw new NotSupportedException(
                $"{transportDescription} cannot apply an explicit file mode as part of a write; it preserves an "
                + "existing file's mode and creates new files at its own default. Refusing rather than returning a "
                + "receipt for a file whose permissions are not what the caller asked for.");
        }
    }
}

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
