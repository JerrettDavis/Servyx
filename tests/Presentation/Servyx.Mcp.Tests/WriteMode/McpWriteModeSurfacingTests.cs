using Microsoft.Extensions.Configuration;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Composition;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;
using Servyx.Mcp.Tools;

namespace Servyx.Mcp.Tests.WriteMode;

/// <summary>
/// Proves every server row this tool surface returns carries the write mode
/// <see cref="WritableServers.Mode"/> would resolve — the same object the transport's own
/// <c>WriteModeGrant</c>s are read from (<c>ServerWriteModes</c>/<c>SshDockerWriteModes</c> both read the
/// exact same <c>Servyx:Servers:&lt;key&gt;:WriteMode</c> configuration this reads), matched by either the
/// discovery id or the container name, and that a <see cref="Servyx.Domain.Transport.WriteMode.PreviewOnly"/>
/// grant is honestly reported as NOT mutable.
/// </summary>
public sealed class McpWriteModeSurfacingTests
{
    private static WritableServers WritableFor(string key, Servyx.Domain.Transport.WriteMode mode)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Servyx:Servers:{key}:WriteMode"] = mode.ToString(),
            })
            .Build();

        return WritableServers.FromConfiguration(configuration, new ProvisioningGate(enabled: true));
    }

    private static ServerSummary MakeSummary(string id, string name) => new(
        id, name, "unknown", ServerState.Running, ServerHealthStatus.Unknown, null, null, "local", []);

    [Fact]
    public async Task Servers_list_row_reports_enabled_when_the_server_carries_a_write_grant()
    {
        var writable = WritableFor("my-container", Servyx.Domain.Transport.WriteMode.Enabled);
        var query = Substitute.For<IServerQueryService>();
        query.GetAdoptedServersWithStatusAsync(Arg.Any<CancellationToken>())
            .Returns(ServerListResult.Ok([MakeSummary("srv-1", "my-container")]));

        var result = await ServerReadTools.ListAsync(query, writable, CancellationToken.None);

        var row = result.Servers.Should().ContainSingle().Subject;
        row.WriteMode.Should().Be("enabled");
        row.MutationsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Servers_list_row_reports_preview_only_as_not_mutable()
    {
        var writable = WritableFor("my-container", Servyx.Domain.Transport.WriteMode.PreviewOnly);
        var query = Substitute.For<IServerQueryService>();
        query.GetAdoptedServersWithStatusAsync(Arg.Any<CancellationToken>())
            .Returns(ServerListResult.Ok([MakeSummary("srv-1", "my-container")]));

        var result = await ServerReadTools.ListAsync(query, writable, CancellationToken.None);

        var row = result.Servers.Should().ContainSingle().Subject;
        row.WriteMode.Should().Be("preview-only");
        row.MutationsAllowed.Should().BeFalse("PreviewOnly may plan but every apply is still refused at the transport");
    }

    [Fact]
    public async Task Servers_list_row_matches_a_write_grant_keyed_on_the_container_name_even_when_the_discovery_id_differs()
    {
        var writable = WritableFor("my-container", Servyx.Domain.Transport.WriteMode.Enabled);
        var query = Substitute.For<IServerQueryService>();
        // The discovery id ("srv-1") differs from the configured key ("my-container") — only the container
        // name matches, exactly the case WritableServers.Mode's two-identifier match exists for.
        query.GetAdoptedServersWithStatusAsync(Arg.Any<CancellationToken>())
            .Returns(ServerListResult.Ok([MakeSummary("srv-1", "my-container")]));

        var result = await ServerReadTools.ListAsync(query, writable, CancellationToken.None);

        result.Servers.Should().ContainSingle().Which.MutationsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Server_get_result_carries_the_same_write_mode_as_the_list_row()
    {
        var writable = WritableFor("my-container", Servyx.Domain.Transport.WriteMode.Enabled);
        var query = Substitute.For<IServerQueryService>();
        var summary = MakeSummary("srv-1", "my-container");
        query.GetServerDetailAsync("srv-1", Arg.Any<CancellationToken>())
            .Returns(new ServerDetail(summary, "image:latest", null, null, null, null, null, null, []));

        var result = await ServerReadTools.GetAsync("srv-1", query, writable, CancellationToken.None);

        result.Server.Should().NotBeNull();
        result.Server!.Summary.WriteMode.Should().Be("enabled");
        result.Server.Summary.MutationsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData(Servyx.Domain.Transport.WriteMode.ReadOnly, false)]
    [InlineData(Servyx.Domain.Transport.WriteMode.PreviewOnly, false)]
    [InlineData(Servyx.Domain.Transport.WriteMode.Enabled, true)]
    public async Task Every_rcon_command_row_reports_InvocableNow_matching_ReadOnly_or_this_servers_write_mode(
        Servyx.Domain.Transport.WriteMode mode, bool mutatingCommandsInvocable)
    {
        var composition = Support.ComposedHost.BuildWithOneDefinition();
        var writable = WritableFor("my-container", mode);
        var query = Substitute.For<IServerQueryService>();
        var summary = MakeSummary("srv-1", "my-container");
        query.GetServerDetailAsync("srv-1", Arg.Any<CancellationToken>())
            .Returns(new ServerDetail(summary, "image:latest", null, null, null, null, null, null, []));

        var catalog = new Servyx.Infrastructure.Rcon.RconCommandCatalog(
        [
            new Servyx.Infrastructure.Rcon.RconCommand("info", "Info", ReadOnly: true),
            new Servyx.Infrastructure.Rcon.RconCommand("save", "Save", ReadOnly: false),
        ]);
        var channels = new ServyxRconChannels(
            Servyx.Composition.RconWiringOptions.Disabled, catalog, client: null, secrets: null, writable);

        var result = await ControlChannelTools.CommandsListAsync("srv-1", composition, query, channels, writable, CancellationToken.None);

        result.Outcome.Should().Be("listed");
        var rows = result.Commands.Should().NotBeNull().And.Subject!;
        rows.Single(r => r.Id == "info").InvocableNow.Should().BeTrue("a read-only command is always invocable");
        rows.Single(r => r.Id == "save").InvocableNow.Should().Be(
            mutatingCommandsInvocable, "a mutating command is only invocable when this server's write mode is Enabled");
    }
}
