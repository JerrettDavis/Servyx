using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Domain.Connectors;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// <see cref="IConnectorPool"/> implementation maintaining one cached <see cref="IConnector"/> per
/// <see cref="ConnectorKey"/>, a <see cref="TimeoutPolicy.MaxConcurrentSessions"/>-sized semaphore per
/// entry, and idle eviction after <see cref="TimeoutPolicy.IdleEviction"/> once every outstanding lease has
/// been released.
/// </summary>
/// <remarks>
/// <b>Honest scope note:</b> "pooling" here means reusing the <see cref="IConnector"/> wrapper and
/// rate-limiting concurrent sessions against it — it does not multiplex several logical channels over a
/// single already-open TCP connection the way <c>docs/connectors.md</c> describes as the ideal. SSH.NET's
/// public API gives each <c>SshClient</c>/<c>SftpClient</c> (each a separate <c>BaseClient</c>) its own
/// <c>Session</c>/socket; there is no supported way to share one underlying connection between an exec
/// client and an sftp client. <see cref="SshConnector.OpenAsync"/> therefore performs its own handshake(s)
/// on every call. What this pool does provide — a stable key derived without ever touching a secret value,
/// bounded concurrency, and idle eviction — is still meaningful pooling behavior; true single-socket
/// multiplexing is future work, not a regression this milestone introduces.
/// </remarks>
public sealed class ConnectorPool : IConnectorPool, IAsyncDisposable
{
    private readonly Func<ConnectorKey, CancellationToken, Task<IConnector>> _connectorFactory;
    private readonly ILogger<ConnectorPool> _logger;
    private readonly Dictionary<ConnectorKey, PooledEntry> _entries = [];
    private readonly SemaphoreSlim _entriesLock = new(1, 1);
    private readonly Timer _evictionTimer;
    private bool _disposed;

    /// <summary>
    /// Creates a <see cref="ConnectorPool"/>. <paramref name="connectorFactory"/> builds (but does not
    /// connect) an <see cref="IConnector"/> for a given key the first time it is leased — callers own the
    /// mapping from a <see cref="ConnectorKey"/> back to whatever <see cref="ConnectorDescriptor"/> and
    /// credentials produced it (typically an application-layer connector registry, out of this project's
    /// scope).
    /// </summary>
    public ConnectorPool(Func<ConnectorKey, CancellationToken, Task<IConnector>> connectorFactory, ILogger<ConnectorPool>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connectorFactory);

        _connectorFactory = connectorFactory;
        _logger = logger ?? NullLogger<ConnectorPool>.Instance;
        _evictionTimer = new Timer(OnEvictionTick, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc />
    public async Task<IConnectorLease> LeaseAsync(ConnectorKey key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);

        var entry = await GetOrCreateEntryAsync(key, ct).ConfigureAwait(false);

        await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref entry.OutstandingLeases);

        return new PoolLease(this, key, entry);
    }

    private async Task<PooledEntry> GetOrCreateEntryAsync(ConnectorKey key, CancellationToken ct)
    {
        await _entriesLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var connector = await _connectorFactory(key, ct).ConfigureAwait(false);
            var entry = new PooledEntry(connector);
            _entries[key] = entry;
            return entry;
        }
        finally
        {
            _entriesLock.Release();
        }
    }

    private void Release(ConnectorKey key, PooledEntry entry)
    {
        entry.LastReleasedAt = DateTimeOffset.UtcNow;
        Interlocked.Decrement(ref entry.OutstandingLeases);
        entry.Semaphore.Release();
    }

    private void OnEvictionTick(object? state)
    {
        _ = EvictIdleEntriesAsync();
    }

    private async Task EvictIdleEntriesAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _entriesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            List<ConnectorKey>? toRemove = null;

            foreach (var (key, entry) in _entries)
            {
                if (entry.OutstandingLeases > 0)
                {
                    continue; // Long-lived consumers holding a lease are exempt from idle eviction.
                }

                var idleFor = now - entry.LastReleasedAt;
                if (idleFor >= entry.Connector.Descriptor.Timeouts.IdleEviction)
                {
                    (toRemove ??= []).Add(key);
                }
            }

            if (toRemove is null)
            {
                return;
            }

            foreach (var key in toRemove)
            {
                _entries.Remove(key);
                _logger.LogDebug("Evicted idle pooled connector for key {Kind}/{Endpoint}.", key.Kind, key.EndpointKey);
            }
        }
        finally
        {
            _entriesLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _evictionTimer.DisposeAsync().ConfigureAwait(false);
        _entriesLock.Dispose();

        foreach (var entry in _entries.Values)
        {
            entry.Semaphore.Dispose();
        }

        _entries.Clear();
    }

    private sealed class PooledEntry
    {
        public PooledEntry(IConnector connector)
        {
            Connector = connector;
            Semaphore = new SemaphoreSlim(
                Math.Max(1, connector.Descriptor.Timeouts.MaxConcurrentSessions),
                Math.Max(1, connector.Descriptor.Timeouts.MaxConcurrentSessions));
            LastReleasedAt = DateTimeOffset.UtcNow;
        }

        public IConnector Connector { get; }

        public SemaphoreSlim Semaphore { get; }

        public int OutstandingLeases;

        public DateTimeOffset LastReleasedAt;
    }

    private sealed class PoolLease : IConnectorLease
    {
        private readonly ConnectorPool _pool;
        private readonly ConnectorKey _key;
        private readonly PooledEntry _entry;
        private bool _disposed;

        public PoolLease(ConnectorPool pool, ConnectorKey key, PooledEntry entry)
        {
            _pool = pool;
            _key = key;
            _entry = entry;
        }

        public IConnector Connector => _entry.Connector;

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _pool.Release(_key, _entry);
            return ValueTask.CompletedTask;
        }
    }
}
