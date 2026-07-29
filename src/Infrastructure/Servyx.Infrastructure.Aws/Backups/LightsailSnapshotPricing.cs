using System.Globalization;

using Servyx.Domain.Provisioning;

using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Backups;

/// <summary>
/// A point-in-time snapshot of AWS's published price for Lightsail snapshot storage — and, more importantly, the
/// one place that states why a per-GB figure computed from an instance's disk sizes <em>overstates</em> what
/// Lightsail actually charges.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THIS FIGURE IS A SNAPSHOT AND WILL GO STALE.</strong> It was transcribed from
/// <see href="https://aws.amazon.com/lightsail/pricing/">AWS's public Lightsail pricing page</see> and the
/// service's own snapshot FAQ on <see cref="SnapshotDate"/>, exactly as <see cref="AwsLightsailPricing"/> was,
/// and nothing refreshes it: there is no call to the AWS Price List API anywhere in this assembly. Re-check the
/// page and update <see cref="SnapshotDate"/> whenever this file is touched.
/// </para>
/// <para>
/// <strong>Lightsail snapshots ARE incremental, on AWS's own record, and that is the whole reason this class has
/// no <c>For(gigabytes)</c> that returns a price.</strong> AWS's snapshot FAQ states it in those words: when
/// successive snapshots are taken of the same instance, "for each new snapshot you take, you're charged only for
/// the part of the instance that changed", with a worked example of a second snapshot of an instance that
/// changed by 2 GB costing $0.10 per month. So a nightly snapshot of a 40 GB instance on which 2 GB changes per
/// day does not cost 40 GB per night, and a naive "size × rate" figure would overstate the second and every later
/// snapshot by a factor that grows with the retention count. Quoting that number as the cost would be the sort of
/// confident wrong figure this codebase treats as worse than no figure.
/// </para>
/// <para>
/// <strong>Lightsail does not tell this adapter the billed size — the same limitation the EBS adapter has, for
/// the same reason.</strong> <c>GetInstanceSnapshots</c> reports <c>sizeInGb</c>, which AWS documents as "the
/// size in GB of the SSD": the <em>source</em> system disk's allocated size, not the stored or billed size. Each
/// attached block storage disk reports its own allocated size the same way. There is no field anywhere on the
/// object for what the snapshot actually consumes, and no Lightsail API that reports it; the real figure lives
/// only in Cost Explorer or the account's bill, neither of which this assembly reads. So the only honest thing
/// derivable from what is on hand is a <strong>ceiling</strong>, and every figure this class produces is labelled
/// as one — including in <see cref="CostEstimate.Source"/>, so the caveat travels with the number onto whatever
/// screen displays it.
/// </para>
/// <para>
/// <strong>The ceiling must be computed over the system disk PLUS every attached disk.</strong> An instance
/// snapshot copies the attached block storage disks too (see
/// <see cref="LightsailSnapshotBackupProvider"/>), so a figure derived from <c>sizeInGb</c> alone would sit
/// <em>below</em> the real charge for any instance with a data disk — the one direction something called a
/// ceiling must never err in. <c>LightsailInstanceSnapshot.TotalSourceGigabytes</c> is what this class is meant
/// to be fed.
/// </para>
/// <para>
/// <strong>The confidence is <see cref="CostConfidence.Estimated"/> and never
/// <see cref="CostConfidence.ListPrice"/>.</strong> The <em>rate</em> is a list price; the <em>quantity</em> it
/// is multiplied by is an upper bound the adapter derived, so the product is a derived estimate. The DigitalOcean
/// snapshot pricing class answers <c>ListPrice</c> because DigitalOcean reports each snapshot's real billed size;
/// Lightsail does not, and pretending the two figures have the same standing would be the quiet kind of
/// dishonesty. Note that this is the opposite direction from <see cref="AwsLightsailPricing"/>, where a Lightsail
/// bundle price is the <em>most</em> trustworthy figure in this codebase: the instance's price is exact and
/// all-in, and its backups' price is not knowable from the API at all.
/// </para>
/// <para>
/// <see cref="CostEstimate.Hourly"/> is deliberately left <see langword="null"/>. AWS bills snapshot storage per
/// GB-month; dividing by 730 would produce a number that looks like a rate AWS charges and is not one.
/// </para>
/// </remarks>
public static class LightsailSnapshotPricing
{
    /// <summary>The date the figure in this file was read off AWS's public pricing page and snapshot FAQ.</summary>
    public const string SnapshotDate = "2026-07-27";

    /// <summary>
    /// The region the figure below was checked for.
    /// </summary>
    /// <remarks>
    /// AWS's snapshot FAQ quotes the $0.05/GB-month figure without qualifying it by region, unlike the bundle
    /// prices in <see cref="AwsLightsailPricing"/>, which do vary. The region is recorded anyway rather than
    /// asserted away: this adapter checked one region's published page and has no basis for claiming the figure
    /// holds everywhere.
    /// </remarks>
    public const string PricedRegion = "us-east-1";

