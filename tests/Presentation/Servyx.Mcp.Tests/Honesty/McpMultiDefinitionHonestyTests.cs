using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Composition;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Rcon;
using Servyx.Mcp.Tests.Support;
using Servyx.Mcp.Tools;

namespace Servyx.Mcp.Tests.Honesty;

/// <summary>
/// The honesty seam this whole tool surface exists to protect: when two or more game definitions are
/// loaded, the single-definition-scoped RCON control-command catalogue is unconfigured fleet-wide, and that
/// must read as an explicit <c>unavailable</c> — never as an empty command list, which would read as "this
/// server has no commands", a different and false fact.
/// </summary>
public sealed class McpMultiDefinitionHonestyTests
{
    private static ServerSummary MakeSummary(string id, string name) => new(
        id, name, "unknown", ServerState.Running, ServerHealthStatus.Unknown, null, null, "local", []);

    private static IServerQueryService QueryReturning(ServerSummary summary)
    {
        var query = Substitute.For<IServerQueryService>();
        query.GetServerDetailAsync(summary.Id, Arg.Any<CancellationToken>())
            .Returns(new ServerDetail(summary, "image:latest", null, null, null, null, null, null, []));
        return query;
    }

    [Fact]
    public async Task Two_or_more_loaded_definitions_makes_the_rcon_command_catalogue_unavailable_never_empty()
    {
        var composition = ComposedHost.BuildWithMultipleDefinitions();
        composition.CatalogMode.Should().Be(DefinitionCatalogMode.Multiple, "this test's premise depends on it");

        var summary = MakeSummary("srv-1", "my-container");
        var query = QueryReturning(summary);
        var writable = WritableServers.None;

        var result = await ControlChannelTools.CommandsListAsync(
            "srv-1", composition, query, ServyxRconChannels.None, writable, CancellationToken.None);

        result.Outcome.Should().Be("unavailable");
        result.Commands.Should().BeNull(
            "an empty (non-null) list would read as 'this server has no commands' — a different, false fact " +
            "from 'this process cannot know what this server's commands are'");
        result.Unavailable.Should().NotBeNull();
        result.Unavailable!.ReasonCode.Should().Be(UnavailableReason.MultipleDefinitionsLoaded);
    }

    [Fact]
    public async Task Multi_definition_unavailability_names_all_three_affected_subsystems_and_every_loaded_id()
    {
        var composition = ComposedHost.BuildWithMultipleDefinitions();
        var loadedIds = composition.DefinitionCatalog.DefinitionsById.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        loadedIds.Should().HaveCountGreaterThanOrEqualTo(2);

        var summary = MakeSummary("srv-1", "my-container");
        var query = QueryReturning(summary);

        var result = await ControlChannelTools.CommandsListAsync(
            "srv-1", composition, query, ServyxRconChannels.None, WritableServers.None, CancellationToken.None);

        result.Unavailable.Should().NotBeNull();
        result.Unavailable!.Explanation.Should().Contain("control-command catalogue")
            .And.Contain("backup quiesce").And.Contain("stop-escalation ladder");
        result.Unavailable.Contributing.Should().BeEquivalentTo(loadedIds);
    }

    [Fact]
    public async Task Zero_definitions_loaded_reports_a_different_reason_code_than_two_definitions_loaded()
    {
        var none = ComposedHost.BuildWithNoDefinitions();
        var multiple = ComposedHost.BuildWithMultipleDefinitions();

        var summary = MakeSummary("srv-1", "my-container");

        var noneResult = await ControlChannelTools.CommandsListAsync(
            "srv-1", none, QueryReturning(summary), ServyxRconChannels.None, WritableServers.None, CancellationToken.None);
        var multipleResult = await ControlChannelTools.CommandsListAsync(
            "srv-1", multiple, QueryReturning(summary), ServyxRconChannels.None, WritableServers.None, CancellationToken.None);

        noneResult.Outcome.Should().Be("unavailable");
        multipleResult.Outcome.Should().Be("unavailable");
        noneResult.Unavailable!.ReasonCode.Should().Be(UnavailableReason.NoDefinitionsLoaded);
        multipleResult.Unavailable!.ReasonCode.Should().Be(UnavailableReason.MultipleDefinitionsLoaded);
        noneResult.Unavailable.ReasonCode.Should().NotBe(multipleResult.Unavailable.ReasonCode);
    }

    [Fact]
    public async Task Exactly_one_loaded_definition_makes_the_catalogue_available()
    {
        var single = ComposedHost.BuildWithOneDefinition();
        single.CatalogMode.Should().Be(DefinitionCatalogMode.Single);

        var summary = MakeSummary("srv-1", "my-container");
        var catalog = new Servyx.Infrastructure.Rcon.RconCommandCatalog(
            [new Servyx.Infrastructure.Rcon.RconCommand("info", "Info", ReadOnly: true)]);
        var channels = new ServyxRconChannels(
            Servyx.Composition.RconWiringOptions.Disabled, catalog, client: null, secrets: null, WritableServers.None);

        var result = await ControlChannelTools.CommandsListAsync(
            "srv-1", single, QueryReturning(summary), channels, WritableServers.None, CancellationToken.None);

        result.Outcome.Should().Be("listed");
        result.Commands.Should().NotBeNull();
        result.Unavailable.Should().BeNull();
    }

    [Fact]
    public async Task A_server_with_no_configured_rcon_channel_is_distinguishable_from_a_process_with_no_catalogue()
    {
        // Process has a usable catalogue (single definition loaded), but THIS server has no RCON channel
        // configured — ServyxRconChannels.GetSessionAsync returns null for it. That must read differently
        // from the "no catalogue at all" case (multi-definition, asserted above).
        var single = ComposedHost.BuildWithOneDefinition();
        var summary = MakeSummary("srv-1", "my-container");

        var noChannelResult = await ControlChannelTools.PlayersListAsync(
            "srv-1", single, QueryReturning(summary), ServyxRconChannels.None, CancellationToken.None);

        var multiple = ComposedHost.BuildWithMultipleDefinitions();
        var noCatalogueResult = await ControlChannelTools.PlayersListAsync(
            "srv-1", multiple, QueryReturning(summary), ServyxRconChannels.None, CancellationToken.None);

        noChannelResult.Outcome.Should().Be("unavailable");
        noCatalogueResult.Outcome.Should().Be("unavailable");
        noChannelResult.Unavailable!.ReasonCode.Should().Be(UnavailableReason.NotConfiguredForServer);
        noCatalogueResult.Unavailable!.ReasonCode.Should().Be(UnavailableReason.MultipleDefinitionsLoaded);
        noChannelResult.Unavailable.ReasonCode.Should().NotBe(noCatalogueResult.Unavailable.ReasonCode);
    }
}
