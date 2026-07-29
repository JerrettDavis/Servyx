using Microsoft.Extensions.DependencyInjection;
using Servyx.Application.Provisioning;
using Servyx.Domain.Provisioning;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Composes provisioning the way <c>Program.cs</c>'s gated block does, so the registration that makes the
/// Apply control live is checked by a test rather than only at startup on an operator's machine.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The ledger is registered scoped here because the real one is.</strong>
/// <c>AddServyxProvisioningLedger()</c> binds <c>EfProvisioningLedger</c>, which rides on the scoped
/// <c>ServyxDbContext</c>. Registering the fake as a singleton would make this test pass while the real
/// composition threw at startup — a lifetime defect is only visible against the real lifetimes.
/// </para>
/// <para>
/// Every failure this file guards against is one that appears for the first time when an operator sets
/// <c>Servyx:Provisioning:Enabled</c> to <c>true</c> — the worst possible moment to find out.
/// </para>
/// </remarks>
public class ProvisioningCompositionTests
{
    private static ProvisioningPlan Plan() => new(
        PlanId: "docker-container:servyx-preview:abc123def456",
        PlanHash: "abc123def456abc123def456abc123def456abc123def456abc123def456abcd",
        Stages: [new("create-container", "docker-container", "Create container 'servyx-preview'.")],
        EstimatedCost: CostEstimate.Unknown("Local Docker containers are not billed by a provider."),
        ExpiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void The_gated_registration_block_resolves_an_apply_capable_dashboard()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Stand-ins for AddServyxDockerProvisioning() and AddServyxProvisioningLedger(), which need a
        // Docker daemon and a SQLite file respectively. Everything below them is the real registration.
        services.AddSingleton<IProvisioner>(new FakeProvisioner(
            "docker-container",
            ProvisioningCapabilities.Create | ProvisioningCapabilities.Destroy,
            Plan()));
        services.AddScoped<IProvisioningLedger, RecordingProvisioningLedger>();

        services.AddServyxProvisioningExecution();
        services.AddScoped<IProvisioningDashboard>(sp => new ProvisioningDashboardService(
            sp.GetServices<IProvisioner>(),
            sp.GetService<IProvisioningLedger>(),
            sp.GetService<ProvisioningExecutor>()));

        // ValidateOnBuild + ValidateScopes are what ASP.NET Core itself turns on in Development. Building
        // with them here is the whole point: a singleton capturing the scoped ledger fails right here,
        // rather than at an operator's first click.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        // Resolved from a scope, exactly as a Blazor circuit does.
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ProvisioningExecutor>().Should().NotBeNull();

        var dashboard = scope.ServiceProvider.GetRequiredService<IProvisioningDashboard>();
        dashboard.LedgerConfigured.Should().BeTrue();
        dashboard.ExecutionConfigured.Should().BeTrue(
            "the composition root must hand the executor to the dashboard, or /deploy renders a gated Apply control");
    }

    [Fact]
    public void Omitting_the_executor_registration_yields_a_dashboard_that_says_it_cannot_apply()
    {
        // The read-and-plan composition. Nothing here can create anything, and the dashboard reports that
        // rather than failing at click time.
        var services = new ServiceCollection();
        services.AddSingleton<IProvisioner>(new FakeProvisioner(
            "docker-container",
            ProvisioningCapabilities.Create,
            Plan()));
        services.AddScoped<IProvisioningLedger, RecordingProvisioningLedger>();
        services.AddScoped<IProvisioningDashboard>(sp => new ProvisioningDashboardService(
            sp.GetServices<IProvisioner>(),
            sp.GetService<IProvisioningLedger>(),
            sp.GetService<ProvisioningExecutor>()));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<ProvisioningExecutor>().Should().BeNull();
        scope.ServiceProvider.GetRequiredService<IProvisioningDashboard>().ExecutionConfigured.Should().BeFalse();
    }
}
