using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Servyx.Domain.Rcon;

namespace Servyx.Infrastructure.Rcon;

/// <summary>
/// One authenticated TCP conversation with a Source RCON server: connect, authenticate, execute, close.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every read is bounded.</strong> A server that accepts the connection and then says nothing is a
/// completely ordinary failure mode — a game still loading its world will do exactly that — and a client
/// that blocks on it forever takes the caller with it. Each phase runs under a linked
/// <see cref="CancellationTokenSource"/> combining the caller's token with the phase's own deadline, and a
/// deadline that fires while the caller's token is still live surfaces as
/// <see cref="RconTimeoutException"/> rather than as a cancellation the caller never requested.
/// </para>
/// <para>
/// <strong>The password never becomes a field, a property, or a string.</strong> It arrives as bytes,
/// is copied once into the send buffer by <see cref="SourceRconProtocol.Encode"/>, and that buffer is
/// zeroed with <see cref="CryptographicOperations.ZeroMemory"/> in a <c>finally</c> the moment the write
/// completes. Nothing on this type retains it, and no exception message is built from it.
/// </para>
/// </remarks>
internal sealed class SourceRconConnection : IAsyncDisposable
{
    /// <summary>
    /// How many unexpected packets to tolerate while waiting for an authentication verdict before giving
    /// up. Several server implementations emit a junk empty <c>SERVERDATA_RESPONSE_VALUE</c> ahead of the
    /// real <c>SERVERDATA_AUTH_RESPONSE</c>; an unbounded skip loop would let a hostile peer keep Servyx
    /// reading forever inside its timeout.
    /// </summary>
    private const int MaxAuthPreamblePackets = 8;

    /// <summary>
    /// A ceiling on the number of packets one command response may be reassembled from. Well beyond any
    /// real <c>ShowPlayers</c> on a 32-slot server, and low enough that a peer cannot stream indefinitely.
    /// </summary>
    private const int MaxResponsePackets = 256;

    /// <summary>A ceiling on the reassembled response length, for the same reason as <see cref="MaxResponsePackets"/>.</summary>
    private const int MaxResponseChars = 1024 * 1024;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly RconEndpoint _endpoint;

    /// <summary>
    /// The next request id. Starts at 1 so no id Servyx generates can ever collide with
    /// <see cref="SourceRconProtocol.AuthFailureId"/>, and increments monotonically so the sentinel that
    /// terminates a multi-packet response is always distinguishable from the command it follows.
    /// </summary>
    private int _nextId = 1;

    private SourceRconConnection(TcpClient tcp, NetworkStream stream, RconEndpoint endpoint)
    {
        _tcp = tcp;
        _stream = stream;
        _endpoint = endpoint;
    }

    /// <summary>Opens a TCP connection to <paramref name="endpoint"/>.</summary>
    /// <param name="endpoint">Where to connect.</param>
    /// <param name="connectTimeout">How long the connect may take.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <exception cref="RconUnreachableException">The endpoint refused, or is not routable.</exception>
    /// <exception cref="RconTimeoutException"><paramref name="connectTimeout"/> elapsed first.</exception>
    internal static async Task<SourceRconConnection> ConnectAsync(
        RconEndpoint endpoint,
        TimeSpan connectTimeout,
        CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };

