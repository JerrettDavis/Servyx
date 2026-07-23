using FluentAssertions;
using Servyx.Domain.Control;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Control;

public class ControlCapabilityEvaluatorTests
{
    private static CapabilityProbeContext Context(ProbeDepth depth = ProbeDepth.Passive) =>
        new("server-1", depth, new TargetIdentity("palworld", 1000, 1000, []), WriteMode.Enabled);

    private sealed class FakeProbe(
        string probeId,
        ControlCapability investigates,
        ProbeDepth minimumDepth,
        TransportCapabilities requiresTransport,
        Func<CapabilityProbeContext, CancellationToken, Task<IReadOnlyList<CapabilityGrant>>>? run = null,
        Exception? throwOnRun = null) : IControlCapabilityProbe
    {
        public string ProbeId { get; } = probeId;
        public ControlCapability Investigates { get; } = investigates;
        public ProbeDepth MinimumDepth { get; } = minimumDepth;
        public TransportCapabilities RequiresTransport { get; } = requiresTransport;
        public int CallCount { get; private set; }

        public async Task<IReadOnlyList<CapabilityGrant>> ProbeAsync(CapabilityProbeContext ctx, CancellationToken ct = default)
        {
            CallCount++;

            if (throwOnRun is not null)
            {
                throw throwOnRun;
            }

            if (run is not null)
            {
                return await run(ctx, ct);
            }

            return [CapabilityGrant.Granted(Investigates, CapabilityConfidence.Verified, [new CapabilityEvidence(ProbeId, "ok", null, DateTimeOffset.UnixEpoch)])];
        }
    }

    [Fact]
    public async Task EvaluateAsync_MergesGrantsFromAllPassingProbes()
    {
        var probeA = new FakeProbe("a", ControlCapability.ReadRuntimeState, ProbeDepth.Passive, TransportCapabilities.None);
        var probeB = new FakeProbe("b", ControlCapability.StreamLogs, ProbeDepth.Passive, TransportCapabilities.None);
        var evaluator = new ControlCapabilityEvaluator([probeA, probeB]);

        var set = await evaluator.EvaluateAsync(Context(), TransportCapabilities.None);

        set.Granted.Should().Be(ControlCapability.ReadRuntimeState | ControlCapability.StreamLogs);
        set.Verified.Should().Be(ControlCapability.ReadRuntimeState | ControlCapability.StreamLogs);
    }

