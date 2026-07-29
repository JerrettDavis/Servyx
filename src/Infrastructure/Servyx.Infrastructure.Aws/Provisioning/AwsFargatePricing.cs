using System.Globalization;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// A point-in-time snapshot of AWS Fargate's published on-demand list price for Linux/X86 tasks.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THIS TABLE IS A SNAPSHOT AND WILL GO STALE.</strong> The two rates below were transcribed from
/// <see href="https://aws.amazon.com/fargate/pricing/">AWS's public Fargate pricing page</see> on
/// <see cref="SnapshotDate"/>, for the <see cref="PricedRegion"/> region. Nothing refreshes them: there is no call
/// to the AWS Price List API anywhere in this assembly, deliberately, because
/// <see cref="AwsEcsFargateProvisioner.PlanAsync"/> must issue no HTTP request at all and a plan is the main
/// consumer of a cost figure. Other regions differ, and Arm64 (Graviton) and Windows tasks are priced differently
/// again — this adapter provisions Linux/X86 and prices Linux/X86.
/// </para>
/// <para>
/// <strong>A formula, not a lookup — the same billing shape as ACI and the opposite of EC2's.</strong>
/// <c>AwsEc2Pricing</c> and <c>AwsLightsailPricing</c> are dictionaries keyed by a named instance type or bundle,
/// and answer <see cref="CostEstimate.Unknown"/> for a key they do not carry. Fargate, like Azure Container
/// Instances, meters vCPU and memory separately, so any reservation can be priced exactly from two rates. AWS
/// publishes those rates per hour and bills per second with a one-minute minimum; the hourly figure below is the
/// published one and needs no conversion.
/// </para>
/// <para>
/// <strong>THE FIGURE IS COMPUTE ONLY. IT IS NOT ALL-IN.</strong> What is priced is the vCPU and memory
/// reservation of one task, and nothing else. What is <em>not</em> priced, and is nonetheless billed for a Servyx
/// Fargate deployment:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>The EFS file system backing the mandatory volume</strong> — per GB-month for stored data, plus
/// throughput, on a resource with an independent lifetime that this adapter neither creates nor destroys and
/// which must outlive the service because it holds the save data. EFS Standard storage is priced per GB-month at
/// a rate several times S3's, so for a save directory of any size this is not a rounding error against a small
/// task.
/// </description></item>
/// <item><description>
/// <strong>CloudWatch Logs ingestion and storage</strong>, per GB, if the task is configured with the
/// <c>awslogs</c> driver — which is the only way a Fargate task's output is readable at all, so the realistic
/// choice is between paying this and having no logs.
/// </description></item>
/// <item><description>
/// <strong>A NAT gateway</strong>, if the task is placed in a private subnet. Fargate must reach the internet to
/// pull its image, and a NAT gateway bills an hourly charge plus per-GB processing that together exceed the cost
/// of a small task outright. A public subnet with <c>assignPublicIp</c> avoids it, which is why this adapter
/// defaults to that.
/// </description></item>
/// <item><description>
/// <strong>Anything added to obtain a stable address</strong> — an Application or Network Load Balancer, or Cloud
/// Map service discovery. A task's public IPv4 changes on every replacement, and a service replaces tasks as a
/// matter of routine, so this is not an optional extra for a server anyone is expected to connect to twice.
/// </description></item>
/// <item><description>
/// <strong>The public IPv4 address itself.</strong> Since 2024-02-01 AWS charges hourly for every public IPv4
/// address in use, including the one attached to a Fargate task's network interface — the same charge
/// <c>AwsEc2Pricing</c> names for an EC2 instance.
/// </description></item>
/// <item><description>Ephemeral task storage beyond the free <see cref="AwsFargateSizing.DefaultEphemeralStorageGib"/> GiB, and outbound data transfer beyond the free allowance.</description></item>
/// </list>
/// <para>
/// The consequence is stated in <see cref="Source"/> so the caveat travels with the number onto whatever screen
/// displays it: this figure understates the real monthly cost of a Servyx Fargate deployment, by more than ACI's
/// equivalent understates its own, and is not comparable like-for-like with an all-in price from another provider.
/// <c>AwsLightsailPricing</c>'s figure genuinely is all-in; this one emphatically is not, and the two must never
/// be put side by side without that being said. Confidence is <see cref="CostConfidence.ListPrice"/> and never
/// <see cref="CostConfidence.Exact"/>; this adapter reads no billing API.
/// </para>
/// </remarks>
public static class AwsFargatePricing
{
    /// <summary>The date the rates in this file were read off AWS's public pricing page.</summary>
    public const string SnapshotDate = "2026-07-29";

    /// <summary>The region the rates below are quoted for. Other regions differ.</summary>
    public const string PricedRegion = "us-east-1";

    /// <summary>The published on-demand list price per vCPU-hour for a Linux/X86 Fargate task.</summary>
    public const decimal VcpuPerHour = 0.04048m;

