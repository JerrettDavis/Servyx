using System.Globalization;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Azure.Backups;

/// <summary>
/// A point-in-time snapshot of Azure's published price for managed-disk snapshot storage — and, more
/// importantly, the one place that states why a per-GB figure computed from a disk's size <em>overstates</em>
/// what Azure actually charges, and by how much more than the EBS equivalent does.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THIS FIGURE IS A SNAPSHOT AND WILL GO STALE.</strong> It was transcribed from
/// <see href="https://azure.microsoft.com/pricing/details/managed-disks/">Azure's public managed-disks pricing
/// page</see> on <see cref="SnapshotDate"/>, for <see cref="PricedRegion"/>, exactly as
/// <see cref="AzureVirtualMachinePricing"/> was, and nothing refreshes it: there is no call to the Azure Retail
/// Prices API anywhere in this assembly. Re-check the page and update <see cref="SnapshotDate"/> whenever this
/// file is touched.
/// </para>
/// <para>
/// <strong>The rate is the Standard HDD LRS snapshot tier, and that is a choice with consequences.</strong>
/// Azure stores a managed-disk snapshot on standard storage regardless of the tier of the disk it came from, so
/// a snapshot of a Premium SSD is not billed at Premium rates. Zone-redundant (ZRS) snapshot storage costs more
/// and is selected by the snapshot's SKU; this adapter never sets one, so the account's default applies and the
/// figure below can understate a ZRS-defaulted subscription. Regions differ. Every one of those caveats is in
/// <see cref="Source"/> so it travels with the number.
/// </para>
/// <para>
/// <strong>Azure snapshots are incremental BECAUSE SERVYX ASKED FOR THAT, and this is the real divergence from
/// the EBS adapter.</strong> An EBS snapshot is incremental as a property of the service — there is no flag and
/// no alternative. <c>Microsoft.Compute/snapshots</c> has an explicit <c>incremental</c> property that ARM
/// defaults to <see langword="false"/>, and a full snapshot is billed against the disk's stored contents on
/// <em>every single capture</em> rather than against the delta. This adapter always writes
/// <c>incremental: true</c> (see <c>ArmSnapshotRequestProperties.Incremental</c>), so the first capture of a
/// disk stores its used blocks and every later one stores only what changed since the previous snapshot of the
/// same disk. Had the default been accepted, a nightly capture of a 128 GB disk would cost roughly thirty times
/// what it does.
/// </para>
/// <para>
/// <strong>Azure does not tell this adapter the billed size.</strong> A snapshot's ARM representation reports
/// <c>diskSizeGB</c>, which is the <em>source disk's provisioned</em> size — not its used size and not the
/// snapshot's stored size. The real consumption of an incremental snapshot is obtainable only by enumerating
/// its changed blocks through the disk data-plane (<c>grantAccess</c> plus the changed-blocks API, separately
/// charged per operation) or from Cost Management, neither of which this assembly calls. So the only honest
/// thing derivable from what is on hand is a <strong>ceiling</strong>, and every figure this class produces is
/// labelled as one — including in <see cref="CostEstimate.Source"/>.
/// </para>
/// <para>
/// <strong>The confidence is <see cref="CostConfidence.Estimated"/> and never
/// <see cref="CostConfidence.ListPrice"/>.</strong> The <em>rate</em> is a list price; the <em>quantity</em> it
/// is multiplied by is an upper bound this adapter derived, so the product is a derived estimate.
/// <see cref="AzureVirtualMachinePricing"/> answers <c>ListPrice</c> because a VM size maps onto a published
/// price with nothing derived in between; pretending the two figures have the same standing would be the quiet
/// kind of dishonesty.
/// </para>
/// <para>
/// <see cref="CostEstimate.Hourly"/> is deliberately left <see langword="null"/>. Azure bills snapshot storage
/// per GB-month; dividing by 730 would produce a number that looks like a rate Azure charges and is not one.
/// </para>
/// <para>
/// <strong>Deleting a snapshot does not always free its apparent size, either.</strong> Incremental snapshots
/// of one disk form a chain in which later members reference earlier members' blocks, so deleting one member
/// frees only the blocks no surviving snapshot still references — Azure transparently re-parents the rest. A
/// prune that removes an old set can therefore reduce the bill by far less than the ceiling suggests. That is
/// stated wherever a deletion is described, for the same reason the creation ceiling is.
/// </para>
/// </remarks>
public static class AzureSnapshotPricing
{
    /// <summary>The date the figure in this file was read off Azure's public pricing page.</summary>
    public const string SnapshotDate = "2026-07-27";

