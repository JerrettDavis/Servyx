using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// <see cref="IMetricsSource"/> that routes each call to the specific host <paramref name="serverId"/>
/// actually lives on, instead of assuming the single process-wide metrics source the composition root
/// happened to wire — the same gap <see cref="HostAwareLogStream"/> closed for console reads.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this cannot simply take a host key as a parameter.</strong> <see cref="IMetricsSource"/>'s own
/// contract — <see cref="StreamAsync"/> — carries only <c>serverId</c>, the same shape
/// <c>LiveDashboardDataService</c> already depends on. So this type resolves the host itself, per call,
/// exactly the way <see cref="HostAwareLogStream"/> does.
/// </para>
/// <para>
/// <strong>How the host is found.</strong> Identical probe as <see cref="HostAwareLogStream"/>:
/// <see cref="IHostConnectionSource.GetConnectionsAsync"/> is queried for every currently-connectable
/// registered/configured host, and each one is asked (concurrently, via a read-only
/// <c>docker container inspect</c>) whether it has a container named <c>serverId</c>. Zero registered/configured
/// hosts short-circuits to the local branch with no round trip. A host that throws while being probed is
/// logged and treated as "not found there."
/// </para>
/// <para>
/// <strong>Local is the default, not a coin flip.</strong> A <c>serverId</c> matched by no registered/configured
/// host resolves through <c>local</c> — the metrics source <c>AddServyxDocker</c> registered, captured before
/// this type's DI registration potentially displaces the <see cref="IMetricsSource"/> service type.
/// </para>
/// <para>
/// <strong>Why a matched remote host is read through <see cref="SshDockerMetricsSource"/>, but the local branch
/// is not.</strong> <see cref="SshDockerMetricsSource"/> runs <c>docker stats</c> via
/// <see cref="IExecutionTarget.ExecuteAsync"/>, which for a host-wide SSH session runs the command on the
/// HOST's own shell — exactly what <c>docker stats &lt;container&gt;</c> needs. Run the exact same way against
/// the LOCAL <see cref="IExecutionTarget"/> (a single-container-scoped Docker Engine API session — see
/// <c>DockerExecutionTarget</c>), that call would instead become a <c>docker exec</c> INTO the target
/// container, trying to run the <c>docker</c> CLI binary <em>inside the game server's own container</em> — the
/// same verified caveat <see cref="HostAwareLogStream"/> documents for <c>docker logs</c>, and it applies here
/// unchanged: <see cref="SshDockerMetricsSource"/> reaches its data exclusively through
/// <see cref="IExecutionTarget.ExecuteAsync"/>, never a distinct mechanism that would sidestep the problem. So
/// the local branch always defers to <c>local</c> (typically
/// <see cref="Servyx.Infrastructure.Docker.DockerMetricsSource"/>, which reads the Docker Engine API's stats
/// endpoint directly), never <see cref="SshDockerMetricsSource"/> built over
/// <see cref="IServerExecutionTargetResolver"/>'s own local-branch target.
/// </para>
/// </remarks>
public sealed class HostAwareMetricsSource : IMetricsSource
{
    private readonly IHostConnectionSource _connections;
    private readonly IServerExecutionTargetResolver _resolver;
    private readonly IMetricsSource? _local;
    private readonly ILogger<HostAwareMetricsSource>? _logger;

    /// <summary>
    /// Creates a metrics source that resolves each call's host through <paramref name="connections"/> and
    /// <paramref name="resolver"/>, falling back to <paramref name="local"/> for a server matched by no
    /// registered/configured host.
    /// </summary>
    /// <param name="connections">The live registered/configured ssh+docker host set.</param>
    /// <param name="resolver">Resolves a matched host key to its connected <see cref="IExecutionTarget"/>.</param>
    /// <param name="local">
    /// The metrics source a server matched by no registered/configured host reads through — typically whatever
    /// <c>AddServyxDocker</c> registered. <see langword="null"/> when this process never registered one; see
    /// <see cref="ResolveAsync"/> for what that does to the local branch.
    /// </param>
    /// <param name="loggerFactory">Optional; used to log (and skip) a host whose probe throws.</param>
    public HostAwareMetricsSource(
        IHostConnectionSource connections,
        IServerExecutionTargetResolver resolver,
        IMetricsSource? local,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(resolver);

        _connections = connections;
        _resolver = resolver;
        _local = local;
        _logger = loggerFactory?.CreateLogger<HostAwareMetricsSource>();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ResourceSample> StreamAsync(
        string serverId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var source = await ResolveAsync(serverId, ct).ConfigureAwait(false);
        await foreach (var sample in source.StreamAsync(serverId, ct).ConfigureAwait(false))
        {
            yield return sample;
        }
    }

    /// <exception cref="InvalidOperationException">
    /// <paramref name="serverId"/> matched no registered/configured host, and this instance was constructed
    /// with no local fallback either — never silently degrades to an empty stream.
    /// </exception>
    private async Task<IMetricsSource> ResolveAsync(string serverId, CancellationToken ct)
    {
        var hostKey = await FindHostKeyAsync(serverId, ct).ConfigureAwait(false);
        if (hostKey is null)
        {
            if (_local is null)
            {
                throw new InvalidOperationException(
                    $"Cannot resolve a metrics source for server '{serverId}': it matched no registered/configured "
                    + "ssh+docker host, and this process registered no local metrics source (AddServyxDocker was "
                    + "never called ahead of AddServyxSshDocker) for it to fall back to.");
            }

            return _local;
        }

        var target = await _resolver.ResolveAsync(serverId, hostKey, ct).ConfigureAwait(false);
        return new SshDockerMetricsSource(target);
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
        try
        {
            var result = await host.ExecutionTarget.ExecuteAsync(DockerCli.Inspect(serverId), ct).ConfigureAwait(false);
            return (host.HostKey, result.Succeeded);
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
