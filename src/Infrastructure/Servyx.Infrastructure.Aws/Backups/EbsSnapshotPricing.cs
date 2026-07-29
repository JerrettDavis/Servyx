using System.Globalization;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Backups;

/// <summary>
/// A point-in-time snapshot of AWS's published price for EBS snapshot storage — and, more importantly, the one
/// place that states why a per-GB figure computed from a volume's size <em>overstates</em> what EBS actually
/// charges.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THIS FIGURE IS A SNAPSHOT AND WILL GO STALE.</strong> It was transcribed from
/// <see href="https://aws.amazon.com/ebs/pricing/">AWS's public EBS pricing page</see> on
/// <see cref="SnapshotDate"/>, for <see cref="PricedRegion"/>, exactly as <c>AwsEc2Pricing</c> was, and nothing
/// refreshes it: there is no call to the AWS Price List API anywhere in this assembly. Re-check the page and
/// update <see cref="SnapshotDate"/> whenever this file is touched.
/// </para>
/// <para>
/// <strong>EBS snapshots are INCREMENTAL, and this is the whole reason this class does not have a
/// <c>For(gigabytes)</c> that returns a price.</strong> The first snapshot of a volume stores the volume's
/// <em>in-use</em> blocks. Every snapshot after it stores only the blocks that changed since the previous
/// snapshot of the same volume; unchanged blocks are referenced, not re-stored, and are not charged twice. So
/// a nightly snapshot of a 100 GiB volume on which 2 GiB changes per day does not cost 100 GiB per night, and
/// a naive "size × rate" figure would overstate the second and every later snapshot by a factor that grows
/// with the retention count. Quoting that number as the cost would be the sort of confident wrong figure this
/// codebase treats as worse than no figure.
/// </para>
/// <para>
/// <strong>AWS does not tell this adapter the billed size.</strong> <c>DescribeSnapshots</c> reports
/// <c>volumeSize</c>, which is the <em>source volume's allocated</em> size — not the in-use size and not the
/// stored size. The actual consumption is obtainable only from the EBS direct APIs
/// (<c>ListSnapshotBlocks</c>/<c>ListChangedBlocks</c>, which are separately priced per request) or from Cost
/// Explorer, neither of which this assembly calls. So the only honest thing derivable from what is on hand is
/// a <strong>ceiling</strong>, and every figure this class produces is labelled as one — including in
/// <see cref="CostEstimate.Source"/>, so the caveat travels with the number onto whatever screen displays it.
/// </para>
/// <para>
/// <strong>The confidence is <see cref="CostConfidence.Estimated"/> and never
/// <see cref="CostConfidence.ListPrice"/>.</strong> The <em>rate</em> is a list price; the <em>quantity</em> it
/// is multiplied by is an upper bound the adapter derived, so the product is a derived estimate. The
/// DigitalOcean snapshot pricing class answers <c>ListPrice</c> because DigitalOcean reports each snapshot's
/// real billed size; AWS does not, and pretending the two figures have the same standing would be the quiet
/// kind of dishonesty.
/// </para>
/// <para>
/// <see cref="CostEstimate.Hourly"/> is deliberately left <see langword="null"/>. AWS bills snapshot storage
/// per GB-month; dividing by 730 would produce a number that looks like a rate AWS charges and is not one.
/// </para>
/// <para>
/// <strong>Deleting a snapshot does not always free its apparent size, either.</strong> Because later
/// snapshots reference earlier snapshots' blocks, deleting one member of a chain frees only the blocks no
/// surviving snapshot still references. A prune that removes an old set can therefore reduce the bill by far
/// less than the ceiling suggests. That is stated wherever a deletion is described, for the same reason the
/// creation ceiling is.
/// </para>
/// </remarks>
public static class EbsSnapshotPricing
{
    /// <summary>The date the figure in this file was read off AWS's public pricing page.</summary>
    public const string SnapshotDate = "2026-07-27";

    /// <summary>The region the figure below is quoted for. Other regions differ.</summary>
    public const string PricedRegion = "us-east-1";

