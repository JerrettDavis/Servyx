using FluentAssertions;
using Servyx.Domain.Common;

namespace Servyx.Domain.Tests.Common;

public class ServerIdTests
{
    [Fact]
    public void New_ProducesUniqueValues()
    {
        var a = ServerId.New();
        var b = ServerId.New();

        a.Should().NotBe(b);
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        var guid = Guid.NewGuid();
        var a = new ServerId(guid);
        var b = new ServerId(guid);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void TryParse_RoundTripsCanonicalString()
    {
        var original = ServerId.New();

        ServerId.TryParse(original.ToString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(original);
    }

    [Fact]
    public void TryParse_RejectsGarbage()
    {
        ServerId.TryParse("not-a-guid", out var parsed).Should().BeFalse();
        parsed.Should().Be(default(ServerId));
    }

    [Fact]
    public void TryParse_RejectsNull()
    {
        ServerId.TryParse(null, out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_ThrowsOnGarbage()
    {
        var act = () => ServerId.Parse("garbage");
        act.Should().Throw<FormatException>();
    }
}

public class HostIdTests
{
    [Fact]
    public void New_ProducesUniqueValues()
    {
        HostId.New().Should().NotBe(HostId.New());
    }

    [Fact]
    public void TryParse_RoundTripsCanonicalString()
    {
        var original = HostId.New();
        HostId.TryParse(original.ToString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(original);
    }

    [Fact]
    public void TryParse_RejectsGarbage()
    {
        HostId.TryParse("garbage", out _).Should().BeFalse();
    }
}

public class BackupIdTests
{
    [Fact]
    public void New_ProducesUniqueValues()
    {
        BackupId.New().Should().NotBe(BackupId.New());
    }

    [Fact]
    public void TryParse_RoundTripsCanonicalString()
    {
        var original = BackupId.New();
        BackupId.TryParse(original.ToString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(original);
    }

    [Fact]
    public void TryParse_RejectsGarbage()
    {
        BackupId.TryParse("garbage", out _).Should().BeFalse();
    }
}

public class ChangePlanIdTests
{
    [Fact]
    public void New_ProducesUniqueValues()
    {
        ChangePlanId.New().Should().NotBe(ChangePlanId.New());
    }

    [Fact]
    public void TryParse_RoundTripsCanonicalString()
    {
        var original = ChangePlanId.New();
        ChangePlanId.TryParse(original.ToString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(original);
    }

    [Fact]
    public void TryParse_RejectsGarbage()
    {
        ChangePlanId.TryParse("garbage", out _).Should().BeFalse();
    }
}

public class ChangeReceiptIdTests
{
    [Fact]
    public void New_ProducesUniqueValues()
    {
        ChangeReceiptId.New().Should().NotBe(ChangeReceiptId.New());
    }

    [Fact]
    public void TryParse_RoundTripsCanonicalString()
    {
        var original = ChangeReceiptId.New();
        ChangeReceiptId.TryParse(original.ToString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(original);
    }

    [Fact]
    public void TryParse_RejectsGarbage()
    {
        ChangeReceiptId.TryParse("garbage", out _).Should().BeFalse();
    }
}

public class ModInstallIdTests
{
    [Fact]
    public void New_ProducesUniqueValues()
    {
        ModInstallId.New().Should().NotBe(ModInstallId.New());
    }

    [Fact]
    public void TryParse_RoundTripsCanonicalString()
    {
        var original = ModInstallId.New();
        ModInstallId.TryParse(original.ToString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(original);
    }

    [Fact]
    public void TryParse_RejectsGarbage()
    {
        ModInstallId.TryParse("garbage", out _).Should().BeFalse();
    }
}
