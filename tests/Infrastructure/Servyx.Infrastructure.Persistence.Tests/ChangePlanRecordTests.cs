using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Entities;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Persistence.Tests;

/// <summary>
/// Tests for <see cref="ChangePlanRecord"/>/<see cref="ChangePlanActionRecord"/> — the durable half of a
/// previewed configuration change plan. This phase is persistence-only: no <c>IPlanExecutor</c> exists yet, so
/// these tests exercise storage guarantees only (round trip, cascade delete, optimistic concurrency, TTL
/// plumbing), not preview/apply/revert behaviour.
/// </summary>
public class ChangePlanRecordTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    private static Server NewServer(ServerId? id = null, string containerId = "container-1") => new()
    {
        Id = id ?? ServerId.New(),
        Name = "palworld-eu-1",
        ContainerId = containerId,
        GameDefinitionId = "palworld",
        DefinitionContentHash = "sha256:4f2c",
        HostId = null,
        AdoptionMode = AdoptionMode.Adopted,
        WriteMode = ServerWriteMode.ReadOnly,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static ChangePlanRecord NewPlan(ChangePlanId id, ServerId serverId, ChangePlanStatus status = ChangePlanStatus.Previewed) => new()
    {
        Id = id,
        ServerId = serverId,
        Status = status,
        CreatedAt = CreatedAt,
        CreatedBy = "operator@servyx",
        ExpiresAt = CreatedAt + ChangePlanRecord.DefaultTtl,
        DefinitionId = "palworld",
        DefinitionVersion = "sha256:4f2c",
        ConsequencesJson = """[{"kind":"RestartRequired","description":"Server must restart."}]""",
        SurfaceHashesJson = """{"config-file":"sha256:aaa111"}""",
        BlockedJson = "[]",
        DiagnosticsJson = "[]",
    };

    [Fact]
    public void ChangePlan_WithOrderedActions_RoundTrips_ThroughANewContext()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();
        var planId = ChangePlanId.New();

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(NewPlan(planId, server.Id));

            // Inserted out of Ordinal order on purpose, to prove ordering on read comes from the Ordinal
            // column and not from insertion order.
            write.ChangePlanActions.AddRange(
                NewAction(planId, ordinal: 1, surfaceId: "control-channel", kind: PlannedActionKind.WriteControlChannel),
                NewAction(planId, ordinal: 0, surfaceId: "config-file", kind: PlannedActionKind.WriteSurface));

            write.SaveChanges().Should().Be(4);
        }

        using var read = fixture.CreateContext();
        var loadedPlan = read.ChangePlans.Single();

        loadedPlan.Id.Should().Be(planId);
        loadedPlan.ServerId.Should().Be(server.Id);
        loadedPlan.Status.Should().Be(ChangePlanStatus.Previewed);
        loadedPlan.CreatedAt.Should().Be(CreatedAt);
        loadedPlan.CreatedBy.Should().Be("operator@servyx");
        loadedPlan.ExpiresAt.Should().Be(CreatedAt + ChangePlanRecord.DefaultTtl);
        loadedPlan.DefinitionId.Should().Be("palworld");
        loadedPlan.DefinitionVersion.Should().Be("sha256:4f2c");
        loadedPlan.ConsequencesJson.Should().Contain("RestartRequired");
        loadedPlan.SurfaceHashesJson.Should().Contain("config-file");
        loadedPlan.BlockedJson.Should().Be("[]");
        loadedPlan.AppliedAt.Should().BeNull();
        loadedPlan.AppliedBy.Should().BeNull();
        loadedPlan.RevertedAt.Should().BeNull();
        loadedPlan.RevertedBy.Should().BeNull();

        var loadedActions = read.ChangePlanActions
            .Where(a => a.ChangePlanId == planId)
            .OrderBy(a => a.Ordinal)
            .ToList();

        loadedActions.Should().HaveCount(2);

        loadedActions[0].Ordinal.Should().Be(0);
        loadedActions[0].SurfaceId.Should().Be("config-file");
        loadedActions[0].Kind.Should().Be(PlannedActionKind.WriteSurface);
        loadedActions[0].ResolvedPath.Should().Be("/data/config-file");
        loadedActions[0].RequiredCapabilities.Should().Be(TransportCapabilities.FileRead | TransportCapabilities.FileWrite);
        loadedActions[0].UnifiedDiff.Should().Contain("config-file");
        loadedActions[0].Reversible.Should().BeTrue();
        loadedActions[0].PreImageHash.Should().Be("sha256:pre");
        loadedActions[0].PreImageContent.Should().Be("old-content");
        loadedActions[0].PostImageContent.Should().Be("new-content");
        loadedActions[0].PostImageHash.Should().Be("sha256:post");
        loadedActions[0].ContainsSecrets.Should().BeFalse();
        loadedActions[0].Status.Should().Be(ChangePlanActionStatus.Pending);
        loadedActions[0].AppliedAt.Should().BeNull();
        loadedActions[0].RevertedAt.Should().BeNull();

        loadedActions[1].Ordinal.Should().Be(1);
        loadedActions[1].SurfaceId.Should().Be("control-channel");
        loadedActions[1].Kind.Should().Be(PlannedActionKind.WriteControlChannel);
    }

    [Fact]
    public void DeletingServer_CascadesToItsChangePlans()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();
        var planId = ChangePlanId.New();

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(NewPlan(planId, server.Id));
            write.SaveChanges();
        }

        using (var write = fixture.CreateContext())
        {
            write.Servers.Remove(write.Servers.Single(s => s.Id == server.Id));
            write.SaveChanges();
        }

        using var read = fixture.CreateContext();

        // A plan is meaningless without the server it targets — see ChangePlanRecord's own remarks on why
        // this cascade is the opposite lifecycle rule from ProvisionedResourceRecord's deliberate no-FK.
        read.ChangePlans.Should().BeEmpty();
    }

    [Fact]
    public void DeletingChangePlan_CascadesToItsActions()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();
        var planId = ChangePlanId.New();

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(NewPlan(planId, server.Id));
            write.ChangePlanActions.Add(NewAction(planId, ordinal: 0));
            write.SaveChanges();
        }

        using (var write = fixture.CreateContext())
        {
            write.ChangePlans.Remove(write.ChangePlans.Single(p => p.Id == planId));
            write.SaveChanges();
        }

        using var read = fixture.CreateContext();

        // Deleting the plan directly (server still exists) must still take its actions with it — an action
        // row has no independent existence.
        read.ChangePlanActions.Should().BeEmpty();
    }

    [Fact]
    public void RowVersion_PreventsADoubleApply_WhenTwoContextsRaceToTransitionStatus()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();
        var planId = ChangePlanId.New();

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(NewPlan(planId, server.Id));
            write.SaveChanges();
        }

        // Two independent apply attempts, each starting from the same on-disk row — exactly the shape of a
        // double-apply race (e.g. two Blazor Server circuits, or a retried request, both acting on the same
        // planId).
        using var firstAttempt = fixture.CreateContext();
        using var secondAttempt = fixture.CreateContext();

        var firstView = firstAttempt.ChangePlans.Single(p => p.Id == planId);
        var secondView = secondAttempt.ChangePlans.Single(p => p.Id == planId);

        firstView.RowVersion.Should().Be(secondView.RowVersion, "both attempts loaded the same never-yet-applied row");

        // The first attempt wins the race: Previewed -> Applying, and its write lands.
        firstView.Status = ChangePlanStatus.Applying;
        firstAttempt.SaveChanges().Should().Be(1);

        // The second attempt is still holding the RowVersion from before the first attempt's write. Its own
        // transition (also Previewed -> Applying, i.e. the same double-apply it must never be allowed to
        // complete) must be rejected by the concurrency token rather than silently overwriting the first
        // attempt's change.
        secondView.Status = ChangePlanStatus.Applying;
        var act = () => secondAttempt.SaveChanges();

        act.Should().Throw<DbUpdateConcurrencyException>();
    }

    [Fact]
    public void RevertedAt_And_RevertedBy_RoundTrip_AtPlanLevel_WhileRevertedAt_RoundTrips_PerAction()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();
        var planId = ChangePlanId.New();
        var revertedAt = new DateTimeOffset(2026, 8, 9, 13, 0, 0, TimeSpan.Zero);

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(NewPlan(planId, server.Id, status: ChangePlanStatus.Applied));
            write.ChangePlanActions.Add(NewAction(planId, ordinal: 0));
            write.SaveChanges();
        }

        // A later update, not part of the original insert — RevertAsync(planId) (a future phase) would do
        // exactly this: transition the plan's own Status/RevertedAt/RevertedBy once, and each of its actions'
        // Status/RevertedAt individually as the revert sweep processes them.
        using (var write = fixture.CreateContext())
        {
            var plan = write.ChangePlans.Single(p => p.Id == planId);
            plan.Status = ChangePlanStatus.Reverted;
            plan.RevertedAt = revertedAt;
            plan.RevertedBy = "operator@servyx";

            var action = write.ChangePlanActions.Single(a => a.ChangePlanId == planId);
            action.Status = ChangePlanActionStatus.Reverted;
            action.RevertedAt = revertedAt;

            write.SaveChanges().Should().Be(2);
        }

        using var read = fixture.CreateContext();
        var loadedPlan = read.ChangePlans.Single();
        var loadedAction = read.ChangePlanActions.Single();

        loadedPlan.Status.Should().Be(ChangePlanStatus.Reverted);
        loadedPlan.RevertedAt.Should().Be(revertedAt);
        loadedPlan.RevertedBy.Should().Be("operator@servyx");

        // Per-action timing round-trips, but there is deliberately no per-action "who" column — see
        // ChangePlanActionRecord.RevertedAt's own remarks on why attribution is plan-level only.
        loadedAction.Status.Should().Be(ChangePlanActionStatus.Reverted);
        loadedAction.RevertedAt.Should().Be(revertedAt);
    }

    [Fact]
    public void ChangePlanAction_ContainsSecrets_IsRecorded_ButThisPhaseAddsNoReadPathToMask()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();
        var planId = ChangePlanId.New();

        var action = NewAction(planId, ordinal: 0, containsSecrets: true);
        action.PreImageContent = "ADMIN_PASSWORD=old-secret";
        action.PostImageContent = "ADMIN_PASSWORD=new-secret";

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(NewPlan(planId, server.Id));
            write.ChangePlanActions.Add(action);
            write.SaveChanges();
        }

        using var read = fixture.CreateContext();
        var loaded = read.ChangePlanActions.Single();

        loaded.ContainsSecrets.Should().BeTrue();

        // DESIGN INTENT, not a gap: PreImageContent/PostImageContent are stored unmasked on purpose — an
        // exact revert (IPlanExecutor.RevertAsync, a later phase) needs the real bytes, not a masked
        // approximation, and UnifiedDiff is already the masked value a human reviews (see this entity's own
        // remarks). This persistence-only phase adds NO service or query surface that reads these two columns
        // back out to a caller; ContainsSecrets exists so that whenever such a read path is eventually added,
        // it has a column to check before deciding whether to return PreImageContent/PostImageContent at all.
        // There is therefore no "never exposes" assertion to write here yet — there is nothing that exposes
        // it, by construction, because nothing reads it.
        loaded.PreImageContent.Should().Be("ADMIN_PASSWORD=old-secret");
        loaded.PostImageContent.Should().Be("ADMIN_PASSWORD=new-secret");
    }

    [Fact]
    public void ExpiresAt_Default15MinuteTtl_IsComputedFromAnInjectedClock_NotRealTime()
    {
        // A hand-rolled manual-advance TimeProvider, matching ScheduledBackupServiceTests' own FakeTimeProvider
        // — the repo's established pattern for clock-dependent tests, so TTL expiry is provable without
        // waiting on a real clock.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));

        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();
        var planId = ChangePlanId.New();

        // ChangePlanRecord.ExpiresAt carries no baked-in default (see its own remarks) — the writer supplies
        // it, here using the injected clock plus the shared DefaultTtl constant, exactly as a future
        // plan-creating service is expected to.
        var expectedExpiry = clock.GetUtcNow() + ChangePlanRecord.DefaultTtl;

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(new ChangePlanRecord
            {
                Id = planId,
                ServerId = server.Id,
                Status = ChangePlanStatus.Previewed,
                CreatedAt = clock.GetUtcNow(),
                CreatedBy = "operator@servyx",
                ExpiresAt = expectedExpiry,
                DefinitionId = "palworld",
                DefinitionVersion = "sha256:4f2c",
                ConsequencesJson = "[]",
                SurfaceHashesJson = "{}",
                BlockedJson = "[]",
                DiagnosticsJson = "[]",
            });
            write.SaveChanges();
        }

        using var read = fixture.CreateContext();
        var loaded = read.ChangePlans.Single();

        ChangePlanRecord.DefaultTtl.Should().Be(TimeSpan.FromMinutes(15));
        loaded.ExpiresAt.Should().Be(expectedExpiry);

        // Not yet expired, by the fake clock.
        (loaded.ExpiresAt < clock.GetUtcNow()).Should().BeFalse();

        // Advance the fake clock 15 minutes and a tick past ExpiresAt — no real waiting involved — and the
        // same row is now detectably stale.
        clock.Advance(ChangePlanRecord.DefaultTtl + TimeSpan.FromSeconds(1));

        (loaded.ExpiresAt < clock.GetUtcNow()).Should().BeTrue();
    }

    private static ChangePlanActionRecord NewAction(
        ChangePlanId planId,
        int ordinal,
        string surfaceId = "config-file",
        PlannedActionKind kind = PlannedActionKind.WriteSurface,
        bool containsSecrets = false) => new()
    {
        Id = Guid.NewGuid(),
        ChangePlanId = planId,
        Ordinal = ordinal,
        Kind = kind,
        SurfaceId = surfaceId,
        ResolvedPath = "/data/" + surfaceId,
        RequiredCapabilities = TransportCapabilities.FileRead | TransportCapabilities.FileWrite,
        UnifiedDiff = $"--- a/{surfaceId}\n+++ b/{surfaceId}\n-old\n+new",
        Reversible = true,
        PreImageHash = "sha256:pre",
        PreImageContent = "old-content",
        PostImageContent = "new-content",
        PostImageHash = "sha256:post",
        ContainsSecrets = containsSecrets,
        Status = ChangePlanActionStatus.Pending,
    };

    /// <summary>A minimal manual-advance <see cref="TimeProvider"/>; the test package's own is not referenced here.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