    [Fact]
    public async Task EvaluateAsync_ProbeThatThrows_YieldsUnknown_AndOtherProbesStillContribute()
    {
        var throwing = new FakeProbe("throws", ControlCapability.WriteComposeFile, ProbeDepth.Passive, TransportCapabilities.None, throwOnRun: new InvalidOperationException("boom"));
        var healthy = new FakeProbe("healthy", ControlCapability.ReadRuntimeState, ProbeDepth.Passive, TransportCapabilities.None);
        var evaluator = new ControlCapabilityEvaluator([throwing, healthy]);

        var set = await evaluator.EvaluateAsync(Context(), TransportCapabilities.None);

        set.Grants[ControlCapability.WriteComposeFile].Confidence.Should().Be(CapabilityConfidence.Unknown);
        set.Grants[ControlCapability.WriteComposeFile].Evidence.Should().Contain(e => e.Detail == "boom");
        set.Grants[ControlCapability.ReadRuntimeState].Confidence.Should().Be(CapabilityConfidence.Verified);
        set.Has(ControlCapability.ReadRuntimeState).Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ProbeMissingRequiredTransport_YieldsUnknown_NotDenied()
    {
        var probe = new FakeProbe("needs-exec", ControlCapability.ExecInWorkload, ProbeDepth.Passive, TransportCapabilities.ExecuteCommand);
        var evaluator = new ControlCapabilityEvaluator([probe]);

        var set = await evaluator.EvaluateAsync(Context(), TransportCapabilities.None);

        probe.CallCount.Should().Be(0);
        set.Grants[ControlCapability.ExecInWorkload].Confidence.Should().Be(CapabilityConfidence.Unknown);
        set.Has(ControlCapability.ExecInWorkload).Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_ProbeExceedingAllowedDepth_IsSkipped_AsUnknown()
    {
        var probe = new FakeProbe("active-only", ControlCapability.WriteSaveData, ProbeDepth.Active, TransportCapabilities.None);
        var evaluator = new ControlCapabilityEvaluator([probe]);

        var set = await evaluator.EvaluateAsync(Context(ProbeDepth.Passive), TransportCapabilities.None);

        probe.CallCount.Should().Be(0);
        set.Grants[ControlCapability.WriteSaveData].Confidence.Should().Be(CapabilityConfidence.Unknown);
    }

    [Fact]
    public async Task EvaluateAsync_ActiveDepthContext_AllowsActiveProbeToRun()
    {
        var probe = new FakeProbe("active-only", ControlCapability.WriteSaveData, ProbeDepth.Active, TransportCapabilities.None);
        var evaluator = new ControlCapabilityEvaluator([probe]);

        var set = await evaluator.EvaluateAsync(Context(ProbeDepth.Active), TransportCapabilities.None);

        probe.CallCount.Should().Be(1);
        set.Has(ControlCapability.WriteSaveData).Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_MergesByHighestConfidence_AndUnionsEvidence()
    {
        var lowConfidence = new FakeProbe(
            "low",
            ControlCapability.ReadRuntimeState,
            ProbeDepth.Passive,
            TransportCapabilities.None,
            run: (_, _) => Task.FromResult<IReadOnlyList<CapabilityGrant>>(
            [
                CapabilityGrant.Granted(ControlCapability.ReadRuntimeState, CapabilityConfidence.Inferred, [new CapabilityEvidence("low", "inferred", null, DateTimeOffset.UnixEpoch)]),
            ]));

        var highConfidence = new FakeProbe(
            "high",
            ControlCapability.ReadRuntimeState,
            ProbeDepth.Passive,
            TransportCapabilities.None,
            run: (_, _) => Task.FromResult<IReadOnlyList<CapabilityGrant>>(
            [
                CapabilityGrant.Granted(ControlCapability.ReadRuntimeState, CapabilityConfidence.Verified, [new CapabilityEvidence("high", "verified", null, DateTimeOffset.UnixEpoch)]),
            ]));

        var evaluator = new ControlCapabilityEvaluator([lowConfidence, highConfidence]);

        var set = await evaluator.EvaluateAsync(Context(), TransportCapabilities.None);

        var grant = set.Grants[ControlCapability.ReadRuntimeState];
        grant.Confidence.Should().Be(CapabilityConfidence.Verified);
        grant.Evidence.Should().HaveCount(2);
        grant.Evidence.Should().Contain(e => e.ProbeId == "low");
        grant.Evidence.Should().Contain(e => e.ProbeId == "high");
    }

    [Fact]
    public async Task EvaluateAsync_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var probe = new FakeProbe("any", ControlCapability.ReadRuntimeState, ProbeDepth.Passive, TransportCapabilities.None);
        var evaluator = new ControlCapabilityEvaluator([probe]);

        var act = async () => await evaluator.EvaluateAsync(Context(), TransportCapabilities.None, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EvaluateAsync_ProbeThrowingOperationCanceledException_Propagates_NotSwallowed()
    {
        var probe = new FakeProbe("cancels", ControlCapability.ReadRuntimeState, ProbeDepth.Passive, TransportCapabilities.None, throwOnRun: new OperationCanceledException());
        var evaluator = new ControlCapabilityEvaluator([probe]);

        var act = async () => await evaluator.EvaluateAsync(Context(), TransportCapabilities.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EvaluateAsync_NoProbes_ReturnsEmptySet()
    {
        var evaluator = new ControlCapabilityEvaluator([]);

        var set = await evaluator.EvaluateAsync(Context(), TransportCapabilities.None);

        set.Granted.Should().Be(ControlCapability.None);
        set.Probed.Should().Be(ControlCapability.None);
    }
}
