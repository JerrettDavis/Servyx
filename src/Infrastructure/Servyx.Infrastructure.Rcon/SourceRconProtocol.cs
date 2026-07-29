using System.Buffers.Binary;
using System.Text;

namespace Servyx.Infrastructure.Rcon;

/// <summary>One decoded Source RCON packet.</summary>
/// <param name="Id">The request id the packet carries. <c>-1</c> on an authentication rejection.</param>
/// <param name="Type">The packet type — see <see cref="SourceRconProtocol"/> for the four values that exist.</param>
/// <param name="Body">The packet body, with its trailing NUL terminator(s) removed.</param>
internal readonly record struct SourceRconPacket(int Id, int Type, string Body);

/// <summary>
/// The Source RCON wire format, hand-rolled over <see cref="System.Net.Sockets.TcpClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Hand-rolled on purpose, and it is four constants and two methods wide.</strong> The protocol is
/// a little-endian <c>int32 size</c>, <c>int32 id</c>, <c>int32 type</c>, a NUL-terminated body and a
/// second NUL terminator. <c>size</c> counts everything after itself. Taking a NuGet dependency for that
/// would cost more than it saves, and every RCON client bug worth guarding against
/// (<see cref="AuthFailureId"/>, multi-packet responses) lives in the sequencing rather than in the
/// framing.
/// </para>
/// <para>
/// <strong>The type numbers overlap and that is not a typo.</strong>
/// <see cref="ServerDataAuthResponse"/> and <see cref="ServerDataExecCommand"/> are both <c>2</c>: the
/// value's meaning depends on direction. Client-to-server, <c>2</c> means "run this command";
/// server-to-client, <c>2</c> means "here is the verdict on your <see cref="ServerDataAuth"/>".
/// </para>
/// </remarks>
internal static class SourceRconProtocol
{
    /// <summary>Client-to-server: authenticate with the body as the password. Valve's <c>SERVERDATA_AUTH</c>.</summary>
    internal const int ServerDataAuth = 3;

    /// <summary>Server-to-client: the verdict on an authentication attempt. Valve's <c>SERVERDATA_AUTH_RESPONSE</c>.</summary>
    internal const int ServerDataAuthResponse = 2;

    /// <summary>Client-to-server: execute the body as a console command. Valve's <c>SERVERDATA_EXECCOMMAND</c>.</summary>
    internal const int ServerDataExecCommand = 2;

    /// <summary>Server-to-client: a (possibly partial) command response. Valve's <c>SERVERDATA_RESPONSE_VALUE</c>.</summary>
    internal const int ServerDataResponseValue = 0;

    /// <summary>
    /// The request id a server returns in a <see cref="ServerDataAuthResponse"/> to reject the credential.
    /// </summary>
    /// <remarks>
    /// The body of that packet is empty, which is exactly why this constant has to be checked: a client
    /// that reads only the body cannot tell a rejected login from a command that legitimately printed
    /// nothing.
    /// </remarks>
    internal const int AuthFailureId = -1;

    /// <summary>Bytes of a packet that are not body: <c>id</c> + <c>type</c> + two NUL terminators.</summary>
    internal const int Overhead = 4 + 4 + 2;

    /// <summary>
    /// The smallest legal value of the <c>size</c> field: an empty body still carries id, type and both
    /// terminators.
    /// </summary>
    internal const int MinimumSize = Overhead;

    /// <summary>
    /// A hard ceiling on the <c>size</c> field. Valve's documented maximum packet is 4096 bytes; this
    /// leaves generous headroom for implementations that exceed it slightly while still ensuring a peer
    /// cannot make Servyx allocate an arbitrary buffer by claiming a huge size.
    /// </summary>
    internal const int MaximumSize = 8192;

    /// <summary>
    /// The maximum length of a command body Servyx will send. Source servers truncate beyond roughly 1446
    /// bytes; refusing before the wire is more honest than sending a command the server will silently cut
    /// in half.
    /// </summary>
    internal const int MaximumCommandBytes = 1400;

    /// <summary>Encodes one packet into a freshly allocated buffer, ready to be written to the socket.</summary>
    /// <remarks>
    /// The caller owns the returned buffer and — when <paramref name="body"/> was a credential — is
    /// responsible for zeroing it after the write completes. That is why this returns a fresh array rather
    /// than writing into a pooled one: a pooled buffer holding a password would be handed to unrelated code
    /// the moment it was returned.
    /// </remarks>
    /// <param name="id">The request id.</param>
    /// <param name="type">The packet type.</param>
    /// <param name="body">The raw body bytes, without any terminator.</param>
    internal static byte[] Encode(int id, int type, ReadOnlySpan<byte> body)
    {
        var size = Overhead + body.Length;
        var buffer = new byte[4 + size];

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), size);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), id);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), type);
        body.CopyTo(buffer.AsSpan(12));

        // The final two bytes are already zero: the body terminator and the packet terminator.
        return buffer;
    }

    /// <summary>Decodes the payload that follows a <c>size</c> field into a packet.</summary>
    /// <remarks>
    /// Trailing NULs are trimmed rather than assumed to be exactly two. Servers in the wild send one, two,
    /// or (for an empty body) sometimes none, and treating the terminator count as load-bearing turns a
    /// cosmetic deviation into a decode failure.
    /// </remarks>
    /// <param name="payload">The <c>size</c> bytes that followed the length prefix.</param>
    /// <exception cref="RconProtocolException">The payload is too short to contain an id and a type.</exception>
    internal static SourceRconPacket Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            throw new RconProtocolException(
                $"An RCON packet claimed {payload.Length} byte(s) of payload, which cannot contain the mandatory "
                + "4-byte request id and 4-byte type.");
        }

        var id = BinaryPrimitives.ReadInt32LittleEndian(payload[..4]);
        var type = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));

        var body = payload[8..];
        while (body.Length > 0 && body[^1] == 0)
        {
            body = body[..^1];
        }

        return new SourceRconPacket(id, type, Encoding.UTF8.GetString(body));
    }
}