    /// <summary>The published on-demand list price per GB-hour of memory for a Linux/X86 Fargate task.</summary>
    public const decimal MemoryGbPerHour = 0.004445m;

    /// <summary>The number of hours AWS's published monthly figures assume in a month.</summary>
    public const decimal HoursPerMonth = 730m;

    /// <summary>The ISO 4217 currency the figures are quoted in.</summary>
    public const string Currency = "USD";

    /// <summary>The human-readable provenance stamped onto every <see cref="CostEstimate"/> this class produces.</summary>
    public const string Source =
        "AWS published on-demand Linux/X86 Fargate list price for region '" + PricedRegion
        + "', snapshot taken " + SnapshotDate
        + " from https://aws.amazon.com/fargate/pricing/ (not refreshed at runtime). "
        + "COMPUTE ONLY - NOT ALL-IN: the EFS file system backing the mandatory data volume, CloudWatch Logs "
        + "ingestion, the hourly public IPv4 address charge, any NAT gateway needed to pull the image from a "
        + "private subnet, and any load balancer or Cloud Map registration added to obtain a stable address are "
        + "all billed separately and are NOT included. This figure therefore understates the real cost of a "
        + "Servyx Fargate deployment and is not directly comparable to an all-in price from another provider. "
        + "Note also that an ECS service keeps a task running - and replaces it when it stops - so the compute "
        + "meter runs continuously by design.";

    /// <summary>
    /// The list price for a Fargate task reserving <paramref name="cpuUnits"/> ECS CPU units and
    /// <paramref name="memoryMib"/> MiB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A non-positive reservation is not priced as zero — it is answered as <see cref="CostEstimate.Unknown"/>,
    /// because a Fargate task cannot exist with one and a confident "$0.00/month" on a deploy screen is the most
    /// misleading number this file could produce.
    /// </para>
    /// <para>
    /// A reservation outside <see cref="AwsFargateSizing"/>'s matrix is still <em>priced</em> rather than refused.
    /// The arithmetic is arithmetic, and refusing here would mean a task AWS runs today under a matrix row this
    /// snapshot predates would report an unknown cost while visibly billing. Rejecting an impossible pair is
    /// <see cref="AwsFargateSizing.Require"/>'s job, at spec construction, where it stops a create rather than a
    /// number.
    /// </para>
    /// </remarks>
    /// <param name="cpuUnits">The task CPU reservation, in ECS CPU units (1024 = 1 vCPU).</param>
    /// <param name="memoryMib">The task memory reservation, in MiB.</param>
    public static CostEstimate For(int cpuUnits, int memoryMib)
    {
        if (cpuUnits <= 0 || memoryMib <= 0)
        {
            return CostEstimate.Unknown(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"An AWS Fargate task cannot be run with a CPU or memory reservation of zero or less (asked for cpu={cpuUnits} units, memory={memoryMib} MiB), so no list price applies. ")
                + Source);
        }

        var vcpu = cpuUnits / (decimal)AwsFargateSizing.CpuUnitsPerVcpu;
        var memoryGb = memoryMib / (decimal)AwsFargateSizing.MibPerGb;
        var hourly = (vcpu * VcpuPerHour) + (memoryGb * MemoryGbPerHour);

        return new CostEstimate(
            decimal.Round(hourly, 4, MidpointRounding.AwayFromZero),
            decimal.Round(hourly * HoursPerMonth, 2, MidpointRounding.AwayFromZero),
            Currency,
            CostConfidence.ListPrice,
            Source);
    }

    /// <summary>
    /// The list price for a task definition's own <c>cpu</c>/<c>memory</c> strings, as ECS reports them.
    /// </summary>
    /// <remarks>
    /// ECS types both fields as strings on the wire — see <see cref="EcsTaskDefinition"/> — so a refresh has text
    /// to price rather than numbers. Text that is not a plain unit count (the vCPU/GB notation ECS accepts for
    /// the EC2 launch type, or a member this adapter never wrote) yields
    /// <see cref="CostEstimate.Unknown"/> naming what was found, rather than a guess.
    /// </remarks>
    /// <param name="cpu">The task definition's <c>cpu</c> value, in ECS CPU units, as text.</param>
    /// <param name="memory">The task definition's <c>memory</c> value, in MiB, as text.</param>
    public static CostEstimate For(string? cpu, string? memory)
    {
        if (!int.TryParse(cpu, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cpuUnits)
            || !int.TryParse(memory, NumberStyles.Integer, CultureInfo.InvariantCulture, out var memoryMib))
        {
            return CostEstimate.Unknown(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The task definition reports cpu='{cpu}' and memory='{memory}', which are not the plain ECS unit counts a Fargate task definition carries, so no list price could be computed from them. ")
                + Source);
        }

        return For(cpuUnits, memoryMib);
    }
}
