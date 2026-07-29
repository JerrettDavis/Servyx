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
/// </remarks>
public sealed class WriteGuardedRconSession : IRconSession
{
    private readonly IRconSession _inner;
    private readonly RconCommandCatalog _catalog;

    /// <summary>Creates a guard over <paramref name="inner"/> for a server in <paramref name="mode"/>.</summary>
    /// <param name="inner">The session every permitted call delegates to.</param>
    /// <param name="catalog">The catalogue whose <c>readOnly</c> flags classify each command.</param>
    /// <param name="mode">The owning server's write posture.</param>
    /// <param name="targetDescription">
    /// A human-readable identifier for the guarded server, used only in refusal messages so an operator can
    /// tell which server refused. Never used for any decision.
    /// </param>
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
        Mode = mode;
        TargetDescription = targetDescription;
    }

    /// <summary>The write posture this guard enforces.</summary>
    public WriteMode Mode { get; }

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
        var command = _catalog.Get(commandId);

        if (!command.ReadOnly && !WritesPermitted)
        {
            var where = TargetDescription is null ? string.Empty : $" on '{TargetDescription}'";
            throw new WritesDisabledException(
                $"Refusing to run control command '{command.Id}'{where}: the definition declares it as mutating "
                + $"(readOnly: false) and the server's write mode is {Mode}. Mutating control commands require "
                + $"{nameof(WriteMode)}.{nameof(WriteMode.Enabled)}, set per server and never globally.");
        }

        return _inner.InvokeAsync(commandId, args, ct);
    }

    /// <inheritdoc />
    /// <exception cref="WritesDisabledException"><see cref="Mode"/> is not <see cref="WriteMode.Enabled"/>.</exception>
    public Task<RconResponse> SendRawAsync(string rawCommand, CancellationToken ct = default)
    {
        if (!WritesPermitted)
        {
            var where = TargetDescription is null ? string.Empty : $" on '{TargetDescription}'";
            throw new WritesDisabledException(
                $"Refusing to send a raw RCON command{where}: a raw line carries no declared readOnly "
                + $"classification, so it is treated as mutating, and the server's write mode is {Mode}.");
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
