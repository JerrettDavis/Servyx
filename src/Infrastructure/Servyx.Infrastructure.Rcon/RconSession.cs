using System.Security.Cryptography;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.Rcon;

/// <summary>
/// Receives a record of every raw, catalogue-bypassing command issued through
/// <see cref="Domain.Rcon.IRconSession.SendRawAsync"/>.
/// </summary>
/// <remarks>
/// Implemented by the composition root, which is the layer that owns the audit trail. It is deliberately
/// not an <c>ILogger</c>: this assembly references no logging package at all, which is what makes "the RCON
/// password is never written to a log" structural rather than a convention (see the .csproj). The sink is
/// handed the command text and the endpoint, never a credential.
/// </remarks>
public interface IRconAuditSink
{
    /// <summary>Records that a raw command was issued, before it is sent.</summary>
    /// <param name="endpoint">The endpoint the command is bound for.</param>
    /// <param name="rawCommand">The operator-authored command text.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordRawCommandAsync(RconEndpoint endpoint, string rawCommand, CancellationToken ct = default);
}

/// <summary>
/// An <see cref="IRconSession"/> bound to one endpoint, one definition catalogue, and one credential URN.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The credential is a URN, never a value.</strong> This type holds a <see cref="SecretUrn"/> and an
/// <see cref="ISecretStore"/>. Each exchange resolves the secret, uses its bytes, and disposes the
/// <see cref="SecretLease"/> — zeroing the buffer — inside the same <c>using</c>. There is no
/// <c>string _password</c>, no cached lease, and no property through which a caller could read one. When
/// the client implements <see cref="ISecretAwareRconClient"/> (as <see cref="SourceRconClient"/> does) the
/// bytes go to the socket without ever becoming a <see cref="string"/>.
/// </para>
/// <para>
/// <strong>This type does not enforce the write mode.</strong> It resolves and renders; gating on each
/// command's declared <see cref="RconCommand.ReadOnly"/> flag is <see cref="WriteGuardedRconSession"/>'s
/// job, kept separate for the same reason <see cref="Domain.Transport.WriteGuardedExecutionTarget"/> is
/// separate from the transports it wraps: a guard that lives inside the thing it guards can be forgotten by
/// the next implementation.
/// </para>
/// </remarks>
public sealed class RconSession : IRconSession
{
    private readonly IRconClient _client;
    private readonly ISecretStore _secrets;
    private readonly SecretUrn _passwordUrn;
    private readonly IRconAuditSink? _audit;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a session.</summary>
    /// <param name="client">The protocol client every exchange goes through.</param>
    /// <param name="endpoint">The endpoint this session talks to.</param>
    /// <param name="catalog">The definition's declared control commands.</param>
    /// <param name="secrets">The store the credential is resolved from, at the point of use.</param>
    /// <param name="passwordUrn">Where the credential lives. A locator, never a value.</param>
    /// <param name="audit">
    /// The audit sink <see cref="SendRawAsync"/> writes to. When <see langword="null"/> the raw escape hatch
    /// is unavailable and says so: <c>docs/abstractions.md</c> §8 requires raw commands to be "always logged
    /// to the audit trail", and a session with nowhere to log has no way to honour that, so it refuses
    /// rather than sending an unrecorded command.
    /// </param>
    /// <param name="timeProvider">Clock used to stamp <see cref="PlayerSnapshot"/>s.</param>
    public RconSession(
        IRconClient client,
        RconEndpoint endpoint,
        RconCommandCatalog catalog,
        ISecretStore secrets,
        SecretUrn passwordUrn,
        IRconAuditSink? audit = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(secrets);

        if (string.IsNullOrEmpty(passwordUrn.Value))
        {
            throw new ArgumentException(
                "An RCON password URN is required. Build one with SecretUrn.Create, e.g. "
                + "SecretUrn.Create(\"server\", \"palworld-server\", \"rcon\", \"password\"); a default(SecretUrn) is "
                + "not a valid URN.",
                nameof(passwordUrn));
        }

        _client = client;
        Endpoint = endpoint;
        Catalog = catalog;
        _secrets = secrets;
        _passwordUrn = passwordUrn;
        _audit = audit;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The endpoint this session talks to.</summary>
    public RconEndpoint Endpoint { get; }

    /// <summary>The definition's declared control commands, and the only vocabulary <see cref="InvokeAsync"/> accepts.</summary>
    public RconCommandCatalog Catalog { get; }

    /// <inheritdoc />
    /// <exception cref="RconUnknownCommandException"><paramref name="commandId"/> is not in <see cref="Catalog"/>.</exception>
    /// <exception cref="RconArgumentException">An argument is missing, unexpected, or could alter the command.</exception>
    public async Task<RconResponse> InvokeAsync(
        string commandId,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        var rendered = Catalog.Render(commandId, args);
        return await SendAsync(rendered, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No <see cref="IRconAuditSink"/> was supplied.</exception>
    public async Task<RconResponse> SendRawAsync(string rawCommand, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCommand);

        if (_audit is null)
        {
            throw new InvalidOperationException(
                "The raw RCON escape hatch requires an audit sink, and this session has none. A raw command bypasses "
                + "the definition's command catalogue and therefore its readOnly classification; the audit record is "
                + "the only remaining account of what was run, so an unrecorded raw command is refused outright.");
        }

        // Rejected before it is recorded and before it is sent: a "command" carrying an embedded newline is
        // two commands, and an audit line naming only the first would be a false record.
        RconCommandText.EnsureSingleCommandLine(rawCommand);

        await _audit.RecordRawCommandAsync(Endpoint, rawCommand, ct).ConfigureAwait(false);
        return await SendAsync(rawCommand, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Invokes the catalogue's <c>players</c> command and parses its reply with the
    /// <c>csv-with-header</c> shape the definition's <c>control.players.parsers</c> block declares for
    /// <c>rcon.players</c>.
    /// </remarks>
    public async Task<PlayerSnapshot> GetPlayersAsync(CancellationToken ct = default)
    {
        var response = await InvokeAsync(PlayersCommandId, null, ct).ConfigureAwait(false);
        return new PlayerSnapshot(_timeProvider.GetUtcNow(), RconPlayerListParser.Parse(response.Text));
    }

    /// <summary>The catalogue id <see cref="GetPlayersAsync"/> invokes, as declared by the definition.</summary>
    public const string PlayersCommandId = "players";

    private async Task<RconResponse> SendAsync(string command, CancellationToken ct)
    {
        using var lease = await _secrets.GetAsync(_passwordUrn, ct).ConfigureAwait(false)
            ?? throw new RconAuthenticationFailedException(
                $"No RCON credential is stored at '{_passwordUrn.Value}', so the control channel for "
                + $"{SourceRconConnection.Describe(Endpoint)} cannot authenticate. Store the server's admin password "
                + "at that URN before enabling the channel.");

        if (_client is ISecretAwareRconClient byteAware)
        {
            // The preferred path: the credential goes from the lease's buffer to the packet buffer without
            // ever becoming a string the runtime could intern or copy where nothing can erase it. The one
            // intermediate array exists only because a ReadOnlySpan cannot cross an await, and it is zeroed
            // in the finally regardless of how the exchange ends.
            var password = lease.Value.ToArray();

            try
            {
                return await byteAware.SendAsync(Endpoint, password, command, ct).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(password);
            }
        }

        // Fallback for a third-party IRconClient that only offers the string-shaped domain contract. The
        // string it receives cannot be scrubbed afterwards, which is exactly why ISecretAwareRconClient
        // exists and why Servyx's own client implements it.
        return await _client.SendAsync(Endpoint, lease.ToUtf8String(), command, ct).ConfigureAwait(false);
    }
}
