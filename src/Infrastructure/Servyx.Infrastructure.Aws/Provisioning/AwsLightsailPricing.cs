using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// A point-in-time snapshot of AWS's published monthly price per Lightsail Linux/Unix bundle.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THIS TABLE IS A SNAPSHOT AND WILL GO STALE</strong> — the same caveat as <c>AwsEc2Pricing</c> and for
/// the same reason: <c>AwsLightsailProvisioner.PlanAsync</c> must issue no HTTP request at all, so there is no
/// call to a pricing API anywhere in this file. The figures were read off
/// <see href="https://aws.amazon.com/lightsail/pricing/">AWS's public Lightsail pricing page</see> on
/// <see cref="SnapshotDate"/>. An unknown bundle answers <see cref="CostEstimate.Unknown"/> rather than a guess.
/// </para>
/// <para>
/// <strong>THE FIGURE IS ALL-IN — the one real pricing improvement this adapter has over the other three in this
/// codebase, and it is said here in exactly those words so the caveat travels with the number.</strong> An EC2
/// or Azure VM figure prices compute alone: the boot disk, the public IPv4 address, and egress each bill
/// separately and are <em>not</em> included, which is why both of those pricing sources carry an explicit
/// "COMPUTE ONLY" warning. A Lightsail bundle price is different in kind, not just in generosity: it is a single
/// flat monthly figure that already bundles the vCPU/RAM allocation, the SSD boot disk, a public IPv4 address
/// (Lightsail does not meter it the way EC2 has since 2024-02-01), and a monthly data-transfer allowance. There
/// is nothing else a Servyx-created Lightsail instance bills for that this figure omits. A caller comparing this
/// number against a DigitalOcean droplet price is comparing two all-in figures; comparing it against an EC2 or
/// Azure figure from this codebase is comparing an all-in figure against a partial one, and the direction of the
/// error is now the other way round from the EC2-vs-DigitalOcean comparison.
/// </para>
/// <para>
/// <strong>The direction of derivation is reversed from <c>AwsEc2Pricing</c>.</strong> EC2's table anchors on a
/// published hourly rate and derives a monthly figure from it. AWS publishes Lightsail bundles the other way
/// round — a monthly price is the number on the pricing page — and bills hourly only up to that monthly amount
/// as a not-to-exceed cap. So this table anchors on the monthly figure and <see cref="For"/> derives the hourly
/// one by dividing by <see cref="HoursPerMonth"/>; the hourly figure here is an approximation of what the cap
/// implies per hour, not a separately published per-hour rate the way EC2's is.
/// </para>
/// <para>
/// Only the seven general-purpose Linux/Unix bundles from <c>nano</c> through <c>2xlarge</c> are listed — the
/// tier a game server is normally sized from. Memory- or compute-optimised, Windows, and the largest
/// (64 GB+ RAM) bundles are therefore <em>unknown</em> here rather than approximated, matching
/// <c>AwsEc2Pricing</c>'s same choice for GPU/HPC/bare-metal EC2 types.
/// </para>
/// <para>
/// The figures are on-demand list prices with no reserved-capacity or bundle-commitment discount, so
/// <see cref="CostConfidence.ListPrice"/> applies and never <see cref="CostConfidence.Exact"/> — this adapter
/// does not read the account's bill.
/// </para>
/// </remarks>
public static class AwsLightsailPricing
{
    /// <summary>The date the figures in this file were read off AWS's public pricing page.</summary>
    public const string SnapshotDate = "2026-07-27";

    /// <summary>The number of hours a month is assumed to have when deriving an hourly figure from the monthly one.</summary>
    public const decimal HoursPerMonth = 730m;

    /// <summary>The human-readable provenance stamped onto every <see cref="CostEstimate"/> this class produces.</summary>
    public const string Source =
        "AWS published Lightsail Linux/Unix bundle monthly price, snapshot taken " + SnapshotDate
        + " from https://aws.amazon.com/lightsail/pricing/ (not refreshed at runtime). ALL-IN: unlike the "
        + "compute-only EC2 and Azure figures elsewhere in this codebase, this price already includes the "
        + "vCPU/RAM allocation, the SSD boot disk, a public IPv4 address, and a monthly data-transfer allowance "
        + "in one flat number - nothing this adapter creates bills separately on top of it. The hourly figure is "
        + "the published monthly price divided by 730 (Lightsail actually bills hourly up to the monthly price "
        + "as a not-to-exceed cap, so the hourly figure here approximates that cap rather than repeating a "
        + "separately published per-hour rate).";

    /// <summary>The ISO 4217 currency the figures are quoted in.</summary>
    public const string Currency = "USD";

    private static readonly IReadOnlyDictionary<string, decimal> MonthlyByBundleId =
        new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["nano_3_0"] = 5m,
            ["micro_3_0"] = 7m,
            ["small_3_0"] = 12m,
            ["medium_3_0"] = 24m,
            ["large_3_0"] = 44m,
            ["xlarge_3_0"] = 84m,
            ["2xlarge_3_0"] = 164m,
        };

    /// <summary>The bundle ids this snapshot carries a price for.</summary>
    public static IReadOnlyCollection<string> KnownBundleIds => (IReadOnlyCollection<string>)MonthlyByBundleId.Keys;

    /// <summary>
    /// The list price for <paramref name="bundleId"/>, or <see cref="CostEstimate.Unknown"/> when this snapshot
    /// does not carry that bundle.
    /// </summary>
    public static CostEstimate For(string? bundleId)
    {
        if (string.IsNullOrWhiteSpace(bundleId))
        {
            return CostEstimate.Unknown(
                "No Lightsail bundle was supplied, so no list price could be looked up. " + Source);
        }

        if (!MonthlyByBundleId.TryGetValue(bundleId, out var monthly))
        {
            return CostEstimate.Unknown(
                $"Lightsail bundle '{bundleId}' is not in Servyx's price snapshot (which covers only these "
                + $"bundles: {string.Join(", ", MonthlyByBundleId.Keys.OrderBy(k => k, StringComparer.Ordinal))}). "
                + Source);
        }

        return new CostEstimate(
            decimal.Round(monthly / HoursPerMonth, 4, MidpointRounding.AwayFromZero),
            monthly,
            Currency,
            CostConfidence.ListPrice,
            Source);
    }
}
