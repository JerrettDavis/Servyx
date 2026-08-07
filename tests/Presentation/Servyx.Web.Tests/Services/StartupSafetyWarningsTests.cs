using Microsoft.Extensions.Logging;
using Servyx.Domain.Transport;
using Servyx.Web.Authentication;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// The startup cross-check between the two gates. "No authentication" and "can create billable
/// infrastructure" are each defensible alone; together they are the configuration that hands an anonymous
/// caller a payment method, and an operator who arrives there must be told at the top of the log rather than
/// by a bill.
/// </summary>
public class StartupSafetyWarningsTests
{
    private static RecordingLogger WarnFor(bool authentication, bool provisioning)
    {
        var logger = new RecordingLogger();

        StartupSafetyWarnings.LogDangerousCombinations(
            logger,
            new AuthenticationGate(authentication),
            new ProvisioningGate(provisioning));

        return logger;
    }

    [Fact]
    public void UnauthenticatedPlusProvisioning_IsLoggedAtCritical_AndNamesBothKeys()
    {
        var logger = WarnFor(authentication: false, provisioning: true);

        var entry = logger.Entries.Should().ContainSingle().Subject;

        entry.Level.Should().Be(LogLevel.Critical,
            "this is not a warning about a preference — it is an unauthenticated caller with a payment method");
        entry.EventId.Should().Be(AuthenticationAudit.UnauthenticatedProvisioning);

        entry.Message.Should().Contain(AuthenticationGate.ConfigurationKey);
        entry.Message.Should().Contain(ProvisioningGate.ConfigurationKey);
        entry.Message.Should().Contain("UNAUTHENTICATED");
        entry.Message.Should().Contain("spend");
    }

    [Fact]
    public void UnauthenticatedAlone_IsStillSaidOutLoud_ButOnlyAsAWarning()
    {
        var logger = WarnFor(authentication: false, provisioning: false);

        var entry = logger.Entries.Should().ContainSingle().Subject;

        entry.Level.Should().Be(LogLevel.Warning);
        entry.EventId.Should().Be(AuthenticationAudit.AuthenticationDisabled);
        entry.Message.Should().Contain(AuthenticationGate.ConfigurationKey);
        entry.Message.Should().Contain("DISABLED");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithAuthenticationOn_NothingIsLoggedAtAll(bool provisioning)
        => WarnFor(authentication: true, provisioning).Entries.Should().BeEmpty(
            "the default configuration must not print scary startup noise it does not deserve");

    private static WriteModeGrant EnabledGrant(string containerName, string endpoint = "ssh:user@10.0.0.9:22") =>
        new(WriteMode.Enabled, "ssh+docker", endpoint,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = containerName });

    [Fact]
    public void WriteGrants_AreEnumeratedAtWarning_EvenWithAuthenticationOn()
    {
        var logger = new RecordingLogger();

        StartupSafetyWarnings.LogDangerousCombinations(
            logger,
            new AuthenticationGate(enabled: true),
            new ProvisioningGate(enabled: true),
            [EnabledGrant("palworld-server")]);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.EventId.Should().Be(AuthenticationAudit.WriteModeGranted);
        entry.Message.Should().Contain("palworld-server");
        entry.Message.Should().Contain("Enabled");
        entry.Message.Should().Contain("ssh+docker");
    }

    [Fact]
    public void NoWriteGrants_LogsNothingBeyondWhatAuthenticationAloneWouldSay()
    {
        var logger = new RecordingLogger();

        StartupSafetyWarnings.LogDangerousCombinations(
            logger,
            new AuthenticationGate(enabled: true),
            new ProvisioningGate(enabled: true),
            []);

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void UnauthenticatedPlusAnEnabledWriteGrant_IsLoggedAtCritical_NamingTheServer()
    {
        var logger = new RecordingLogger();

        StartupSafetyWarnings.LogDangerousCombinations(
            logger,
            new AuthenticationGate(enabled: false),
            new ProvisioningGate(enabled: true),
            [EnabledGrant("palworld-server")]);

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Critical
            && e.EventId == AuthenticationAudit.UnauthenticatedWriteAccess
            && e.Message.Contains("palworld-server")
            && e.Message.Contains(AuthenticationGate.ConfigurationKey));
    }

    [Fact]
    public void UnauthenticatedPlusOnlyAPreviewGrant_DoesNotLogTheWriteAccessCritical()
    {
        var logger = new RecordingLogger();
        var previewGrant = new WriteModeGrant(WriteMode.PreviewOnly, "ssh+docker", "ssh:user@10.0.0.9:22",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = "palworld-server" });

        StartupSafetyWarnings.LogDangerousCombinations(
            logger,
            new AuthenticationGate(enabled: false),
            new ProvisioningGate(enabled: false),
            [previewGrant]);

        logger.Entries.Should().NotContain(e => e.EventId == AuthenticationAudit.UnauthenticatedWriteAccess,
            "PreviewOnly can never mutate anything, so it does not deserve the same alarm as Enabled");
    }
}
