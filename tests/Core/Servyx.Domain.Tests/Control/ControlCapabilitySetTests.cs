using FluentAssertions;
using Servyx.Domain.Control;

namespace Servyx.Domain.Tests.Control;

public class ControlCapabilitySetTests
{
    private static CapabilityEvidence Evidence(string probeId) => new(probeId, "observed", null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Has_IsTrueOnlyWhenEveryBitInMaskIsGranted()
    {
        var set = Build(ControlCapability.ReadRuntimeState | ControlCapability.StreamLogs);

        set.Has(ControlCapability.ReadRuntimeState).Should().BeTrue();
        set.Has(ControlCapability.ReadRuntimeState | ControlCapability.StreamLogs).Should().BeTrue();
        set.Has(ControlCapability.ReadRuntimeState | ControlCapability.ReadMetrics).Should().BeFalse();
    }

    [Fact]
    public void Has_IsTrueForNone_RegardlessOfGrantedCapabilities()
    {
        var empty = ControlCapabilitySet.Empty;
        var populated = Build(ControlCapability.ReadRuntimeState);

        empty.Has(ControlCapability.None).Should().BeTrue();
        populated.Has(ControlCapability.None).Should().BeTrue();
    }

    [Fact]
    public void Missing_ReturnsExactlyTheAbsentBits()
    {
        var set = Build(ControlCapability.ReadRuntimeState);

        var missing = set.Missing(ControlCapability.ReadRuntimeState | ControlCapability.StreamLogs | ControlCapability.ReadMetrics);

        missing.Should().Be(ControlCapability.StreamLogs | ControlCapability.ReadMetrics);
    }

    [Fact]
    public void Missing_IsNone_WhenEverythingRequiredIsGranted()
    {
        var set = Build(ControlCapability.ReadRuntimeState | ControlCapability.StreamLogs);

        set.Missing(ControlCapability.ReadRuntimeState).Should().Be(ControlCapability.None);
    }

    [Fact]
    public void Empty_HasNoGrantsAndNoCapabilities()
    {
        var empty = ControlCapabilitySet.Empty;

        empty.Granted.Should().Be(ControlCapability.None);
        empty.Verified.Should().Be(ControlCapability.None);
        empty.Probed.Should().Be(ControlCapability.None);
        empty.Grants.Should().BeEmpty();
    }

    [Fact]
    public void Build_ComputesGrantedAsVerifiedOrInferred_ButNotDeniedOrUnknown()
    {
        var grants = new Dictionary<ControlCapability, CapabilityGrant>
        {
            [ControlCapability.ReadRuntimeState] = CapabilityGrant.Granted(ControlCapability.ReadRuntimeState, CapabilityConfidence.Verified, [Evidence("p1")]),
            [ControlCapability.StreamLogs] = CapabilityGrant.Granted(ControlCapability.StreamLogs, CapabilityConfidence.Inferred, [Evidence("p2")]),
            [ControlCapability.ReadMetrics] = CapabilityGrant.Denied(ControlCapability.ReadMetrics, [Evidence("p3")]),
            [ControlCapability.PortForward] = CapabilityGrant.Unknown(ControlCapability.PortForward, [Evidence("p4")]),
        };

        var set = ControlCapabilitySet.Build(grants, DateTimeOffset.UnixEpoch);

        set.Granted.Should().Be(ControlCapability.ReadRuntimeState | ControlCapability.StreamLogs);
        set.Verified.Should().Be(ControlCapability.ReadRuntimeState);
        set.Probed.Should().Be(ControlCapability.ReadRuntimeState | ControlCapability.StreamLogs | ControlCapability.ReadMetrics | ControlCapability.PortForward);
    }

    [Fact]
    public void Fingerprint_IsDeterministic_RegardlessOfInsertionOrder()
    {
        var grantsInOneOrder = new Dictionary<ControlCapability, CapabilityGrant>
        {
            [ControlCapability.ReadRuntimeState] = CapabilityGrant.Granted(ControlCapability.ReadRuntimeState, CapabilityConfidence.Verified, [Evidence("p1")]),
            [ControlCapability.StreamLogs] = CapabilityGrant.Granted(ControlCapability.StreamLogs, CapabilityConfidence.Inferred, [Evidence("p2")]),
        };

        var grantsInReverseOrder = new Dictionary<ControlCapability, CapabilityGrant>
        {
            [ControlCapability.StreamLogs] = CapabilityGrant.Granted(ControlCapability.StreamLogs, CapabilityConfidence.Inferred, [Evidence("p2")]),
            [ControlCapability.ReadRuntimeState] = CapabilityGrant.Granted(ControlCapability.ReadRuntimeState, CapabilityConfidence.Verified, [Evidence("p1")]),
        };

        var fingerprintOne = ControlCapabilitySet.Build(grantsInOneOrder, DateTimeOffset.UnixEpoch).Fingerprint;
        var fingerprintTwo = ControlCapabilitySet.Build(grantsInReverseOrder, DateTimeOffset.UnixEpoch).Fingerprint;

        fingerprintOne.Should().Be(fingerprintTwo);
    }

    [Fact]
    public void Fingerprint_Differs_WhenAConfidenceChanges()
    {
        var verified = new Dictionary<ControlCapability, CapabilityGrant>
        {
            [ControlCapability.ReadRuntimeState] = CapabilityGrant.Granted(ControlCapability.ReadRuntimeState, CapabilityConfidence.Verified, [Evidence("p1")]),
        };

        var inferred = new Dictionary<ControlCapability, CapabilityGrant>
        {
            [ControlCapability.ReadRuntimeState] = CapabilityGrant.Granted(ControlCapability.ReadRuntimeState, CapabilityConfidence.Inferred, [Evidence("p1")]),
        };

        var fingerprintVerified = ControlCapabilitySet.Build(verified, DateTimeOffset.UnixEpoch).Fingerprint;
        var fingerprintInferred = ControlCapabilitySet.Build(inferred, DateTimeOffset.UnixEpoch).Fingerprint;

        fingerprintVerified.Should().NotBe(fingerprintInferred);
    }

    [Fact]
    public void Fingerprint_OfEmptySet_IsStable()
    {
        ControlCapabilitySet.Empty.Fingerprint.Should().Be(ControlCapabilitySet.ComputeFingerprint([]));
    }

    private static ControlCapabilitySet Build(ControlCapability granted)
    {
        var grants = new Dictionary<ControlCapability, CapabilityGrant>();
        foreach (var bit in Enum.GetValues<ControlCapability>())
        {
            if (bit == ControlCapability.None || (granted & bit) != bit)
            {
                continue;
            }

            grants[bit] = CapabilityGrant.Granted(bit, CapabilityConfidence.Verified, [Evidence(bit.ToString())]);
        }

        return ControlCapabilitySet.Build(grants, DateTimeOffset.UnixEpoch);
    }
}
