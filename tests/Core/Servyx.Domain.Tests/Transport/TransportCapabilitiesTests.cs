using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Transport;

public class TransportCapabilitiesTests
{
    [Fact]
    public void Flags_Combine()
    {
        var combined = TransportCapabilities.ExecuteCommand | TransportCapabilities.FileWrite;

        combined.HasFlag(TransportCapabilities.ExecuteCommand).Should().BeTrue();
        combined.HasFlag(TransportCapabilities.FileWrite).Should().BeTrue();
        combined.HasFlag(TransportCapabilities.PortForward).Should().BeFalse();
    }

    [Fact]
    public void None_HasNoFlagsSet()
    {
        TransportCapabilities.None.HasFlag(TransportCapabilities.ExecuteCommand).Should().BeFalse();
    }

    [Fact]
    public void AllValues_AreDistinctBitPositions()
    {
        var values = Enum.GetValues<TransportCapabilities>().Where(v => v != TransportCapabilities.None).ToArray();

        var aggregate = TransportCapabilities.None;
        foreach (var value in values)
        {
            (aggregate & value).Should().Be(TransportCapabilities.None, $"{value} should not overlap with previously combined flags");
            aggregate |= value;
        }
    }

    [Fact]
    public void RemovingAFlag_LeavesOthersIntact()
    {
        var combined = TransportCapabilities.ExecuteCommand | TransportCapabilities.StreamOutput | TransportCapabilities.FileRead;

        var withoutStreaming = combined & ~TransportCapabilities.StreamOutput;

        withoutStreaming.HasFlag(TransportCapabilities.ExecuteCommand).Should().BeTrue();
        withoutStreaming.HasFlag(TransportCapabilities.FileRead).Should().BeTrue();
        withoutStreaming.HasFlag(TransportCapabilities.StreamOutput).Should().BeFalse();
    }
}
