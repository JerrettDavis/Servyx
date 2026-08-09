using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Rcon;

/// <summary>
/// A decorator over <see cref="IRconSession"/> that refuses any command the definition did not declare
/// <see cref="RconCommand.ReadOnly"/> unless the owning server's <see cref="WriteMode"/> is
/// <see cref="WriteMode.Enabled"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the seam <c>docs/abstractions.md</c> §8's implementer note describes.</strong> A control
/// command cannot be classified by verb — <c>docker exec</c> is a mutating Docker API call whether it runs
/// <c>Info</c> or <c>Shutdown</c>, and a Source RCON <c>SERVERDATA_EXECCOMMAND</c> packet looks identical
/// either way. Classification therefore comes from declared intent: the <c>readOnly</c> flag the definition
/// attaches to each command. <c>info</c> and <c>players</c> pass this gate on a read-only server;
/// <c>save</c>, <c>broadcast</c>, <c>kick</c>, <c>ban</c>, <c>shutdown</c> and <c>doexit</c> do not, even
/// though all eight travel the identical code path.
/// </para>
/// <para>
/// <strong>An unknown command id is refused before the mode is even consulted.</strong> An id with no
/// catalogue entry carries no classification, so there is nothing to gate; letting it through on an
/// <see cref="WriteMode.Enabled"/> server and refusing it elsewhere would make the guard's behaviour depend
/// on a flag rather than on the definition.
/// </para>
/// <para>
/// <strong><see cref="SendRawAsync"/> always requires <see cref="WriteMode.Enabled"/>.</strong> A raw,
/// operator-authored line has, by construction, no declared intent. The guard will not parse it and guess —
/// that is exactly the "guess intent from the verb" mistake the definition exists to avoid — so the escape
/// hatch is treated as mutating and is unavailable on a read-only server.
/// </para>
/// <para>
/// <strong><see cref="Mode"/> is re-read per command, not captured when the session was built.</strong> This
/// is the second, entirely independent capture site for a server's write posture — the first being
/// <c>WriteGuardedExecutionTarget</c> on the exec path — and it fails independently of it. RCON sessions are
/// memoized per channel for the life of the process and are never evicted once acquisition succeeds, so a
/// posture baked in at build time would let mutating control commands (<c>save</c>, <c>broadcast</c>,
/// <c>shutdown</c>) keep flowing on an already-open session long after an operator revoked the grant. The
/// <see cref="WriteGuardedRconSession(IRconSession, RconCommandCatalog, Func{WriteMode}, string?)"/>
/// constructor takes a live source instead, so revocation lands on the next command without the channel
/// cache having to be evicted at all.
/// </para>
/// </remarks>
public sealed class WriteGuardedRconSession : IRconSession
{
    private readonly IRconSession _inner;
    private readonly RconCommandCatalog _catalog;
    private readonly WriteMode _fixedMode;
    private readonly Func<WriteMode>? _liveMode;

    /// <summary>Creates a guard over <paramref name="inner"/> for a server fixed in <paramref name="mode"/>.</summary>
    /// <param name="inner">The session every permitted call delegates to.</param>
    /// <param name="catalog">The catalogue whose <c>readOnly</c> flags classify each command.</param>
    /// <param name="mode">The owning server's write posture, held for this object's whole lifetime.</param>
    /// <param name="targetDescription">
    /// A human-readable identifier for the guarded server, used only in refusal messages so an operator can
    /// tell which server refused. Never used for any decision.
    /// </param>
    /// <remarks>
    /// A guard built this way cannot observe a grant revoked after construction. Prefer the live-source
    /// overload for any session that is cached — see this type's own remarks.
    /// </remarks>
    public WriteGuardedRconSession(
        IRconSession inner,
        RconCommandCatalog catalog,
        WriteMode mode,
        string? targetDescription = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(catalog);

        _inner = inner;
        _catalog = catalog;
        _fixedMode = mode;
        TargetDescription = targetDescription;
    }

