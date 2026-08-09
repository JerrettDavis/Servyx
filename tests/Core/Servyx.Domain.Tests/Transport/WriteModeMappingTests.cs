using Servyx.Domain.Entities;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Transport;

/// <summary>
/// The two-enum hazard, pinned. <see cref="ServerWriteMode"/> (the persisted column) and
/// <see cref="WriteMode"/> (what the write guard enforces) are different types with identical members, so a
/// cast between them compiles cleanly and is correct only by coincidence — sitting directly on the
/// enforcement path. These tests, plus <see cref="WriteModeMapping"/>'s own default-arm-free switch
/// expressions, are what turn divergence into a build failure instead of a silent mis-grant.
/// </summary>
public class WriteModeMappingTests
{
    [Fact]
    public void The_two_enums_declare_the_same_members_in_the_same_order()
    {
        // Order matters as well as membership: both are persisted/compared by name today, but a cast between
        // them is ordinal-based, and every accidental cast that survives review survives because the orders
        // happen to agree. If a future change reorders one, that assumption must break loudly here rather
        // than quietly at a transport seam.
        var domain = Enum.GetNames<ServerWriteMode>();
        var transport = Enum.GetNames<WriteMode>();

        transport.Should().Equal(domain,
            because: "the transport enum is a projection of the domain enum; any divergence in name or order " +
                "makes a cast between them silently wrong on the enforcement path");
    }

    [Theory]
    [InlineData(ServerWriteMode.ReadOnly, WriteMode.ReadOnly)]
    [InlineData(ServerWriteMode.PreviewOnly, WriteMode.PreviewOnly)]
    [InlineData(ServerWriteMode.Enabled, WriteMode.Enabled)]
    public void Every_domain_member_projects_onto_its_transport_counterpart(ServerWriteMode domain, WriteMode transport)
    {
        WriteModeMapping.ToTransport(domain).Should().Be(transport);
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly, ServerWriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly, ServerWriteMode.PreviewOnly)]
    [InlineData(WriteMode.Enabled, ServerWriteMode.Enabled)]
    public void Every_transport_member_projects_back_onto_its_domain_counterpart(WriteMode transport, ServerWriteMode domain)
    {
        WriteModeMapping.ToDomain(transport).Should().Be(domain);
    }

    [Fact]
    public void Round_tripping_every_declared_member_is_the_identity()
    {
        foreach (var mode in Enum.GetValues<ServerWriteMode>())
        {
            WriteModeMapping.ToDomain(WriteModeMapping.ToTransport(mode)).Should().Be(mode);
        }
    }

    [Fact]
    public void Only_Enabled_projects_onto_the_one_value_the_write_guard_permits()
    {
        // The single most consequential line of the mapping: WriteGuardedExecutionTarget permits a mutating
        // call only for WriteMode.Enabled, so exactly one domain member may reach it.
        Enum.GetValues<ServerWriteMode>()
            .Where(mode => WriteModeMapping.ToTransport(mode) == WriteMode.Enabled)
            .Should().ContainSingle()
            .Which.Should().Be(ServerWriteMode.Enabled);
    }
}
