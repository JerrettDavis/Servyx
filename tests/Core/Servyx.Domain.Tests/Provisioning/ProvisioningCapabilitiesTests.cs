using Servyx.Domain.Provisioning;

namespace Servyx.Domain.Tests.Provisioning;

public class ProvisioningCapabilitiesTests
{
    [Fact]
    public void AllValues_AreDistinctBitPositions()
    {
        var values = Enum.GetValues<ProvisioningCapabilities>().Where(v => v != ProvisioningCapabilities.None).ToArray();

        var aggregate = ProvisioningCapabilities.None;
        foreach (var value in values)
        {
            (aggregate & value).Should().Be(ProvisioningCapabilities.None, $"{value} should not overlap with previously combined flags");
            aggregate |= value;
        }
    }

    [Fact]
    public void None_HasNoFlagsSet()
    {
        ProvisioningCapabilities.None.HasFlag(ProvisioningCapabilities.Create).Should().BeFalse();
    }

    [Fact]
    public void Flags_Combine()
    {
        var combined = ProvisioningCapabilities.Create | ProvisioningCapabilities.TagQuery;

        combined.HasFlag(ProvisioningCapabilities.Create).Should().BeTrue();
        combined.HasFlag(ProvisioningCapabilities.TagQuery).Should().BeTrue();
        combined.HasFlag(ProvisioningCapabilities.Destroy).Should().BeFalse();
    }
}
