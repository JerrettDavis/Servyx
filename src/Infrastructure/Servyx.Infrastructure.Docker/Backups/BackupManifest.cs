using System.Text.Json;
using System.Text.Json.Serialization;

namespace Servyx.Infrastructure.Docker.Backups;

/// <summary>
/// The index written alongside every Servyx-owned archive, recording what was captured, when, and the
/// content hash of the archive it describes.
/// </summary>
/// <remarks>
/// <para>
/// The manifest is a <em>sidecar</em> file (<c>&lt;archive&gt;.manifest.json</c>), not an entry inside the
/// archive. That is what makes <c>IBackupProvider.InspectAsync</c>'s "reads the index without
/// extracting its content" literal rather than approximate: answering "what is in this backup?" reads one small JSON
/// file and never opens the tarball at all, so the cost of inspecting a 40 GB save archive is the same as
/// inspecting an empty one, and no decompression path is reachable from a read-only question.
/// </para>
/// <para>
/// The trade-off is that the manifest can be separated from its archive by something moving files around
/// outside Servyx. That is acceptable here because the manifest is an index, not the authority: the
/// archive remains self-describing through its own tar headers, which is exactly the path
/// <see cref="DockerBackupProvider.InspectAsync"/> falls back to for foreign archives that have no
/// manifest at all.
/// </para>
/// </remarks>
/// <param name="SchemaVersion">Manifest schema version, so a future reader can recognise an old file.</param>
/// <param name="ServerId">The server this backup was taken from.</param>
/// <param name="CreatedAt">When the archive was written.</param>
/// <param name="ArchiveFileName">The archive file this manifest describes, relative to the same directory.</param>
/// <param name="ArchiveSha256">Lowercase hex SHA-256 of the archive's bytes as written.</param>
/// <param name="ArchiveSizeBytes">Size of the archive in bytes.</param>
/// <param name="QuiescedWith">The control command id used to quiesce before archiving, or null if none was declared.</param>
/// <param name="Entries">Every archive entry name, in the order they were written.</param>
public sealed record BackupManifest(
    int SchemaVersion,
    string ServerId,
    DateTimeOffset CreatedAt,
    string ArchiveFileName,
    string ArchiveSha256,
    long ArchiveSizeBytes,
    string? QuiescedWith,
    IReadOnlyList<string> Entries)
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serializes this manifest to UTF-8 JSON.</summary>
    public byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions);

    /// <summary>Deserializes a manifest from UTF-8 JSON.</summary>
    /// <param name="utf8Json">The manifest bytes.</param>
    /// <returns>The parsed manifest, or <see langword="null"/> when the payload is not a manifest.</returns>
    public static BackupManifest? FromUtf8Json(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<BackupManifest>(utf8Json, SerializerOptions);

            // A JSON document that parses but carries no entry list is not a manifest — treat it as absent
            // so callers fall back to the archive's own tar headers rather than reporting "nothing in here".
            return manifest is { Entries: not null, ArchiveFileName: not null } ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
