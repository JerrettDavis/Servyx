using System.Security.Cryptography;
using System.Text;

namespace Servyx.Domain.Connectors;

/// <summary>
/// Default <see cref="IHostKeyVerifier"/> implementation, backed by an <see cref="IHostKeyStore"/>.
/// </summary>
/// <remarks>
/// Verification order is: revocation always wins first, then <see cref="TrustPolicy.PinnedFingerprints"/>
/// is checked purely against the supplied list, and otherwise the persistent store is consulted. Note that
/// <see cref="TrustPolicy.RequirePinned"/> and <see cref="TrustPolicy.TrustOnFirstUse"/> produce identical
/// verdicts from this type — both consult the same persistent store, and both report an unpinned host as
/// <see cref="HostKeyVerdict.Unknown"/>. The two policies differ only in what the caller is expected to do
/// with that verdict (refuse outright, versus prompt a human to confirm and pin), which is a caller-side
/// concern, not something this verifier decides.
/// </remarks>
public sealed class HostKeyVerifier : IHostKeyVerifier
{
    private readonly IHostKeyStore _store;

    /// <summary>Creates a <see cref="HostKeyVerifier"/> backed by <paramref name="store"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public HostKeyVerifier(IHostKeyStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async Task<HostKeyVerdict> VerifyAsync(
        string host,
        int port,
        string algorithm,
        byte[] publicKeyBlob,
        TrustPolicy policy,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);
        ArgumentNullException.ThrowIfNull(publicKeyBlob);
        ArgumentNullException.ThrowIfNull(policy);

        var presentedFingerprint = HostKeyFingerprint.ComputeSha256(publicKeyBlob);

        // Revocation always wins, regardless of policy: a host that has been explicitly revoked must never
        // be reported as anything other than Revoked, whether the caller is comparing against a pinned
        // store record or a caller-supplied fingerprint list.
        if (await _store.IsRevokedAsync(host, port, ct).ConfigureAwait(false))
        {
            return HostKeyVerdict.Revoked;
        }

        if (policy is TrustPolicy.PinnedFingerprints pinned)
        {
            return ConstantTimeContains(pinned.Sha256, presentedFingerprint)
                ? HostKeyVerdict.Trusted
                : HostKeyVerdict.Unknown;
        }

        var record = await _store.FindAsync(host, port, ct).ConfigureAwait(false);

        if (record is null)
        {
            // Applies identically under RequirePinned and TrustOnFirstUse: neither policy auto-pins, so an
            // unrecorded host is Unknown either way. See the type-level remarks for why.
            return HostKeyVerdict.Unknown;
        }

        return FixedTimeEquals(record.Sha256Fingerprint, presentedFingerprint)
            ? HostKeyVerdict.Trusted
            : HostKeyVerdict.Changed;
    }

    private static bool ConstantTimeContains(IReadOnlyList<string> candidates, string presented)
    {
        // Deliberately does not short-circuit on the first match: every candidate is compared so that the
        // time taken does not reveal which entry (if any) matched. This still leaks the length of
        // `candidates` via timing, which is not considered sensitive.
        var matched = false;

        foreach (var candidate in candidates)
        {
            if (FixedTimeEquals(candidate, presented))
            {
                matched = true;
            }
        }

        return matched;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
