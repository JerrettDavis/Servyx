namespace Servyx.Domain.Definitions;

/// <summary>Trust tier assigned to a definition.</summary>
public enum TrustTier
{
    /// <summary>Ships with Servyx itself; fully trusted.</summary>
    Builtin,

    /// <summary>Signed by a recognized publisher and verified successfully.</summary>
    Verified,

    /// <summary>Unsigned, or signed by an unrecognized publisher; subject to the strictest capability restrictions.</summary>
    Unverified,
}

/// <summary>Result of trust evaluation, including which capabilities are denied as a result.</summary>
/// <param name="Tier">The assigned trust tier.</param>
/// <param name="DeniedCapabilities">Capability identifiers this definition requested but is not permitted, given its tier.</param>
/// <param name="Reason">Human-readable explanation, especially when capabilities were denied or a signature failed.</param>
public sealed record TrustVerdict(TrustTier Tier, IReadOnlyList<string> DeniedCapabilities, string? Reason);