        try
        {
            using var deadline = new CancellationTokenSource(connectTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

            try
            {
                await tcp.ConnectAsync(endpoint.Host, endpoint.Port, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RconTimeoutException(
                    $"Connecting to the RCON endpoint {Describe(endpoint)} did not complete within {connectTimeout}.");
            }
            catch (SocketException ex)
            {
                throw new RconUnreachableException(
                    $"The RCON endpoint {Describe(endpoint)} could not be reached: {ex.SocketErrorCode}.", ex);
            }

            return new SourceRconConnection(tcp, tcp.GetStream(), endpoint);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Performs the <c>SERVERDATA_AUTH</c> handshake.</summary>
    /// <param name="password">The credential's raw bytes. Never retained, never stringified.</param>
    /// <param name="timeout">How long the handshake may take.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <exception cref="RconAuthenticationFailedException">The server answered with id <c>-1</c>.</exception>
    /// <exception cref="RconProtocolException">The server's reply was not a recognisable auth verdict.</exception>
    internal async Task AuthenticateAsync(ReadOnlyMemory<byte> password, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        var requestId = NextId();

        try
        {
            await SendCredentialAsync(requestId, password, linked.Token).ConfigureAwait(false);

            for (var skipped = 0; skipped <= MaxAuthPreamblePackets; skipped++)
            {
                var packet = await ReadPacketAsync(linked.Token).ConfigureAwait(false);

                // Several implementations emit an empty SERVERDATA_RESPONSE_VALUE immediately before the
                // real verdict. It carries no information; the verdict is the next packet.
                if (packet.Type == SourceRconProtocol.ServerDataResponseValue)
                {
                    continue;
                }

                if (packet.Type != SourceRconProtocol.ServerDataAuthResponse)
                {
                    throw new RconProtocolException(
                        $"The RCON endpoint {Describe(_endpoint)} answered the authentication request with packet type "
                        + $"{packet.Type}, which is not SERVERDATA_AUTH_RESPONSE ({SourceRconProtocol.ServerDataAuthResponse}).");
                }

                // THE trap. An empty body plus id -1 is a rejection, not a successful silent command.
                if (packet.Id == SourceRconProtocol.AuthFailureId)
                {
                    throw new RconAuthenticationFailedException(
                        $"The RCON endpoint {Describe(_endpoint)} rejected the stored credential "
                        + "(SERVERDATA_AUTH_RESPONSE with request id -1). No command was sent.");
                }

                if (packet.Id != requestId)
                {
                    throw new RconProtocolException(
                        $"The RCON endpoint {Describe(_endpoint)} answered the authentication request with id "
                        + $"{packet.Id}, which matches neither the request ({requestId}) nor a rejection (-1).");
                }

                return;
            }

            throw new RconProtocolException(
                $"The RCON endpoint {Describe(_endpoint)} sent more than {MaxAuthPreamblePackets} packets without "
                + "ever answering the authentication request.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RconTimeoutException(
                $"Authenticating against the RCON endpoint {Describe(_endpoint)} did not complete within {timeout}.");
        }
    }

    /// <summary>Executes one already-rendered command line and returns the reassembled response text.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Multi-packet reassembly.</strong> A Source RCON response longer than a single packet is
    /// split across several <c>SERVERDATA_RESPONSE_VALUE</c> packets that all carry the command's request
    /// id, and the protocol provides no "this was the last one" flag. The standard technique — used here —
    /// is a sentinel: immediately after the command, a second, empty <c>SERVERDATA_EXECCOMMAND</c> is sent
    /// with a different id. Servers answer requests in order, so the first packet bearing the sentinel's id
    /// is proof that every fragment of the real response has already arrived. Fragments are concatenated in
    /// arrival order; the sentinel's own body (some servers reply <c>Unknown request</c> to it) is
    /// discarded.
    /// </para>
    /// <para>
    /// A single-packet response therefore costs one extra round trip and never a truncated result, which is
    /// the right trade for a channel whose whole purpose here is to make a backup trustworthy.
    /// </para>
    /// </remarks>
    /// <param name="command">The rendered command line.</param>
    /// <param name="timeout">How long the exchange may take.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal async Task<string> ExecuteAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(command);
        if (body.Length > SourceRconProtocol.MaximumCommandBytes)
        {
            throw new RconArgumentException(
                $"The rendered RCON command is {body.Length} bytes, beyond the {SourceRconProtocol.MaximumCommandBytes}-byte "
                + "limit a Source server will accept without silently truncating it.");
        }

        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        var commandId = NextId();
        var sentinelId = NextId();

        try
        {
            await WriteAsync(
                SourceRconProtocol.Encode(commandId, SourceRconProtocol.ServerDataExecCommand, body),
                linked.Token).ConfigureAwait(false);
            await WriteAsync(
                SourceRconProtocol.Encode(sentinelId, SourceRconProtocol.ServerDataExecCommand, []),
                linked.Token).ConfigureAwait(false);

            var reassembled = new StringBuilder();

            for (var packets = 0; packets < MaxResponsePackets; packets++)
            {
                var packet = await ReadPacketAsync(linked.Token).ConfigureAwait(false);

                if (packet.Id == sentinelId)
                {
                    return reassembled.ToString();
                }

                if (packet.Id == SourceRconProtocol.AuthFailureId)
                {
                    // Some servers drop an unauthenticated session by answering -1 mid-conversation.
                    throw new RconAuthenticationFailedException(
                        $"The RCON endpoint {Describe(_endpoint)} answered the command with request id -1, which means the "
                        + "session is not authenticated. The response, if any, must not be treated as a result.");
                }

                if (packet.Id != commandId)
                {
                    throw new RconProtocolException(
                        $"The RCON endpoint {Describe(_endpoint)} answered with request id {packet.Id}, which belongs to "
                        + $"neither the command ({commandId}) nor its end-of-response sentinel ({sentinelId}).");
                }

                reassembled.Append(packet.Body);

                if (reassembled.Length > MaxResponseChars)
                {
                    throw new RconProtocolException(
                        $"The RCON endpoint {Describe(_endpoint)} sent more than {MaxResponseChars} characters in response "
                        + "to a single command without terminating it.");
                }
            }

            throw new RconProtocolException(
                $"The RCON endpoint {Describe(_endpoint)} sent more than {MaxResponsePackets} response packets without "
                + "acknowledging the end-of-response sentinel.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RconTimeoutException(
                $"The RCON command exchange with {Describe(_endpoint)} did not complete within {timeout}.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
    }

    /// <summary>Renders an endpoint for a message. Host and port only — there is nothing else to leak.</summary>
    internal static string Describe(RconEndpoint endpoint) => $"{endpoint.Host}:{endpoint.Port}";

    private int NextId() => _nextId++;

    private async Task SendCredentialAsync(int requestId, ReadOnlyMemory<byte> password, CancellationToken ct)
    {
        var buffer = SourceRconProtocol.Encode(requestId, SourceRconProtocol.ServerDataAuth, password.Span);

        try
        {
            await WriteAsync(buffer, ct).ConfigureAwait(false);
        }
        finally
        {
            // The only copy of the credential this type ever made, erased as soon as the socket has it.
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private async Task WriteAsync(byte[] buffer, CancellationToken ct)
    {
        try
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // A socket torn down by our own deadline surfaces as an I/O failure on some platforms. Report
            // the cancellation that actually happened rather than inventing an unreachable endpoint.
            ct.ThrowIfCancellationRequested();

            throw new RconUnreachableException(
                $"The connection to the RCON endpoint {Describe(_endpoint)} was lost while sending.", ex);
        }
    }

    private async Task<SourceRconPacket> ReadPacketAsync(CancellationToken ct)
    {
        var sizeBytes = new byte[4];
        await ReadExactlyAsync(sizeBytes, ct).ConfigureAwait(false);

        var size = BinaryPrimitives.ReadInt32LittleEndian(sizeBytes);
        if (size < SourceRconProtocol.MinimumSize || size > SourceRconProtocol.MaximumSize)
        {
            throw new RconProtocolException(
                $"The RCON endpoint {Describe(_endpoint)} declared a packet size of {size}, outside the legal range "
                + $"{SourceRconProtocol.MinimumSize}-{SourceRconProtocol.MaximumSize}. Refusing to allocate for it.");
        }

        var payload = new byte[size];
        await ReadExactlyAsync(payload, ct).ConfigureAwait(false);

        return SourceRconProtocol.Decode(payload);
    }

    private async Task ReadExactlyAsync(Memory<byte> destination, CancellationToken ct)
    {
        try
        {
            await _stream.ReadExactlyAsync(destination, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            ct.ThrowIfCancellationRequested();

            throw new RconProtocolException(
                $"The RCON endpoint {Describe(_endpoint)} closed the connection part-way through a packet.", ex);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // See WriteAsync: our own deadline can present as an I/O failure. Cancellation wins.
            ct.ThrowIfCancellationRequested();

            throw new RconUnreachableException(
                $"The connection to the RCON endpoint {Describe(_endpoint)} was lost while receiving.", ex);
        }
    }
}
