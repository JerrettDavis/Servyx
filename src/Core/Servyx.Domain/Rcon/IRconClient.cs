namespace Servyx.Domain.Rcon;

/// <summary>Address of an RCON endpoint.</summary>
/// <param name="Host">Hostname or IP address.</param>
/// <param name="Port">TCP port.</param>
public sealed record RconEndpoint(string Host, int Port);

/// <summary>Raw response from an RCON invocation.</summary>
/// <param name="Text">The response text.</param>
/// <param name="Success">Whether the invocation succeeded.</param>
public sealed record RconResponse(string Text, bool Success);

/// <summary>A single connected player, as reported by the game.</summary>
/// <param name="Name">In-game display name.</param>
/// <param name="PlayerUid">The game's own player identifier.</param>
/// <param name="SteamId">The player's Steam identifier, if applicable.</param>
public sealed record PlayerInfo(string Name, string PlayerUid, string? SteamId);

/// <summary>A point-in-time list of connected players.</summary>
/// <param name="Timestamp">When the snapshot was taken.</param>
/// <param name="Players">The connected players.</param>
public sealed record PlayerSnapshot(DateTimeOffset Timestamp, IReadOnlyList<PlayerInfo> Players);

/// <summary>Low-level RCON protocol client.</summary>
public interface IRconClient
{
    /// <summary>Sends a single raw RCON command and returns its response.</summary>
    Task<RconResponse> SendAsync(RconEndpoint endpoint, string password, string command, CancellationToken ct = default);
}

/// <summary>A higher-level RCON session bound to a specific server and definition.</summary>
public interface IRconSession
{
    /// <summary>
    /// Invokes a command by its definition-declared command id (e.g. "players"), never by raw string, so
    /// the write guard can enforce each command's declared <c>readOnly</c> flag.
    /// </summary>
    Task<RconResponse> InvokeAsync(string commandId, IReadOnlyDictionary<string, string>? args, CancellationToken ct = default);

    /// <summary>
    /// Sends a raw, operator-authored RCON command as an audited escape hatch, bypassing the command
    /// catalogue. Always logged to the audit trail.
    /// </summary>
    Task<RconResponse> SendRawAsync(string rawCommand, CancellationToken ct = default);

    /// <summary>Returns the current list of connected players.</summary>
    Task<PlayerSnapshot> GetPlayersAsync(CancellationToken ct = default);
}

/// <summary>
/// Determines and establishes how an RCON (or other control-channel) endpoint is actually reached, since
/// the port is frequently not published to the host network.
/// </summary>
public interface IRconReachability
{
    /// <summary>"direct-tcp" | "docker-exec-tool" | "docker-exec-network" | "ssh-tunnel".</summary>
    string StrategyId { get; }

    /// <summary>
    /// Checks whether this strategy is currently usable. MUST be side-effect free: it may not publish a
    /// port or edit compose as a side effect of checking.
    /// </summary>
    Task<bool> IsAvailableAsync(RconEndpoint endpoint, CancellationToken ct = default);

    /// <summary>
    /// A short, human-readable, non-secret explanation of why the most recent <see cref="IsAvailableAsync"/>
    /// call returned <see langword="false"/> — or <see langword="null"/> if it has not been called yet, or
    /// last returned <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists so <c>RconReachabilityChain</c> can fold a WHY into <c>RconUnreachableException</c>
    /// instead of naming only the strategy ids it tried. It is diagnostics only: nothing reads this value to
    /// decide whether the strategy is available, and setting it never changes what
    /// <see cref="IsAvailableAsync"/> returns.
    /// </para>
    /// <para>
    /// <strong>Must never contain a password, secret, or credential.</strong> Implementations of
    /// <see cref="IsAvailableAsync"/> take no credential parameter, so there is structurally nothing secret
    /// for this property to leak; any captured process output (e.g. a probe's stderr) must be truncated to a
    /// bounded length before being stored here.
    /// </para>
    /// <para>
    /// A strategy is probed sequentially, one at a time, by <c>RconReachabilityChain</c> within a single
    /// <c>AcquireAsync</c> call, so a single mutable field per instance is enough for that use. An
    /// implementation shared across concurrent probes of the same instance should not assume this value stays
    /// attributable to one particular caller's call.
    /// </para>
    /// </remarks>
    string? LastUnavailableReason { get; }

    /// <summary>Establishes a session using this strategy.</summary>
    Task<IRconSession> AcquireAsync(RconEndpoint endpoint, CancellationToken ct = default);
}
