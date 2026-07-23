using System.Security.Cryptography;
using System.Text;

namespace Servyx.Domain.Secrets;

/// <summary>
/// Holds a resolved secret value in an unmanaged-lifetime-conscious way and zeroes its backing buffer when
/// disposed. Deliberately NOT a <see cref="string"/>: a .NET string is immutable and may be interned or
/// copied by the runtime in ways application code cannot see or control, so a secret that was ever
/// materialized as a string cannot be reliably erased from managed memory before the garbage collector
/// eventually reclaims it. A <see cref="SecretLease"/> instead owns a single <c>byte[]</c> that it can
/// deterministically zero on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// Callers should hold a <see cref="SecretLease"/> for as short a time as possible — resolve it, use it,
/// dispose it — and should call <see cref="ToUtf8String"/> only when a protocol genuinely requires a
/// managed string (at which point the same non-erasable-string caveat applies to that copy), as late as
/// possible and held as briefly as possible.
/// </remarks>
public sealed class SecretLease : IDisposable
{
    private byte[]? _buffer;

    /// <summary>
    /// Creates a <see cref="SecretLease"/> that takes ownership of <paramref name="value"/>. The caller
    /// must not retain or reuse the array after constructing the lease — ownership, and the responsibility
    /// for zeroing it, transfers entirely to the lease.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public SecretLease(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _buffer = value;
    }

    /// <summary>The secret's raw bytes.</summary>
    /// <exception cref="ObjectDisposedException">The lease has already been disposed.</exception>
    public ReadOnlySpan<byte> Value
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            return _buffer;
        }
    }

    /// <summary>
    /// Decodes the secret's bytes as UTF-8 text, for protocols that demand a <see cref="string"/> (e.g. an
    /// HTTP header or a connection string). Call this as late as possible and hold the result as briefly as
    /// possible — once materialized as a <see cref="string"/>, the value can no longer be reliably scrubbed
    /// from managed memory.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The lease has already been disposed.</exception>
    public string ToUtf8String()
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        return Encoding.UTF8.GetString(_buffer);
    }

    /// <summary>Zeroes the underlying buffer via <see cref="CryptographicOperations.ZeroMemory"/>.</summary>
    public void Dispose()
    {
        if (_buffer is not null)
        {
            CryptographicOperations.ZeroMemory(_buffer);
            _buffer = null;
        }
    }
}
