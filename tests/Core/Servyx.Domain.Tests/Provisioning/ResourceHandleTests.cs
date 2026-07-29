using Servyx.Domain.Provisioning;

namespace Servyx.Domain.Tests.Provisioning;

public class ResourceHandleTests
{
    [Fact]
    public void RecordsWithEqualValues_AreEqual()
    {
        var tags = new Dictionary<string, string> { ["env"] = "prod" };

        var a = new ResourceHandle("hetzner", "vm-123", "fsn1", tags);
        var b = new ResourceHandle("hetzner", "vm-123", "fsn1", tags);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void RecordsWithDifferentProviderResourceId_AreNotEqual()
    {
        var tags = new Dictionary<string, string>();

        var a = new ResourceHandle("hetzner", "vm-123", "fsn1", tags);
        var b = new ResourceHandle("hetzner", "vm-456", "fsn1", tags);

        a.Should().NotBe(b);
    }

    [Fact]
    public void With_ProducesModifiedCopy_LeavingOriginalUnchanged()
    {
        var original = new ResourceHandle("hetzner", "vm-123", "fsn1", new Dictionary<string, string>());

        var updated = original with { Region = "nbg1" };

        updated.Region.Should().Be("nbg1");
        original.Region.Should().Be("fsn1");
    }
}
