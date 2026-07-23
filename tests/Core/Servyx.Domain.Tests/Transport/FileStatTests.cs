using FluentAssertions;
using Servyx.Domain.Control;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Transport;

public class FileStatTests
{
    private static FileStat Stat(int mode, int? uid = null, int? gid = null, string? owner = null, string? group = null, bool readOnlyMount = false) =>
        new(true, false, null, null, null)
        {
            Mode = mode,
            Uid = uid,
            Gid = gid,
            Owner = owner,
            Group = group,
            IsReadOnlyMount = readOnlyMount,
        };

    private static TargetIdentity Identity(int? uid = null, int? gid = null, string? userName = null, params int[] supplementaryGids) =>
        new(userName, uid, gid, supplementaryGids);

    [Fact]
    public void PermitsWriteBy_OwnerMatchByUid_WithOwnerWriteBit_ReturnsTrue()
    {
        var stat = Stat(Convert.ToInt32("600", 8), uid: 1000, gid: 1000);
        var identity = Identity(uid: 1000, gid: 2000);

        stat.PermitsWriteBy(identity).Should().BeTrue();
    }

    [Fact]
    public void PermitsWriteBy_OwnerMatchByUid_WithoutOwnerWriteBit_ReturnsFalse()
    {
        var stat = Stat(Convert.ToInt32("400", 8), uid: 1000, gid: 1000);
        var identity = Identity(uid: 1000, gid: 2000);

        stat.PermitsWriteBy(identity).Should().BeFalse();
    }

    [Fact]
    public void PermitsWriteBy_OwnerMatchByName_WhenUidsAreMissing_ReturnsTrue()
    {
        var stat = Stat(Convert.ToInt32("600", 8), owner: "bob");
        var identity = Identity(userName: "bob");

        stat.PermitsWriteBy(identity).Should().BeTrue();
    }

    [Fact]
    public void PermitsWriteBy_GroupMatchByPrimaryGid_ChecksGroupWriteBit()
    {
        var stat = Stat(Convert.ToInt32("060", 8), uid: 1, gid: 2000);
        var identity = Identity(uid: 999, gid: 2000);

        stat.PermitsWriteBy(identity).Should().BeTrue();
    }

    [Fact]
    public void PermitsWriteBy_GroupMatchByPrimaryGid_WithoutGroupWriteBit_ReturnsFalse()
    {
        var stat = Stat(Convert.ToInt32("040", 8), uid: 1, gid: 2000);
        var identity = Identity(uid: 999, gid: 2000);

        stat.PermitsWriteBy(identity).Should().BeFalse();
    }

    [Fact]
    public void PermitsWriteBy_GroupMatchBySupplementaryGid_ReturnsTrue()
    {
        var stat = Stat(Convert.ToInt32("060", 8), uid: 1, gid: 2000);
        var identity = Identity(uid: 999, gid: 1000, supplementaryGids: 2000);

        stat.PermitsWriteBy(identity).Should().BeTrue();
    }

    [Fact]
    public void PermitsWriteBy_NoOwnerOrGroupMatch_FallsBackToOtherBit()
    {
        var stat = Stat(Convert.ToInt32("006", 8), uid: 1, gid: 1);
        var identity = Identity(uid: 999, gid: 999);

        stat.PermitsWriteBy(identity).Should().BeTrue();
    }

    [Fact]
    public void PermitsWriteBy_NoOwnerOrGroupMatch_WithoutOtherWriteBit_ReturnsFalse()
    {
        var stat = Stat(Convert.ToInt32("004", 8), uid: 1, gid: 1);
        var identity = Identity(uid: 999, gid: 999);

        stat.PermitsWriteBy(identity).Should().BeFalse();
    }

    [Fact]
    public void PermitsWriteBy_ReadOnlyMount_OverridesEvenFullPermissions()
    {
        var stat = Stat(Convert.ToInt32("777", 8), uid: 1000, readOnlyMount: true);
        var identity = Identity(uid: 1000);

        stat.PermitsWriteBy(identity).Should().BeFalse();
    }

    [Fact]
    public void PermitsWriteBy_NullMode_ReturnsPlatformDependentAnswer()
    {
        var stat = new FileStat(true, false, null, null, null);
        var identity = Identity(uid: 1000);

        // Deliberate asymmetry: Windows has no POSIX mode bits to distrust, so a missing Mode is treated
        // as no evidence against write access there; every other platform treats a missing Mode as "we
        // genuinely don't know" and refuses.
        stat.PermitsWriteBy(identity).Should().Be(OperatingSystem.IsWindows());
    }

    [Fact]
    public void PermitsWriteBy_ThrowsOnNullIdentity()
    {
        var stat = Stat(Convert.ToInt32("777", 8));

        var act = () => stat.PermitsWriteBy(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
