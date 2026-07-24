using System.Text;
using Servyx.Domain.Secrets;

namespace Servyx.Domain.Tests.Secrets;

public class SecretLeaseTests
{
    [Fact]
    public void Value_ReturnsExactBytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        using var lease = new SecretLease(bytes);

        lease.Value.ToArray().Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public void ToUtf8String_DecodesCorrectly()
    {
        var bytes = Encoding.UTF8.GetBytes("hunter2");
        using var lease = new SecretLease(bytes);

        lease.ToUtf8String().Should().Be("hunter2");
    }

    [Fact]
    public void Dispose_ZeroesUnderlyingBuffer()
    {
        var bytes = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1 };
        var lease = new SecretLease(bytes);

        lease.Dispose();

        // `bytes` is the exact array the lease took ownership of, captured before Dispose() so the zeroing
        // can be observed directly rather than through the (now-inaccessible) lease API.
        bytes.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var lease = new SecretLease([1, 2, 3]);

        lease.Dispose();
        var act = () => lease.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Value_AfterDispose_ThrowsObjectDisposedException()
    {
        var lease = new SecretLease([1, 2, 3]);
        lease.Dispose();

        var act = () => lease.Value.Length;

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void ToUtf8String_AfterDispose_ThrowsObjectDisposedException()
    {
        var lease = new SecretLease(Encoding.UTF8.GetBytes("secret"));
        lease.Dispose();

        var act = () => lease.ToUtf8String();

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Constructor_NullValue_Throws()
    {
        var act = () => new SecretLease(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
