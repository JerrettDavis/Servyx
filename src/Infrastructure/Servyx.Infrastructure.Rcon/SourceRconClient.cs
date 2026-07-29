using System.Security.Cryptography;
using System.Text;
using Servyx.Domain.Connectors;
using Servyx.Domain.Rcon;

namespace Servyx.Infrastructure.Rcon;

/// <summary>
/// An <see cref="IRconClient"/> that can be handed the credential as raw bytes, so a
/// <see cref="Domain.Secrets.SecretLease"/> never has to be turned into a <see cref="string"/> on the way
/// to the socket.
/// </summary>
/// <remarks>
/// <see cref="IRconClient.SendAsync(RconEndpoint, string, string, CancellationToken)"/> takes a
/// <see cref="string"/> password because that is the published domain contract, and a .NET string cannot be
/// reliably erased once it exists — which is precisely what
/// <see cref="Domain.Secrets.SecretLease"/> exists to avoid. This interface is the byte-shaped path
/// alongside it: <see cref="RconSession"/> prefers it whenever the configured client implements it, and
/// falls back to the string overload only for a third-party client that does not.
/// </remarks>
public interface ISecretAwareRconClient : IRconClient
{
    /// <summary>Sends a single raw RCON command, authenticating with <paramref name="password"/>'s bytes.</summary>
    /// <param name="endpoint">Where to connect.</param>
    /// <param name="password">The credential's raw bytes, typically a <see cref="Domain.Secrets.SecretLease"/>'s value.</param>
    /// <param name="command">The already-rendered command line.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RconResponse> SendAsync(RconEndpoint endpoint, ReadOnlyMemory<byte> password, string command, CancellationToken ct = default);
}

/// <summary>
/// The Source RCON protocol client: one connection, one authentication, one command, one close.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No connection pooling in this milestone, on purpose.</strong> A pooled RCON connection is a
/// long-lived authenticated capability sitting in memory, and reusing one across commands means a
/// half-consumed multi-packet response from a previous call can be mis-attributed to the next. Connecting
/// per command costs a TCP handshake and an auth round trip, which is irrelevant next to the 30-second
/// quiesce window this exists to serve, and it makes every exchange independently correct.
/// </para>
/// <para>
/// <strong>What <see cref="RconResponse.Success"/> means here.</strong> It means the server authenticated
/// the session, accepted the command and answered it. Source RCON has no status code — the reply is just
/// the console text the game would have printed — so a client cannot honestly infer whether the game liked
/// the command from the protocol alone. Anything that did not complete cleanly (rejected credential,
/// unreachable endpoint, framing violation, timeout) is raised as an <see cref="RconException"/> and never
/// returned as a <see cref="RconResponse"/> with <see cref="RconResponse.Success"/> set either way, so a
/// caller can never mistake a failure for an empty result.
/// </para>
/// </remarks>
public sealed class SourceRconClient : ISecretAwareRconClient
{
    private readonly TimeoutPolicy _timeouts;

    /// <summary>Creates a client.</summary>
    /// <param name="timeouts">
    /// The connector-style timeout policy. <see cref="TimeoutPolicy.Connect"/> bounds the TCP connect and
    /// the authentication handshake; <see cref="TimeoutPolicy.Command"/> bounds the command exchange.
    /// Defaults to <see cref="TimeoutPolicy.Default"/> — the same 10s/30s every other Servyx connector uses.
    /// </param>
    public SourceRconClient(TimeoutPolicy? timeouts = null) => _timeouts = timeouts ?? TimeoutPolicy.Default;

    /// <summary>The timeout policy every exchange from this client is bounded by.</summary>
    public TimeoutPolicy Timeouts => _timeouts;

    /// <inheritdoc />
    /// <remarks>
    /// Prefer <see cref="SendAsync(RconEndpoint, ReadOnlyMemory{byte}, string, CancellationToken)"/>. This
    /// overload exists to satisfy <see cref="IRconClient"/>; it encodes <paramref name="password"/> to a
    /// byte array, uses it, and zeroes that array — but the caller's <see cref="string"/> itself remains in
    /// managed memory until the garbage collector reclaims it, which no library can prevent.
    /// </remarks>
    public async Task<RconResponse> SendAsync(RconEndpoint endpoint, string password, string command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(password);

        var bytes = Encoding.UTF8.GetBytes(password);

        try
        {
            return await SendAsync(endpoint, bytes, command, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <inheritdoc />
    public async Task<RconResponse> SendAsync(
        RconEndpoint endpoint,
        ReadOnlyMemory<byte> password,
        string command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(command);

        // Belt and braces behind RconCommandCatalog's argument rules: whatever route produced this line,
        // exactly one command reaches the wire per packet. A raw command from the audited escape hatch
        // arrives here too, and it gets the same treatment.
        RconCommandText.EnsureSingleCommandLine(command);

        await using var connection = await SourceRconConnection
            .ConnectAsync(endpoint, _timeouts.Connect, ct)
            .ConfigureAwait(false);

        await connection.AuthenticateAsync(password, _timeouts.Connect, ct).ConfigureAwait(false);

        var text = await connection.ExecuteAsync(command, _timeouts.Command, ct).ConfigureAwait(false);
        return new RconResponse(text, Success: true);
    }
}
