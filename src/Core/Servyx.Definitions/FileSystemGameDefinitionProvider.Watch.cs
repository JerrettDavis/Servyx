using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Definitions;

namespace Servyx.Definitions;

/// <summary>
/// <see cref="FileSystemGameDefinitionProvider.WatchAsync"/> and the plumbing behind it. Kept in its own
/// partial file, mirroring how <c>Servyx.Definitions</c>'s other large class (<c>GameDefinitionYamlParser</c>)
/// is split by concern, since debounce-and-hash-compare is a distinct piece of machinery from listing/loading.
/// </summary>
public sealed partial class FileSystemGameDefinitionProvider
{
    /// <summary>
    /// How long a path must go quiet after its last filesystem event before it is re-hashed and possibly
    /// reported. Absorbs editors that write a file more than once per save (e.g. a temp-file-then-rename
    /// sequence), which would otherwise fire multiple <see cref="System.IO.FileSystemWatcher"/> events for
    /// a single logical change.
    /// </summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Emits only when the content hash actually changed.</strong> A no-op rewrite (same bytes,
    /// e.g. an editor's "save" that did not change anything) is compared against the last known hash for
    /// that path and produces no event.
    /// </para>
    /// <para>
    /// <strong>A watcher fault renews the watcher, never faults the sequence.</strong>
    /// <see cref="System.IO.FileSystemWatcher.Error"/> (an internal buffer overflow is the common cause) is
    /// handled by disposing the failed watcher and creating a fresh one over the same root, logged at
    /// Warning — the returned <see cref="IAsyncEnumerable{T}"/> itself never throws or completes because of
    /// it.
    /// </para>
    /// <para>
    /// <strong>Best-effort <see cref="GameDefinitionRef.Id"/>.</strong> A changed file's new content might
    /// itself be invalid (mid-edit, or genuinely broken) at the moment it is detected, so this method does
    /// not attempt full validation before emitting — that is <see cref="FileSystemGameDefinitionProvider.LoadAsync"/>'s
    /// job, invoked by whatever consumes this stream (see <see cref="DefinitionCatalogRefreshService"/>,
    /// which always re-lists and re-loads on any signal, so a change to invalid content still surfaces as a
    /// fault rather than being silently dropped). When the new content's header cannot be read at all, the
    /// file's own name (without its extension) stands in for the id, purely so the emitted reference is
    /// never empty.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<GameDefinitionRef> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<GameDefinitionRef>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // Seeded with whatever is on disk right now, so the first genuine edit after watching starts has
        // something to compare against instead of spuriously reporting every already-existing file as
        // "changed".
        var knownHashes = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in SafeDiscoverPaths())
        {
            if (TryComputeFileHash(path, out var hash))
            {
                knownHashes[path] = hash;
            }
        }

        var debounceTokens = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);

        void OnPathTouched(string path)
        {
            if (!string.Equals(Path.GetExtension(path), ".yaml", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var cts = new CancellationTokenSource();
            debounceTokens.AddOrUpdate(
                path,
                cts,
                (_, existing) =>
                {
                    // A newer event for the same path arrived before the previous debounce window elapsed —
                    // cancel it so only the latest edit is checked, restarting the 500ms window.
                    existing.Cancel();
                    existing.Dispose();
                    return cts;
                });

            _ = DebounceAndEmitAsync(path, cts, channel.Writer, knownHashes, debounceTokens, ct);
        }

        using var subscription = StartWatcher(OnPathTouched, ct);

        try
        {
            await foreach (var reference in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return reference;
            }
        }
        finally
        {
            foreach (var pending in debounceTokens.Values)
            {
                pending.Cancel();
                pending.Dispose();
            }
        }
    }

    private async Task DebounceAndEmitAsync(
        string path,
        CancellationTokenSource debounceCts,
        ChannelWriter<GameDefinitionRef> writer,
        ConcurrentDictionary<string, string> knownHashes,
        ConcurrentDictionary<string, CancellationTokenSource> debounceTokens,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceDelay, debounceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Superseded by a newer event for the same path within the debounce window.
        }
        finally
        {
            debounceCts.Dispose();
        }

        // The debounce window elapsed without being superseded — this is the current (and only) pending
        // check for this path, so it can be dropped from the tracking table now rather than accumulating
        // one disposed entry per path ever touched for the lifetime of the watch.
        debounceTokens.TryRemove(new KeyValuePair<string, CancellationTokenSource>(path, debounceCts));

        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (!TryComputeFileHash(path, out var newHash))
        {
            // Deleted, or transiently unreadable mid-write. Only signal if this path was previously known —
            // a path we never tracked disappearing is not a "change" from this watcher's point of view.
            if (knownHashes.TryRemove(path, out _))
            {
                var idGuess = Path.GetFileNameWithoutExtension(path);
                await TryWriteAsync(writer, new GameDefinitionRef(idGuess, string.Empty, SourceId, path), ct).ConfigureAwait(false);
            }

            return;
        }

        if (knownHashes.TryGetValue(path, out var previousHash) && string.Equals(previousHash, newHash, StringComparison.Ordinal))
        {
            return; // Same bytes — an editor's save-twice, not a real change. No event.
        }

        knownHashes[path] = newHash;

        var id = TryReadHeaderId(path) ?? Path.GetFileNameWithoutExtension(path);
        await TryWriteAsync(writer, new GameDefinitionRef(id, newHash, SourceId, path), ct).ConfigureAwait(false);
    }

    private static async Task TryWriteAsync(ChannelWriter<GameDefinitionRef> writer, GameDefinitionRef reference, CancellationToken ct)
    {
        try
        {
            await writer.WriteAsync(reference, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The consumer stopped watching; nothing left to notify.
        }
        catch (ChannelClosedException)
        {
            // Same reasoning, for the narrow race where the channel completed just after the cancellation check.
        }
    }

    private IDisposable StartWatcher(Action<string> onPathTouched, CancellationToken ct) =>
        new WatcherSubscription(_root, onPathTouched, _logger, ct);

    private static bool TryComputeFileHash(string path, out string hash)
    {
        try
        {
            hash = ComputeContentHash(File.ReadAllBytes(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            hash = string.Empty;
            return false;
        }
    }

    private string? TryReadHeaderId(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return TryReadHeader(bytes, out var header, out _, out _, out _) ? header.Id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private IEnumerable<string> SafeDiscoverPaths()
    {
        try
        {
            return DiscoverFiles();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Owns a single <see cref="System.IO.FileSystemWatcher"/> over the definitions root, recursively (so
    /// both the flat and bundle layouts are covered), and transparently recreates it on
    /// <see cref="System.IO.FileSystemWatcher.Error"/> so a watcher fault never surfaces as a fault in the
    /// <see cref="WatchAsync"/> sequence itself.
    /// </summary>
    private sealed class WatcherSubscription : IDisposable
    {
        private readonly string _root;
        private readonly Action<string> _onPathTouched;
        private readonly ILogger? _logger;
        private readonly CancellationToken _ct;
        private readonly object _gate = new();
        private FileSystemWatcher? _watcher;
        private bool _disposed;

        public WatcherSubscription(string root, Action<string> onPathTouched, ILogger? logger, CancellationToken ct)
        {
            _root = root;
            _onPathTouched = onPathTouched;
            _logger = logger;
            _ct = ct;
            Attach();
        }

        private void Attach()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (!Directory.Exists(_root))
                {
                    // Nothing to watch yet. A directory that appears later is picked up by the next
                    // catalog-level refresh, not by this watcher retrying — FileSystemWatcher throws
                    // immediately when constructed over a path that does not exist.
                    return;
                }

                var watcher = new FileSystemWatcher(_root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                        | NotifyFilters.Size | NotifyFilters.CreationTime,
                    Filter = "*.yaml",
                };

                watcher.Changed += OnEvent;
                watcher.Created += OnEvent;
                watcher.Renamed += OnRenamed;
                watcher.Deleted += OnEvent;
                watcher.Error += OnError;

                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
        }

        private void OnEvent(object sender, FileSystemEventArgs e) => Invoke(e.FullPath);

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            Invoke(e.FullPath);
            Invoke(e.OldFullPath);
        }

        private void Invoke(string path)
        {
            try
            {
                _onPathTouched(path);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error handling a filesystem change for '{Path}'.", path);
            }
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            _logger?.LogWarning(e.GetException(), "The definitions directory watcher for '{Root}' failed; renewing it.", _root);

            lock (_gate)
            {
                var old = _watcher;
                _watcher = null;

                if (old is not null)
                {
                    Detach(old);
                    old.Dispose();
                }
            }

            if (!_ct.IsCancellationRequested)
            {
                Attach();
            }
        }

        private void Detach(FileSystemWatcher watcher)
        {
            watcher.Changed -= OnEvent;
            watcher.Created -= OnEvent;
            watcher.Renamed -= OnRenamed;
            watcher.Deleted -= OnEvent;
            watcher.Error -= OnError;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;

                if (_watcher is not null)
                {
                    Detach(_watcher);
                    _watcher.Dispose();
                    _watcher = null;
                }
            }
        }
    }
}
