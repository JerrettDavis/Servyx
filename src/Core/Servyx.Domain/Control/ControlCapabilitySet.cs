using System.Security.Cryptography;
using System.Text;

namespace Servyx.Domain.Control;

/// <summary>
/// The full result of evaluating every <see cref="ControlCapability"/> Servyx knows how to probe for a
/// server: which are granted, which were directly verified, which were examined at all, and the
/// per-capability evidence behind each conclusion.
/// </summary>
public sealed class ControlCapabilitySet
{
    /// <summary>
    /// The capabilities Servyx currently acts on: the union of everything with
    /// <see cref="CapabilityConfidence.Verified"/> or <see cref="CapabilityConfidence.Inferred"/> confidence.
    /// </summary>
    public required ControlCapability Granted { get; init; }

    /// <summary>The subset of <see cref="Granted"/> that was directly, positively verified.</summary>
    public required ControlCapability Verified { get; init; }

    /// <summary>Every capability that was examined at all, regardless of the conclusion reached.</summary>
    public required ControlCapability Probed { get; init; }

    /// <summary>The per-capability grant detail, keyed by the exact capability (combination) each grant covers.</summary>
    public required IReadOnlyDictionary<ControlCapability, CapabilityGrant> Grants { get; init; }

    /// <summary>When this set was evaluated.</summary>
    public required DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>
    /// A deterministic fingerprint of this set's grants, suitable for change detection. See
    /// <see cref="ComputeFingerprint"/> for how it is derived.
    /// </summary>
    public required string Fingerprint { get; init; }

    /// <summary>
    /// True when every bit in <paramref name="required"/> is present in <see cref="Granted"/>. Always
    /// true when <paramref name="required"/> is <see cref="ControlCapability.None"/>.
    /// </summary>
    public bool Has(ControlCapability required) => (Granted & required) == required;

    /// <summary>Returns exactly the bits of <paramref name="required"/> that are not present in <see cref="Granted"/>.</summary>
    public ControlCapability Missing(ControlCapability required) => required & ~Granted;

    /// <summary>An empty set: nothing probed, nothing granted. The safe default before any evaluation has run.</summary>
    public static ControlCapabilitySet Empty { get; } = Build(new Dictionary<ControlCapability, CapabilityGrant>(), DateTimeOffset.MinValue);

    /// <summary>
    /// Builds a <see cref="ControlCapabilitySet"/> from a completed grants dictionary, computing
    /// <see cref="Granted"/>, <see cref="Verified"/>, <see cref="Probed"/>, and <see cref="Fingerprint"/>
    /// automatically so callers cannot forget to keep them in sync with the grants.
    /// </summary>
    /// <param name="grants">The per-capability grants, keyed by the capability (combination) each covers.</param>
    /// <param name="evaluatedAt">When this set was evaluated.</param>
    public static ControlCapabilitySet Build(IReadOnlyDictionary<ControlCapability, CapabilityGrant> grants, DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(grants);

        var granted = ControlCapability.None;
        var verified = ControlCapability.None;
        var probed = ControlCapability.None;

        foreach (var (capability, grant) in grants)
        {
            probed |= capability;

            switch (grant.Confidence)
            {
                case CapabilityConfidence.Verified:
                    verified |= capability;
                    granted |= capability;
                    break;
                case CapabilityConfidence.Inferred:
                    granted |= capability;
                    break;
            }
        }

        return new ControlCapabilitySet
        {
            Granted = granted,
            Verified = verified,
            Probed = probed,
            Grants = grants,
            EvaluatedAt = evaluatedAt,
            Fingerprint = ComputeFingerprint(grants.Values),
        };
    }

    /// <summary>
    /// Computes a deterministic SHA-256 fingerprint over the ordered set of (capability, confidence,
    /// probeId) triples contributed by <paramref name="grants"/>. The triples are sorted before hashing,
    /// so the result is stable regardless of grant or evidence ordering, and changes whenever a
    /// capability's confidence (or the set of probes backing it) changes.
    /// </summary>
    /// <param name="grants">The grants to fingerprint.</param>
    public static string ComputeFingerprint(IEnumerable<CapabilityGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);

        var triples = new List<string>();

        foreach (var grant in grants)
        {
            if (grant.Evidence.Count == 0)
            {
                triples.Add(FormatTriple(grant.Capability, grant.Confidence, string.Empty));
                continue;
            }

            foreach (var evidence in grant.Evidence)
            {
                triples.Add(FormatTriple(grant.Capability, grant.Confidence, evidence.ProbeId));
            }
        }

        triples.Sort(StringComparer.Ordinal);

        var canonical = string.Join('\n', triples);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string FormatTriple(ControlCapability capability, CapabilityConfidence confidence, string probeId)
        => $"{(ulong)capability}:{(int)confidence}:{probeId}";
}
