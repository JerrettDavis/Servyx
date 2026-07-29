using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Azure.Provisioning;

/// <summary>
/// A point-in-time snapshot of Azure's published pay-as-you-go list price per Linux VM size.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THIS TABLE IS A SNAPSHOT AND WILL GO STALE.</strong> The figures below were transcribed from
/// <see href="https://azure.microsoft.com/pricing/details/virtual-machines/linux/">Azure's public Linux
/// virtual-machine pricing page</see> on <see cref="SnapshotDate"/>, for the <see cref="PricedRegion"/>
/// region. Nothing refreshes them: there is no call to the Azure Retail Prices API anywhere in this assembly,
/// deliberately, because <see cref="AzureVirtualMachineProvisioner.PlanAsync"/> must issue no HTTP request at
/// all and a plan is the main consumer of a cost figure. Re-check the page and update
/// <see cref="SnapshotDate"/> whenever this file is touched; a figure that has silently drifted is worse than
/// no figure, which is why every unknown size answers <see cref="CostEstimate.Unknown"/> instead of guessing.
/// </para>
/// <para>
/// <strong>THE FIGURE IS COMPUTE ONLY, AND THAT IS A REAL DIFFERENCE FROM THE DIGITALOCEAN TABLE.</strong>
/// A DigitalOcean droplet price is the whole machine: CPU, memory, its boot disk, its public IPv4 and a
/// transfer allowance, in one number. Azure prices those separately, and this adapter creates several of
/// them. What is priced here is the VM compute meter alone. What is <em>not</em> priced, and is nonetheless
/// created and billed by every successful provision:
/// </para>
/// <list type="bullet">
/// <item><description>the managed OS disk (per GB-month, by tier — a Premium SSD P4 is roughly $5–6/month);</description></item>
/// <item><description>the Standard-SKU static public IPv4 address (roughly $0.005/hour, about $3.60/month, and billed whether or not the VM is running);</description></item>
/// <item><description>outbound data transfer beyond the free allowance.</description></item>
/// </list>
/// <para>
/// The virtual network, subnet and network interface are genuinely free. The consequence is that a figure
/// from this table <em>understates</em> the real monthly cost of a Servyx Azure host by roughly $9–10, and a
/// caller comparing it like-for-like against a DigitalOcean figure is comparing an all-in price against a
/// partial one. That is stated in <see cref="Source"/> so the caveat travels with the number onto whatever
/// screen displays it, rather than living only here.
/// </para>
/// <para>
/// Only common general-purpose and burstable sizes are listed, because those are the tiers a game server is
/// normally sized from. A GPU, high-memory, storage-optimised, HPC or confidential-compute size is therefore
/// <em>unknown</em> here, and answers as unknown rather than being approximated from a similar size.
/// <see cref="CostConfidence.Estimated"/> exists for derived figures and is deliberately not used: nothing in
/// this file derives anything.
/// </para>
/// <para>
/// The figures are list prices before any enterprise agreement, reservation or savings-plan discount, so the
/// confidence is <see cref="CostConfidence.ListPrice"/> and never <see cref="CostConfidence.Exact"/> — this
/// adapter does not read the subscription's billing API and so cannot know what the account is actually
/// charged. Azure prices vary by region; a size's price in another region will differ from the figure here.
/// </para>
/// </remarks>
public static class AzureVirtualMachinePricing
{
    /// <summary>The date the figures in this file were read off Azure's public pricing page.</summary>
    public const string SnapshotDate = "2026-07-27";

    /// <summary>The region the figures below are quoted for. Other regions differ.</summary>
    public const string PricedRegion = "eastus";

    /// <summary>The number of hours Azure's published monthly figures assume in a month.</summary>
    public const decimal HoursPerMonth = 730m;

    /// <summary>The human-readable provenance stamped onto every <see cref="CostEstimate"/> this class produces.</summary>
    public const string Source =
        "Azure published pay-as-you-go Linux VM compute list price for region '" + PricedRegion
        + "', snapshot taken " + SnapshotDate
        + " from https://azure.microsoft.com/pricing/details/virtual-machines/linux/ (not refreshed at runtime). "
        + "COMPUTE ONLY: the managed OS disk, the static public IPv4 address and egress are billed separately "
        + "and are NOT included, so this figure understates the real cost of a Servyx Azure host and is not "
        + "directly comparable to an all-in DigitalOcean droplet price.";

    /// <summary>The ISO 4217 currency the figures are quoted in.</summary>
    public const string Currency = "USD";

    private static readonly IReadOnlyDictionary<string, decimal> HourlyBySize =
        new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            // Burstable (B-series) - the tier a small always-on game server is normally sized from.
            ["Standard_B1s"] = 0.0104m,
            ["Standard_B1ms"] = 0.0207m,
            ["Standard_B2s"] = 0.0416m,
            ["Standard_B2ms"] = 0.0832m,
            ["Standard_B4ms"] = 0.1660m,

            // General purpose, current-generation Intel and AMD.
            ["Standard_D2s_v5"] = 0.0960m,
            ["Standard_D4s_v5"] = 0.1920m,
            ["Standard_D2as_v5"] = 0.0860m,
            ["Standard_D4as_v5"] = 0.1720m,

            // Compute optimised.
            ["Standard_F2s_v2"] = 0.0846m,
            ["Standard_F4s_v2"] = 0.1690m,
        };

    /// <summary>The VM sizes this snapshot carries a price for.</summary>
    public static IReadOnlyCollection<string> KnownSizes => (IReadOnlyCollection<string>)HourlyBySize.Keys;

    /// <summary>
    /// The list price for <paramref name="vmSize"/>, or <see cref="CostEstimate.Unknown"/> when this snapshot
    /// does not carry that size.
    /// </summary>
    /// <remarks>
    /// An unknown size is a real possibility rather than a defensive branch — Azure's size catalogue is far
    /// larger than this table and gains generations regularly — so the unknown answer names the size it could
    /// not price, which is what a user needs in order to go and look the number up.
    /// </remarks>
    public static CostEstimate For(string? vmSize)
    {
        if (string.IsNullOrWhiteSpace(vmSize))
        {
            return CostEstimate.Unknown(
                "No Azure VM size was supplied, so no list price could be looked up. " + Source);
        }

        if (!HourlyBySize.TryGetValue(vmSize, out var hourly))
        {
            return CostEstimate.Unknown(
                $"Azure VM size '{vmSize}' is not in Servyx's Azure price snapshot (which covers only these "
                + $"sizes: {string.Join(", ", HourlyBySize.Keys.OrderBy(k => k, StringComparer.Ordinal))}). "
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
