using FluentAssertions;
using Servyx.Domain.Control;

namespace Servyx.Domain.Tests.Control;

public class ControlCapabilityTests
{
    [Fact]
    public void AllValues_AreDistinctBitPositions()
    {
        var values = Enum.GetValues<ControlCapability>().Where(v => v != ControlCapability.None).ToArray();

        var aggregate = ControlCapability.None;
        foreach (var value in values)
        {
            (aggregate & value).Should().Be(ControlCapability.None, $"{value} should not overlap with previously combined flags");
            aggregate |= value;
        }
    }

    [Fact]
    public void None_HasNoFlagsSet()
    {
        ControlCapability.None.HasFlag(ControlCapability.ReadRuntimeState).Should().BeFalse();
    }

    [Fact]
    public void Flags_Combine()
    {
        var combined = ControlCapability.ReadRuntimeState | ControlCapability.WriteEnvFile;

        combined.HasFlag(ControlCapability.ReadRuntimeState).Should().BeTrue();
        combined.HasFlag(ControlCapability.WriteEnvFile).Should().BeTrue();
        combined.HasFlag(ControlCapability.WriteComposeFile).Should().BeFalse();
    }
}
