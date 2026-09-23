using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Servyx.Web;

namespace Servyx.Web.Tests;

/// <summary>
/// Regression coverage for a real production incident: the web host's own Data Protection provider (the one
/// antiforgery tokens and the operator auth cookie go through) was never configured, so ASP.NET Core fell
/// back to an ephemeral key ring on Linux and every container restart invalidated every outstanding token
/// and session. These tests assert the key ring is persisted to a real directory rather than relying on an
/// operator noticing "The key {...} was not found in the key ring" in production logs.
/// </summary>
public class DataProtectionCompositionTests
{
    [Fact]
    public void AddServyxWebHostDataProtection_PersistsTheKeyRingToTheFileSystem()
    {
        var keyRingDirectory = Path.Combine(Path.GetTempPath(), "servyx-tests", Guid.NewGuid().ToString("n"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServyxWebHostDataProtection(keyRingDirectory);

        using var provider = services.BuildServiceProvider();
        var keyManagementOptions = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        keyManagementOptions.XmlRepository.Should().BeOfType<FileSystemXmlRepository>()
            .Which.Directory.FullName.Should().Be(keyRingDirectory,
                "an unpersisted (default) key ring is ephemeral in a Linux container — every restart "
                + "invalidates every outstanding antiforgery token and session cookie");
    }

    [Fact]
    public void AddServyxWebHostDataProtection_DefaultsUnderServyxData_LikeTheRestOfTheApp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServyxWebHostDataProtection();

        using var provider = services.BuildServiceProvider();
        var keyManagementOptions = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        var expected = Path.Combine(AppContext.BaseDirectory, "servyx-data", "dataprotection-keys");
        keyManagementOptions.XmlRepository.Should().BeOfType<FileSystemXmlRepository>()
            .Which.Directory.FullName.Should().Be(expected,
                "the default must land on the durable servyx-data volume the Dockerfile already grants "
                + "the non-root container user write access to, matching SecretsOptions' own convention");
    }
}
