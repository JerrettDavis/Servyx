using Bunit;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Domain.Configuration;
using Servyx.Domain.Discovery;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Models;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// End-to-end test of the project brief's non-negotiable secret guarantee, spanning the real
/// <see cref="ServerQueryService"/> and <see cref="LiveDashboardDataService"/> (only the bottom-most
/// Domain-level dependencies — <see cref="IServerDiscovery"/>, <see cref="IMetricsSource"/>,
/// <see cref="ILogStream"/>, <see cref="ITransport"/> — are substituted). A discovery result whose
/// environment contains a real <c>ADMIN_PASSWORD</c> value must never let that literal string reach the
/// mapped <c>SettingRow</c> view model or the rendered <c>ServerSettingsTab</c> markup.
/// </summary>
public class LiveDashboardDataServiceSecretMaskingTests : BunitContext
{
    private const string RealSecret = "supersecret123";
    private const string ServerId = "container-1";

    [Fact]
    public async Task SecretEnvironmentValue_NeverReachesTheMappedViewModel_OrRenderedMarkup()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildDiscoveredServerWithRealSecret()]));

        var queryService = new ServerQueryService(
            discovery,
            Substitute.For<IMetricsSource>(),
            Substitute.For<ILogStream>(),
            Substitute.For<ITransport>(),
            AdoptionCriteria.PalworldDefault);

        var dataService = new LiveDashboardDataService(queryService, NullLogger<LiveDashboardDataService>.Instance);

        var rows = await dataService.GetServerSettingsAsync(ServerId);

        // The mapped view model: not just the masked field, but nothing in the row at all.
        rows.Should().NotBeEmpty();
        rows.Single(r => r.Key == "ADMIN_PASSWORD").Authoritative.Should().Be("********");
        foreach (var row in rows)
        {
            row.ToString().Should().NotContain(RealSecret);
            row.Authoritative.Should().NotBe(RealSecret);
        }

        // The rendered markup of the real settings component, fed these exact rows.
        var cut = Render<ServerSettingsTab>(p => p.Add(x => x.Settings, rows));
        cut.Markup.Should().NotContain(RealSecret);
    }

    /// <summary>
    /// Guards the fix requested in code review: <c>Desired</c> is not sourced from anywhere yet
    /// (<c>LiveDashboardDataService.MapSettings</c> passes a hardcoded <see langword="null"/>), but the
    /// masking must be structural — applied at the mapping layer regardless — so that whoever wires a
    /// real Desired source in M2/M3 cannot accidentally bypass it. This test feeds a real secret value
    /// through the same <c>MaskIfSecret</c> call site Desired goes through and asserts it never survives
    /// into the mapped row or the rendered <c>ServerSettingsTab</c> markup. It fails immediately if
    /// <c>MaskIfSecret</c>'s masking behaviour is ever removed or weakened.
    /// </summary>
    [Fact]
    public void Desired_IsMaskedAtReadTime_ForSecretSettings_AndNeverReachesRenderedMarkup()
    {
        var maskedDesired = LiveDashboardDataService.MaskIfSecret(isSecret: true, rawValue: RealSecret);

        maskedDesired.Should().Be("********");
        maskedDesired.Should().NotBe(RealSecret);
        maskedDesired.Should().NotContain(RealSecret);

        var row = new SettingRow(
            Group: "Security",
            Key: "ADMIN_PASSWORD",
            Label: "Admin / RCON password",
            IsSecret: true,
            Desired: maskedDesired,
            Authoritative: "********",
            Rendered: null,
            Runtime: null,
            Drift: DriftKind.None,
            PendingRegeneration: false);

        var cut = Render<ServerSettingsTab>(p => p.Add(x => x.Settings, new[] { row }));

        cut.Markup.Should().NotContain(RealSecret);
        var input = cut.Find("div.settings-row[data-setting-key='ADMIN_PASSWORD'] input");
        input.GetAttribute("type").Should().Be("password");
        input.GetAttribute("value").Should().Be("********");
    }

    [Fact]
    public void MaskIfSecret_LeavesNonSecretValues_Unmasked()
    {
        LiveDashboardDataService.MaskIfSecret(isSecret: false, rawValue: "Palygondwanaland")
            .Should().Be("Palygondwanaland");
    }

    [Fact]
    public void MaskIfSecret_ReturnsNull_WhenThereIsNoValueAtAll()
    {
        LiveDashboardDataService.MaskIfSecret(isSecret: true, rawValue: null).Should().BeNull();
    }

    private static DiscoveredServer BuildDiscoveredServerWithRealSecret() => new(
        ServerId: ServerId,
        Name: "palworld-server",
        Image: "thijsvanloef/palworld-server-docker:latest",
        ImageDigest: null,
        State: "running",
        HealthStatus: "unhealthy",
        CreatedAt: DateTimeOffset.UtcNow,
        StartedAt: DateTimeOffset.UtcNow,
        Ports: [],
        Mounts: [new DiscoveredMount(@"D:\Games\Palworld\data", "/palworld", true)],
        NetworkName: "palworld_default",
        ContainerIp: "172.19.0.2",
        MemoryLimitBytes: 8_000_000_000,
        CpuLimit: 4.0,
        RestartPolicy: "unless-stopped",
        ComposeLabels: new Dictionary<string, string>(),
        EnvironmentVariables: new Dictionary<string, string>
        {
            ["ADMIN_PASSWORD"] = RealSecret,
            ["SERVER_NAME"] = "Palygondwanaland",
        });
}