    /// <summary>The region the figure below is quoted for. Other regions differ.</summary>
    public const string PricedRegion = "eastus";

    /// <summary>
    /// The published price, in USD, of one gigabyte-month of Standard HDD (LRS) managed-disk snapshot storage.
    /// </summary>
    /// <remarks>
    /// The tier Azure stores snapshots on by default, whatever the source disk's tier. ZRS snapshot storage is
    /// dearer; this adapter never selects a SKU, so a subscription defaulted to ZRS is charged more than this.
    /// </remarks>
    public const decimal UsdPerGigabyteMonth = 0.05m;

    /// <summary>The ISO 4217 currency Azure publishes managed-disk prices in.</summary>
    public const string Currency = "USD";

    /// <summary>The human-readable provenance stamped onto every <see cref="CostEstimate"/> this class produces.</summary>
    public const string Source =
        "Azure published Standard HDD (LRS) managed-disk snapshot storage list price of $0.05 per GB-month for "
        + "region '" + PricedRegion + "', snapshot taken " + SnapshotDate
        + " from https://azure.microsoft.com/pricing/details/managed-disks/ (not refreshed at runtime). "
        + "THIS IS A CEILING, NOT A PRICE. Servyx creates every snapshot with incremental=true, so the first "
        + "capture of a disk stores only its used blocks and every later capture stores only the blocks changed "
        + "since the previous one; the second and subsequent captures normally cost a small fraction of this "
        + "figure. Azure does not report a snapshot's billed size - the diskSizeGB field is the SOURCE DISK's "
        + "PROVISIONED size - so this figure is computed from an upper bound and overstates the real charge, by "
        + "a wide margin for every capture after the first. Zone-redundant (ZRS) snapshot storage costs more "
        + "than this rate, and this adapter does not select a SKU. Read the subscription's Cost Management for "
        + "the real number.";

    /// <summary>
    /// The <em>maximum</em> monthly list price of storing <paramref name="diskGigabytes"/> of source disk as
    /// snapshots, or <see cref="CostEstimate.Unknown"/> when no size is available.
    /// </summary>
    /// <remarks>
    /// Named <c>Ceiling</c> rather than <c>For</c> so a caller cannot read it as "the price" at the call site.
    /// A snapshot whose source disk size ARM has not reported answers unknown rather than zero: zero would be a
    /// lie about a resource that is already accruing charges.
    /// </remarks>
    /// <param name="diskGigabytes">The total provisioned size, in GB, of the disks the snapshots were taken from.</param>
    public static CostEstimate Ceiling(decimal? diskGigabytes)
    {
        if (diskGigabytes is not { } gigabytes || gigabytes < 0m)
        {
            return CostEstimate.Unknown(
                "Azure did not report a source disk size for this snapshot, so not even a ceiling on its storage "
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
    /// <param name="diskGigabytes">The total provisioned size, in GB, of the disks the snapshots were taken from.</param>
    /// <param name="isFirstOfChain">
    /// Whether this is the only capture Servyx holds of these disks. When <see langword="false"/> the sentence
    /// says outright that the real charge is a fraction of the figure, because that is the case where quoting
    /// the ceiling alone would mislead most.
    /// </param>
    public static string DescribeMonthlyCeiling(decimal? diskGigabytes, bool isFirstOfChain = true)
    {
        var estimate = Ceiling(diskGigabytes);

        if (estimate.Monthly is not { } monthly || diskGigabytes is not { } gigabytes)
        {
            return "Cost: Azure has not reported a source disk size, so not even a ceiling on this capture's "
                + "monthly charge can be computed. It is billing regardless, and the charge recurs until the "
                + "snapshots are deleted.";
        }

        return string.Create(
                CultureInfo.InvariantCulture,
                $"Cost ceiling: at most {gigabytes} GB at ${UsdPerGigabyteMonth}/GB-month = ${monthly} {Currency} "
                + $"per month, recurring for as long as the snapshots exist. ")
            + "This is an UPPER BOUND, not a price: Servyx creates these snapshots with incremental=true, so "
            + "only changed blocks are stored and charged. "
            + (isFirstOfChain
                ? "This is the only capture Servyx holds of these disks, so it stores their used blocks and is "
                  + "the closest any capture gets to the ceiling — though blocks never written still cost nothing."
                : "Servyx already holds an earlier capture of these disks, so this one stores only what changed "
                  + "since then and its real charge is normally a small fraction of the figure above.");
    }
}
