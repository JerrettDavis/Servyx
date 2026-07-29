using System.Text;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Rcon.Tests.Backups;

/// <summary>
/// A tiny in-memory filesystem behind an <see cref="IExecutionTarget"/>, with a shared journal so a test
/// can assert what happened <em>and in what order</em>.
/// </summary>
/// <remarks>
/// The journal is the point. "The quiesce ran before anything was archived" is an ordering claim, and the
/// only way to make it is to have the control channel and the filesystem write into the same list.
/// </remarks>
internal sealed class InMemoryTarget(string root, List<string>? journal = null) : IExecutionTarget
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    /// <summary>The absolute root every <see cref="TargetPath"/> is resolved against.</summary>
    public string Root { get; } = root;

    /// <summary>The shared ordering journal.</summary>
    public List<string> Journal { get; } = journal ?? [];

    /// <summary>Every path currently present.</summary>
    public IEnumerable<string> Paths => _files.Keys;

    public InMemoryTarget With(string path, string content)
    {
        _files[Normalize(path)] = Encoding.UTF8.GetBytes(content);
        return this;
    }

    public byte[] Read(string path) => _files[Normalize(path)];

    public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default)
    {
        var key = Normalize(path.Value);
        Journal.Add($"exists:{key}");
        return Task.FromResult(_files.ContainsKey(key) || DirectoryExists(key));
    }

    public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default)
    {
        var key = Normalize(path.Value);
        Journal.Add($"stat:{key}");

        if (_files.TryGetValue(key, out var bytes))
        {
            return Task.FromResult(new FileStat(true, false, bytes.LongLength, DateTimeOffset.UnixEpoch, null));
        }

        return Task.FromResult(DirectoryExists(key)
            ? new FileStat(true, true, null, null, null)
            : new FileStat(false, false, null, null, null));
    }

    public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default)
    {
        var dir = Normalize(path.Value);
        Journal.Add($"list:{dir}");

        if (!DirectoryExists(dir))
        {
            throw new DirectoryNotFoundException($"'{dir}' not found.");
        }

        var prefix = dir.Length == 0 ? string.Empty : dir + "/";
        var children = new Dictionary<string, FileEntry>(StringComparer.Ordinal);

        foreach (var key in _files.Keys)
        {
            if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = key[prefix.Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);

            if (slash < 0)
            {
                children[rest] = new FileEntry(rest, false, _files[key].LongLength, DateTimeOffset.UnixEpoch);
                continue;
            }

            var name = rest[..slash];
            children.TryAdd(name, new FileEntry(name, true, null, null));
        }

        return Task.FromResult<IReadOnlyList<FileEntry>>(
            [.. children.Values.OrderBy(e => e.Name, StringComparer.Ordinal)]);
    }

    public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default)
    {
        var key = Normalize(path.Value);
        Journal.Add($"read:{key}");

        if (!_files.TryGetValue(key, out var bytes))
        {
            throw new FileNotFoundException($"'{key}' not found.", key);
        }

        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default)
    {
        var key = Normalize(path.Value);
        Journal.Add($"write:{key}");

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        _files[key] = buffer.ToArray();

        return Task.FromResult(new FileWriteReceipt(null, "sha", DateTimeOffset.UnixEpoch));
    }

    public Task DeleteAsync(TargetPath path, CancellationToken ct = default)
    {
        var key = Normalize(path.Value);
        Journal.Add($"delete:{key}");
        _files.Remove(key);
        return Task.CompletedTask;
    }

    public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException("Command execution is not supported by this double.");

    public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException("Command execution is not supported by this double.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private bool DirectoryExists(string dir) =>
        dir.Length == 0 || _files.Keys.Any(k => k.StartsWith(dir + "/", StringComparison.Ordinal));

    private static string Normalize(string? path) => (path ?? string.Empty).Replace('\\', '/').Trim('/');
}