    /// <summary>The published price, in USD, of one gigabyte-month of Lightsail snapshot storage.</summary>
    /// <remarks>
    /// The same rate applies to manual and automatic snapshots alike, per AWS's snapshot FAQ. This adapter only
    /// ever creates manual ones — it never enables the automatic-snapshot add-on — but the rate is quoted for
    /// both, so a figure covering foreign automatic snapshots seen in the account uses the same number honestly.
    /// </remarks>
    public const decimal UsdPerGigabyteMonth = 0.05m;

    /// <summary>The ISO 4217 currency AWS publishes Lightsail prices in.</summary>
    public const string Currency = "USD";

    /// <summary>The human-readable provenance stamped onto every <see cref="CostEstimate"/> this class produces.</summary>
    public const string Source =
        "AWS published Lightsail snapshot storage list price of $0.05 per GB-month, checked for region '"
        + PricedRegion + "' on " + SnapshotDate
        + " against https://aws.amazon.com/lightsail/pricing/ and the Lightsail snapshot FAQ (not refreshed at "
        + "runtime). THIS IS A CEILING, NOT A PRICE. Lightsail snapshots are INCREMENTAL and AWS says so "
        + "explicitly: for each new snapshot of the same instance you are charged only for the part of the "
        + "instance that changed, so the second and subsequent snapshots normally cost a small fraction of this "
        + "figure. Lightsail does not report a snapshot's billed size through GetInstanceSnapshots - the sizeInGb "
        + "field is the SOURCE disk's allocated size - so this figure is computed from an upper bound (the system "
        + "disk plus every attached block storage disk) and overstates the real charge, by a wide margin for "
        + "every snapshot after the first. Read the account's Cost Explorer for the real number.";

    /// <summary>
    /// The <em>maximum</em> monthly list price of storing <paramref name="sourceGigabytes"/> of source disk as a
    /// snapshot, or <see cref="CostEstimate.Unknown"/> when no size is available.
    /// </summary>
    /// <remarks>
    /// Named <c>Ceiling</c> rather than <c>For</c> so a caller cannot read it as "the price" at the call site. A
    /// snapshot whose source size Lightsail has not reported answers unknown rather than zero: zero would be a
    /// lie about a resource that is already accruing charges.
    /// </remarks>
    /// <param name="sourceGigabytes">The total allocated size, in GB, of the system disk and every attached disk.</param>
    public static CostEstimate Ceiling(decimal? sourceGigabytes)
    {
        if (sourceGigabytes is not { } gigabytes || gigabytes < 0m)
        {
            return CostEstimate.Unknown(
                "Lightsail did not report a source disk size for this snapshot, so not even a ceiling on its "
                + "storage cost could be computed. It is still billing. " + Source);
        }

        return new CostEstimate(
            Hourly: null,
            Monthly: decimal.Round(gigabytes * UsdPerGigabyteMonth, 4, MidpointRounding.AwayFromZero),
            Currency,
            CostConfidence.Estimated,
            Source);
    }

    /// <summary>
    /// One sentence stating the upper bound on what a capture of this size costs, that the charge recurs, and
    /// that the real figure is lower.
    /// </summary>
    /// <param name="sourceGigabytes">The total allocated size, in GB, of the system disk and every attached disk.</param>
    /// <param name="isFirstOfChain">
    /// Whether this is the only snapshot Servyx holds of this instance. When <see langword="false"/> the sentence
    /// says outright that the real charge is a fraction of the figure, because that is the case where quoting the
    /// ceiling alone would mislead most.
    /// </param>
    public static string DescribeMonthlyCeiling(decimal? sourceGigabytes, bool isFirstOfChain = true)
    {
        var estimate = Ceiling(sourceGigabytes);

        if (estimate.Monthly is not { } monthly || sourceGigabytes is not { } gigabytes)
        {
            return "Cost: Lightsail has not reported a source disk size, so not even a ceiling on this snapshot's "
                + "monthly charge can be computed. It is billing regardless, and the charge recurs until the "
                + "snapshot is deleted.";
        }

        return string.Create(
                CultureInfo.InvariantCulture,
                $"Cost ceiling: at most {gigabytes} GB at ${UsdPerGigabyteMonth}/GB-month = ${monthly} {Currency} "
                + $"per month, recurring for as long as the snapshot exists. ")
            + "This is an UPPER BOUND, not a price: Lightsail snapshots are incremental and AWS charges only for "
            + "the part of the instance that changed since the previous snapshot. "
            + (isFirstOfChain
                ? "This is the only snapshot Servyx holds of this instance, so it is the closest any capture gets "
                  + "to the ceiling — though blocks never written still cost nothing."
                : "Servyx already holds an earlier snapshot of this instance, so this one stores only what changed "
                  + "since then and its real charge is normally a small fraction of the figure above.")
            + " Note the ceiling counts the system disk AND every attached block storage disk, because an instance "
            + "snapshot copies both.";
    }
}
