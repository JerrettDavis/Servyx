using System.Globalization;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Backups;

/// <summary>
/// A point-in-time snapshot of DigitalOcean's published price for storing droplet snapshots.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THIS FIGURE IS A SNAPSHOT AND WILL GO STALE.</strong> It was transcribed from
/// <see href="https://www.digitalocean.com/pricing/droplets">DigitalOcean's public pricing page</see> on
/// <see cref="SnapshotDate"/>, exactly as <c>DigitalOceanDropletPricing</c> was, and nothing refreshes it:
/// there is no pricing API call anywhere in this assembly. Re-check the page and update
/// <see cref="SnapshotDate"/> whenever this file is touched.
/// </para>
/// <para>
/// <strong>A snapshot's cost is recurring, and that is the thing this file exists to make visible.</strong>
/// A droplet backup on any other Servyx adapter is a tar file on a disk somebody is already paying for; a
/// DigitalOcean snapshot is a separate billable resource that charges per gigabyte per <em>month</em>, for as
/// long as it exists, with no expiry of any kind. Taking one starts a charge; only deleting it stops one. So
/// every place this adapter describes a snapshot — <c>InspectAsync</c>, the restore preview, and the error
/// raised when a snapshot cannot be marked as Servyx's — states the monthly figure and states that it
/// recurs.
/// </para>
/// <para>
/// <see cref="CostEstimate.Hourly"/> is deliberately left <see langword="null"/>. DigitalOcean bills snapshot
/// storage per GB-month, not per hour; dividing the monthly figure by 730 would produce a number that looks
/// like a rate DigitalOcean charges and is not one.
/// </para>
/// </remarks>
public static class DigitalOceanSnapshotPricing
{
    /// <summary>The date the figure in this file was read off DigitalOcean's public pricing page.</summary>
    public const string SnapshotDate = "2026-07-27";

    /// <summary>The published price, in USD, of storing one gigabyte of snapshot for one month.</summary>
    public const decimal UsdPerGigabyteMonth = 0.06m;

    /// <summary>The ISO 4217 currency DigitalOcean publishes snapshot prices in.</summary>
    public const string Currency = "USD";

    /// <summary>The human-readable provenance stamped onto every <see cref="CostEstimate"/> this class produces.</summary>
    public const string Source =
        "DigitalOcean published snapshot storage list price of $0.06 per GB-month, snapshot taken " + SnapshotDate
        + " from https://www.digitalocean.com/pricing/droplets (not refreshed at runtime). Snapshot storage is a "
        + "RECURRING charge that continues for as long as the snapshot exists and never expires on its own.";

    /// <summary>
    /// The monthly list price of storing <paramref name="sizeGigabytes"/> of snapshot, or
    /// <see cref="CostEstimate.Unknown"/> when DigitalOcean did not report a size.
    /// </summary>
    /// <remarks>
    /// A snapshot whose size DigitalOcean has not yet computed — which is normal for the first moments after
    /// one is taken — answers unknown rather than zero. Zero would be a lie about a resource that is already
    /// accruing charges.
    /// </remarks>
    public static CostEstimate For(decimal? sizeGigabytes)
    {
        if (sizeGigabytes is not { } gigabytes || gigabytes < 0m)
        {
            return CostEstimate.Unknown(
                "DigitalOcean did not report a size for this snapshot, so its storage cost could not be computed. "
                + "It is still billing. " + Source);
        }

        return new CostEstimate(
            Hourly: null,
            Monthly: decimal.Round(gigabytes * UsdPerGigabyteMonth, 4, MidpointRounding.AwayFromZero),
            Currency,
            CostConfidence.ListPrice,
            Source);
    }

    /// <summary>One sentence stating what a snapshot of this size costs and that the charge recurs.</summary>
    /// <param name="sizeGigabytes">The snapshot's billed size, as DigitalOcean reports it.</param>
    public static string DescribeMonthlyCost(decimal? sizeGigabytes)
    {
        var estimate = For(sizeGigabytes);
        return estimate.Monthly is { } monthly && sizeGigabytes is { } gigabytes
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Cost: {gigabytes} GB at ${UsdPerGigabyteMonth}/GB-month = ${monthly} {Currency} per month, ")
              + "recurring for as long as this snapshot exists. Deleting it is the only thing that stops the charge."
            : "Cost: DigitalOcean has not reported a size for this snapshot yet, so its monthly charge cannot be "
              + "computed. It is billing regardless.";
    }
}
