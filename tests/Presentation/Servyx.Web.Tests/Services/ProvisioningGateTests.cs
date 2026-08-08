using Microsoft.Extensions.Configuration;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// The gate must fail closed. Servyx has no authentication, so an open gate hands mutating,
/// money-spending capability to anyone who can reach the web port — a typo must never do that.
/// </summary>
public class ProvisioningGateTests
{
    private static ProvisioningGate GateFor(string? value)
    {
        var settings = new Dictionary<string, string?>();
        if (value is not null)
        {
            settings[ProvisioningGate.ConfigurationKey] = value;
        }

        return ProvisioningGate.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    [Fact]
    public void MissingKey_IsClosed() => GateFor(null).Enabled.Should().BeFalse();

    [Theory]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("no")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("enabled")]
    public void AnythingThatIsNotAParseableTrue_IsClosed(string value)
        => GateFor(value).Enabled.Should().BeFalse();

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData(" true ")]
    public void AnExplicitTrue_OpensTheGate(string value)
        => GateFor(value).Enabled.Should().BeTrue();

    [Fact]
    public void ClosedSingleton_IsClosed() => ProvisioningGate.Closed.Enabled.Should().BeFalse();
}