    /// <summary>
    /// Creates a guard that reads the server's posture from <paramref name="mode"/> on every gated call, so a
    /// grant flipped after this session was acquired takes effect on the next command.
    /// </summary>
    /// <param name="inner">The session every permitted call delegates to.</param>
    /// <param name="catalog">The catalogue whose <c>readOnly</c> flags classify each command.</param>
    /// <param name="mode">
    /// The live posture source. Invoked per gated call; expected to be a cache lookup rather than a
    /// round-trip, exactly as the exec path's <c>IWriteModeResolver</c> is.
    /// </param>
    /// <param name="targetDescription">A human-readable identifier used only in refusal messages.</param>
    public WriteGuardedRconSession(
        IRconSession inner,
        RconCommandCatalog catalog,
        Func<WriteMode> mode,
        string? targetDescription = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(mode);

        _inner = inner;
        _catalog = catalog;
        _liveMode = mode;
        _fixedMode = WriteMode.ReadOnly;
        TargetDescription = targetDescription;
    }

    /// <summary>
    /// The write posture this guard enforces <em>right now</em>. Re-read on every access when this guard was
    /// built over a live source; a constant when it was built over a fixed mode.
    /// </summary>
    public WriteMode Mode => _liveMode is null ? _fixedMode : _liveMode();

    /// <summary>A human-readable identifier for the guarded server, used only in refusal messages.</summary>
    public string? TargetDescription { get; }

    /// <summary>Whether <see cref="Mode"/> permits mutating control commands to reach the inner session.</summary>
    public bool WritesPermitted => Mode == WriteMode.Enabled;

    /// <inheritdoc />
    /// <exception cref="WritesDisabledException">
    /// The command is not declared <c>readOnly</c> and <see cref="Mode"/> is not
    /// <see cref="WriteMode.Enabled"/>. Thrown before the inner session, the secret store, or the socket is
    /// touched at all.
    /// </exception>
    public Task<RconResponse> InvokeAsync(
        string commandId,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        // Throws RconUnknownCommandException for an id the definition does not declare, whatever the mode.
        // Evaluated before the posture is read at all, so a read-only command never depends on the grant
        // store being reachable — the same ordering the exec-path guard uses for CommandIntent.ReadOnly.
        var command = _catalog.Get(commandId);

        if (command.ReadOnly)
        {
            return _inner.InvokeAsync(commandId, args, ct);
        }

        // Read once, so the refusal message names the posture the decision was actually taken against.
        var mode = Mode;
        if (mode != WriteMode.Enabled)
        {
            var where = TargetDescription is null ? string.Empty : $" on '{TargetDescription}'";
            throw new WritesDisabledException(
                $"Refusing to run control command '{command.Id}'{where}: the definition declares it as mutating "
                + $"(readOnly: false) and the server's write mode is {mode}. Mutating control commands require "
                + $"{nameof(WriteMode)}.{nameof(WriteMode.Enabled)}, set per server and never globally.");
        }

        return _inner.InvokeAsync(commandId, args, ct);
    }

    /// <inheritdoc />
    /// <exception cref="WritesDisabledException"><see cref="Mode"/> is not <see cref="WriteMode.Enabled"/>.</exception>
    public Task<RconResponse> SendRawAsync(string rawCommand, CancellationToken ct = default)
    {
        var mode = Mode;
        if (mode != WriteMode.Enabled)
        {
            var where = TargetDescription is null ? string.Empty : $" on '{TargetDescription}'";
            throw new WritesDisabledException(
                $"Refusing to send a raw RCON command{where}: a raw line carries no declared readOnly "
                + $"classification, so it is treated as mutating, and the server's write mode is {mode}.");
        }

        return _inner.SendRawAsync(rawCommand, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegated unguarded. Listing players is the archetypal read-only control command — the definition
    /// declares <c>players</c> with <c>readOnly: true</c> — and a read-only server that could not answer
    /// "who is connected?" would defeat the purpose of the read-only tier.
    /// </remarks>
    public Task<PlayerSnapshot> GetPlayersAsync(CancellationToken ct = default) => _inner.GetPlayersAsync(ct);
}
