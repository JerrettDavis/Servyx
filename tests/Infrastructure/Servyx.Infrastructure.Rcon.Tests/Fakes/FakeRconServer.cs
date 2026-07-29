using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Servyx.Domain.Rcon;

namespace Servyx.Infrastructure.Rcon.Tests.Fakes;

/// <summary>
/// A loopback <see cref="TcpListener"/> that speaks the Source RCON wire protocol, so the client can be
/// driven end to end without a game server, a container, or any external process.
/// </summary>
/// <remarks>
/// <para>
/// This double is the only honest way to test the framing traps that matter — an authentication rejection
/// carried by request id <c>-1</c>, and a response deliberately split across several packets. A mocked
/// <c>IRconClient</c> would assert nothing about either, because both live below that interface.
/// </para>
/// <para>
/// It binds port 0 and reads the assigned port back, so parallel test runs never collide, and it listens on
/// <see cref="IPAddress.Loopback"/> only.
/// </para>
/// </remarks>
internal sealed class FakeRconServer : IAsyncDisposable
{
    /// <summary>The password the server accepts unless a test says otherwise.</summary>
    internal const string DefaultPassword = "correct-horse-battery-staple";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly byte[] _password;
    private readonly Lock _sync = new();
    private readonly List<string> _commands = [];

    internal FakeRconServer(
        string password = DefaultPassword,
        bool rejectAuth = false,
        IReadOnlyList<string>? responseFragments = null,
        bool silentAfterAuth = false,
        bool emitJunkPreamble = false)
    {
        _password = Encoding.UTF8.GetBytes(password);
        RejectAuth = rejectAuth;
        ResponseFragments = responseFragments ?? ["ok"];
        SilentAfterAuth = silentAfterAuth;
        EmitJunkPreamble = emitJunkPreamble;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Endpoint = new RconEndpoint(
            IPAddress.Loopback.ToString(),
            ((IPEndPoint)_listener.LocalEndpoint).Port);

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Where the client should connect.</summary>
    internal RconEndpoint Endpoint { get; }

    /// <summary>When set, every authentication attempt is answered with request id <c>-1</c>.</summary>
    internal bool RejectAuth { get; }

    /// <summary>
    /// The fragments a command response is split into. More than one exercises multi-packet reassembly.
    /// </summary>
    internal IReadOnlyList<string> ResponseFragments { get; }

    /// <summary>When set, the server authenticates and then never answers a command. Drives the timeout path.</summary>
    internal bool SilentAfterAuth { get; }

    /// <summary>
    /// When set, an empty <c>SERVERDATA_RESPONSE_VALUE</c> precedes the authentication verdict, as several
    /// real implementations emit.
    /// </summary>
    internal bool EmitJunkPreamble { get; }

    /// <summary>Every non-empty command body the server received, in order.</summary>
    internal IReadOnlyList<string> Commands
    {
        get
        {
            lock (_sync)
            {
                return [.. _commands];
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();

        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // The listener is being torn down; a cancelled accept is the expected way this ends.
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var packet = await ReadPacketAsync(stream, _cts.Token);
                    if (packet is not { } received)
                    {
                        return;
                    }

                    await RespondAsync(stream, received, _cts.Token);
                }
            }
            catch (Exception)
            {
                // A client that closed mid-conversation is a normal end to a scenario, not a test failure.
            }
        }
    }

    private async Task RespondAsync(NetworkStream stream, (int Id, int Type, byte[] Body) packet, CancellationToken ct)
    {
        if (packet.Type == SourceRconWire.Auth)
        {
            if (EmitJunkPreamble)
            {
                await WritePacketAsync(stream, 0, SourceRconWire.ResponseValue, [], ct);
            }

            var accepted = !RejectAuth && packet.Body.AsSpan().SequenceEqual(_password);

            await WritePacketAsync(
                stream,
                accepted ? packet.Id : SourceRconWire.AuthFailureId,
                SourceRconWire.AuthResponse,
                [],
                ct);

            return;
        }

        if (packet.Type != SourceRconWire.ExecCommand)
        {
            return;
        }

        // An empty EXECCOMMAND is the client's end-of-response sentinel. Echoing it is what tells a
        // multi-packet reader that every fragment of the previous command has already arrived.
        if (packet.Body.Length == 0)
        {
            if (!SilentAfterAuth)
            {
                await WritePacketAsync(stream, packet.Id, SourceRconWire.ResponseValue, [], ct);
            }

            return;
        }

        lock (_sync)
        {
            _commands.Add(Encoding.UTF8.GetString(packet.Body));
        }

        if (SilentAfterAuth)
        {
            return;
        }

        foreach (var fragment in ResponseFragments)
        {
            await WritePacketAsync(stream, packet.Id, SourceRconWire.ResponseValue, Encoding.UTF8.GetBytes(fragment), ct);
        }
    }

    private static async Task<(int Id, int Type, byte[] Body)?> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var sizeBytes = new byte[4];

        try
        {
            await stream.ReadExactlyAsync(sizeBytes, ct);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var size = BinaryPrimitives.ReadInt32LittleEndian(sizeBytes);
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, ct);

        var id = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        var type = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));

        var body = payload.AsSpan(8);
        while (body.Length > 0 && body[^1] == 0)
        {
            body = body[..^1];
        }

        return (id, type, body.ToArray());
    }

    private static async Task WritePacketAsync(NetworkStream stream, int id, int type, byte[] body, CancellationToken ct)
    {
        var buffer = SourceRconWire.Encode(id, type, body);
        await stream.WriteAsync(buffer, ct);
        await stream.FlushAsync(ct);
    }
}

/// <summary>
/// The wire constants and encoder, restated here rather than reached for through <c>internal</c> access.
/// </summary>
/// <remarks>
/// Deliberately an independent implementation. If the test double shared the production encoder, a sign
/// error or an endianness mistake would cancel itself out on both sides and every test would still pass.
/// Writing the bytes twice is the only way a byte-layout test means anything.
/// </remarks>
internal static class SourceRconWire
{
    internal const int Auth = 3;
    internal const int AuthResponse = 2;
    internal const int ExecCommand = 2;
    internal const int ResponseValue = 0;
    internal const int AuthFailureId = -1;

    internal static byte[] Encode(int id, int type, ReadOnlySpan<byte> body)
    {
        var size = 4 + 4 + body.Length + 2;
        var buffer = new byte[4 + size];

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), size);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), id);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), type);
        body.CopyTo(buffer.AsSpan(12));

        return buffer;
    }
}
