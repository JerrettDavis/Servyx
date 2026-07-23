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
public sealed record FileStat(bool Exists, bool IsDirectory, long? SizeBytes, DateTimeOffset? ModifiedAt, string? Sha256);

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
