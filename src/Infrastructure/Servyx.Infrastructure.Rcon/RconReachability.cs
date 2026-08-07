using System.Net.Sockets;
using Servyx.Domain.Connectors;
using Servyx.Domain.Rcon;

namespace Servyx.Infrastructure.Rcon;

/// <summary>
/// The <c>direct-tcp</c> reachability strategy: connect straight to the RCON port on the host network.
/// </summary>
/// <remarks>
/// <para>
/// The definition lists three strategies, in order — <c>direct-tcp</c>, then <c>docker-exec-tool</c> running
/// the image's bundled <c>rcon-cli</c> (see <see cref="DockerExecToolRconReachability"/>), then
/// <c>docker-exec-network</c>. The last one is still represented by <see cref="UnavailableRconReachability"/>,
/// which reports <see cref="IsAvailableAsync"/> as <see langword="false"/> and refuses
/// <see cref="AcquireAsync"/> with a stated reason, rather than by a stub that pretends.
/// </para>
/// <para>
/// <strong>Known limitation on the real Palworld deployment.</strong> That definition declares RCON 25575
/// with <c>published: false</c>, and the adopted <c>thijsvanloef/palworld-server-docker</c> container does
/// not publish it. So on that host this strategy will correctly report itself unavailable — but the chain
/// falls through to <see cref="DockerExecToolRconReachability"/>, which is what actually reaches RCON on that
/// container. If a deployment somehow lacked both, <see cref="RconReachabilityChain"/> would raise
/// <see cref="RconUnreachableException"/> naming every strategy it tried.
/// </para>
/// <para>
/// <see cref="IsAvailableAsync"/> is side-effect free as the contract demands: it opens a TCP connection,
/// observes whether the port accepts, and closes it. It publishes no port, edits no compose file and
/// restarts nothing.
/// </para>
/// </remarks>
public sealed class DirectTcpRconReachability : IRconReachability
{
    /// <summary>The strategy id this type implements.</summary>
    public const string Id = "direct-tcp";

    private readonly Func<RconEndpoint, IRconSession> _sessionFactory;
    private readonly TimeSpan _probeTimeout;
    private string? _lastUnavailableReason;

    /// <summary>Creates the strategy.</summary>
    /// <param name="sessionFactory">
    /// Builds the session for an endpoint this strategy can reach. Taking a factory rather than a secret
    /// store keeps credential resolution in exactly one place — <see cref="RconSession"/> — instead of
    /// duplicating it into every reachability strategy.
    /// </param>
    /// <param name="probeTimeout">
    /// How long <see cref="IsAvailableAsync"/> waits for the port to accept. Defaults to
    /// <see cref="TimeoutPolicy.Default"/>'s connect timeout.
    /// </param>
    public DirectTcpRconReachability(Func<RconEndpoint, IRconSession> sessionFactory, TimeSpan? probeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);

        _sessionFactory = sessionFactory;
        _probeTimeout = probeTimeout ?? TimeoutPolicy.Default.Connect;
    }

    /// <inheritdoc />
    public string StrategyId => Id;

    /// <inheritdoc />
    /// <remarks>
    /// On failure, records a short reason in <see cref="LastUnavailableReason"/> — the socket error (e.g.
    /// "connection refused", the exact shape of an unpublished port) or that the probe window elapsed. Built
    /// entirely from <see cref="SocketException"/>/timeout facts about the TCP handshake: this method takes
    /// no credential, so there is nothing secret it could fold in.
    /// </remarks>
    public async Task<bool> IsAvailableAsync(RconEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        using var tcp = new TcpClient();
        using var deadline = new CancellationTokenSource(_probeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        try
        {
            await tcp.ConnectAsync(endpoint.Host, endpoint.Port, linked.Token).ConfigureAwait(false);
            _lastUnavailableReason = null;
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The port did not accept inside the probe window. Unavailable, not an error.
            _lastUnavailableReason = $"timed out waiting {_probeTimeout} for the port to accept a connection";
            return false;
        }
        catch (SocketException ex)
        {
            _lastUnavailableReason = $"TCP connect failed ({ex.SocketErrorCode})";
            return false;
        }
    }

    /// <inheritdoc />
    public string? LastUnavailableReason => _lastUnavailableReason;

    /// <inheritdoc />
    public Task<IRconSession> AcquireAsync(RconEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return Task.FromResult(_sessionFactory(endpoint));
    }
}

