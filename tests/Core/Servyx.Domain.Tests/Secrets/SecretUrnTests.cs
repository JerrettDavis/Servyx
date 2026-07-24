using Servyx.Domain.Secrets;

namespace Servyx.Domain.Tests.Secrets;

public class SecretUrnTests
{
    [Fact]
    public void Create_ValidSegments_ComposesExpectedValue()
    {
        var urn = SecretUrn.Create("server", "palworld-01", "rcon", "password");

        urn.Value.Should().Be("secret://server/palworld-01/rcon/password");
        urn.Scope.Should().Be("server");
        urn.ScopeId.Should().Be("palworld-01");
        urn.Category.Should().Be("rcon");
        urn.Name.Should().Be("password");
        urn.ToString().Should().Be(urn.Value);
    }

    [Fact]
    public void Create_AllowsDotsHyphensUnderscoresWithinASegment()
    {
        var urn = SecretUrn.Create("connector", "ssh-prod.1_a", "ssh", "private-key.v2");

        urn.Value.Should().Be("secret://connector/ssh-prod.1_a/ssh/private-key.v2");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("...")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("café")]
    public void Create_RejectsHostileOrInvalidSegment(string hostileSegment)
    {
        var act = () => SecretUrn.Create(hostileSegment, "scope-id", "category", "name");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsSegmentLongerThanMax()
    {
        var tooLong = new string('a', SecretUrn.MaxSegmentLength + 1);

        var act = () => SecretUrn.Create("server", "id", "rcon", tooLong);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryParse_ValidUrn_Succeeds()
    {
        var ok = SecretUrn.TryParse("secret://server/palworld-01/rcon/password", out var urn);

        ok.Should().BeTrue();
        urn.Scope.Should().Be("server");
        urn.ScopeId.Should().Be("palworld-01");
        urn.Category.Should().Be("rcon");
        urn.Name.Should().Be("password");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-urn")]
    [InlineData("http://server/palworld-01/rcon/password")]
    [InlineData("secret://server/palworld-01/rcon")]
    [InlineData("secret://server/palworld-01/rcon/password/extra")]
    [InlineData("secret://server//rcon/password")]
    [InlineData("secret:///palworld-01/rcon/password")]
    [InlineData("secret://server/palworld-01/rcon/")]
    [InlineData("secret://server/palworld-01/rcon/password/")]
    [InlineData("secret://../../../etc/passwd/rcon/password")]
    [InlineData("secret://server/../connector/rcon/password")]
    [InlineData("secret://server/palworld-01/../rcon/password")]
    [InlineData("secret://server/palworld-01/rcon/..")]
    [InlineData("secret://server/palworld-01/rcon/.")]
    [InlineData("secret://server/palworld-01/rcon/a b")]
    [InlineData("secret://server/palworld-01/rcon/a\0b")]
    [InlineData("secret://server/palworld-01/rcon/a\nb")]
    [InlineData("secret://server/pal\tworld/rcon/password")]
    public void TryParse_HostileOrMalformedInput_ReturnsFalse(string? input)
    {
        var ok = SecretUrn.TryParse(input, out var urn);

        ok.Should().BeFalse();
        urn.Should().Be(default(SecretUrn));
    }

    [Fact]
    public void TryParse_SegmentContainingEmbeddedSlashViaEncodedAttempt_ReturnsFalse()
    {
        // A segment can never itself contain '/': attempting to smuggle a fifth path component into what
        // looks like the "name" segment simply produces a URN with too many segments, which is rejected.
        var ok = SecretUrn.TryParse("secret://server/palworld-01/rcon/password/../../../secret", out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParse_AbsurdlyLongInput_ReturnsFalse()
    {
        var huge = "secret://server/" + new string('a', 100_000) + "/rcon/password";

        var ok = SecretUrn.TryParse(huge, out var urn);

        ok.Should().BeFalse();
        urn.Should().Be(default(SecretUrn));
    }

    [Fact]
    public void TryParse_ThenToString_RoundTripsExactly()
    {
        const string original = "secret://connector/ssh-prod-1/ssh/private-key";

        SecretUrn.TryParse(original, out var urn).Should().BeTrue();

        urn.ToString().Should().Be(original);
    }

    [Fact]
    public void IsValidSegment_NullOrEmpty_ReturnsFalse()
    {
        SecretUrn.IsValidSegment(null).Should().BeFalse();
        SecretUrn.IsValidSegment(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void IsValidSegment_OrdinarySegment_ReturnsTrue()
    {
        SecretUrn.IsValidSegment("palworld-01").Should().BeTrue();
    }

    [Fact]
    public void DefaultSecretUrn_HasNullFields()
    {
        var urn = default(SecretUrn);

        urn.Value.Should().BeNull();
        urn.Scope.Should().BeNull();
    }
}
