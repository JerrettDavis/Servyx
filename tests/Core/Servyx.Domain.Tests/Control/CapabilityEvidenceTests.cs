using FluentAssertions;
using Servyx.Domain.Control;

namespace Servyx.Domain.Tests.Control;

public class CapabilityEvidenceTests
{
    private static CapabilityEvidence Evidence() => new("probe-1", "observed", null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Granted_Throws_WhenConfidenceIsDeniedOrUnknown()
    {
        var deniedAct = () => CapabilityGrant.Granted(ControlCapability.ReadRuntimeState, CapabilityConfidence.Denied, [Evidence()]);
        var unknownAct = () => CapabilityGrant.Granted(ControlCapability.ReadRuntimeState, CapabilityConfidence.Unknown, [Evidence()]);

        deniedAct.Should().Throw<ArgumentOutOfRangeException>();
        unknownAct.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(CapabilityConfidence.Verified)]
    [InlineData(CapabilityConfidence.Inferred)]
    public void Granted_Succeeds_ForVerifiedOrInferred(CapabilityConfidence confidence)
    {
        var grant = CapabilityGrant.Granted(ControlCapability.ReadRuntimeState, confidence, [Evidence()]);

        grant.Confidence.Should().Be(confidence);
        grant.Capability.Should().Be(ControlCapability.ReadRuntimeState);
    }

    [Fact]
    public void Denied_ProducesDeniedConfidence()
    {
        var grant = CapabilityGrant.Denied(ControlCapability.WriteComposeFile, [Evidence()]);

        grant.Confidence.Should().Be(CapabilityConfidence.Denied);
    }

    [Fact]
    public void Unknown_ProducesUnknownConfidence_AndDefaultsToGenericRemediation()
    {
        var grant = CapabilityGrant.Unknown(ControlCapability.PortForward, [Evidence()]);

        grant.Confidence.Should().Be(CapabilityConfidence.Unknown);
        grant.Remediations.Should().ContainSingle();
        grant.Remediations[0].Should().Be(RemediationHint.Unknown(ControlCapability.PortForward));
    }

    [Fact]
    public void RemediationHint_Unknown_TargetsTheGivenCapability()
    {
        var hint = RemediationHint.Unknown(ControlCapability.ExecInWorkload);

        hint.Unlocks.Should().Be(ControlCapability.ExecInWorkload);
        hint.Actor.Should().Be(RemediationActor.Servyx);
    }

    [Fact]
    public void TargetIdentity_ToString_IsReadable_WithFullInfo()
    {
        var identity = new TargetIdentity("palworld", 1000, 1000, [27, 100]);

        identity.ToString().Should().Be("palworld (uid=1000, gid=1000, groups=[27,100])");
    }

    [Fact]
    public void TargetIdentity_ToString_HandlesUnknownFields()
    {
        var identity = new TargetIdentity(null, null, null, []);

        identity.ToString().Should().Be("(unknown user) (uid=?, gid=?)");
    }
}
