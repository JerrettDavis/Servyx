using System.Text;
using Servyx.Web.Authentication;

namespace Servyx.Web.Tests.Authentication;

/// <summary>
/// The password verifier itself: what it produces, what it never produces, and what it refuses.
/// </summary>
public class OperatorPasswordHashTests
{
    private const string Password = "correct-horse-battery-staple";

    [Fact]
    public void TheCorrectPassword_Verifies()
        => OperatorPasswordHash.Verify(OperatorPasswordHash.Create(Password), Password).Should().BeTrue();

    [Theory]
    [InlineData("wrong-horse-battery-staple")]
    [InlineData("correct-horse-battery-stapl")]
    [InlineData("correct-horse-battery-staple ")]
    [InlineData("Correct-horse-battery-staple")]
    [InlineData("")]
    public void AnythingElse_DoesNot(string candidate)
        => OperatorPasswordHash.Verify(OperatorPasswordHash.Create(Password), candidate).Should().BeFalse();

    [Fact]
    public void TheEncodedVerifier_ContainsNoTraceOfThePlaintext()
    {
        var encoded = OperatorPasswordHash.Create(Password);

        encoded.Should().NotContain(Password);
        Encoding.UTF8.GetString(OperatorPasswordHash.ToStoredBytes(encoded)).Should().NotContain(Password);

        // Not just the whole string: no run of the password long enough to be worth guessing from survives
        // anywhere in the encoding either.
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
        // A per-install random salt is what makes this true, and what makes a precomputed table useless.
        var first = OperatorPasswordHash.Create(Password);
        var second = OperatorPasswordHash.Create(Password);

        first.Should().NotBe(second);
        OperatorPasswordHash.Verify(first, Password).Should().BeTrue();
        OperatorPasswordHash.Verify(second, Password).Should().BeTrue();
    }

    [Fact]
    public void TheEncoding_NamesItsAlgorithmAndCarriesItsOwnCost()
    {
        var parts = OperatorPasswordHash.Create(Password).Split('$');

        parts.Should().HaveCount(4);
        parts[0].Should().Be("PBKDF2-SHA256");
        parts[1].Should().Be(OperatorPasswordHash.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Convert.FromBase64String(parts[2]).Should().HaveCount(OperatorPasswordHash.SaltSizeBytes);
        Convert.FromBase64String(parts[3]).Should().HaveCount(OperatorPasswordHash.KeySizeBytes);
    }

    [Fact]
    public void TheIterationCount_IsAtLeastTheCurrentOwaspFigureForPbkdf2Sha256()
        => OperatorPasswordHash.Iterations.Should().BeGreaterThanOrEqualTo(600_000);

    [Fact]
    public void AVerifierIsCheckedAtTheCostItRecords_NotTheCostCompiledInToday()
    {
        // Hand-built at a deliberately different (and much cheaper) iteration count than Iterations, which is
        // what an install bootstrapped before the figure was raised would have on disk. It must still verify:
        // raising the constant may not lock the existing operator out.
        var salt = new byte[OperatorPasswordHash.SaltSizeBytes];
        var key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            Password, salt, 1000, System.Security.Cryptography.HashAlgorithmName.SHA256, OperatorPasswordHash.KeySizeBytes);

        var legacy = $"PBKDF2-SHA256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";

        OperatorPasswordHash.Verify(legacy, Password).Should().BeTrue();
        OperatorPasswordHash.Verify(legacy, "something else").Should().BeFalse();
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
        // Never throws, and — the point — never returns true. A corrupted or tampered secrets file must mean
        // "nobody gets in until this is re-bootstrapped", not "everybody does".
        OperatorPasswordHash.Verify(encoded, Password).Should().BeFalse();
        OperatorPasswordHash.Verify(encoded, "").Should().BeFalse();
        OperatorPasswordHash.Verify(encoded, "anything at all").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreatingAVerifierForNothing_IsRefused(string? password)
    {
        var act = () => OperatorPasswordHash.Create(password!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AVerifierRoundTripsThroughItsStoredByteForm()
    {
        var encoded = OperatorPasswordHash.Create(Password);
        var restored = OperatorPasswordHash.FromStoredBytes(OperatorPasswordHash.ToStoredBytes(encoded));

        restored.Should().Be(encoded);
        OperatorPasswordHash.Verify(restored, Password).Should().BeTrue();
    }
}
