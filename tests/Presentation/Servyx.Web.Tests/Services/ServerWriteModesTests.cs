using Microsoft.Extensions.Configuration;
using Servyx.Composition;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// <c>Servyx:Servers:&lt;key&gt;:WriteMode</c> no longer grants anything to an adopted server: the per-server
/// grant lives on the <c>Server.WriteMode</c> column and is flipped from the UI. What is left here is
/// detection, so an operator who still has the old key is told, by name, that it is being ignored — the
/// difference between a diagnosable behaviour change and a silent one.
/// </summary>
public class ServerWriteModesTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    [Fact]
    public void No_configured_servers_yields_nothing_to_warn_about()
    {
        ServerWriteModes.FindIgnoredLegacyKeys(Configuration()).Should().BeEmpty();
    }

    [Fact]
    public void A_server_section_with_no_write_mode_key_is_not_reported()
    {
        ServerWriteModes.FindIgnoredLegacyKeys(
                Configuration(("Servyx:Servers:palworld-server:Backup:Enabled", "true")))
            .Should().BeEmpty(
                "a server section that never mentioned WriteMode has nothing an operator could be confused about");
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("PreviewOnly")]
    [InlineData("ReadOnly")]
    [InlineData("yes")]
    [InlineData("")]
    public void Every_present_write_mode_key_is_reported_whatever_it_says(string value)
    {
        ServerWriteModes.FindIgnoredLegacyKeys(Configuration(("Servyx:Servers:palworld-server:WriteMode", value)))
            .Should().ContainSingle()
            .Which.Should().Be("Servyx:Servers:palworld-server:WriteMode",
                because: "an operator staring at a key that says Enabled next to a read-only server deserves to " +
                    "be told the key stopped being consulted — including when it says ReadOnly, or does not parse");
    }

    [Fact]
    public void Every_configured_server_is_named_individually()
    {
        var keys = ServerWriteModes.FindIgnoredLegacyKeys(Configuration(
            ("Servyx:Servers:palworld-server:WriteMode", "Enabled"),
            ("Servyx:Servers:minecraft-server:WriteMode", "PreviewOnly"),
            ("Servyx:Servers:valheim-server:Backup:Enabled", "true")));

        keys.Should().BeEquivalentTo(
        [
            "Servyx:Servers:palworld-server:WriteMode",
            "Servyx:Servers:minecraft-server:WriteMode",
        ],
        because: "the warning names each ignored key, so an operator knows exactly which servers to re-grant");
    }

    [Fact]
    public void Detection_does_not_depend_on_the_provisioning_gate()
    {
        // Deliberately different from the grant-reading it replaced, which returned nothing when the gate was
        // closed. A key that is being ignored is being ignored either way, and staying silent about it on a
        // read-only host would hide it from exactly the operator who is about to open the gate and wonder why
        // nothing became writable.
        ServerWriteModes.FindIgnoredLegacyKeys(Configuration(
                ("Servyx:Provisioning:Enabled", "false"),
                ("Servyx:Servers:palworld-server:WriteMode", "Enabled")))
            .Should().ContainSingle();
    }
}
