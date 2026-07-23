using Servyx.Domain.Transport;

namespace Servyx.Domain.Control;

/// <summary>
/// Default <see cref="IControlCapabilityEvaluator"/> implementation. Runs every registered
/// <see cref="IControlCapabilityProbe"/>, skipping (never failing) probes whose transport or depth
/// requirements are not met, tolerating individual probe failures, and merging the results by capability.
/// </summary>
public sealed class ControlCapabilityEvaluator : IControlCapabilityEvaluator
{
    private readonly IReadOnlyList<IControlCapabilityProbe> _probes;

    /// <summary>Creates an evaluator over the given probes.</summary>
    /// <param name="probes">The probes to run on every <see cref="EvaluateAsync"/> call.</param>
    public ControlCapabilityEvaluator(IEnumerable<IControlCapabilityProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = probes.ToArray();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A probe whose <see cref="IControlCapabilityProbe.RequiresTransport"/> is not a subset of
    /// <paramref name="available"/>, or whose <see cref="IControlCapabilityProbe.MinimumDepth"/> exceeds
    /// <c>ctx.Depth</c>, contributes a <see cref="CapabilityConfidence.Unknown"/> grant rather than being
    /// silently dropped or treated as denied. A probe that throws also contributes
    /// <see cref="CapabilityConfidence.Unknown"/> (with the exception message as evidence) and does not
    /// prevent any other probe from running, except for <see cref="OperationCanceledException"/>, which
    /// propagates immediately.
    /// </remarks>
    public async Task<ControlCapabilitySet> EvaluateAsync(CapabilityProbeContext ctx, TransportCapabilities available, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var merged = new Dictionary<ControlCapability, CapabilityGrant>();

        foreach (var probe in _probes)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<CapabilityGrant> grants;

            if ((probe.RequiresTransport & available) != probe.RequiresTransport)
            {
                var missingTransport = probe.RequiresTransport & ~available;
                grants =
                [
                    CapabilityGrant.Unknown(
                        probe.Investigates,
                        [new CapabilityEvidence(probe.ProbeId, "Required transport capability not available.", $"Missing: {missingTransport}", DateTimeOffset.UtcNow)]),
                ];
            }
            else if (probe.MinimumDepth > ctx.Depth)
            {
                grants =
                [
                    CapabilityGrant.Unknown(
                        probe.Investigates,
                        [new CapabilityEvidence(probe.ProbeId, "Probe requires deeper access than permitted for this evaluation.", $"MinimumDepth={probe.MinimumDepth}, Allowed={ctx.Depth}", DateTimeOffset.UtcNow)]),
                ];
            }
            else
            {
                try
                {
                    grants = await probe.ProbeAsync(ctx, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    grants =
                    [
                        CapabilityGrant.Unknown(
                            probe.Investigates,
                            [new CapabilityEvidence(probe.ProbeId, "Probe threw an exception.", ex.Message, DateTimeOffset.UtcNow)]),
                    ];
                }
            }

            foreach (var grant in grants)
            {
                Merge(merged, grant);
            }
        }

        return ControlCapabilitySet.Build(merged, DateTimeOffset.UtcNow);
    }

    private static void Merge(Dictionary<ControlCapability, CapabilityGrant> merged, CapabilityGrant grant)
    {
        if (!merged.TryGetValue(grant.Capability, out var existing))
        {
            merged[grant.Capability] = grant;
            return;
        }

        var winner = grant.Confidence > existing.Confidence ? grant : existing;
        var evidence = existing.Evidence.Concat(grant.Evidence).ToArray();
        var remediations = existing.Remediations
            .Concat(grant.Remediations)
            .GroupBy(hint => hint.Code)
            .Select(group => group.First())
            .ToArray();

        merged[grant.Capability] = winner with { Evidence = evidence, Remediations = remediations };
    }
}
