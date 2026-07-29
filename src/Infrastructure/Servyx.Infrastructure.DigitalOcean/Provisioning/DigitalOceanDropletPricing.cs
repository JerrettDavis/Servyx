using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Provisioning;

/// <summary>
/// A point-in-time snapshot of DigitalOcean's published list price per droplet size slug.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THIS TABLE IS A SNAPSHOT AND WILL GO STALE.</strong> The figures below were transcribed from
/// <see href="https://www.digitalocean.com/pricing/droplets">DigitalOcean's public droplet pricing page</see>
/// on <see cref="SnapshotDate"/>. Nothing refreshes them: there is no call to a pricing API anywhere in this
/// assembly, deliberately, because <see cref="DigitalOceanDropletProvisioner.PlanAsync"/> must issue no HTTP
/// request at all and a plan is the main consumer of a cost figure. Re-check the page and update
/// <see cref="SnapshotDate"/> whenever this file is touched; a figure that has silently drifted is worse than
/// no figure, which is why every unknown slug answers <see cref="CostEstimate.Unknown"/> instead of guessing.
/// </para>
/// <para>
/// Only the "Basic" shared-CPU tier is listed, because that is the tier a game server is normally sized from.
/// A premium-Intel, premium-AMD, general-purpose, CPU-optimised, or memory-optimised slug is therefore
/// <em>unknown</em> here, and answers as unknown rather than being approximated from a similar basic size.
/// <see cref="CostConfidence.Estimated"/> exists for derived figures and is deliberately not used: nothing in
/// this file derives anything.
/// </para>
/// <para>
/// The figures are list prices before any account discount, so the confidence is
/// <see cref="CostConfidence.ListPrice"/> and never <see cref="CostConfidence.Exact"/> — this adapter does not
/// read the account's billing API and so cannot know what the account is actually charged.
/// </para>
/// <para>
/// Note also that as of 2026-01-01 DigitalOcean bills droplets per second with a 60-second minimum, so the
/// hourly figure is a rate rather than a billing increment. The monthly figure is the published cap.
/// </para>
/// </remarks>
public static class DigitalOceanDropletPricing
{
    /// <summary>The date the figures in this file were read off DigitalOcean's public pricing page.</summary>
    public const string SnapshotDate = "2026-07-27";

    /// <summary>The human-readable provenance stamped onto every <see cref="CostEstimate"/> this class produces.</summary>
    public const string Source =
        "DigitalOcean published Basic droplet list price, snapshot taken " + SnapshotDate
        + " from https://www.digitalocean.com/pricing/droplets (not refreshed at runtime).";

    /// <summary>The ISO 4217 currency DigitalOcean publishes droplet prices in.</summary>
    public const string Currency = "USD";

    private static readonly IReadOnlyDictionary<string, (decimal Hourly, decimal Monthly)> BasicTier =
        new Dictionary<string, (decimal, decimal)>(StringComparer.Ordinal)
        {
            ["s-1vcpu-512mb-10gb"] = (0.00595m, 4.00m),
            ["s-1vcpu-1gb"] = (0.00893m, 6.00m),
            ["s-1vcpu-2gb"] = (0.01786m, 12.00m),
            ["s-2vcpu-2gb"] = (0.02679m, 18.00m),
            ["s-2vcpu-4gb"] = (0.03571m, 24.00m),
            ["s-4vcpu-8gb"] = (0.07143m, 48.00m),
            ["s-8vcpu-16gb"] = (0.14286m, 96.00m),
        };

    /// <summary>The size slugs this snapshot carries a price for.</summary>
    public static IReadOnlyCollection<string> KnownSizeSlugs => (IReadOnlyCollection<string>)BasicTier.Keys;

    /// <summary>
    /// The list price for <paramref name="sizeSlug"/>, or <see cref="CostEstimate.Unknown"/> when this
    /// snapshot does not carry that slug.
    /// </summary>
    /// <remarks>
    /// An unknown slug is a real possibility rather than a defensive branch — DigitalOcean adds size classes
    /// regularly — so the unknown answer names the slug it could not price, which is what a user needs in
    /// order to go and look the number up.
    /// </remarks>
    public static CostEstimate For(string? sizeSlug)
    {
        if (string.IsNullOrWhiteSpace(sizeSlug))
        {
            return CostEstimate.Unknown(
                "No droplet size slug was supplied, so no list price could be looked up. " + Source);
        }

        if (!BasicTier.TryGetValue(sizeSlug, out var price))
        {
            return CostEstimate.Unknown(
                $"Droplet size '{sizeSlug}' is not in Servyx's DigitalOcean price snapshot (which covers only the "
                + $"Basic shared-CPU tier: {string.Join(", ", BasicTier.Keys.OrderBy(k => k, StringComparer.Ordinal))}). "
                + Source);
        }

        return new CostEstimate(price.Hourly, price.Monthly, Currency, CostConfidence.ListPrice, Source);
    }
}
