using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Entities;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Persistence.Configuration;

namespace Servyx.Infrastructure.Persistence.Tests;

/// <summary>
/// Tests for <see cref="EfChangePlanStore.ListRecentAsync"/> — the projection-only "recent plans" listing
/// added for a future change-plan history page.
/// </summary>
/// <remarks>
/// The properties pinned here: the limit guard refuses out-of-range values rather than silently clamping,
/// results are deterministically ordered newest-first with an id tiebreak, only the requested server's plans
/// come back, each plan's actions arrive ordered by <see cref="ChangePlanActionRecord.Ordinal"/>, and the
/// summary projection still returns correct data for a plan whose actions carry image blobs — even though
/// <see cref="ChangePlanActionSummary"/> has no properties to carry those blobs back out.
/// </remarks>
public class EfChangePlanStoreListRecentTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    // ── Limit guard ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task ListRecentAsync_WithALimitOutsideOneToOneHundred_ThrowsArgumentOutOfRangeException(int limit)
    {
        using var fixture = new SqliteDatabaseFixture();
        var store = Store(fixture);
        var serverId = ServerId.New();

        var list = async () => await store.ListRecentAsync(serverId, limit);

        await list.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ListRecentAsync_WithLimitOfOneHundred_DoesNotThrow()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();
        Seed(fixture, server, 1);
        var store = Store(fixture);

        var list = async () => await store.ListRecentAsync(server.Id, 100);

        await list.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ListRecentAsync_WithLimitOfOne_ReturnsExactlyOneRow()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();
        Seed(fixture, server, 3);
        var store = Store(fixture);

        var result = await store.ListRecentAsync(server.Id, 1);

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListRecentAsync_WhenMoreRowsExistThanTheLimit_ReturnsExactlyTheLimit()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();
        Seed(fixture, server, 5);
        var store = Store(fixture);

        var result = await store.ListRecentAsync(server.Id, 2);

        result.Should().HaveCount(2);
    }

    // ── Ordering ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListRecentAsync_OrdersNewestCreatedAtFirst()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();

        var older = ChangePlanId.New();
        var newer = ChangePlanId.New();

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(NewPlan(older, server.Id, CreatedAt));
            write.ChangePlans.Add(NewPlan(newer, server.Id, CreatedAt.AddMinutes(5)));
            write.SaveChanges();
        }

        var store = Store(fixture);
        var result = await store.ListRecentAsync(server.Id, 10);

        result.Select(row => row.Id).Should().Equal(newer, older);
    }

    [Fact]
    public async Task ListRecentAsync_WithIdenticalCreatedAt_BreaksTheTieByIdDescending()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();

        // Two plans previewed in the same instant. Without an id tiebreak, this ordering would be whatever
        // the storage engine happens to return, and a history view flip-flopping between page loads is
        // exactly the kind of "usually works" bug this test exists to catch.
        var lowerId = ChangePlanId.Parse("00000000-0000-0000-0000-000000000001");
        var higherId = ChangePlanId.Parse("00000000-0000-0000-0000-000000000002");

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(NewPlan(lowerId, server.Id, CreatedAt));
            write.ChangePlans.Add(NewPlan(higherId, server.Id, CreatedAt));
            write.SaveChanges();
        }

        var store = Store(fixture);
        var result = await store.ListRecentAsync(server.Id, 10);

        result.Select(row => row.Id).Should().Equal(higherId, lowerId);
    }

    [Fact]
    public async Task ListRecentAsync_OverALongHistory_OrdersAndLimitsInSql_RatherThanAfterLoadingTheTable()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();

        // Far more plans than the page asks for. Change plans accumulate until the retention sweep removes
        // them — the sweep exists precisely because they do — so a server's history is not bounded by
        // anything a listing may assume.
        const int history = 400;
        const int page = 25;
        Seed(fixture, server, history);

        var sql = new List<string>();
        var store = new EfChangePlanStore(new CapturingFactory(fixture, sql));

        var result = await store.ListRecentAsync(server.Id, page);

        // Seed writes plan i at CreatedAt + i minutes, so the newest page is the last `page` of them, newest
        // first. Asserted as exact values rather than as "is descending": a query that returned the OLDEST
        // twenty-five would satisfy a descending-order check on its own.
        var expected = Enumerable.Range(history - page, page)
            .Reverse()
            .Select(i => CreatedAt.AddMinutes(i));

        result.Should().HaveCount(page);
        result.Select(row => row.CreatedAt).Should().Equal(expected);

        // AND THE ORDERING HAPPENED IN THE DATABASE. Every assertion above is equally satisfied by loading all
        // four hundred rows and sorting them in application memory, which is what this method used to do and
        // what a well-meaning revert to `.ToListAsync().OrderByDescending(...)` would restore. The generated
        // SQL is the only thing that tells the two apart.
        var planQuery = sql.Should()
            .ContainSingle(statement => statement.Contains("FROM \"ChangePlans\"", StringComparison.Ordinal))
            .Which;

        planQuery.Should().Contain("ORDER BY");
        planQuery.Should().Contain("CreatedAtTicks");
        planQuery.Should().Contain("LIMIT");
    }

    // ── Server filtering ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListRecentAsync_ExcludesPlansBelongingToADifferentServer()
    {
        using var fixture = new SqliteDatabaseFixture();
        var targetServer = NewServer();
        var otherServer = NewServer();

        var targetPlan = ChangePlanId.New();
        var otherPlan = ChangePlanId.New();

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(targetServer);
            write.Servers.Add(otherServer);
            write.ChangePlans.Add(NewPlan(targetPlan, targetServer.Id, CreatedAt));
            write.ChangePlans.Add(NewPlan(otherPlan, otherServer.Id, CreatedAt.AddMinutes(5)));
            write.SaveChanges();
        }

        var store = Store(fixture);
        var result = await store.ListRecentAsync(targetServer.Id, 10);

        result.Should().ContainSingle().Which.Id.Should().Be(targetPlan);
    }

    [Fact]
    public async Task ListRecentAsync_ForAServerWithNoPlans_ReturnsAnEmptyListNotNull()
    {
        using var fixture = new SqliteDatabaseFixture();
        var store = Store(fixture);

        var result = await store.ListRecentAsync(ServerId.New(), 10);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    // ── Action projection ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListRecentAsync_OrdersActionsWithinAPlanByOrdinalAscending()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();
        var planId = ChangePlanId.New();

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(NewPlan(planId, server.Id, CreatedAt));
            // Inserted out of order on purpose — the query must sort, not rely on insertion order.
            write.ChangePlanActions.Add(NewAction(planId, 2));
            write.ChangePlanActions.Add(NewAction(planId, 0));
            write.ChangePlanActions.Add(NewAction(planId, 1));
            write.SaveChanges();
        }

        var store = Store(fixture);
        var result = await store.ListRecentAsync(server.Id, 10);

        result.Single().Actions.Select(a => a.Ordinal).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task ListRecentAsync_ProjectsActionSummariesCorrectly_EvenWhenTheUnderlyingRowsCarryImageBlobs()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();
        var planId = ChangePlanId.New();

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(NewPlan(planId, server.Id, CreatedAt));
            write.ChangePlanActions.Add(NewAction(planId, 0));
            write.SaveChanges();
        }

        var store = Store(fixture);
        var result = await store.ListRecentAsync(server.Id, 10);

        var action = result.Single().Actions.Single();
        action.SurfaceId.Should().Be("config-file");
        action.ResolvedPath.Should().Be("/data/config-file");
        action.Kind.Should().Be(PlannedActionKind.WriteSurface);
        action.Status.Should().Be(ChangePlanActionStatus.Applied);
        action.WriteReachedServer.Should().BeTrue();
        action.PostImageHash.Should().Be("bbb222");
        action.ObservedPostImageHash.Should().Be("bbb222");
        action.PostWriteVerification.Should().Be(PostWriteVerification.Verified);

        // ChangePlanActionSummary has no PreImageContent/PostImageContent/UnifiedDiff properties at all —
        // the exclusion is compile-time. What this asserts is that the rest of the row still came back
        // correctly despite those blob columns being present (and non-null) underneath.
    }

    [Fact]
    public async Task ListRecentAsync_ProjectsPlanSummaryFieldsCorrectly()
    {
        using var fixture = new SqliteDatabaseFixture();
        var server = NewServer();
        var planId = ChangePlanId.New();
        var appliedAt = CreatedAt.AddMinutes(1);
        var revertedAt = CreatedAt.AddMinutes(2);

        using (var write = fixture.CreateContext())
        {
            write.Servers.Add(server);
            write.ChangePlans.Add(NewPlan(planId, server.Id, CreatedAt, appliedAt, revertedAt));
            write.SaveChanges();
        }

        var store = Store(fixture);
        var summary = (await store.ListRecentAsync(server.Id, 10)).Single();

        summary.Id.Should().Be(planId);
        summary.ServerId.Should().Be(server.Id);
        summary.Status.Should().Be(ChangePlanStatus.Reverted);
        summary.CreatedAt.Should().Be(CreatedAt);
        summary.CreatedBy.Should().Be("operator@servyx");
        summary.AppliedAt.Should().Be(appliedAt);
        summary.AppliedBy.Should().Be("operator@servyx");
        summary.RevertedAt.Should().Be(revertedAt);
        summary.RevertedBy.Should().Be("operator@servyx");
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────────────

    private static EfChangePlanStore Store(SqliteDatabaseFixture fixture) => new(new FixtureFactory(fixture));

    private sealed class FixtureFactory(SqliteDatabaseFixture fixture) : IDbContextFactory<ServyxDbContext>
    {
        public ServyxDbContext CreateDbContext() => fixture.CreateContext();
    }

    /// <summary>
    /// The same database, through contexts that record every SQL statement they execute.
    /// </summary>
    /// <remarks>
    /// Built here rather than on <see cref="SqliteDatabaseFixture"/> so no suite that does not want logging
    /// pays for it, following <c>EfChangePlanStorePurgeTests</c>'s own reason for a local factory. What it is
    /// for is stated at its one call site: "the ordering is correct" and "the ordering was done by the
    /// database" are different claims, and only the second one is what stops this method loading a whole
    /// table to return a page of it.
    /// </remarks>
    private sealed class CapturingFactory(SqliteDatabaseFixture fixture, List<string> statements)
        : IDbContextFactory<ServyxDbContext>
    {
        public ServyxDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ServyxDbContext>()
                .UseSqlite(fixture.Connection)
                .LogTo(statements.Add, [RelationalEventId.CommandExecuted])
                .Options;

            return new ServyxDbContext(options);
        }
    }

    /// <summary>Seeds <paramref name="count"/> plans for <paramref name="server"/>, each a minute apart.</summary>
    private static void Seed(SqliteDatabaseFixture fixture, Server server, int count)
    {
        using var write = fixture.CreateContext();
        write.Servers.Add(server);
        for (var i = 0; i < count; i++)
        {
            write.ChangePlans.Add(NewPlan(ChangePlanId.New(), server.Id, CreatedAt.AddMinutes(i)));
        }

        write.SaveChanges();
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
        DateTimeOffset createdAt,
        DateTimeOffset? appliedAt = null,
        DateTimeOffset? revertedAt = null) => new()
    {
        Id = id,
        ServerId = serverId,
        Status = revertedAt is not null
            ? ChangePlanStatus.Reverted
            : appliedAt is not null
                ? ChangePlanStatus.Applied
                : ChangePlanStatus.Previewed,
        CreatedAt = createdAt,
        CreatedBy = "operator@servyx",
        ExpiresAt = createdAt + ChangePlanRecord.DefaultTtl,
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

    private static ChangePlanActionRecord NewAction(ChangePlanId planId, int ordinal) => new()
    {
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
        ObservedPostImageHash = "bbb222",
        PostWriteVerification = PostWriteVerification.Verified,
        WriteReachedServer = true,
        ContainsSecrets = true,
        Status = ChangePlanActionStatus.Applied,
        AppliedAt = CreatedAt.AddMinutes(1),
    };
}
