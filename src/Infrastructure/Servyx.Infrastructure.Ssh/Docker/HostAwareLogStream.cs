using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// <see cref="ILogStream"/> that routes each call to the specific host <paramref name="serverId"/> actually
/// lives on, instead of assuming the single process-wide log stream the composition root happened to wire —
/// the same gap <see cref="HostAwareServerDiscovery"/> closed for discovery and
/// <see cref="IServerExecutionTargetResolver"/> exists to let later surfaces close for themselves.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this cannot simply take a host key as a parameter.</strong> <see cref="ILogStream"/>'s own
/// contract — <see cref="FollowAsync"/>/<see cref="ReadAsync"/>/<see cref="WriteAsync"/> — carries only
/// <c>serverId</c>, the same shape every existing caller (<c>ServerQueryService</c>, <c>PollingLogLineSource</c>,
/// <c>ServyxServerLifecycles</c>) already depends on. None of them hold a <c>ServerSummary.HostKey</c> at their
/// own call sites — <c>ServerQueryService.ReadRecentLogsAsync</c> forwards the raw <c>serverId</c> straight
/// to <c>_logStream.ReadAsync</c> with nothing else in hand. So this type resolves the host itself, per call,
/// rather than pushing a wider interface change onto every existing consumer for this one increment.
/// </para>
/// <para>
/// <strong>How the host is found.</strong> <see cref="IHostConnectionSource.GetConnectionsAsync"/> — the same
/// live, cached, restart-free-refreshed set <see cref="CompositeServerDiscovery"/> already fans discovery
/// queries over — is queried for every currently-connectable registered/configured host, and each one is asked
/// (concurrently, via a read-only <c>docker container inspect</c>) whether it has a container named
/// <c>serverId</c>. A registered host's <see cref="IExecutionTarget"/> is a host-wide SSH session capable of
/// addressing any container on that host by id (see <see cref="ServerExecutionTargetResolver"/>'s own remarks),
/// so this probe is exactly the read-only existence check <see cref="SshDockerServerDiscovery"/> already issues
/// during discovery — no new command shape, no mutation. Zero registered/configured hosts (the common,
/// zero-config install) short-circuits to the local branch with no round trip at all. A host that throws while
/// being probed (unreachable, mid-registration, etc.) is logged and treated as "not found there," mirroring
/// <see cref="CompositeServerDiscovery"/>'s own per-host partial-failure handling — one bad host must not stop
/// the search for a good one.
/// </para>
/// <para>
/// <strong>Local is the default, not a coin flip.</strong> A <c>serverId</c> matched by no registered/configured
/// host resolves through <c>local</c> — the log stream <c>AddServyxDocker</c> registered, captured before this
/// type's DI registration potentially displaces the <see cref="ILogStream"/> service type. This mirrors
/// <see cref="ServerExecutionTargetResolver"/>'s own null-host-key branch, which likewise never queries
/// <see cref="IHostConnectionSource"/> at all for the local case — the difference here is that this type has
/// to actively rule remote hosts out first, since (unlike that resolver) it is never handed a host key to begin
/// with.
/// </para>
/// <para>
/// <strong>Why a matched remote host is not read through <c>local</c>'s twin, <see cref="SshDockerLogStream"/>,
/// applied uniformly.</strong> It is — for the remote branch. <see cref="SshDockerLogStream"/> runs
/// <c>docker logs</c> via <see cref="IExecutionTarget.ExecuteAsync"/>, which for a host-wide SSH session runs
/// the command on the HOST's own shell. Run the exact same way against the LOCAL <see cref="IExecutionTarget"/>
/// (a single-container-scoped Docker Engine API session — see <c>DockerExecutionTarget</c>), that call would
/// instead become a <c>docker exec</c> INTO the target container, trying to run the <c>docker</c> CLI binary
/// <em>inside the game server's own container</em> — not present there, and even if it were, without access to
/// the host's Docker socket. So the local branch is a genuine, necessary special case: it always defers to
/// <c>local</c> (typically <see cref="Servyx.Infrastructure.Docker.DockerLogStream"/>, which reads the Docker
/// Engine API's logs endpoint directly), never <see cref="SshDockerLogStream"/> built over
/// <see cref="IServerExecutionTargetResolver"/>'s own local-branch target.
/// </para>
/// </remarks>
public sealed class HostAwareLogStream : ILogStream
{
    /// <summary>The per-host budget for one <c>docker container inspect</c> probe leg.</summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly IHostConnectionSource _connections;
    private readonly IServerExecutionTargetResolver _resolver;
    private readonly ILogStream? _local;
    private readonly TimeSpan _probeTimeout;
    private readonly ILogger<HostAwareLogStream>? _logger;

