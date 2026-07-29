using System.Globalization;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Azure.Provisioning;

/// <summary>
/// A point-in-time snapshot of Azure Container Instances' published pay-as-you-go list price for Linux
/// container groups.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THIS TABLE IS A SNAPSHOT AND WILL GO STALE.</strong> The two per-second rates below were
/// transcribed from <see href="https://azure.microsoft.com/pricing/details/container-instances/">Azure's
/// public Container Instances pricing page</see> on <see cref="SnapshotDate"/>, for the
/// <see cref="PricedRegion"/> region. Nothing refreshes them: there is no call to the Azure Retail Prices
/// API anywhere in this assembly, deliberately, because
/// <see cref="AzureContainerInstanceProvisioner.PlanAsync"/> must issue no HTTP request at all and a plan is
/// the main consumer of a cost figure.
/// </para>
/// <para>
/// <strong>The billing model is genuinely different from a VM's, and that is why this is a formula rather
/// than a lookup.</strong> A virtual machine is priced per named size, so
/// <see cref="AzureVirtualMachinePricing"/> is a dictionary and answers
/// <see cref="CostEstimate.Unknown"/> for a size it does not carry. ACI has no size catalogue at all: vCPU
/// and memory are two independent continuous allocations, each billed per second, so any allocation can be
/// priced exactly from two rates. There is consequently no "unknown allocation" case here — which is a real
/// difference in the shape of the provider, not a stronger claim about the numbers.
/// </para>
/// <para>
/// <strong>THE FIGURE IS COMPUTE ONLY.</strong> What is priced is the vCPU-second and GB-second meters of
/// the container group, and nothing else. What is <em>not</em> priced, and is nonetheless billed for every
/// deployment this adapter creates:
/// </para>
/// <list type="bullet">
/// <item><description>
/// the storage account backing the mandatory Azure Files share — per GB-month for capacity plus per-10,000
/// transactions, on a resource with an independent lifetime that this adapter neither creates nor destroys,
/// and which must outlive the container group because it holds the save data;
/// </description></item>
/// <item><description>outbound data transfer beyond the free allowance;</description></item>
/// <item><description>
/// anything an operator adds to obtain a stable address. ACI's own documentation warns a container group's
/// public IP may change when the group restarts, and the usual answer — an Application Gateway or similar —
/// bills more per month than a small container group does.
/// </description></item>
/// </list>
/// <para>
/// The consequence is the same as for the VM table and is stated in <see cref="Source"/> so the caveat
/// travels with the number onto whatever screen displays it: this figure understates the real monthly cost
/// of a Servyx ACI deployment, and is not comparable like-for-like with an all-in price from another
/// provider. Confidence is <see cref="CostConfidence.ListPrice"/> and never
/// <see cref="CostConfidence.Exact"/>; this adapter does not read the subscription's billing API.
/// </para>
/// </remarks>
public static class AzureContainerInstancePricing
{
    /// <summary>The date the rates in this file were read off Azure's public pricing page.</summary>
    public const string SnapshotDate = "2026-07-27";

    /// <summary>The region the rates below are quoted for. Other regions differ.</summary>
    public const string PricedRegion = "eastus";

    /// <summary>The published list price per vCPU-second for a Linux container group.</summary>
    public const decimal VcpuPerSecond = 0.0000135m;

    /// <summary>The published list price per GB-second of memory for a Linux container group.</summary>
    public const decimal MemoryGbPerSecond = 0.0000015m;

    /// <summary>Seconds in an hour, the unit <see cref="CostEstimate.Hourly"/> is quoted in.</summary>
    public const decimal SecondsPerHour = 3600m;

    /// <summary>The number of hours Azure's published monthly figures assume in a month.</summary>
    public const decimal HoursPerMonth = 730m;

    /// <summary>The ISO 4217 currency the figures are quoted in.</summary>
    public const string Currency = "USD";

    /// <summary>The human-readable provenance stamped onto every <see cref="CostEstimate"/> this class produces.</summary>
    public const string Source =
        "Azure published pay-as-you-go Linux Container Instances list price for region '" + PricedRegion
        + "', snapshot taken " + SnapshotDate
        + " from https://azure.microsoft.com/pricing/details/container-instances/ (not refreshed at runtime). "
        + "COMPUTE ONLY: the storage account backing the mandatory Azure Files share, its transactions, egress, "
        + "and anything added to obtain a stable address are all billed separately and are NOT included, so this "
        + "figure understates the real cost of a Servyx ACI deployment and is not directly comparable to an "
        + "all-in price from another provider. Note also that ACI bills for a container group whenever it is "
        + "running, and the default restart policy keeps it running.";

    /// <summary>
    /// The list price for a container group allocated <paramref name="cpu"/> vCPU and
    /// <paramref name="memoryInGb"/> GB.
    /// </summary>
    /// <remarks>
    /// A non-positive allocation is not priced as zero — it is answered as
    /// <see cref="CostEstimate.Unknown"/>, because a container group cannot be created with one and a
    /// confident "$0.00/month" on a deploy screen is the most misleading number this file could produce.
    /// </remarks>
    /// <param name="cpu">The vCPU allocation, in cores.</param>
    /// <param name="memoryInGb">The memory allocation, in GB.</param>
    public static CostEstimate For(decimal cpu, decimal memoryInGb)
    {
        if (cpu <= 0m || memoryInGb <= 0m)
        {
            return CostEstimate.Unknown(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"An Azure Container Instances group cannot be created with a vCPU or memory allocation of zero or less (asked for cpu={cpu}, memoryInGB={memoryInGb}), so no list price applies. ")
                + Source);
        }

        var hourly = ((cpu * VcpuPerSecond) + (memoryInGb * MemoryGbPerSecond)) * SecondsPerHour;

        return new CostEstimate(
            decimal.Round(hourly, 4, MidpointRounding.AwayFromZero),
            decimal.Round(hourly * HoursPerMonth, 2, MidpointRounding.AwayFromZero),
            Currency,
            CostConfidence.ListPrice,
            Source);
    }
}
