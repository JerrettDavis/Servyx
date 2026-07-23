using FluentAssertions;
using Servyx.Domain.Configuration;

namespace Servyx.Domain.Tests.Configuration;

public class DriftKindTests
{
    [Fact]
    public void Flags_Combine()
    {
        var combined = DriftKind.DesiredVsAuthoritative | DriftKind.RenderedVsRuntime;

        combined.HasFlag(DriftKind.DesiredVsAuthoritative).Should().BeTrue();
        combined.HasFlag(DriftKind.RenderedVsRuntime).Should().BeTrue();
        combined.HasFlag(DriftKind.AuthoritativeVsRendered).Should().BeFalse();
        combined.HasFlag(DriftKind.Unreadable).Should().BeFalse();
    }

    [Fact]
    public void None_MeansNoDrift()
    {
        DriftKind.None.HasFlag(DriftKind.DesiredVsAuthoritative).Should().BeFalse();
        ((int)DriftKind.None).Should().Be(0);
    }

    [Fact]
    public void AllFourFlags_AreDistinctBitPositions()
    {
        var values = new[]
        {
            DriftKind.DesiredVsAuthoritative,
            DriftKind.AuthoritativeVsRendered,
            DriftKind.RenderedVsRuntime,
            DriftKind.Unreadable,
        };

        var aggregate = DriftKind.None;
        foreach (var value in values)
        {
            (aggregate & value).Should().Be(DriftKind.None);
            aggregate |= value;
        }

        aggregate.Should().Be(DriftKind.DesiredVsAuthoritative | DriftKind.AuthoritativeVsRendered | DriftKind.RenderedVsRuntime | DriftKind.Unreadable);
    }

    [Fact]
    public void SettingState_CarriesDriftAlongsideItsFourColumns()
    {
        var state = new SettingState(
            Desired: "5000",
            Authoritative: "5000",
            Rendered: "4000",
            Runtime: "4000",
            Drift: DriftKind.AuthoritativeVsRendered,
            PendingRegeneration: false,
            IsWritable: true,
            NotWritableReason: null);

        state.Drift.Should().Be(DriftKind.AuthoritativeVsRendered);
        state.Drift.HasFlag(DriftKind.DesiredVsAuthoritative).Should().BeFalse();
    }
}
