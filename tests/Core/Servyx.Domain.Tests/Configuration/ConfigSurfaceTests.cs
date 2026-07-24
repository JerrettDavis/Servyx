using Servyx.Domain.Configuration;

namespace Servyx.Domain.Tests.Configuration;

public class ConfigSurfaceTests
{
    [Theory]
    [InlineData(SurfaceRole.Authoritative, true)]
    [InlineData(SurfaceRole.Derived, false)]
    [InlineData(SurfaceRole.Runtime, false)]
    public void ServyxMayWrite_IsTrueOnlyForAuthoritative(SurfaceRole role, bool expected)
    {
        var surface = new ConfigSurface("env", role, new SurfaceLocator.HostFile("/data/.env"), "dotenv", null);

        surface.ServyxMayWrite.Should().Be(expected);
    }

    [Fact]
    public void HostFileLocator_CarriesPath()
    {
        var locator = new SurfaceLocator.HostFile("/data/PalWorldSettings.ini");

        locator.Path.Should().Be("/data/PalWorldSettings.ini");
    }

    [Fact]
    public void ControlChannelLocator_CarriesChannelAndQuery()
    {
        var locator = new SurfaceLocator.ControlChannel("rcon", "ShowPlayers");

        locator.ChannelId.Should().Be("rcon");
        locator.Query.Should().Be("ShowPlayers");
    }

    [Fact]
    public void SurfaceLocator_VariantsAreNotEqualAcrossTypes()
    {
        SurfaceLocator a = new SurfaceLocator.HostFile("/data/.env");
        SurfaceLocator b = new SurfaceLocator.ControlChannel("rcon", "ShowPlayers");

        a.Should().NotBe(b);
    }
}
