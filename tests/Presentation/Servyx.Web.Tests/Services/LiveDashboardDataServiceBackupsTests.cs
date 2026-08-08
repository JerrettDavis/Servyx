using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Backups;
using Servyx.Application.Servers;
using Servyx.Domain.Backups;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// <see cref="LiveDashboardDataService"/>'s backup wiring: <see cref="LiveDashboardDataService.GetAllBackupsWithStatusAsync"/>
/// must distinguish a genuine (possibly empty) listing from a listing failure from "no backup provider is
/// configured in this process at all" — the same three-way honesty <c>GetServersWithStatusAsync</c> already
/// applies to server discovery.
/// </summary>
public class LiveDashboardDataServiceBackupsTests
{
    private static readonly TargetDescriptor Target =
        new("docker", "npipe://./pipe/docker_engine", null, null, new Dictionary<string, string>());

    private static readonly Servyx.Application.Servers.ServerSummary ServerA = new(
        "server-a", "Server A", "Palworld", ServerState.Running, ServerHealthStatus.Healthy, null, null,
        "docker", []);

    private static readonly Servyx.Application.Servers.ServerSummary ServerB = new(
        "server-b", "Server B", "Palworld", ServerState.Running, ServerHealthStatus.Healthy, null, null,
        "docker", []);

