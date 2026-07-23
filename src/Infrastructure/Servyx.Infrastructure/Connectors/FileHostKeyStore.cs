using System.Text.Json;
using Servyx.Domain.Connectors;

namespace Servyx.Infrastructure.Connectors;

/// <summary>
/// <see cref="IHostKeyStore"/> backed by a single JSON file. Writes are atomic (write to a temp file, then
/// rename over the real one), and an in-process <see cref="SemaphoreSlim"/> serializes the read-modify-write
/// cycle for <see cref="PinAsync"/> and <see cref="RevokeAsync"/> so two concurrent in-process callers
/// cannot silently clobber each other's update.
/// </summary>
/// <remarks>
/// Revocation is tracked as a durable flag on the stored entry — including a tombstone entry for a host
/// that was revoked without ever having been pinned — rather than by deleting the record outright, which is
/// what lets <see cref="IsRevokedAsync"/> distinguish "explicitly revoked" from "never seen" even though
/// <see cref="FindAsync"/> reports both as no pinned key.
/// </remarks>
public sealed class FileHostKeyStore : IHostKeyStore
{
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Creates a <see cref="FileHostKeyStore"/> persisting to <paramref name="filePath"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is null, empty, or whitespace.</exception>
    public FileHostKeyStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <inheritdoc />
    public async Task<HostKeyRecord?> FindAsync(string host, int port, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var document = await LoadAsync(ct).ConfigureAwait(false);
        var entry = FindEntry(document, host, port);
        return entry is null || entry.Revoked ? null : ToRecord(entry);
    }

    /// <inheritdoc />
    public async Task PinAsync(HostKeyRecord record, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync(ct).ConfigureAwait(false);
            var existing = FindEntry(document, record.Host, record.Port);
            if (existing is not null)
            {
                document.Entries.Remove(existing);
            }

            document.Entries.Add(new StoredEntry
            {
                Host = record.Host,
                Port = record.Port,
                Algorithm = record.Algorithm,
                Sha256Fingerprint = record.Sha256Fingerprint,
                PublicKeyBlobBase64 = Convert.ToBase64String(record.PublicKeyBlob),
                PinnedAt = record.PinnedAt,
                PinnedByActor = actor,
                Revoked = false,
            });

            await SaveAsync(document, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RevokeAsync(string host, int port, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync(ct).ConfigureAwait(false);
            var existing = FindEntry(document, host, port);

            if (existing is null)
            {
                // Revoking a host that was never pinned is allowed, so a compromised key learned about from
                // an external source can be pre-emptively blocked. The tombstone has no real key material.
                document.Entries.Add(new StoredEntry
                {
                    Host = host,
                    Port = port,
                    Revoked = true,
                    RevokedAt = DateTimeOffset.UtcNow,
                    RevokedByActor = actor,
                });
            }
            else
            {
                existing.Revoked = true;
                existing.RevokedAt = DateTimeOffset.UtcNow;
                existing.RevokedByActor = actor;
            }

            await SaveAsync(document, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsRevokedAsync(string host, int port, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var document = await LoadAsync(ct).ConfigureAwait(false);
        var entry = FindEntry(document, host, port);
        return entry is not null && entry.Revoked;
    }

    private static StoredEntry? FindEntry(StoreDocument document, string host, int port)
    {
        foreach (var entry in document.Entries)
        {
            if (entry.Port == port && string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static HostKeyRecord ToRecord(StoredEntry entry) => new(
        entry.Host,
        entry.Port,
        entry.Algorithm,
        entry.Sha256Fingerprint,
        Convert.FromBase64String(entry.PublicKeyBlobBase64),
        entry.PinnedAt,
        entry.PinnedByActor);

    private async Task<StoreDocument> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new StoreDocument();
        }

        var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new StoreDocument();
        }

        return JsonSerializer.Deserialize<StoreDocument>(json) ?? new StoreDocument();
    }

    private async Task SaveAsync(StoreDocument document, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(document, SaveOptions);
        var tempPath = _filePath + ".tmp" + Guid.NewGuid().ToString("N");

        await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Atomic rename: a concurrent reader sees either the old complete file or the new complete file.
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private sealed class StoreDocument
    {
        public List<StoredEntry> Entries { get; set; } = [];
    }

    private sealed class StoredEntry
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Algorithm { get; set; } = string.Empty;
        public string Sha256Fingerprint { get; set; } = string.Empty;
        public string PublicKeyBlobBase64 { get; set; } = string.Empty;
        public DateTimeOffset PinnedAt { get; set; }
        public string PinnedByActor { get; set; } = string.Empty;
        public bool Revoked { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public string? RevokedByActor { get; set; }
    }
}
