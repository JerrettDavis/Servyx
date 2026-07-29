using Microsoft.Extensions.Configuration;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Authentication;

/// <summary>
/// The authentication gate must fail <em>closed</em>, which for a protection means staying on. This is the
/// mirror image of <c>ProvisioningGateTests</c>: there, anything unparseable must leave a capability off;
/// here, anything unparseable must leave the protection on. Both are the same rule — a typo must never
/// widen what an anonymous caller can reach.
/// </summary>
public class AuthenticationGateTests
{
    private static AuthenticationGate GateFor(string? value)
    {
        var settings = new Dictionary<string, string?>();
        if (value is not null)
        {
            settings[AuthenticationGate.ConfigurationKey] = value;
        }

        return AuthenticationGate.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    [Fact]
    public void MissingKey_LeavesAuthenticationOn() => GateFor(null).Enabled.Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("no")]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("disabled")]
    [InlineData("fasle")]
    public void AnythingThatIsNotAParseableFalse_LeavesAuthenticationOn(string value)
        => GateFor(value).Enabled.Should().BeTrue(
            "a value that cannot be read as an explicit 'false' must never be treated as one");

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData(" true ")]
    public void AnExplicitTrue_LeavesAuthenticationOn(string value)
        => GateFor(value).Enabled.Should().BeTrue();

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData(" false ")]
    public void OnlyAnExplicitFalse_TurnsAuthenticationOff(string value)
        => GateFor(value).Enabled.Should().BeFalse();

    [Fact]
    public void EnforcedSingleton_IsEnabled() => AuthenticationGate.Enforced.Enabled.Should().BeTrue();

    [Fact]
    public void DisabledSingleton_IsDisabled() => AuthenticationGate.Disabled.Enabled.Should().BeFalse();

    [Fact]
    public void TheTwoGatesDefaultInOppositeDirections()
    {
        var empty = new ConfigurationBuilder().Build();

        AuthenticationGate.FromConfiguration(empty).Enabled.Should().BeTrue(
            "a protection must be on when nothing says otherwise");
        ProvisioningGate.FromConfiguration(empty).Enabled.Should().BeFalse(
            "a capability must be off when nothing says otherwise");
    }
}
