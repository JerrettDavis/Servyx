using System.Security.Cryptography;
using System.Text;

namespace Servyx.Infrastructure.Ssh.Tests.Integration;

/// <summary>
/// Generates a throwaway RSA keypair for integration tests, entirely in-process via
/// <see cref="System.Security.Cryptography.RSA"/> — no dependency on an external <c>ssh-keygen</c> binary
/// being on <c>PATH</c>. Produces both a PKCS8 PEM private key (which SSH.NET's <c>PrivateKeyFile</c> can
/// parse directly) and an RFC 4253 <c>ssh-rsa</c> authorized_keys line (for injecting into the test
/// container via its <c>PUBLIC_KEY</c> environment variable).
/// </summary>
public sealed class SshRsaKeyPair : IDisposable
{
    private readonly RSA _rsa;

    private SshRsaKeyPair(RSA rsa, string privateKeyPem, string authorizedKeyLine)
    {
        _rsa = rsa;
        PrivateKeyPem = privateKeyPem;
        AuthorizedKeyLine = authorizedKeyLine;
    }

    /// <summary>The PKCS8 PEM-encoded private key.</summary>
    public string PrivateKeyPem { get; }

    /// <summary>The public key as a full <c>ssh-rsa AAAA... comment</c> authorized_keys line.</summary>
    public string AuthorizedKeyLine { get; }

    /// <summary>Generates a new 2048-bit RSA keypair.</summary>
    public static SshRsaKeyPair Generate(string comment = "servyx-integration-test")
    {
        var rsa = RSA.Create(2048);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        var authorizedKeyLine = BuildAuthorizedKeyLine(rsa, comment);
        return new SshRsaKeyPair(rsa, privateKeyPem, authorizedKeyLine);
    }

    /// <summary>Opens a fresh, independent stream over <see cref="PrivateKeyPem"/> for each call (SSH.NET consumes and disposes the stream it's given).</summary>
    public MemoryStream OpenPrivateKeyStream() => new(Encoding.ASCII.GetBytes(PrivateKeyPem));

    private static string BuildAuthorizedKeyLine(RSA rsa, string comment)
    {
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        var blob = new List<byte>();
        blob.AddRange(EncodeSshString("ssh-rsa"u8.ToArray()));
        blob.AddRange(EncodeMpint(parameters.Exponent!));
        blob.AddRange(EncodeMpint(parameters.Modulus!));
        return $"ssh-rsa {Convert.ToBase64String(blob.ToArray())} {comment}";
    }

    private static byte[] EncodeSshString(byte[] data)
    {
        var result = new byte[4 + data.Length];
        BinaryPrimitivesWriteUInt32BigEndian(result, (uint)data.Length);
        Buffer.BlockCopy(data, 0, result, 4, data.Length);
        return result;
    }

    /// <summary>
    /// Encodes an unsigned big-endian integer as an SSH <c>mpint</c>: length-prefixed, with a leading
    /// zero byte inserted if the high bit of the first byte would otherwise be mistaken for a sign bit
    /// (mpints are signed two's-complement; RSA public exponents/moduli are always positive).
    /// </summary>
    private static byte[] EncodeMpint(byte[] unsignedBigEndian)
    {
        var start = 0;
        while (start < unsignedBigEndian.Length - 1 && unsignedBigEndian[start] == 0)
        {
            start++;
        }

        var trimmed = unsignedBigEndian[start..];
        var needsPadding = trimmed.Length > 0 && (trimmed[0] & 0x80) != 0;

        if (!needsPadding)
        {
            return EncodeSshString(trimmed);
        }

        var padded = new byte[trimmed.Length + 1];
        Buffer.BlockCopy(trimmed, 0, padded, 1, trimmed.Length);
        return EncodeSshString(padded);
    }

    private static void BinaryPrimitivesWriteUInt32BigEndian(byte[] destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    /// <inheritdoc />
    public void Dispose() => _rsa.Dispose();
}
