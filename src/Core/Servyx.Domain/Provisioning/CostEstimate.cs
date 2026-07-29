namespace Servyx.Domain.Provisioning;

/// <summary>
/// How much a <see cref="CostEstimate"/>'s figures can be trusted.
/// </summary>
public enum CostConfidence
{
    /// <summary>
    /// No figure is available at all — the adapter could not produce one (e.g. it lacks
    /// <see cref="ProvisioningCapabilities.EstimatesCost"/>, or the provider's pricing API was unreachable).
    /// A caller must render this as "unknown" to the user; it must never be displayed as, defaulted to, or
    /// silently treated as zero or any other fabricated number.
    /// </summary>
    Unknown,

    /// <summary>A rough, derived estimate (e.g. computed from a similar resource's historical cost).</summary>
    Estimated,

    /// <summary>The provider's published list price for the resource, before any discounts.</summary>
    ListPrice,

    /// <summary>The precise, account-specific price, including any applicable discounts.</summary>
    Exact,
}

/// <summary>
/// A cost figure for a provisioned or planned resource.
/// </summary>
/// <param name="Hourly">Estimated cost per hour, or <see langword="null"/> if not applicable/known.</param>
/// <param name="Monthly">Estimated cost per month, or <see langword="null"/> if not applicable/known.</param>
/// <param name="Currency">The ISO 4217 currency code the figures are denominated in, e.g. <c>"USD"</c>.</param>
/// <param name="Confidence">How much the figures in this estimate can be trusted.</param>
/// <param name="Source">Human-readable description of where the figure came from, e.g. a provider pricing API name.</param>
public sealed record CostEstimate(
    decimal? Hourly,
    decimal? Monthly,
    string Currency,
    CostConfidence Confidence,
    string Source)
{
    /// <summary>
    /// Creates a <see cref="CostEstimate"/> that represents the absence of any cost figure — both amounts
    /// are <see langword="null"/> and <see cref="Confidence"/> is <see cref="CostConfidence.Unknown"/>. Use
    /// this instead of guessing a number when no real figure is available.
    /// </summary>
    /// <param name="source">Human-readable description of why no figure is available.</param>
    public static CostEstimate Unknown(string source) => new(null, null, "USD", CostConfidence.Unknown, source);
}