    /// <summary>
    /// Creates a log stream that resolves each call's host through <paramref name="connections"/> and
    /// <paramref name="resolver"/>, falling back to <paramref name="local"/> for a server matched by no
    /// registered/configured host.
    /// </summary>
    /// <param name="connections">The live registered/configured ssh+docker host set.</param>
    /// <param name="resolver">Resolves a matched host key to its connected <see cref="IExecutionTarget"/>.</param>
    /// <param name="local">
    /// The log stream a server matched by no registered/configured host reads through — typically whatever
    /// <c>AddServyxDocker</c> registered. <see langword="null"/> when this process never registered one (a
    /// hypothetical ssh+docker-only host with no local Docker surface at all); see <see cref="ResolveAsync"/>
    /// for what that does to the local branch.
    /// </param>
    /// <param name="loggerFactory">Optional; used to log (and skip) a host whose probe throws.</param>
    /// <param name="probeTimeout">Overrides <see cref="DefaultProbeTimeout"/>.</param>
    public HostAwareLogStream(
        IHostConnectionSource connections,
        IServerExecutionTargetResolver resolver,
        ILogStream? local,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? probeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(resolver);
        if (probeTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(probeTimeout), timeout, "The probe timeout must be positive.");
        }

        _connections = connections;
        _resolver = resolver;
        _local = local;
        _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
        _logger = loggerFactory?.CreateLogger<HostAwareLogStream>();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A fixed value, not resolved per server: every <see cref="ILogStream"/> implementation this codebase has
    /// today (<see cref="Servyx.Infrastructure.Docker.DockerLogStream"/>, <see cref="SshDockerLogStream"/>)
    /// reports <see langword="false"/>, so there is nothing meaningful to route between yet.
    /// </remarks>
    public bool SupportsInput => false;

    /// <inheritdoc />
    public async IAsyncEnumerable<ConsoleLine> FollowAsync(
        string serverId, ConsoleTailOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(options);

        var stream = await ResolveAsync(serverId, ct).ConfigureAwait(false);
        await foreach (var line in stream.FollowAsync(serverId, options, ct).ConfigureAwait(false))
        {
            yield return line;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConsoleLine>> ReadAsync(
        string serverId, long fromOffset, int count, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var stream = await ResolveAsync(serverId, ct).ConfigureAwait(false);
        return await stream.ReadAsync(serverId, fromOffset, count, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteAsync(string serverId, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var stream = await ResolveAsync(serverId, ct).ConfigureAwait(false);
        await stream.WriteAsync(serverId, text, ct).ConfigureAwait(false);
    }

    /// <exception cref="InvalidOperationException">
    /// <paramref name="serverId"/> matched no registered/configured host, and this instance was constructed
    /// with no local fallback either — never silently degrades to a no-op stream.
    /// </exception>
    private async Task<ILogStream> ResolveAsync(string serverId, CancellationToken ct)
    {
        var hostKey = await FindHostKeyAsync(serverId, ct).ConfigureAwait(false);
        if (hostKey is null)
        {
            if (_local is null)
            {
                throw new InvalidOperationException(
                    $"Cannot resolve a log stream for server '{serverId}': it matched no registered/configured "
                    + "ssh+docker host, and this process registered no local log stream (AddServyxDocker was "
                    + "never called ahead of AddServyxSshDocker) for it to fall back to.");
            }

            return _local;
        }

        var target = await _resolver.ResolveAsync(serverId, hostKey, ct).ConfigureAwait(false);
        return new SshDockerLogStream(target);
    }

    /// <summary>
    /// Probes every currently-connectable registered/configured host concurrently for a container named
    /// <paramref name="serverId"/>, returning the first match's host key (or <see langword="null"/> if none
    /// match, including the common zero-host case, which short-circuits before any probe is issued).
    /// </summary>
    private async Task<string?> FindHostKeyAsync(string serverId, CancellationToken ct)
    {
        var hosts = await _connections.GetConnectionsAsync(ct).ConfigureAwait(false);
        if (hosts.Count == 0)
        {
            return null;
        }

        var probes = await Task.WhenAll(hosts.Select(host => ProbeAsync(host, serverId, ct))).ConfigureAwait(false);
        foreach (var (hostKey, found) in probes)
        {
            if (found)
            {
                return hostKey;
            }
        }

        return null;
    }

    private async Task<(string HostKey, bool Found)> ProbeAsync(HostConnection host, string serverId, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_probeTimeout);

        try
        {
            var result = await host.ExecutionTarget.ExecuteAsync(DockerCli.Inspect(serverId), budget.Token).ConfigureAwait(false);
            return (host.HostKey, result.Succeeded);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger?.LogWarning(
                "Host '{HostKey}' did not answer the probe for server '{ServerId}' within {Timeout}s; treating "
                + "it as not found on that host.",
                host.HostKey, serverId, _probeTimeout.TotalSeconds);
            return (host.HostKey, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Failed to probe host '{HostKey}' for server '{ServerId}'; treating it as not found on that host.",
                host.HostKey, serverId);
            return (host.HostKey, false);
        }
    }
}
