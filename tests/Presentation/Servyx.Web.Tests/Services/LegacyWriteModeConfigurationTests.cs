using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servyx.Composition;
using Servyx.Domain.Transport;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// <c>Servyx:Servers:&lt;key&gt;:WriteMode</c> used to be the only route to a write grant. It is now ignored
/// for adopted servers — neither honoured as an override nor imported as a seed — and the operator is told,
/// by name, about every key that is being ignored.
/// </summary>
/// <remarks>
/// The security argument for ignoring rather than importing: the key names a container by NAME while the
/// grant is bound to a container ID, so importing one would attach write access to whatever container
/// currently answers to that name — which on a host where a container was rebuilt is not the workload the
/// operator was thinking of. A configuration file can also be stale, copied from another host, or committed
/// to a repository. Failing closed and making the operator re-grant once, in the UI, with attribution, is
/// the correct trade — but doing it silently would not be.
/// </remarks>
public class LegacyWriteModeConfigurationTests
{
    private static HostApplicationBuilder BuilderWithLegacyKeys(RecordingLoggerFactory loggers)
    {
        var builder = Host.CreateApplicationBuilder();

        // Point the definition catalog at an empty directory so this test composes nothing game-specific and
        // stays independent of which definitions the repository currently ships.
        builder.Configuration["Servyx:Definitions:Path"] = Directory
            .CreateTempSubdirectory("servyx-web-tests-legacy-writemode-").FullName;

        // The master switch is OPEN, which is the interesting case: with it closed everything is read-only
        // anyway and the assertion below would pass vacuously.
        builder.Configuration["Servyx:Provisioning:Enabled"] = "true";
        builder.Configuration["Servyx:Servers:palworld-server:WriteMode"] = "Enabled";
        builder.Configuration["Servyx:Servers:minecraft-server:WriteMode"] = "PreviewOnly";

        _ = loggers;
        return builder;
    }

    [Fact]
    public void A_legacy_write_mode_key_grants_nothing_and_is_named_in_a_startup_warning()
    {
        var loggers = new RecordingLoggerFactory();
        var builder = BuilderWithLegacyKeys(loggers);

        builder.AddServyxCore(loggers);

        var warnings = loggers.Entries
            .Where(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("WriteMode"))
            .ToList();

        warnings.Should().ContainSingle(
            because: "one warning naming every ignored key beats one line per key drowning the startup log");

        var message = warnings[0].Message;
        message.Should().Contain("Servyx:Servers:palworld-server:WriteMode");
        message.Should().Contain("Servyx:Servers:minecraft-server:WriteMode");
        message.Should().Contain("NO LONGER honoured",
            because: "an operator has to be able to tell this apart from the old 'that value did not parse' warning");
    }

    [Fact]
    public void A_legacy_key_never_produces_a_write_grant_or_a_writable_server()
    {
        var loggers = new RecordingLoggerFactory();
        var builder = BuilderWithLegacyKeys(loggers);

        builder.AddServyxCore(loggers);
        using var host = builder.Build();

        // No local-docker grant singleton is registered for these servers any more.
        host.Services.GetServices<WriteModeGrant>()
            .Where(grant => grant.TransportId == "docker")
            .Should().BeEmpty();

        var resolver = host.Services.GetRequiredService<IWriteModeResolver>();

        foreach (var spelling in (string[])["containerName", "containerId", "container"])
        {
            var target = new TargetDescriptor(
                "docker",
                "npipe://./pipe/docker_engine",
                null,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal) { [spelling] = "palworld-server" });

            resolver.Resolve(target).Should().Be(WriteMode.ReadOnly,
                because: $"a stale configuration key spelled '{spelling}' must not re-grant an adopted container");
        }

        host.Services.GetRequiredService<WritableServers>().Any.Should().BeFalse(
            because: "the UI label reads the same source the guard does, so it must not advertise a grant " +
                "that no longer exists");
    }

    [Fact]
    public void A_configuration_with_no_legacy_key_warns_about_nothing()
    {
        var loggers = new RecordingLoggerFactory();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Servyx:Definitions:Path"] = Directory
            .CreateTempSubdirectory("servyx-web-tests-legacy-writemode-clean-").FullName;

        builder.AddServyxCore(loggers);

        loggers.Entries.Should().NotContain(entry => entry.Message.Contains("NO LONGER honoured"));
    }
}
