using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Composition;
using Servyx.Config;
using Servyx.Domain.Configuration;
using Servyx.Infrastructure.Persistence.Configuration;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Pins the wiring that makes configuration-surface reading operative at all.
/// </summary>
/// <remarks>
/// <para>
/// Before this composition existed, <c>AddServyxConfig()</c> had no caller anywhere under <c>src/</c>: the
/// whole configuration engine — every adapter, the codec, the surface resolver — was reachable only from
/// its own tests. Even after a real <see cref="ISurfaceResolutionContextSource"/> was written, the feature
/// would have stayed inert without the registration these tests assert.
/// </para>
/// <para>
/// The ordering assertion is the load-bearing one. <c>AddServyxConfig</c> registers its placeholders with
/// <c>TryAdd</c>, so they yield only to a registration made <em>first</em>. Reordering those two lines would
/// silently revert every server to "no surface-resolution context is known" — a change that breaks no
/// compile and throws no exception, and would otherwise be caught by nothing.
/// </para>
/// </remarks>
public class AddServyxCoreConfigSurfaceCompositionTests
{
    /// <summary>A fresh, single-bundled-definition install with the provisioning gate at its default (closed).</summary>
    private static HostApplicationBuilder BuildFreshInstallBuilder()
    {
        var definitionsDir = Directory.CreateTempSubdirectory("servyx-web-tests-surfaces-defs-");
        var repoDefinitionsDir = Path.Combine(RepoRootLocator.Find().FullName, "definitions");
        var source = Directory.EnumerateFiles(repoDefinitionsDir, "*.yaml").First();
        File.Copy(source, Path.Combine(definitionsDir.FullName, Path.GetFileName(source)));

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Servyx:Definitions:Path"] = definitionsDir.FullName;
        return builder;
    }

    [Fact]
    public void The_composition_root_supersedes_the_placeholder_surface_resolution_context_source()
    {
        var builder = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);

        using var host = builder.Build();

        var source = host.Services.GetRequiredService<ISurfaceResolutionContextSource>();

        source.Should().BeOfType<ServyxSurfaceResolutionContextSource>();
        source.Should().NotBeOfType<UnconfiguredSurfaceResolutionContextSource>(
            because: "a placeholder that knows about no server resolves no surface, which is exactly the "
                + "inert state this composition exists to leave behind");
    }

    [Fact]
    public void The_composition_root_supersedes_the_placeholder_config_session_source()
    {
        var builder = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);

        using var host = builder.Build();

        var source = host.Services.GetRequiredService<IServerConfigSessionSource>();

        source.Should().BeOfType<ServyxSurfaceResolutionContextSource>();
        source.Should().NotBeOfType<UnconfiguredServerConfigSessionSource>();
    }

    [Fact]
    public void Both_seams_resolve_to_the_same_instance_so_one_session_set_answers_both_questions()
    {
        var builder = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);

        using var host = builder.Build();

        // Two registrations, one object. A second instance would open a second pair of sessions per server
        // and — worse — would answer GetAsync(serverId, target) with "unknown" for a target the other one
        // opened, because sessions are matched by identity.
        host.Services.GetRequiredService<ISurfaceResolutionContextSource>()
            .Should().BeSameAs(host.Services.GetRequiredService<IServerConfigSessionSource>());
    }

    [Fact]
    public void The_setting_state_factory_and_every_config_adapter_resolve_from_the_real_container()
    {
        var builder = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);

        using var host = builder.Build();

        host.Services.GetRequiredService<ISettingStateResolverFactory>().Should().BeOfType<SettingStateResolverFactory>();
        host.Services.GetRequiredService<ISurfaceResolver>().Should().BeOfType<SurfaceResolver>();
        host.Services.GetServices<IConfigAdapter>().Select(a => a.FormatId)
            .Should().Contain(["dotenv", "ini", "properties", "json", "yaml"]);
    }

    [Fact]
    public void The_plan_executor_and_its_two_new_seams_resolve_from_the_real_container()
    {
        var builder = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);

        using var host = builder.Build();

        // Registered by AddServyxCore, not by either Program.cs — CompositionRootSingleSourceTests forbids
        // the latter, and IPlanExecutor could not live in AddServyxConfig() anyway because it needs a
        // persistence-backed IChangePlanStore that package deliberately knows nothing about.
        host.Services.GetRequiredService<IPlanExecutor>().Should().BeOfType<PlanExecutor>();
        host.Services.GetRequiredService<IServerPlanCatalogSource>().Should().BeOfType<ServyxServerPlanCatalogSource>();
        host.Services.GetRequiredService<IChangePlanStore>().Should().BeOfType<EfChangePlanStore>();
    }

    /// <summary>
    /// The plan executor sits on exactly the dependencies <c>SettingStateResolverFactory</c> already sits on,
    /// and deliberately not on <c>IServerQueryService</c> — which optionally consumes
    /// <see cref="ISettingStateResolverFactory"/>, itself a consumer of
    /// <see cref="IServerConfigSessionSource"/>, all three singletons. Resolving both in one container is the
    /// cheap structural check that no such edge was reintroduced: a cycle through singletons fails or hangs
    /// here rather than at the first operator click.
    /// </summary>
    [Fact]
    public void Resolving_the_plan_executor_alongside_the_settings_pipeline_neither_cycles_nor_hangs()
    {
        var builder = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);

        using var host = builder.Build();

        var act = () =>
        {
            host.Services.GetRequiredService<ISettingStateResolverFactory>();
            host.Services.GetRequiredService<IPlanExecutor>();
            host.Services.GetRequiredService<IServerConfigSessionSource>();
        };

        act.Should().NotThrow();
    }

    /// <summary>
    /// A composition-root service that dials a daemon while the container is being built would turn every
    /// startup into a Docker availability check. Sessions are opened lazily, on the first question about a
    /// specific server, and never during construction.
    /// </summary>
    [Fact]
    public void Resolving_the_context_source_opens_no_session_and_contacts_no_daemon()
    {
        var builder = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);

        using var host = builder.Build();

        // Resolving is construction. If construction connected, this would throw or hang on a machine with
        // no Docker daemon — which is exactly what CI is.
        var act = () => host.Services.GetRequiredService<ServyxSurfaceResolutionContextSource>();

        act.Should().NotThrow();
    }
}
