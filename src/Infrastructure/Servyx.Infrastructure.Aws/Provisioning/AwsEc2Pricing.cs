using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// A point-in-time snapshot of AWS's published on-demand list price per EC2 instance type.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THIS TABLE IS A SNAPSHOT AND WILL GO STALE.</strong> The figures below were transcribed from
/// <see href="https://aws.amazon.com/ec2/pricing/on-demand/">AWS's public EC2 on-demand pricing page</see> on
/// <see cref="SnapshotDate"/>, for Linux in the <see cref="PricedRegion"/> region. Nothing refreshes them:
/// there is no call to the AWS Price List API anywhere in this assembly, deliberately, because
/// <see cref="AwsEc2Provisioner.PlanAsync"/> must issue no HTTP request at all and a plan is the main consumer
/// of a cost figure. Re-check the page and update <see cref="SnapshotDate"/> whenever this file is touched; a
/// figure that has silently drifted is worse than no figure, which is why every unknown instance type answers
/// <see cref="CostEstimate.Unknown"/> instead of guessing.
/// </para>
/// <para>
/// <strong>THE FIGURE IS COMPUTE ONLY, exactly as the Azure table's is, and for the same reason.</strong> A
/// DigitalOcean droplet price is the whole machine — CPU, memory, boot disk, public IPv4 and a transfer
/// allowance — in one number. AWS prices those separately, and a Servyx EC2 host creates several of them. What
/// is priced here is the instance-hour meter alone. What is <em>not</em> priced, and is nonetheless created and
/// billed by every successful provision:
/// </para>
/// <list type="bullet">
/// <item><description>
/// the EBS root volume, per GB-month by type — a gp3 volume is about $0.08/GB-month, so a stock 8&#8211;30 GiB
/// root disk is roughly $0.65&#8211;$2.40/month;
/// </description></item>
/// <item><description>
/// the public IPv4 address. AWS began charging for every in-use public IPv4 address on 2024-02-01 at
/// $0.005/hour, which is about $3.65/month and is billed for as long as the address is attached — this is the
/// single most commonly missed line on an EC2 bill;
/// </description></item>
/// <item><description>outbound data transfer beyond the free allowance.</description></item>
/// </list>
/// <para>
/// So a figure from this table <strong>understates</strong> the real monthly cost of a Servyx EC2 host by
/// roughly $4&#8211;6, and a caller comparing it like-for-like against a DigitalOcean figure is comparing an
/// all-in price against a partial one. That is stated in <see cref="Source"/> so the caveat travels with the
/// number onto whatever screen displays it, rather than living only here.
/// </para>
/// <para>
/// Only common burstable, general-purpose and compute-optimised sizes are listed, because those are the tiers a
/// game server is normally sized from. A GPU, memory-optimised beyond <c>r5.large</c>, storage-optimised, HPC,
/// Mac or bare-metal type is therefore <em>unknown</em> here, and answers as unknown rather than being
/// approximated from a similar size. <see cref="CostConfidence.Estimated"/> exists for derived figures and is
/// deliberately not used: nothing in this file derives anything.
/// </para>
/// <para>
/// The figures are on-demand list prices before any savings plan, reserved-instance or EDP discount, and they
/// are not spot prices, so the confidence is <see cref="CostConfidence.ListPrice"/> and never
/// <see cref="CostConfidence.Exact"/> — this adapter does not read the account's Cost Explorer and so cannot
/// know what the account is actually charged. EC2 prices vary by region; a type's price in another region will
/// differ from the figure here, and this adapter can be configured for any region.
/// </para>
/// </remarks>
public static class AwsEc2Pricing
{
    /// <summary>The date the figures in this file were read off AWS's public pricing page.</summary>
    public const string SnapshotDate = "2026-07-27";

    /// <summary>The region the figures below are quoted for. Other regions differ.</summary>
    public const string PricedRegion = "us-east-1";

    /// <summary>The number of hours AWS's published monthly figures assume in a month.</summary>
    public const decimal HoursPerMonth = 730m;

    /// <summary>The human-readable provenance stamped onto every <see cref="CostEstimate"/> this class produces.</summary>
    public const string Source =
        "AWS published EC2 on-demand Linux list price for region '" + PricedRegion
        + "', snapshot taken " + SnapshotDate
        + " from https://aws.amazon.com/ec2/pricing/on-demand/ (not refreshed at runtime). "
        + "COMPUTE ONLY: the EBS root volume, the public IPv4 address (charged at $0.005/hour since 2024-02-01) "
        + "and egress are billed separately and are NOT included, so this figure understates the real cost of a "
        + "Servyx EC2 host and is not directly comparable to an all-in DigitalOcean droplet price.";

    /// <summary>The ISO 4217 currency the figures are quoted in.</summary>
    public const string Currency = "USD";

    private static readonly IReadOnlyDictionary<string, decimal> HourlyByInstanceType =
        new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            // Burstable (T3, x86) - the tier a small always-on game server is normally sized from.
            ["t3.nano"] = 0.0052m,
            ["t3.micro"] = 0.0104m,
            ["t3.small"] = 0.0208m,
            ["t3.medium"] = 0.0416m,
            ["t3.large"] = 0.0832m,
            ["t3.xlarge"] = 0.1664m,
            ["t3.2xlarge"] = 0.3328m,

            // Burstable (T4g, Graviton/arm64). Note the AMI must be arm64 to boot on these.
            ["t4g.small"] = 0.0168m,
            ["t4g.medium"] = 0.0336m,
            ["t4g.large"] = 0.0672m,

            // General purpose.
            ["m5.large"] = 0.0960m,
            ["m5.xlarge"] = 0.1920m,
            ["m6i.large"] = 0.0960m,
            ["m6i.xlarge"] = 0.1920m,

            // Compute optimised - the usual choice for a single-threaded game simulation.
            ["c5.large"] = 0.0850m,
            ["c5.xlarge"] = 0.1700m,
            ["c6i.large"] = 0.0850m,
            ["c6i.xlarge"] = 0.1700m,

            // Memory optimised, smallest size only.
            ["r5.large"] = 0.1260m,
        };

    /// <summary>The instance types this snapshot carries a price for.</summary>
    public static IReadOnlyCollection<string> KnownInstanceTypes => (IReadOnlyCollection<string>)HourlyByInstanceType.Keys;

    /// <summary>
    /// The list price for <paramref name="instanceType"/>, or <see cref="CostEstimate.Unknown"/> when this
    /// snapshot does not carry that type.
    /// </summary>
    /// <remarks>
    /// An unknown type is a real possibility rather than a defensive branch — AWS's instance catalogue runs to
    /// several hundred types and gains generations constantly — so the unknown answer names the type it could
    /// not price, which is what a user needs in order to go and look the number up.
    /// </remarks>
    public static CostEstimate For(string? instanceType)
    {
        if (string.IsNullOrWhiteSpace(instanceType))
        {
            return CostEstimate.Unknown(
                "No EC2 instance type was supplied, so no list price could be looked up. " + Source);
        }

        if (!HourlyByInstanceType.TryGetValue(instanceType, out var hourly))
        {
            return CostEstimate.Unknown(
                $"EC2 instance type '{instanceType}' is not in Servyx's AWS price snapshot (which covers only "
                + $"these types: {string.Join(", ", HourlyByInstanceType.Keys.OrderBy(k => k, StringComparer.Ordinal))}). "
                + Source);
        }

        return new CostEstimate(
            hourly,
            decimal.Round(hourly * HoursPerMonth, 2, MidpointRounding.AwayFromZero),
            Currency,
            CostConfidence.ListPrice,
            Source);
    }
}
