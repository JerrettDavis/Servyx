using Microsoft.Extensions.Configuration;
using Servyx.Domain.Transport;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// The composition root's only route to a write-enabled server. Like <see cref="ProvisioningGate"/>, it
/// must fail closed: absent, empty, and misspelled all mean read-only, and a closed provisioning gate means
/// read-only no matter what any other key says.
/// </summary>
public class ServerWriteModesTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    private static TargetDescriptor Container(string name) => new(
        "docker",
        "npipe://./pipe/docker_engine",
        null,
        null,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = name });

    private static WriteMode Resolve(IReadOnlyList<WriteModeGrant> grants, TargetDescriptor target) =>
        new GrantedWriteModeResolver(grants).Resolve(target);

    [Fact]
    public void A_closed_provisioning_gate_yields_no_grants_however_the_servers_are_configured()
    {
        // The flag-off host. Its behaviour has to be identical to the milestone that had no write path at all.
        var grants = ServerWriteModes.ReadGrants(
            Configuration(("Servyx:Servers:palworld-server:WriteMode", "Enabled")),
            ProvisioningGate.Closed);

        grants.Should().BeEmpty();
        Resolve(grants, Container("palworld-server")).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void No_configured_servers_yields_no_grants()
    {
        ServerWriteModes.ReadGrants(Configuration(), new ProvisioningGate(enabled: true)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("ReadOnly")]
    [InlineData("readonly")]
    [InlineData("yes")]
    [InlineData("true")]
    [InlineData("on")]
    public void Anything_that_is_not_a_parseable_writing_mode_grants_nothing(string value)
    {
        var grants = ServerWriteModes.ReadGrants(
            Configuration(("Servyx:Servers:palworld-server:WriteMode", value)),
            new ProvisioningGate(enabled: true));

        grants.Should().BeEmpty();
        Resolve(grants, Container("palworld-server")).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void A_server_configured_Enabled_becomes_writable_and_nothing_else_does()
    {
        var grants = ServerWriteModes.ReadGrants(
            Configuration(
                ("Servyx:Servers:palworld-server:WriteMode", "Enabled"),
                ("Servyx:Servers:minecraft-server:WriteMode", "ReadOnly")),
            new ProvisioningGate(enabled: true));

        Resolve(grants, Container("palworld-server")).Should().Be(WriteMode.Enabled);
        Resolve(grants, Container("minecraft-server")).Should().Be(WriteMode.ReadOnly);
        Resolve(grants, Container("never-mentioned")).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void PreviewOnly_is_carried_through_as_itself()
    {
        var grants = ServerWriteModes.ReadGrants(
            Configuration(("Servyx:Servers:palworld-server:WriteMode", "PreviewOnly")),
            new ProvisioningGate(enabled: true));

        Resolve(grants, Container("palworld-server")).Should().Be(WriteMode.PreviewOnly);
    }

    [Theory]
    [InlineData("containerId")]
    [InlineData("containerName")]
    [InlineData("container")]
    public void A_grant_applies_however_the_descriptor_spells_the_container(string optionKey)
    {
        var grants = ServerWriteModes.ReadGrants(
            Configuration(("Servyx:Servers:palworld-server:WriteMode", "Enabled")),
            new ProvisioningGate(enabled: true));

        var target = new TargetDescriptor(
            "docker",
            "npipe://./pipe/docker_engine",
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { [optionKey] = "palworld-server" });

        Resolve(grants, target).Should().Be(WriteMode.Enabled);
    }

    [Fact]
    public void A_grant_never_reaches_a_different_transport()
    {
        var grants = ServerWriteModes.ReadGrants(
            Configuration(("Servyx:Servers:palworld-server:WriteMode", "Enabled")),
            new ProvisioningGate(enabled: true));

        var overSsh = new TargetDescriptor(
            "ssh",
            "ssh://host:22",
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = "palworld-server" });

        Resolve(grants, overSsh).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void Case_is_not_what_stands_between_an_operator_and_a_working_configuration()
    {
        var grants = ServerWriteModes.ReadGrants(
            Configuration(("Servyx:Servers:palworld-server:WriteMode", "enabled")),
            new ProvisioningGate(enabled: true));

        Resolve(grants, Container("palworld-server")).Should().Be(WriteMode.Enabled);
    }
}
