using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Application.Servers;
using Servyx.Composition;
using Servyx.Domain.Definitions;
using Servyx.Domain.Servers;
using Servyx.Infrastructure.Persistence;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Proves the fix for the bug Phase 1 (server adoption) exists to close: on a fresh, single-bundled-
/// definition, provisioning-gate-closed install — Servyx's own default configuration — server adoption used
/// to be entirely unreachable, because persistence (and therefore <see cref="IServerAdoptionService"/>'s own
/// dependencies) was only ever registered when the provisioning gate was open or more than one game
/// definition was loaded. See <c>ServyxCoreCompositionExtensions</c>'s "Persistence, server-definition
/// binding, and server adoption/forget" remarks for the fix.
/// </summary>
public class AddServyxCoreAdoptionCompositionTests
{
    /// <summary>Builds a host-application builder pointed at exactly one bundled definition, with the provisioning gate left at its default (closed).</summary>
    private static HostApplicationBuilder BuildFreshInstallBuilder()
    {
        var definitionsDir = Directory.CreateTempSubdirectory("servyx-web-tests-adoption-defs-");
        var repoDefinitionsDir = Path.Combine(RepoRootLocator.Find().FullName, "definitions");
        var source = Directory.EnumerateFiles(repoDefinitionsDir, "*.yaml").First();
        File.Copy(source, Path.Combine(definitionsDir.FullName, Path.GetFileName(source)));

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Servyx:Definitions:Path"] = definitionsDir.FullName;

        // Servyx:Provisioning:Enabled is deliberately left unset — the default, gate-closed configuration a
        // fresh install actually ships with.
        return builder;
    }

    [Fact]
    public void A_fresh_single_definition_gate_closed_install_still_resolves_the_adoption_service_and_its_persistence()
    {
        var builder = BuildFreshInstallBuilder();

        var composition = builder.AddServyxCore(NullLoggerFactory.Instance);

        composition.CatalogMode.Should().Be(DefinitionCatalogMode.Single);
        composition.Provisioning.Enabled.Should().BeFalse();
        composition.RequiresDatabaseMigration.Should().BeTrue(
            because: "adoption needs somewhere durable to live even on the smallest, most default install");

        using var host = builder.Build();

        // The executable proof that "a fresh install is no longer inert": every dependency
        // ServerAdoptionService needs resolves from the container, on the exact configuration this phase
        // targets — no provisioning flag, one bundled definition, nothing else set.
        host.Services.GetRequiredService<IServerAdoptionService>().Should().NotBeNull();
        host.Services.GetRequiredService<IServerRepository>().Should().NotBeNull();
        host.Services.GetRequiredService<IServerDefinitionBindingStore>().Should().NotBeNull();
        host.Services.GetRequiredService<IDbContextFactory<ServyxDbContext>>().Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void The_only_resolvable_server_repository_invalidates_the_grant_cache_whatever_the_gate_says(string? provisioningEnabled)
    {
        // Grant-cache invalidation is only structural if EVERY host gets the decorated repository. The
        // registration sits in AddServyxCoreCore's plain method body — no `if (provisioningGate.Enabled)`,
        // no `useSingleCriteriaMode` branch — but indentation is not a guarantee and a future edit could
        // move it inside either one. A condition ORTHOGONAL to the gate would be the dangerous shape: hosts
        // with the gate OPEN but the repository UNDECORATED, where forgetting a writable server silently
        // stops invalidating and leaves a live grant behind for a container id nothing tracks any more.
        //
        // Asserted on the concrete type rather than "not null", because the whole point is WHICH
        // implementation answers: an undecorated EfServerRepository resolves perfectly well and is exactly
        // the bug.
        var builder = BuildFreshInstallBuilder();
        if (provisioningEnabled is not null)
        {
            builder.Configuration["Servyx:Provisioning:Enabled"] = provisioningEnabled;
        }

        builder.AddServyxCore(NullLoggerFactory.Instance);

        using var host = builder.Build();

        host.Services.GetRequiredService<IServerRepository>()
            .Should().BeOfType<GrantInvalidatingServerRepository>(
                because: "every write to a Server row must drop the write-grant cache, and the only way to " +
                    "guarantee that for callers nobody has written yet is for the decorated repository to be " +
                    "the sole IServerRepository the container can hand out");
    }

    [Fact]
    public async Task A_fresh_install_migrates_its_database_and_can_list_tracked_servers_without_throwing()
    {
        var builder = BuildFreshInstallBuilder();
        var dataDir = Directory.CreateTempSubdirectory("servyx-web-tests-adoption-data-");
        builder.Configuration["Servyx:Persistence:ConnectionString"] =
            $"Data Source={Path.Combine(dataDir.FullName, "servyx.db")}";

        var composition = builder.AddServyxCore(NullLoggerFactory.Instance);
        using var host = builder.Build();

        await composition.MigrateDatabaseAsync(host.Services);

        var adoption = host.Services.GetRequiredService<IServerAdoptionService>();
        var tracked = await adoption.ListTrackedAsync();

        tracked.TrackingFailed.Should().BeFalse();
        tracked.Servers.Should().BeEmpty(because: "a fresh install has adopted nothing yet, but the read must succeed rather than throw");
    }

    [Fact]
    public async Task A_migration_failure_does_not_prevent_the_host_from_starting()
    {
        var builder = BuildFreshInstallBuilder();

        // Stands in for an unwritable data directory: the connection string's directory component is
        // actually a FILE (Path.GetTempFileName() creates one), so SQLite cannot open (or create) anything
        // beneath it. Database.MigrateAsync is guaranteed to throw against this connection string.
        var unwritableParent = Path.GetTempFileName();
        builder.Configuration["Servyx:Persistence:ConnectionString"] =
            $"Data Source={Path.Combine(unwritableParent, "servyx.db")}";

        var composition = builder.AddServyxCore(NullLoggerFactory.Instance);
        using var host = builder.Build();

        var act = async () => await composition.MigrateDatabaseAsync(host.Services);

        await act.Should().NotThrowAsync(
            because: "a migration failure (e.g. an unwritable data directory) must never turn a previously " +
                      "working read-only install into one that crashes at startup");

        File.Delete(unwritableParent);
    }
}
