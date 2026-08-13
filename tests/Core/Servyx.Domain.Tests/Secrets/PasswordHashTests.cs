using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Servyx.Domain.Secrets;

namespace Servyx.Domain.Tests.Secrets;

/// <summary>
/// The shared PBKDF2-HMAC-SHA256 verifier: what it produces, what it never produces, and what it refuses.
/// Mirrors <c>Servyx.Web.Tests.Authentication.OperatorPasswordHashTests</c>, which now exercises the same
/// algorithm indirectly through <c>OperatorPasswordHash</c>'s forwarding wrapper.
/// </summary>
public class PasswordHashTests
{
    private const string Password = "correct-horse-battery-staple";

    [Fact]
    public void TheCorrectPassword_Verifies()
        => PasswordHash.Verify(PasswordHash.Create(Password), Password).Should().BeTrue();

    [Theory]
    [InlineData("wrong-horse-battery-staple")]
    [InlineData("correct-horse-battery-stapl")]
    [InlineData("correct-horse-battery-staple ")]
    [InlineData("Correct-horse-battery-staple")]
    [InlineData("")]
    public void AnythingElse_DoesNot(string candidate)
        => PasswordHash.Verify(PasswordHash.Create(Password), candidate).Should().BeFalse();

    [Fact]
    public void TheEncodedVerifier_ContainsNoTraceOfThePlaintext()
    {
        var encoded = PasswordHash.Create(Password);

        encoded.Should().NotContain(Password);
        Encoding.UTF8.GetString(PasswordHash.ToStoredBytes(encoded)).Should().NotContain(Password);

        for (var start = 0; start + 6 <= Password.Length; start++)
        {
            encoded.Should().NotContain(
                Password.Substring(start, 6),
                because: "a verifier that leaks fragments of the password is not a verifier");
        }
    }

    [Fact]
    public void TwoVerifiersForTheSamePassword_Differ()
    {
        var first = PasswordHash.Create(Password);
        var second = PasswordHash.Create(Password);

        first.Should().NotBe(second);
        PasswordHash.Verify(first, Password).Should().BeTrue();
        PasswordHash.Verify(second, Password).Should().BeTrue();
    }

    [Fact]
    public void TheEncoding_NamesItsAlgorithmAndCarriesItsOwnCost()
    {
        var parts = PasswordHash.Create(Password).Split('$');

        parts.Should().HaveCount(4);
        parts[0].Should().Be("PBKDF2-SHA256");
        parts[1].Should().Be(PasswordHash.Iterations.ToString(CultureInfo.InvariantCulture));

        Convert.FromBase64String(parts[2]).Should().HaveCount(PasswordHash.SaltSizeBytes);
        Convert.FromBase64String(parts[3]).Should().HaveCount(PasswordHash.KeySizeBytes);
    }

    [Fact]
    public void TheIterationCount_IsAtLeastTheCurrentOwaspFigureForPbkdf2Sha256()
        => PasswordHash.Iterations.Should().BeGreaterThanOrEqualTo(600_000);

    [Fact]
    public void AVerifierIsCheckedAtTheCostItRecords_NotTheCostCompiledInToday()
    {
        var salt = new byte[PasswordHash.SaltSizeBytes];
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Password, salt, 1000, HashAlgorithmName.SHA256, PasswordHash.KeySizeBytes);

        var legacy = $"PBKDF2-SHA256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";

        PasswordHash.Verify(legacy, Password).Should().BeTrue();
        PasswordHash.Verify(legacy, "something else").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-encoded-verifier")]
    [InlineData("PBKDF2-SHA256$600000$notbase64$alsonot")]
    [InlineData("PBKDF2-SHA256$0$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("PBKDF2-SHA256$-1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("SHA1$600000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("PBKDF2-SHA256$600000$AAAAAAAAAAAAAAAAAAAAAA==")]
    public void AnUnreadableVerifier_LocksEveryoneOutRatherThanLettingAnyoneIn(string? encoded)
    {
        PasswordHash.Verify(encoded, Password).Should().BeFalse();
        PasswordHash.Verify(encoded, "").Should().BeFalse();
        PasswordHash.Verify(encoded, "anything at all").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreatingAVerifierForNothing_IsRefused(string? password)
    {
        var act = () => PasswordHash.Create(password!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AVerifierRoundTripsThroughItsStoredByteForm()
    {
        var encoded = PasswordHash.Create(Password);
        var restored = PasswordHash.FromStoredBytes(PasswordHash.ToStoredBytes(encoded));

        restored.Should().Be(encoded);
        PasswordHash.Verify(restored, Password).Should().BeTrue();
    }
}
