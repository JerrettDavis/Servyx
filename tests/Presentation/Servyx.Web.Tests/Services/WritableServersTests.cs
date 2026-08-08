using Microsoft.Extensions.Configuration;
using Servyx.Domain.Transport;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// The UI-facing label derived from the same <c>Servyx:Servers:&lt;key&gt;:WriteMode</c> configuration the
/// write guard itself reads. <see cref="WritableServers.IsWritable"/> must mean "a live write control is
/// safe to render" — which a <see cref="WriteMode.PreviewOnly"/> server is NOT: every actual write still
/// throws <see cref="WritesDisabledException"/> at the transport for it, only planning is permitted.
/// </summary>
public class WritableServersTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Preview_only_does_not_report_the_server_as_writable()
    {
        var configuration = Configuration(("Servyx:Servers:palworld-server:WriteMode", "PreviewOnly"));
        var writable = WritableServers.FromConfiguration(configuration, new ProvisioningGate(enabled: true));

        writable.IsWritable("palworld-server").Should().BeFalse(
            "a PreviewOnly server may plan, but every apply still throws at the transport — a page that " +
            "renders a live write control for it would be lying");
        writable.Mode("palworld-server").Should().Be(WriteMode.PreviewOnly,
            "the UI needs to distinguish preview-capable from fully writable, not collapse both into one bool");
    }

    [Fact]
    public void Enabled_reports_the_server_as_writable()
    {
        var configuration = Configuration(("Servyx:Servers:palworld-server:WriteMode", "Enabled"));
        var writable = WritableServers.FromConfiguration(configuration, new ProvisioningGate(enabled: true));

        writable.IsWritable("palworld-server").Should().BeTrue();
        writable.Mode("palworld-server").Should().Be(WriteMode.Enabled);
    }

    [Fact]
    public void An_unmentioned_server_is_read_only_and_reports_ReadOnly()
    {
        var writable = WritableServers.FromConfiguration(
            Configuration(("Servyx:Servers:palworld-server:WriteMode", "Enabled")),
            new ProvisioningGate(enabled: true));

        writable.IsWritable("never-mentioned").Should().BeFalse();
        writable.Mode("never-mentioned").Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void The_legacy_string_constructor_still_grants_Enabled()
    {
        var writable = new WritableServers(["palworld-server"]);

        writable.IsWritable("palworld-server").Should().BeTrue();
        writable.Mode("palworld-server").Should().Be(WriteMode.Enabled);
    }

    [Fact]
    public void A_closed_gate_yields_None_and_every_server_reports_ReadOnly()
    {
        var writable = WritableServers.FromConfiguration(
            Configuration(("Servyx:Servers:palworld-server:WriteMode", "Enabled")),
            ProvisioningGate.Closed);

        writable.Any.Should().BeFalse();
        writable.IsWritable("palworld-server").Should().BeFalse();
        writable.Mode("palworld-server").Should().Be(WriteMode.ReadOnly);
    }
}