    /// <summary>
    /// The published price, in USD, of one gigabyte-month of standard EBS snapshot storage.
    /// </summary>
    /// <remarks>
    /// This is the standard tier. AWS also sells an archive tier at a much lower per-GB-month rate with a
    /// per-GB retrieval charge and a 90-day minimum; this adapter never moves a snapshot to it, so quoting the
    /// archive rate anywhere here would price storage Servyx does not use.
    /// </remarks>
    public const decimal UsdPerGigabyteMonth = 0.05m;

    /// <summary>The ISO 4217 currency AWS publishes EBS prices in.</summary>
    public const string Currency = "USD";

    /// <summary>The human-readable provenance stamped onto every <see cref="CostEstimate"/> this class produces.</summary>
    public const string Source =
        "AWS published standard EBS snapshot storage list price of $0.05 per GB-month for region '" + PricedRegion
        + "', snapshot taken " + SnapshotDate
        + " from https://aws.amazon.com/ebs/pricing/ (not refreshed at runtime). "
        + "THIS IS A CEILING, NOT A PRICE. EBS snapshots are INCREMENTAL: the first snapshot of a volume stores "
        + "its in-use blocks, and every later snapshot stores only blocks changed since the previous one, so the "
        + "second and subsequent snapshots normally cost a small fraction of this figure. AWS does not report a "
        + "snapshot's billed size through DescribeSnapshots - the volumeSize field is the SOURCE VOLUME's "
        + "allocated size - so this figure is computed from an upper bound and overstates the real charge, by a "
        + "wide margin for every snapshot after the first. Read the account's Cost Explorer for the real number.";

    /// <summary>
    /// The <em>maximum</em> monthly list price of storing <paramref name="volumeGigabytes"/> of source volume as
    /// a snapshot, or <see cref="CostEstimate.Unknown"/> when no size is available.
    /// </summary>
    /// <remarks>
    /// Named <c>Ceiling</c> rather than <c>For</c> so a caller cannot read it as "the price" at the call site.
    /// A snapshot whose source size AWS has not reported answers unknown rather than zero: zero would be a lie
    /// about a resource that is already accruing charges.
    /// </remarks>
    /// <param name="volumeGigabytes">The total allocated size, in GiB, of the volumes the snapshots were taken from.</param>
    public static CostEstimate Ceiling(decimal? volumeGigabytes)
    {
        if (volumeGigabytes is not { } gigabytes || gigabytes < 0m)
        {
            return CostEstimate.Unknown(
                "AWS did not report a source volume size for this snapshot, so not even a ceiling on its storage "
                + "cost could be computed. It is still billing. " + Source);
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
    /// <param name="volumeGigabytes">The total allocated size, in GiB, of the volumes the snapshots were taken from.</param>
    /// <param name="isFirstOfChain">
    /// Whether this is the only snapshot Servyx holds of these volumes. When <see langword="false"/> the
    /// sentence says outright that the real charge is a fraction of the figure, because that is the case where
    /// quoting the ceiling alone would mislead most.
    /// </param>
    public static string DescribeMonthlyCeiling(decimal? volumeGigabytes, bool isFirstOfChain = true)
    {
        var estimate = Ceiling(volumeGigabytes);

        if (estimate.Monthly is not { } monthly || volumeGigabytes is not { } gigabytes)
        {
            return "Cost: AWS has not reported a source volume size, so not even a ceiling on this capture's "
                + "monthly charge can be computed. It is billing regardless, and the charge recurs until the "
                + "snapshots are deleted.";
        }

        return string.Create(
                CultureInfo.InvariantCulture,
                $"Cost ceiling: at most {gigabytes} GB at ${UsdPerGigabyteMonth}/GB-month = ${monthly} {Currency} "
                + $"per month, recurring for as long as the snapshots exist. ")
            + "This is an UPPER BOUND, not a price: EBS snapshots are incremental and only changed blocks are "
            + "stored and charged. "
            + (isFirstOfChain
                ? "This is the only capture Servyx holds of these volumes, so it stores their in-use blocks and is "
                  + "the closest any capture gets to the ceiling — though blocks never written still cost nothing."
                : "Servyx already holds an earlier capture of these volumes, so this one stores only what changed "
                  + "since then and its real charge is normally a small fraction of the figure above.");
    }
}