/// <summary>
/// A reachability strategy Servyx names but has not implemented, which reports itself unavailable and
/// refuses to be acquired.
/// </summary>
/// <remarks>
/// The alternative — omitting the strategy entirely — would make a definition that lists
/// <c>docker-exec-tool</c> look like a definition that lists nothing, and an operator would have no way to
/// tell "Servyx cannot do this yet" from "this definition never asked for it". This type keeps the declared
/// ordering visible and honest.
/// </remarks>
public sealed class UnavailableRconReachability : IRconReachability
{
    /// <summary>Creates an unavailable strategy.</summary>
    /// <param name="strategyId">The strategy id being stood in for.</param>
    /// <param name="reason">Why it is unavailable, surfaced verbatim from <see cref="AcquireAsync"/>.</param>
    public UnavailableRconReachability(string strategyId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        StrategyId = strategyId;
        Reason = reason;
    }

    /// <summary>The <c>docker-exec-network</c> strategy — reaching the port from a sibling container on the same network.</summary>
    public static UnavailableRconReachability DockerExecNetwork { get; } = new(
        "docker-exec-network",
        "Servyx cannot yet reach RCON from a sibling container on the workload's Docker network: starting a "
        + "helper container is a provisioning action this milestone does not take for a control probe.");

    /// <summary>The <c>ssh-tunnel</c> strategy — forwarding the port over an SSH session to the host.</summary>
    public static UnavailableRconReachability SshTunnel { get; } = new(
        "ssh-tunnel",
        "Servyx cannot yet reach RCON through an SSH port-forward: no tunnel is established for control "
        + "channels at this milestone.");

    /// <inheritdoc />
    public string StrategyId { get; }

    /// <summary>Why this strategy is unavailable.</summary>
    public string Reason { get; }

    /// <inheritdoc />
    /// <remarks>Always <see langword="false"/>, and it costs nothing — no socket is opened to find out.</remarks>
    public Task<bool> IsAvailableAsync(RconEndpoint endpoint, CancellationToken ct = default) => Task.FromResult(false);

    /// <inheritdoc />
    /// <remarks>Always <see cref="Reason"/>: this strategy's unavailability is declared, not probed.</remarks>
    public string? LastUnavailableReason => Reason;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. See <see cref="Reason"/>.</exception>
    public Task<IRconSession> AcquireAsync(RconEndpoint endpoint, CancellationToken ct = default) =>
        throw new NotSupportedException(Reason);
}

/// <summary>
/// The definition's ordered <c>reachability</c> list: the first strategy that reports itself available
/// wins.
/// </summary>
public sealed class RconReachabilityChain
{
    private readonly IReadOnlyList<IRconReachability> _strategies;

    /// <summary>Creates a chain over <paramref name="strategies"/>, in the definition's declared order.</summary>
    /// <param name="strategies">The ordered strategies.</param>
    public RconReachabilityChain(IEnumerable<IRconReachability> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);

        _strategies = [.. strategies];

        if (_strategies.Count == 0)
        {
            throw new ArgumentException("A reachability chain must declare at least one strategy.", nameof(strategies));
        }
    }

    /// <summary>The strategies, in the order they are tried.</summary>
    public IReadOnlyList<IRconReachability> Strategies => _strategies;

    /// <summary>Acquires a session using the first available strategy.</summary>
    /// <param name="endpoint">The endpoint to reach.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="RconUnreachableException">
    /// No strategy reported itself available. The message names every strategy that was tried, so an
    /// operator can see which ones exist and which are merely not implemented.
    /// </exception>
    public async Task<IRconSession> AcquireAsync(RconEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var attempts = new List<string>(_strategies.Count);

        foreach (var strategy in _strategies)
        {
            if (await strategy.IsAvailableAsync(endpoint, ct).ConfigureAwait(false))
            {
                return await strategy.AcquireAsync(endpoint, ct).ConfigureAwait(false);
            }

            attempts.Add(DescribeAttempt(strategy));
        }

        throw new RconUnreachableException(
            $"No reachability strategy could reach the RCON endpoint {SourceRconConnection.Describe(endpoint)}. "
            + $"Tried, in order: {string.Join(", ", attempts)}.");
    }

    /// <summary>
    /// Renders one strategy's attempt for <see cref="RconUnreachableException"/>'s message: the strategy id,
    /// plus its <see cref="IRconReachability.LastUnavailableReason"/> in parentheses when it reported one.
    /// </summary>
    private static string DescribeAttempt(IRconReachability strategy)
    {
        var reason = strategy.LastUnavailableReason;
        return string.IsNullOrWhiteSpace(reason) ? strategy.StrategyId : $"{strategy.StrategyId} ({reason})";
    }
}
