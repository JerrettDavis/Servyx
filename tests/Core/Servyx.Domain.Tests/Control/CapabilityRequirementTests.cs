using FluentAssertions;
using Servyx.Domain.Control;

namespace Servyx.Domain.Tests.Control;

public class CapabilityRequirementTests
{
    private static CapabilityEvidence Evidence(string probeId) => new(probeId, "observed", null, DateTimeOffset.UnixEpoch);

    private static ControlCapabilitySet SetOf(params ControlCapability[] granted)
    {
        var grants = new Dictionary<ControlCapability, CapabilityGrant>();
        foreach (var capability in granted)
        {
            grants[capability] = CapabilityGrant.Granted(capability, CapabilityConfidence.Verified, [Evidence(capability.ToString())]);
        }

        return ControlCapabilitySet.Build(grants, DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void All_IsSatisfied_OnlyWhenEveryBitInMaskIsGranted()
    {
        var requirement = new CapabilityRequirement.All(ControlCapability.ReadRuntimeState | ControlCapability.StreamLogs);

        requirement.IsSatisfiedBy(SetOf(ControlCapability.ReadRuntimeState, ControlCapability.StreamLogs)).Should().BeTrue();
        requirement.IsSatisfiedBy(SetOf(ControlCapability.ReadRuntimeState)).Should().BeFalse();
    }

    [Fact]
    public void All_UnsatisfiedAlternatives_ReturnsIndividualMissingBits()
    {
        var requirement = new CapabilityRequirement.All(ControlCapability.ReadRuntimeState | ControlCapability.StreamLogs | ControlCapability.ReadMetrics);

        var missing = requirement.UnsatisfiedAlternatives(SetOf(ControlCapability.ReadRuntimeState));

        missing.Should().BeEquivalentTo([ControlCapability.StreamLogs, ControlCapability.ReadMetrics]);
    }

    [Fact]
    public void All_UnsatisfiedAlternatives_IsEmpty_WhenSatisfied()
    {
        var requirement = new CapabilityRequirement.All(ControlCapability.ReadRuntimeState);

        requirement.UnsatisfiedAlternatives(SetOf(ControlCapability.ReadRuntimeState)).Should().BeEmpty();
    }

    [Fact]
    public void AnyOf_IsSatisfiedByAnySingleAlternative()
    {
        var requirement = new CapabilityRequirement.AnyOf(ControlCapability.WriteAuthoritativeConfig, ControlCapability.WriteEnvFile, ControlCapability.WriteComposeFile);

        requirement.IsSatisfiedBy(SetOf(ControlCapability.WriteEnvFile)).Should().BeTrue();
        requirement.IsSatisfiedBy(SetOf(ControlCapability.WriteComposeFile)).Should().BeTrue();
        requirement.IsSatisfiedBy(SetOf(ControlCapability.ReadRuntimeState)).Should().BeFalse();
    }

    [Fact]
    public void AnyOf_UnsatisfiedAlternatives_ListsAllAlternatives_WhenNoneHold()
    {
        var requirement = new CapabilityRequirement.AnyOf(ControlCapability.WriteAuthoritativeConfig, ControlCapability.WriteEnvFile, ControlCapability.WriteComposeFile);

        var missing = requirement.UnsatisfiedAlternatives(SetOf());

        missing.Should().BeEquivalentTo([ControlCapability.WriteAuthoritativeConfig, ControlCapability.WriteEnvFile, ControlCapability.WriteComposeFile]);
    }

    [Fact]
    public void AnyOf_EmptyAlternatives_IsUnsatisfiable()
    {
        var requirement = new CapabilityRequirement.AnyOf();

        requirement.IsSatisfiedBy(SetOf(ControlCapability.ReadRuntimeState)).Should().BeFalse();
        requirement.UnsatisfiedAlternatives(SetOf(ControlCapability.ReadRuntimeState)).Should().BeEmpty();
    }

    [Fact]
    public void Every_RequiresAllPartsToBeSatisfied()
    {
        var requirement = new CapabilityRequirement.Every(
            new CapabilityRequirement.All(ControlCapability.ReadRuntimeState),
            new CapabilityRequirement.AnyOf(ControlCapability.WriteEnvFile, ControlCapability.WriteComposeFile));

        requirement.IsSatisfiedBy(SetOf(ControlCapability.ReadRuntimeState, ControlCapability.WriteEnvFile)).Should().BeTrue();
        requirement.IsSatisfiedBy(SetOf(ControlCapability.ReadRuntimeState)).Should().BeFalse();
        requirement.IsSatisfiedBy(SetOf(ControlCapability.WriteEnvFile)).Should().BeFalse();
    }

    [Fact]
    public void Every_UnsatisfiedAlternatives_IsUnionOfUnsatisfiedParts()
    {
        var requirement = new CapabilityRequirement.Every(
            new CapabilityRequirement.All(ControlCapability.ReadRuntimeState),
            new CapabilityRequirement.AnyOf(ControlCapability.WriteEnvFile, ControlCapability.WriteComposeFile));

        var missing = requirement.UnsatisfiedAlternatives(SetOf());

        missing.Should().BeEquivalentTo([ControlCapability.ReadRuntimeState, ControlCapability.WriteEnvFile, ControlCapability.WriteComposeFile]);
    }

    [Fact]
    public void Every_EmptyParts_IsVacuouslyTrue()
    {
        var requirement = new CapabilityRequirement.Every();

        requirement.IsSatisfiedBy(SetOf()).Should().BeTrue();
        requirement.UnsatisfiedAlternatives(SetOf()).Should().BeEmpty();
    }
}
