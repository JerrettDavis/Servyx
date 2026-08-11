using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Entities;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Persistence.Configuration;

namespace Servyx.Infrastructure.Persistence.Tests;

/// <summary>
/// Tests for <see cref="EfChangePlanStore.PurgeImagesAsync"/> and
/// <see cref="EfChangePlanStore.UpdateAsync"/> — the retention sweep and the concurrency-guarded transition
/// that had to land alongside <c>IPlanExecutor.ApplyAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// The sweep exists because <see cref="ChangePlanActionRecord.PreImageContent"/>/
/// <see cref="ChangePlanActionRecord.PostImageContent"/> hold whole configuration files verbatim and
/// unmasked, secrets included, and nothing ever read <see cref="ChangePlanRecord.ExpiresAt"/> before this
/// phase. The pair of properties these tests pin are: nothing that could still be needed for a revert is
/// destroyed inside the retention window, and nothing that provably cannot be needed is kept at all.
/// </para>
/// </remarks>
public class EfChangePlanStorePurgeTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    // ── Expired, never-applied plans ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PurgeImagesAsync_ForAnExpiredPreviewedPlan_MarksItStaleAndDiscardsItsImages()
    {
        using var fixture = new SqliteDatabaseFixture();
        var planId = Seed(fixture, ChangePlanStatus.Previewed, ChangePlanActionStatus.Pending);
        var store = Store(fixture);

        var result = await store.PurgeImagesAsync(CreatedAt + ChangePlanRecord.DefaultTtl + TimeSpan.FromMinutes(1), Retention);

        result.ExpiredPlansMarkedStale.Should().Be(1);
        result.PlansPurged.Should().Be(1);
        result.ActionsPurged.Should().Be(1);

        var stored = await store.TryGetAsync(planId);
        stored!.Plan.Status.Should().Be(ChangePlanStatus.Stale);
        stored.Actions.Single().PreImageContent.Should().BeNull();
        stored.Actions.Single().PostImageContent.Should().BeNull();

        // The digests stay: they are not secrets, and they are what still lets an audit say what the file was.
        stored.Actions.Single().PreImageHash.Should().Be("aaa111");
        stored.Actions.Single().PostImageHash.Should().Be("bbb222");
    }

    [Fact]
    public async Task PurgeImagesAsync_ForAPreviewedPlanThatHasNotExpired_LeavesItCompletelyAlone()
    {
        using var fixture = new SqliteDatabaseFixture();
        var planId = Seed(fixture, ChangePlanStatus.Previewed, ChangePlanActionStatus.Pending);
        var store = Store(fixture);

        // One tick before it expires. A plan about to be applied must keep the post-image the apply writes.
        var result = await store.PurgeImagesAsync(
            CreatedAt + ChangePlanRecord.DefaultTtl - TimeSpan.FromTicks(1), Retention);

        result.Any.Should().BeFalse();

        var stored = await store.TryGetAsync(planId);
        stored!.Plan.Status.Should().Be(ChangePlanStatus.Previewed);
        stored.Actions.Single().PreImageContent.Should().Be("old-content");
        stored.Actions.Single().PostImageContent.Should().Be("new-content");
    }

    [Fact]
    public async Task PurgeImagesAsync_ForAPlanMidApply_LeavesItAlone()
    {
        using var fixture = new SqliteDatabaseFixture();
        var planId = Seed(fixture, ChangePlanStatus.Applying, ChangePlanActionStatus.Applying);
        var store = Store(fixture);

        // A century later. Applying is non-terminal: an apply is in flight and needs its post-image.
        var result = await store.PurgeImagesAsync(CreatedAt.AddYears(100), Retention);

        result.Any.Should().BeFalse();
        (await store.TryGetAsync(planId))!.Actions.Single().PostImageContent.Should().Be("new-content");
    }

    // ── An applied plan's revert capability is protected for the whole window ──────────────────────────

    [Fact]
    public async Task PurgeImagesAsync_ForAnAppliedPlanInsideTheRetentionWindow_DoesNotDestroyItsRevertCapability()
    {
        using var fixture = new SqliteDatabaseFixture();
        var appliedAt = CreatedAt.AddMinutes(1);
        var planId = Seed(fixture, ChangePlanStatus.Applied, ChangePlanActionStatus.Applied, appliedAt);
        var store = Store(fixture);

        // One tick short of the window. The pre-image is the ONLY way to undo a change that reached a live
        // server, so nothing here may touch it.
        var result = await store.PurgeImagesAsync(appliedAt + Retention - TimeSpan.FromTicks(1), Retention);

        result.Any.Should().BeFalse();

        var stored = await store.TryGetAsync(planId);
        stored!.Actions.Single().PreImageContent.Should().Be("old-content");
        stored.Actions.Single().PostImageContent.Should().Be("new-content");
    }

    [Fact]
    public async Task PurgeImagesAsync_ForAnAppliedPlanPastTheRetentionWindow_DiscardsItsImages()
    {
        using var fixture = new SqliteDatabaseFixture();
        var appliedAt = CreatedAt.AddMinutes(1);
        var planId = Seed(fixture, ChangePlanStatus.Applied, ChangePlanActionStatus.Applied, appliedAt);
        var store = Store(fixture);

        var result = await store.PurgeImagesAsync(appliedAt + Retention, Retention);

        // The documented trade: past the window this plan is no longer revertable, by design.
        result.PlansPurged.Should().Be(1);
        result.ExpiredPlansMarkedStale.Should().Be(0);

        var stored = await store.TryGetAsync(planId);
        stored!.Plan.Status.Should().Be(ChangePlanStatus.Applied, "purging images is not a status transition");
        stored.Actions.Single().PreImageContent.Should().BeNull();
        stored.Actions.Single().PostImageContent.Should().BeNull();
    }

    [Fact]
    public async Task PurgeImagesAsync_ForARevertedPlan_AnchorsTheWindowOnTheRevertRatherThanTheApply()
    {
        using var fixture = new SqliteDatabaseFixture();
        var appliedAt = CreatedAt.AddMinutes(1);
        var revertedAt = appliedAt + TimeSpan.FromDays(20);
        var planId = Seed(fixture, ChangePlanStatus.Reverted, ChangePlanActionStatus.Applied, appliedAt, revertedAt);
        var store = Store(fixture);

        // Well past AppliedAt + retention, but not past RevertedAt + retention.
        var result = await store.PurgeImagesAsync(appliedAt + Retention + TimeSpan.FromDays(1), Retention);

        result.Any.Should().BeFalse();
        (await store.TryGetAsync(planId))!.Actions.Single().PreImageContent.Should().Be("old-content");
    }

    // ── The safety predicate is derived from ACTION state, never from the plan's summary status ────────

    [Fact]
    public async Task PurgeImagesAsync_ForAFailedPlanThatNonethelessHasOneAppliedAction_KeepsItsImagesForTheFullWindow()
    {
        using var fixture = new SqliteDatabaseFixture();
        var appliedAt = CreatedAt.AddMinutes(1);

        // A plan whose status says "nothing landed" and whose actions say otherwise. That disagreement should
        // never happen — but if a future transition ever produces it, the images MUST survive, because a real
        // change really did reach a live server and its pre-image is the only way back.
        //
        // Note the action leaves WriteReachedServer at its default false while saying Applied — the shape of
        // a row written before that column existed, and of any future bug that sets one without the other.
        // Applied alone must still be enough to protect the images; this test is what pins that.
        var planId = Seed(fixture, ChangePlanStatus.Failed, ChangePlanActionStatus.Applied, appliedAt);
        var store = Store(fixture);

        var result = await store.PurgeImagesAsync(appliedAt + TimeSpan.FromDays(1), Retention);

        result.Any.Should().BeFalse("the purge decision must come from the actions, not from plan.Status");
        (await store.TryGetAsync(planId))!.Actions.Single().PreImageContent.Should().Be("old-content");
    }

    [Fact]
    public async Task PurgeImagesAsync_ForAPlanWhoseOnlyWriteLandedCorrupted_KeepsItsImagesEvenThoughNoActionSaysApplied()
    {
        using var fixture = new SqliteDatabaseFixture();
        var appliedAt = CreatedAt.AddMinutes(1);
        var planId = ChangePlanId.New();
        var server = NewServer();

        // THE READ-BACK FIDELITY MISMATCH SHAPE, exactly as ApplyAsync records it. Action #0's write reached
        // the server, the read-back afterwards found bytes nobody approved, so #0 is Failed and #1 is
        // Skipped — NOTHING in this plan says Applied. A live server is holding corrupted content and
        // PreImageContent is the only way back, so this is the single worst plan in the schema to purge.
        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(NewPlan(planId, server.Id, ChangePlanStatus.PartiallyApplied, appliedAt));
            write.ChangePlanActions.AddRange(
                NewAction(
                    planId,
                    0,
                    ChangePlanActionStatus.Failed,
                    writeReachedServer: true,
                    verification: PostWriteVerification.Mismatched),
                NewAction(planId, 1, ChangePlanActionStatus.Skipped));
            write.SaveChanges();
        }

        var store = Store(fixture);
        var result = await store.PurgeImagesAsync(appliedAt + TimeSpan.FromDays(1), Retention);

        result.Any.Should().BeFalse(
            "an action recording that its write reached the server means a live server changed, whatever its status says");

        var stored = await store.TryGetAsync(planId);
        stored!.Actions.Should().OnlyContain(a => a.PreImageContent == "old-content");
        stored.Actions.Should().NotContain(a => a.Status == ChangePlanActionStatus.Applied);
    }

    [Fact]
    public async Task PurgeImagesAsync_ForAFailedPlanWhereNoWriteEverReachedTheServer_StillDiscardsItsImagesImmediately()
    {
        using var fixture = new SqliteDatabaseFixture();
        var planId = ChangePlanId.New();
        var server = NewServer();

        // The near-miss of the test above, and the reason the flag is the predicate rather than "is this
        // plan PartiallyApplied": same statuses, same plan status, but the write was refused before any I/O
        // (a revoked grant, a drift the transport caught), so nothing on the server needs putting back and
        // these plaintext secrets have no reason to survive.
        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(
                NewPlan(planId, server.Id, ChangePlanStatus.PartiallyApplied, CreatedAt.AddMinutes(1)));
            write.ChangePlanActions.AddRange(
                NewAction(planId, 0, ChangePlanActionStatus.Failed),
                NewAction(planId, 1, ChangePlanActionStatus.Skipped));
            write.SaveChanges();
        }

        var store = Store(fixture);
        var result = await store.PurgeImagesAsync(CreatedAt.AddMinutes(2), Retention);

        result.PlansPurged.Should().Be(1);
        (await store.TryGetAsync(planId))!.Actions.Should().OnlyContain(a => a.PreImageContent == null);
    }

    [Fact]
    public async Task PurgeImagesAsync_ForAFailedPlanWhereNothingLanded_DiscardsItsImagesImmediately()
    {
        using var fixture = new SqliteDatabaseFixture();
        var planId = Seed(fixture, ChangePlanStatus.Failed, ChangePlanActionStatus.Failed, CreatedAt.AddMinutes(1));
        var store = Store(fixture);

        // No window at all: nothing was written, so no revert can ever want these bytes, and holding an
        // operator's password in plaintext for thirty days to no purpose is the thing this sweep exists for.
        var result = await store.PurgeImagesAsync(CreatedAt.AddMinutes(2), Retention);

        result.PlansPurged.Should().Be(1);
        (await store.TryGetAsync(planId))!.Actions.Single().PreImageContent.Should().BeNull();
    }

    [Fact]
    public async Task PurgeImagesAsync_ForAPartiallyAppliedPlan_KeepsEveryActionsImages_NotOnlyTheAppliedOnes()
    {
        using var fixture = new SqliteDatabaseFixture();
        var appliedAt = CreatedAt.AddMinutes(1);
        var planId = ChangePlanId.New();
        var server = NewServer();

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(NewPlan(planId, server.Id, ChangePlanStatus.PartiallyApplied, appliedAt));
            write.ChangePlanActions.AddRange(
                NewAction(planId, 0, ChangePlanActionStatus.Applied),
                NewAction(planId, 1, ChangePlanActionStatus.Failed),
                NewAction(planId, 2, ChangePlanActionStatus.Skipped));
            write.SaveChanges();
        }

        var store = Store(fixture);
        var result = await store.PurgeImagesAsync(appliedAt + TimeSpan.FromDays(1), Retention);

        // Retention is a per-PLAN decision. Reverting a partially applied plan means restoring the one action
        // that landed, and an operator diagnosing the mess needs the whole plan's images to read, not a
        // subset that survived a per-action rule.
        result.Any.Should().BeFalse();
        (await store.TryGetAsync(planId))!.Actions.Should().OnlyContain(a => a.PreImageContent == "old-content");
    }

    [Fact]
    public async Task PurgeImagesAsync_RunTwice_IsIdempotentAndReportsNothingTheSecondTime()
    {
        using var fixture = new SqliteDatabaseFixture();
        Seed(fixture, ChangePlanStatus.Superseded, ChangePlanActionStatus.Pending);
        var store = Store(fixture);

        (await store.PurgeImagesAsync(CreatedAt.AddMinutes(1), Retention)).PlansPurged.Should().Be(1);
        (await store.PurgeImagesAsync(CreatedAt.AddMinutes(2), Retention)).Any.Should().BeFalse();
    }

    [Fact]
    public async Task PurgeImagesAsync_WithANegativeRetention_IsRefused()
    {
        using var fixture = new SqliteDatabaseFixture();
        var store = Store(fixture);

        var purge = async () => await store.PurgeImagesAsync(CreatedAt, TimeSpan.FromDays(-1));

        await purge.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ── UpdateAsync's concurrency guard ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_WhenTheRowMovedOnSinceItWasRead_ThrowsChangePlanConcurrencyException()
    {
        using var fixture = new SqliteDatabaseFixture();
        var planId = Seed(fixture, ChangePlanStatus.Previewed, ChangePlanActionStatus.Pending);
        var store = Store(fixture);

        var first = await store.TryGetAsync(planId);
        var second = await store.TryGetAsync(planId);
        first!.Plan.RowVersion.Should().Be(second!.Plan.RowVersion);

        first.Plan.Status = ChangePlanStatus.Applying;
        await store.UpdateAsync(first.Plan, []);

        second.Plan.Status = ChangePlanStatus.Applying;
        var update = async () => await store.UpdateAsync(second.Plan, []);

        (await update.Should().ThrowAsync<ChangePlanConcurrencyException>())
            .Which.PlanId.Should().Be(planId.ToString());
    }

    [Fact]
    public async Task UpdateAsync_RotatesTheTokenInPlace_SoTheSameInstanceCanBeUpdatedAgain()
    {
        using var fixture = new SqliteDatabaseFixture();
        var planId = Seed(fixture, ChangePlanStatus.Previewed, ChangePlanActionStatus.Pending);
        var store = Store(fixture);

        var loaded = await store.TryGetAsync(planId);
        var original = loaded!.Plan.RowVersion;

        loaded.Plan.Status = ChangePlanStatus.Applying;
        await store.UpdateAsync(loaded.Plan, []);
        loaded.Plan.RowVersion.Should().NotBe(original);

        // Apply transitions the same instance several times (Applying, then Applied) without re-reading, so
        // a token that was not refreshed in place would make the second transition fail spuriously.
        loaded.Plan.Status = ChangePlanStatus.Applied;
        var second = async () => await store.UpdateAsync(loaded.Plan, []);
        await second.Should().NotThrowAsync();

        (await store.TryGetAsync(planId))!.Plan.Status.Should().Be(ChangePlanStatus.Applied);
    }

    [Fact]
    public async Task UpdateAsync_PersistsActionRowsAlongsideThePlan()
    {
        using var fixture = new SqliteDatabaseFixture();
        var planId = Seed(fixture, ChangePlanStatus.Previewed, ChangePlanActionStatus.Pending);
        var store = Store(fixture);

        var loaded = await store.TryGetAsync(planId);
        loaded!.Plan.Status = ChangePlanStatus.Applying;
        loaded.Actions[0].Status = ChangePlanActionStatus.Applied;
        loaded.Actions[0].AppliedAt = CreatedAt.AddMinutes(2);

        await store.UpdateAsync(loaded.Plan, loaded.Actions);

        var reread = await store.TryGetAsync(planId);
        reread!.Actions[0].Status.Should().Be(ChangePlanActionStatus.Applied);
        reread.Actions[0].AppliedAt.Should().Be(CreatedAt.AddMinutes(2));
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The real store over the fixture's database. Given a local factory rather than a new member on
    /// <see cref="SqliteDatabaseFixture"/>, so this file adds nothing to a fixture other suites share.
    /// </summary>
    private static EfChangePlanStore Store(SqliteDatabaseFixture fixture) => new(new FixtureFactory(fixture));

    private sealed class FixtureFactory(SqliteDatabaseFixture fixture) : IDbContextFactory<ServyxDbContext>
    {
        public ServyxDbContext CreateDbContext() => fixture.CreateContext();
    }

    private static ChangePlanId Seed(
        SqliteDatabaseFixture fixture,
        ChangePlanStatus planStatus,
        ChangePlanActionStatus actionStatus,
        DateTimeOffset? appliedAt = null,
        DateTimeOffset? revertedAt = null)
    {
        var planId = ChangePlanId.New();
        var server = NewServer();

        using var write = fixture.CreateContext();
        write.Servers.Add(server);
        write.ChangePlans.Add(NewPlan(planId, server.Id, planStatus, appliedAt, revertedAt));
        write.ChangePlanActions.Add(NewAction(planId, 0, actionStatus));
        write.SaveChanges();

        return planId;
    }

    private static Server NewServer() => new()
    {
        Id = ServerId.New(),
        Name = "palworld-eu-1",
        ContainerId = "container-" + Guid.NewGuid().ToString("N"),
        GameDefinitionId = "palworld",
        DefinitionContentHash = "sha256:4f2c",
        HostId = null,
        AdoptionMode = AdoptionMode.Adopted,
        WriteMode = ServerWriteMode.ReadOnly,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static ChangePlanRecord NewPlan(
        ChangePlanId id,
        ServerId serverId,
        ChangePlanStatus status,
        DateTimeOffset? appliedAt = null,
        DateTimeOffset? revertedAt = null) => new()
    {
        Id = id,
        ServerId = serverId,
        Status = status,
        CreatedAt = CreatedAt,
        CreatedBy = "operator@servyx",
        ExpiresAt = CreatedAt + ChangePlanRecord.DefaultTtl,
        AppliedAt = appliedAt,
        AppliedBy = appliedAt is null ? null : "operator@servyx",
        RevertedAt = revertedAt,
        RevertedBy = revertedAt is null ? null : "operator@servyx",
        DefinitionId = "palworld",
        DefinitionVersion = "sha256:4f2c",
        ConsequencesJson = "[]",
        SurfaceHashesJson = """{"config-file":"aaa111"}""",
        BlockedJson = "[]",
        DiagnosticsJson = "[]",
    };

    private static ChangePlanActionRecord NewAction(
        ChangePlanId planId,
        int ordinal,
        ChangePlanActionStatus status,
        bool writeReachedServer = false,
        PostWriteVerification verification = PostWriteVerification.NotAttempted) => new()
    {
        WriteReachedServer = writeReachedServer,
        PostWriteVerification = verification,
        Id = Guid.NewGuid(),
        ChangePlanId = planId,
        Ordinal = ordinal,
        Kind = PlannedActionKind.WriteSurface,
        SurfaceId = "config-file",
        ResolvedPath = "/data/config-file",
        RequiredCapabilities = TransportCapabilities.FileRead | TransportCapabilities.FileWrite,
        UnifiedDiff = "--- a/config-file\n+++ b/config-file\n-old\n+new",
        Reversible = true,
        PreImageHash = "aaa111",
        PreImageContent = "old-content",
        PostImageContent = "new-content",
        PostImageHash = "bbb222",
        ContainsSecrets = true,
        Status = status,
    };
}