    private static IServerQueryService QueryReturning(params Servyx.Application.Servers.ServerSummary[] servers)
    {
        var query = Substitute.For<IServerQueryService>();
        query.GetAdoptedServersWithStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Servyx.Application.Servers.ServerListResult(servers, DiscoveryFailed: false, FailureDetail: null)));
        return query;
    }

    private static BackupArtifact Artifact(string id, Servyx.Domain.Backups.BackupOwnership ownership, string location) =>
        new(id, ownership, new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero), 1024, location);

    [Fact]
    public async Task Backups_from_a_configured_provider_are_listed()
    {
        var dashboard = Substitute.For<IBackupDashboard>();
        dashboard.ProviderConfigured.Returns(true);
        dashboard.ListAsync("server-a", Arg.Any<CancellationToken>()).Returns(Task.FromResult<BackupListResult>(
            new BackupListResult.Listed(
                [Artifact("id-1", Servyx.Domain.Backups.BackupOwnership.Servyx, "/palworld/servyx-backups/id-1.tar.gz")],
                [])));

        var sut = new LiveDashboardDataService(
            QueryReturning(ServerA), NullLogger<LiveDashboardDataService>.Instance, Target, backupDashboard: dashboard);

        var result = await sut.GetAllBackupsWithStatusAsync();

        result.Availability.Should().Be(BackupsAvailability.Listed);
        result.FailureDetail.Should().BeNull();
        result.Backups.Should().ContainSingle(b => b.ServerId == "server-a" && b.FileName == "id-1.tar.gz");
    }

    /// <summary>
    /// THE honesty test: a listing failure for one server must never collapse into the same
    /// <see cref="BackupsAvailability.Listed"/> case an empty-but-successful listing reports.
    /// </summary>
    [Fact]
    public async Task A_backup_listing_failure_is_distinguishable_from_no_backups()
    {
        var dashboard = Substitute.For<IBackupDashboard>();
        dashboard.ProviderConfigured.Returns(true);
        dashboard.ListAsync("server-a", Arg.Any<CancellationToken>()).Returns(Task.FromResult<BackupListResult>(
            new BackupListResult.Failed("daemon unreachable", "IOException")));

        var sut = new LiveDashboardDataService(
            QueryReturning(ServerA), NullLogger<LiveDashboardDataService>.Instance, Target, backupDashboard: dashboard);

        var result = await sut.GetAllBackupsWithStatusAsync();

        result.Availability.Should().Be(BackupsAvailability.Failed);
        result.FailureDetail.Should().Contain("daemon unreachable");

        // Not the same fact as "there are none": Backups is empty here too, so a caller must switch on
        // Availability, never infer failure from an empty list.
        result.Backups.Should().BeEmpty();
    }

    /// <summary>An exception thrown outright (rather than a returned <c>Failed</c> case) degrades the same way.</summary>
    [Fact]
    public async Task A_thrown_listing_exception_also_reports_failed_not_empty()
    {
        var dashboard = Substitute.For<IBackupDashboard>();
        dashboard.ProviderConfigured.Returns(true);
        dashboard.ListAsync("server-a", Arg.Any<CancellationToken>())
            .Returns<Task<BackupListResult>>(_ => throw new InvalidOperationException("session closed"));

        var sut = new LiveDashboardDataService(
            QueryReturning(ServerA), NullLogger<LiveDashboardDataService>.Instance, Target, backupDashboard: dashboard);

        var result = await sut.GetAllBackupsWithStatusAsync();

        result.Availability.Should().Be(BackupsAvailability.Failed);
        result.FailureDetail.Should().Contain("session closed");
    }

    [Fact]
    public async Task No_backup_dashboard_registered_reports_not_configured()
    {
        var sut = new LiveDashboardDataService(
            QueryReturning(ServerA), NullLogger<LiveDashboardDataService>.Instance, Target, backupDashboard: null);

        var result = await sut.GetAllBackupsWithStatusAsync();

        result.Availability.Should().Be(BackupsAvailability.NotConfigured);
        result.Backups.Should().BeEmpty();
        result.FailureDetail.Should().BeNull();
    }

    /// <summary>A dashboard is registered, but no <c>IBackupProvider</c> backs it — still "not configured", not "failed".</summary>
    [Fact]
    public async Task A_dashboard_with_no_provider_also_reports_not_configured()
    {
        var dashboard = Substitute.For<IBackupDashboard>();
        dashboard.ProviderConfigured.Returns(false);

        var sut = new LiveDashboardDataService(
            QueryReturning(ServerA), NullLogger<LiveDashboardDataService>.Instance, Target, backupDashboard: dashboard);

        var result = await sut.GetAllBackupsWithStatusAsync();

        result.Availability.Should().Be(BackupsAvailability.NotConfigured);
        await dashboard.DidNotReceive().ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Foreign_cron_archives_are_reported_in_the_summary_count()
    {
        var dashboard = Substitute.For<IBackupDashboard>();
        dashboard.ProviderConfigured.Returns(true);
        dashboard.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<BackupListResult>(
            new BackupListResult.Listed(
                [Artifact("owned-1", Servyx.Domain.Backups.BackupOwnership.Servyx, "/palworld/servyx-backups/owned-1.tar.gz")],
                [
                    Artifact("cron-1", Servyx.Domain.Backups.BackupOwnership.Foreign, "/palworld/backups/cron-1.tar.gz"),
                    Artifact("cron-2", Servyx.Domain.Backups.BackupOwnership.Foreign, "/palworld/backups/cron-2.tar.gz"),
                ])));

        var query = QueryReturning(ServerA);
        query.GetConnectionStateAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DockerConnectionState(true, "npipe", null)));

        var sut = new LiveDashboardDataService(
            query, NullLogger<LiveDashboardDataService>.Instance, Target, backupDashboard: dashboard);

        var summary = await sut.GetDashboardSummaryAsync();

        summary.ForeignBackupsCount.Should().Be(2);
    }

    [Fact]
    public async Task A_listing_that_partly_fails_still_reports_the_entries_the_other_servers_found()
    {
        var dashboard = Substitute.For<IBackupDashboard>();
        dashboard.ProviderConfigured.Returns(true);
        dashboard.ListAsync("server-a", Arg.Any<CancellationToken>()).Returns(Task.FromResult<BackupListResult>(
            new BackupListResult.Listed(
                [Artifact("owned-1", Servyx.Domain.Backups.BackupOwnership.Servyx, "/palworld/servyx-backups/owned-1.tar.gz")],
                [])));
        dashboard.ListAsync("server-b", Arg.Any<CancellationToken>()).Returns(Task.FromResult<BackupListResult>(
            new BackupListResult.Failed("unreachable", "IOException")));

        var sut = new LiveDashboardDataService(
            QueryReturning(ServerA, ServerB), NullLogger<LiveDashboardDataService>.Instance, Target, backupDashboard: dashboard);

        var result = await sut.GetAllBackupsWithStatusAsync();

        result.Availability.Should().Be(BackupsAvailability.Failed);
        result.Backups.Should().ContainSingle(b => b.ServerId == "server-a");
        result.FailureDetail.Should().Contain("server-b");
    }

    [Fact]
    public async Task GetAllBackupsAsync_still_returns_the_flat_list_for_callers_that_only_need_it()
    {
        var dashboard = Substitute.For<IBackupDashboard>();
        dashboard.ProviderConfigured.Returns(true);
        dashboard.ListAsync("server-a", Arg.Any<CancellationToken>()).Returns(Task.FromResult<BackupListResult>(
            new BackupListResult.Listed(
                [Artifact("owned-1", Servyx.Domain.Backups.BackupOwnership.Servyx, "/palworld/servyx-backups/owned-1.tar.gz")],
                [])));

        var sut = new LiveDashboardDataService(
            QueryReturning(ServerA), NullLogger<LiveDashboardDataService>.Instance, Target, backupDashboard: dashboard);

        var backups = await sut.GetAllBackupsAsync();

        backups.Should().ContainSingle(b => b.ServerId == "server-a");
    }
}
