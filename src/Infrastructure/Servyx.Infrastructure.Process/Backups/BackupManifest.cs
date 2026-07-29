using System.Text.Json;
using System.Text.Json.Serialization;

namespace Servyx.Infrastructure.Process.Backups;

/// <summary>
/// The index written alongside every Servyx-owned archive, recording what was captured, when, and the
/// content hash of the archive it describes.
/// </summary>
/// <remarks>
/// <para>
/// The manifest is a <em>sidecar</em> file (<c>&lt;archive&gt;.manifest.json</c>), not an entry inside the
/// archive. That is what makes <c>IBackupProvider.InspectAsync</c>'s "reads the index without extracting its
/// content" literal rather than approximate: answering "what is in this backup?" is one small JSON read, so
/// inspecting a 40 GB save archive costs the same as inspecting an empty one and never decompresses a byte.
/// </para>
/// <para>
/// <strong><see cref="ArchiveSha256"/> is computed in-process, not by a <c>sha256sum</c> binary.</strong>
/// The SSH provider has to ask the host to hash the file, because the file is on the host; here the bytes
/// are already in hand, so <see cref="System.Security.Cryptography.SHA256"/> answers the same question with
/// no external tool and no platform assumption. There is nothing on Windows that reliably answers to
/// <c>sha256sum</c>, and an adapter that only fingerprints its archives on Linux would be worse than one
/// that never claimed to.
/// </para>
/// <para>
/// <strong>There is no <c>quiescedWith</c> field, because this provider takes no quiesce.</strong> The SSH
/// and Docker manifests carry one so an archive of flushed state stays distinguishable from an archive of
/// whatever the server last wrote to disk. Local backups take no quiesce at all (see the remarks on
/// <see cref="LocalProcessBackupProvider"/>), so every archive this provider writes is of the second kind
/// and a field that could only ever say <c>null</c> would be noise rather than information. If a local
/// quiesce is ever added, it arrives with a schema version bump and a field, not with a silent change of
/// meaning for archives already on disk.
/// </para>
/// <para>
/// The trade-off of a sidecar is that the manifest can be separated from its archive by something moving
/// files around outside Servyx. That is acceptable because the manifest is an index, not the authority: the
/// archive remains self-describing through its own tar headers, which is the path
/// <see cref="LocalProcessBackupProvider.InspectAsync"/> falls back to — reading headers with
/// <c>copyData: false</c>, extracting nothing — for archives that have no manifest at all.
/// </para>
/// </remarks>
/// <param name="SchemaVersion">Manifest schema version, so a future reader can recognise an old file.</param>
/// <param name="ServerId">The server this backup was taken from.</param>
/// <param name="CreatedAt">When the archive was written.</param>
/// <param name="ArchiveFileName">The archive file this manifest describes, relative to the same directory.</param>
/// <param name="ArchiveSha256">Lowercase hex SHA-256 of the archive's bytes.</param>
/// <param name="ArchiveSizeBytes">Size of the archive in bytes.</param>
/// <param name="ArchiveRoot">The absolute local directory the archive's entry names are relative to.</param>
/// <param name="Entries">Every archive entry name, in the order they were written.</param>
public sealed record BackupManifest(
    int SchemaVersion,
    string ServerId,
    DateTimeOffset CreatedAt,
    string ArchiveFileName,
    string ArchiveSha256,
    long ArchiveSizeBytes,
    string? ArchiveRoot,
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

            // A JSON document that parses but carries no entry list is not a manifest — treat it as absent so
            // callers fall back to the archive's own tar headers rather than reporting "nothing in here".
            return manifest is { Entries: not null, ArchiveFileName: not null } ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
