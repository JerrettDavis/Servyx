using Servyx.Application.Backups;
using Servyx.Domain.Backups;

namespace Servyx.Application.Tests.Backups;

/// <summary>
/// Unit tests for <see cref="BackupDashboardService"/> — the Application-layer surface every backup
/// mutation goes through, whether it was driven by the Backups page or by the scheduler.
/// </summary>
/// <remarks>
/// Most of these assert non-invocation. "Planning did not restore" and "a dry run deleted nothing" are
/// only meaningful as claims about which provider members were reached, so the provider counts calls and
/// the assertions are on those counters, not on the shape of a returned result.
/// </remarks>
public class BackupDashboardServiceTests
{
    private const string ServerId = "palworld-server";
    private const string ServyxArtifact = "palworld-server::/palworld/servyx-backups/servyx-20260101T000000Z.tar.gz";
    private const string ForeignArtifact = "palworld-server::/palworld/backups/palworld-2026-01-01.tar.gz";

    private static readonly RetentionPolicy Policy = new(KeepHourly: 1, KeepDaily: 1, KeepWeekly: 1);

    [Fact]
    public void A_dashboard_with_no_provider_reports_it_and_refuses_loudly()
    {
        var dashboard = new BackupDashboardService(provider: null);

        dashboard.ProviderConfigured.Should().BeFalse();

        // Loud, not a Failed result: no provider is a composition defect, not an outcome of the attempt.
        var act = () => dashboard.CreateAsync(ServerId);
        act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Listing_partitions_by_ownership()
    {
        var provider = new RecordingBackupProvider()
            .With(ServyxArtifact, BackupOwnership.Servyx)
            .With(ForeignArtifact, BackupOwnership.Foreign);

        var result = await new BackupDashboardService(provider).ListAsync(ServerId);

        var listed = result.Should().BeOfType<BackupListResult.Listed>().Subject;
        listed.ServyxOwned.Should().ContainSingle().Which.Id.Should().Be(ServyxArtifact);
        listed.Foreign.Should().ContainSingle().Which.Id.Should().Be(ForeignArtifact);
    }

    [Fact]
    public async Task A_listing_failure_is_a_case_not_an_empty_list()
    {
        var provider = new RecordingBackupProvider { ListThrows = new IOException("daemon unreachable") };

        var result = await new BackupDashboardService(provider).ListAsync(ServerId);

        var failed = result.Should().BeOfType<BackupListResult.Failed>().Subject;
        failed.Detail.Should().Be("daemon unreachable");
        failed.FailureKind.Should().Be(nameof(IOException));
        failed.Message.Should().Contain("not the same as 'there are none'");
    }

    // ── The prune barrier ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_service_refuses_to_prune_a_foreign_artifact_when_driven_directly()
    {
        // A provider that names a foreign artifact as a removal candidate. The real DockerBackupProvider
        // cannot do this — it partitions foreign artifacts out before retention is even computed — which
        // is precisely why the Application layer must refuse independently rather than trusting it.
        var provider = new RecordingBackupProvider()
            .With(ServyxArtifact, BackupOwnership.Servyx)
            .With(ForeignArtifact, BackupOwnership.Foreign);
        provider.PruneReturns.Add(ForeignArtifact);

        var result = await new BackupDashboardService(provider).ApplyPruneAsync(ServerId, Policy);

        var refused = result.Should().BeOfType<BackupPruneResult.RefusedForeign>().Subject;
        refused.ForeignIds.Should().ContainSingle().Which.Should().Be(ForeignArtifact);
        refused.Message.Should().Contain("Foreign artifacts are never pruned");

        // The refusal happened on the dry run, before the deleting call was ever issued.
        provider.LivePruneCalls.Should().Be(0);
    }

    [Fact]
    public async Task Previewing_a_prune_reports_candidates_and_deletes_nothing()
    {
        var provider = new RecordingBackupProvider()
            .With(ServyxArtifact, BackupOwnership.Servyx)
            .With(ForeignArtifact, BackupOwnership.Foreign);
        provider.PruneReturns.Add(ServyxArtifact);
        provider.PruneSkippedForeign = 1;

        var result = await new BackupDashboardService(provider).PreviewPruneAsync(ServerId, Policy);

        var previewed = result.Should().BeOfType<BackupPruneResult.Previewed>().Subject;
        previewed.Candidates.Should().ContainSingle().Which.Should().Be(ServyxArtifact);
        previewed.SkippedForeign.Should().Be(1);
        previewed.Message.Should().Contain("Nothing has been deleted");

        // The claim that matters: the deleting overload was never reached.
        provider.LivePruneCalls.Should().Be(0);
        provider.DryRunPruneCalls.Should().Be(1);
    }

    [Fact]
    public async Task Applying_a_prune_dry_runs_first_and_then_deletes()
    {
        var provider = new RecordingBackupProvider().With(ServyxArtifact, BackupOwnership.Servyx);
        provider.PruneReturns.Add(ServyxArtifact);

        var result = await new BackupDashboardService(provider).ApplyPruneAsync(ServerId, Policy);

        result.Should().BeOfType<BackupPruneResult.Pruned>()
            .Which.Removed.Should().ContainSingle().Which.Should().Be(ServyxArtifact);

        provider.DryRunPruneCalls.Should().Be(1);
        provider.LivePruneCalls.Should().Be(1);
    }

    [Fact]
    public async Task Applying_a_prune_with_no_candidates_never_reaches_the_deleting_call()
    {
        var provider = new RecordingBackupProvider().With(ServyxArtifact, BackupOwnership.Servyx);

        var result = await new BackupDashboardService(provider).ApplyPruneAsync(ServerId, Policy);

        result.Should().BeOfType<BackupPruneResult.Pruned>().Which.Removed.Should().BeEmpty();
        provider.LivePruneCalls.Should().Be(0);
    }

    [Fact]
    public void A_foreign_artifact_is_never_prunable()
    {
        var foreign = new BackupArtifact(ForeignArtifact, BackupOwnership.Foreign, DateTimeOffset.UnixEpoch, 1, "/x");
        var owned = new BackupArtifact(ServyxArtifact, BackupOwnership.Servyx, DateTimeOffset.UnixEpoch, 1, "/y");

        BackupDashboardService.IsPrunable(foreign).Should().BeFalse();
        BackupDashboardService.IsPrunable(owned).Should().BeTrue();
    }

    // ── The restore barrier ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Planning_a_restore_never_restores()
    {
        var provider = new RecordingBackupProvider().With(ServyxArtifact, BackupOwnership.Servyx);
        var dashboard = new BackupDashboardService(provider);

        var result = await dashboard.PlanRestoreAsync(ServyxArtifact);

        var planned = result.Should().BeOfType<RestorePlanResult.Planned>().Subject;
        planned.Plan.AffectedPaths.Should().NotBeEmpty();
        planned.Message.Should().Contain("Nothing has been written");

        // The whole point: previewing reached PlanRestoreAsync and never RestoreAsync.
        provider.PlanRestoreCalls.Should().Be(1);
        provider.RestoreCalls.Should().Be(0);
    }

    [Fact]
    public async Task Applying_a_restore_requires_a_plan_this_process_issued()
    {
        var provider = new RecordingBackupProvider().With(ServyxArtifact, BackupOwnership.Servyx);
        var dashboard = new BackupDashboardService(provider);

        // No PlanRestoreAsync first: a fabricated plan id is refused before the provider is contacted.
        var result = await dashboard.ApplyRestoreAsync("restore-fabricated", expectedPathCount: 1);

        result.Should().BeOfType<RestoreApplyResult.Stale>()
            .Which.Message.Should().Contain("was not issued by this process");
        provider.RestoreCalls.Should().Be(0);
    }

    [Fact]
    public async Task Applying_a_restore_refuses_a_confirmation_that_does_not_match_the_preview()
    {
        var provider = new RecordingBackupProvider().With(ServyxArtifact, BackupOwnership.Servyx);
        var dashboard = new BackupDashboardService(provider);

        var planned = (RestorePlanResult.Planned)await dashboard.PlanRestoreAsync(ServyxArtifact);

        // The operator was shown one path; the confirmation claims three.
        var result = await dashboard.ApplyRestoreAsync(planned.Plan.Id, expectedPathCount: 3);

        result.Should().BeOfType<RestoreApplyResult.Stale>();
        provider.RestoreCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_plan_is_single_use()
    {
        var provider = new RecordingBackupProvider().With(ServyxArtifact, BackupOwnership.Servyx);
        var dashboard = new BackupDashboardService(provider);

        var planned = (RestorePlanResult.Planned)await dashboard.PlanRestoreAsync(ServyxArtifact);
        var count = planned.Plan.AffectedPaths.Count;

        (await dashboard.ApplyRestoreAsync(planned.Plan.Id, count)).Should().BeOfType<RestoreApplyResult.Restored>();
        (await dashboard.ApplyRestoreAsync(planned.Plan.Id, count)).Should().BeOfType<RestoreApplyResult.Stale>();

        provider.RestoreCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_provider_refusal_is_reported_as_stale_not_as_a_partial_failure()
    {
        var provider = new RecordingBackupProvider().With(ServyxArtifact, BackupOwnership.Servyx);
        provider.RestoreThrows = new RestorePlanStaleException("The plan expired.");
        var dashboard = new BackupDashboardService(provider);

        var planned = (RestorePlanResult.Planned)await dashboard.PlanRestoreAsync(ServyxArtifact);
        var result = await dashboard.ApplyRestoreAsync(planned.Plan.Id, planned.Plan.AffectedPaths.Count);

        // Stale means nothing was written; Failed means some paths may already be overwritten. The two
        // must never be rendered by the same branch.
        result.Should().BeOfType<RestoreApplyResult.Stale>().Which.Message.Should().Contain("Nothing was overwritten");
    }

    [Fact]
    public async Task A_restore_that_fails_part_way_says_so()
    {
        var provider = new RecordingBackupProvider().With(ServyxArtifact, BackupOwnership.Servyx);
        provider.RestoreThrows = new IOException("connection reset");
        var dashboard = new BackupDashboardService(provider);

        var planned = (RestorePlanResult.Planned)await dashboard.PlanRestoreAsync(ServyxArtifact);
        var result = await dashboard.ApplyRestoreAsync(planned.Plan.Id, planned.Plan.AffectedPaths.Count);

        result.Should().BeOfType<RestoreApplyResult.Failed>()
            .Which.Message.Should().Contain("may already have been overwritten");
    }

    // ── Create ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Creating_a_backup_reports_the_artifact()
    {
        var provider = new RecordingBackupProvider();

        var result = await new BackupDashboardService(provider).CreateAsync(ServerId);

        result.Should().BeOfType<BackupCreateResult.Created>().Which.Artifact.Ownership.Should().Be(BackupOwnership.Servyx);
        provider.CreateCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_failing_backup_is_surfaced_not_swallowed()
    {
        var provider = new RecordingBackupProvider
        {
            CreateThrows = new InvalidOperationException("Quiesce command 'save' reported failure."),
        };

        var result = await new BackupDashboardService(provider).CreateAsync(ServerId);

        var failed = result.Should().BeOfType<BackupCreateResult.Failed>().Subject;
        failed.Detail.Should().Contain("Quiesce command 'save' reported failure.");
        failed.FailureKind.Should().Be(nameof(InvalidOperationException));
        failed.Message.Should().Contain("The backup was not created");
    }

    [Fact]
    public async Task Cancellation_is_never_translated_into_a_backup_failure()
    {
        var provider = new RecordingBackupProvider { CreateThrows = new OperationCanceledException() };

        var act = () => new BackupDashboardService(provider).CreateAsync(ServerId);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Inspecting_reads_the_index_and_reports_its_entries()
    {
        var provider = new RecordingBackupProvider().With(ForeignArtifact, BackupOwnership.Foreign);

        var result = await new BackupDashboardService(provider).InspectAsync(ForeignArtifact);

        result.Should().BeOfType<BackupInspectResult.Inspected>().Which.Entries.Should().ContainSingle();

        // Inspecting a foreign artifact is allowed and reaches nothing that writes.
        provider.RestoreCalls.Should().Be(0);
        provider.LivePruneCalls.Should().Be(0);
    }
}
