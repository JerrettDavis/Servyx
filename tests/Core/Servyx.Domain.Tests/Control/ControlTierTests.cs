using Servyx.Domain.Control;

namespace Servyx.Domain.Tests.Control;

public class ControlTierTests
{
    private static CapabilityEvidence Evidence(string probeId) => new(probeId, "observed", null, DateTimeOffset.UnixEpoch);

    private static readonly ControlCapability ConfigureBaseline =
        ControlCapability.ReadRuntimeState
        | ControlCapability.ReadAuthoritativeConfig
        | ControlCapability.StartWorkload
        | ControlCapability.StopWorkloadGraceful;

    private static ControlCapability[] SplitFlags(ControlCapability mask)
        => [.. Enum.GetValues<ControlCapability>().Where(v => v != ControlCapability.None && (mask & v) == v)];

    private static ControlCapabilitySet SetOf(params ControlCapability[] granted)
        => SetOfWithRemediations(granted, remediationsByCapability: null);

    private static ControlCapabilitySet SetOfWithRemediations(
        ControlCapability[] granted,
        IReadOnlyDictionary<ControlCapability, RemediationHint[]>? remediationsByCapability)
    {
        var grants = new Dictionary<ControlCapability, CapabilityGrant>();
        foreach (var capability in granted)
        {
            grants[capability] = CapabilityGrant.Granted(capability, CapabilityConfidence.Verified, [Evidence(capability.ToString())]);
        }

        if (remediationsByCapability is not null)
        {
            foreach (var (capability, hints) in remediationsByCapability)
            {
                grants[capability] = CapabilityGrant.Denied(capability, [Evidence(capability.ToString())], hints);
            }
        }

        return ControlCapabilitySet.Build(grants, DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Evaluate_ReturnsBlind_WhenNothingIsGranted()
    {
        ControlTiers.Evaluate(ControlCapabilitySet.Empty).Should().Be(ControlTier.Blind);
    }

    [Fact]
    public void Evaluate_ReturnsObserve_WhenOnlyReadRuntimeStateIsGranted()
    {
        var set = SetOf(ControlCapability.ReadRuntimeState);

        ControlTiers.Evaluate(set).Should().Be(ControlTier.Observe);
    }

    [Fact]
    public void Evaluate_ReachesConfigure_ViaWriteEnvFile_WithoutWriteComposeFile()
    {
        var set = SetOf([.. SplitFlags(ConfigureBaseline), ControlCapability.WriteEnvFile]);

        ControlTiers.Evaluate(set).Should().Be(ControlTier.Configure);
        set.Has(ControlCapability.WriteComposeFile).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_ReachesConfigure_ViaWriteAuthoritativeConfigOnly_NoEnvNoCompose()
    {
        // Core "take what we can get" behavior: any single alternative write mechanism is enough.
        var set = SetOf([.. SplitFlags(ConfigureBaseline), ControlCapability.WriteAuthoritativeConfig]);

        ControlTiers.Evaluate(set).Should().Be(ControlTier.Configure);
        set.Has(ControlCapability.WriteEnvFile).Should().BeFalse();
        set.Has(ControlCapability.WriteComposeFile).Should().BeFalse();
    }

    [Fact]
    public void GapToNext_FromConfigure_ListsOperatesMissingAlternatives()
    {
        var set = SetOf([.. SplitFlags(ConfigureBaseline), ControlCapability.WriteEnvFile]);

        var gap = ControlTiers.GapToNext(set);

        gap.Should().NotBeNull();
        gap!.Current.Should().Be(ControlTier.Configure);
        gap.Next.Should().Be(ControlTier.Operate);
        gap.MissingAlternatives.Should().BeEquivalentTo(
        [
            ControlCapability.CreateBackup,
            ControlCapability.RestoreBackup,
            ControlCapability.ExecInWorkload,
            ControlCapability.ControlChannelWrite,
        ]);
    }

    [Fact]
    public void GapToNext_FromOperate_ListsProvisionsMissingAlternatives()
    {
        // Satisfies Operate.Required (Configure baseline + backup/restore + exec) but none of the
        // Provision-only mechanisms (compose write, recreate, create).
        var set = SetOf([
            .. SplitFlags(ConfigureBaseline),
            ControlCapability.WriteEnvFile,
            ControlCapability.CreateBackup,
            ControlCapability.RestoreBackup,
            ControlCapability.ExecInWorkload,
        ]);

        ControlTiers.Evaluate(set).Should().Be(ControlTier.Operate);

        var gap = ControlTiers.GapToNext(set);

        gap.Should().NotBeNull();
        gap!.Current.Should().Be(ControlTier.Operate);
        gap.Next.Should().Be(ControlTier.Provision);
        gap.MissingAlternatives.Should().BeEquivalentTo(
        [
            ControlCapability.WriteComposeFile,
            ControlCapability.RecreateWorkload,
            ControlCapability.CreateWorkload,
        ]);
    }

    [Fact]
    public void GapToNext_ReturnsNull_AtProvision()
    {
        var set = SetOf([
            .. SplitFlags(ConfigureBaseline),
            ControlCapability.WriteEnvFile,
            ControlCapability.CreateBackup,
            ControlCapability.RestoreBackup,
            ControlCapability.ExecInWorkload,
            ControlCapability.WriteComposeFile,
            ControlCapability.RecreateWorkload,
            ControlCapability.CreateWorkload,
        ]);

        ControlTiers.Evaluate(set).Should().Be(ControlTier.Provision);
        ControlTiers.GapToNext(set).Should().BeNull();
    }

    [Fact]
    public void GapToNext_FromBlind_ListsObservesRequirement()
    {
        var gap = ControlTiers.GapToNext(ControlCapabilitySet.Empty);

        gap.Should().NotBeNull();
        gap!.Current.Should().Be(ControlTier.Blind);
        gap.Next.Should().Be(ControlTier.Observe);
        gap.MissingAlternatives.Should().BeEquivalentTo([ControlCapability.ReadRuntimeState]);
    }

    [Fact]
    public void GapToNext_OrdersBlockers_EndUserFixableFirst_AndDeduplicatesByCode()
    {
        var hostAdminHint = new RemediationHint("SVX-CAP-0001", "Mount the RCON port.", null, RemediationActor.HostAdmin, ControlCapability.ExecInWorkload, null);
        var endUserHint = new RemediationHint("SVX-CAP-0002", "Enable RCON in settings.", null, RemediationActor.EndUser, ControlCapability.ControlChannelWrite, null);
        var duplicateHint = new RemediationHint("SVX-CAP-0002", "Duplicate, should be deduped.", null, RemediationActor.EndUser, ControlCapability.ControlChannelWrite, null);

        var set = SetOfWithRemediations(
            [.. SplitFlags(ConfigureBaseline), ControlCapability.WriteEnvFile],
            new Dictionary<ControlCapability, RemediationHint[]>
            {
                [ControlCapability.ExecInWorkload] = [hostAdminHint],
                [ControlCapability.ControlChannelWrite] = [endUserHint, duplicateHint],
            });

        var gap = ControlTiers.GapToNext(set);

        gap.Should().NotBeNull();
        gap!.Blockers.Should().HaveCount(2);
        gap.Blockers[0].Should().Be(endUserHint);
        gap.Blockers[1].Should().Be(hostAdminHint);
    }

    [Fact]
    public void IsDegraded_IsTrue_WhenTierHeldButRecommendedCapabilityMissing()
    {
        var set = SetOf(ControlCapability.ReadRuntimeState);

        ControlTiers.IsDegraded(set, ControlTier.Observe).Should().BeTrue();
    }

    [Fact]
    public void IsDegraded_IsFalse_WhenAllRecommendedCapabilitiesArePresent()
    {
        var set = SetOf(
            ControlCapability.ReadRuntimeState,
            ControlCapability.StreamLogs,
            ControlCapability.ReadMetrics,
            ControlCapability.ReadDerivedConfig);

        ControlTiers.IsDegraded(set, ControlTier.Observe).Should().BeFalse();
    }

    [Fact]
    public void IsDegraded_IsFalse_WhenTierIsNotHeldAtAll()
    {
        ControlTiers.IsDegraded(ControlCapabilitySet.Empty, ControlTier.Configure).Should().BeFalse();
    }

    [Fact]
    public void Definition_ThrowsForBlind()
    {
        var act = () => ControlTiers.Definition(ControlTier.Blind);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
