using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Auditing;
using Servyx.Domain.Entities;
using Servyx.Infrastructure.Persistence.Auditing;

namespace Servyx.Infrastructure.Persistence.Tests;

/// <summary>
/// A minimal <see cref="IDbContextFactory{TContext}"/> over a <see cref="SqliteDatabaseFixture"/>'s
/// already-migrated connection, so <see cref="EfAuditEntryRepository"/> — which takes a factory rather than a
/// context directly, see its own remarks — can be exercised against the same real, relational, throwaway
/// database every other persistence test uses. Mirrors <c>EfUserRepositoryTests</c>' own
/// <c>FixtureDbContextFactory</c>.
/// </summary>
file sealed class FixtureDbContextFactory(SqliteDatabaseFixture fixture) : IDbContextFactory<ServyxDbContext>
{
    public ServyxDbContext CreateDbContext() => fixture.CreateContext();
}

/// <summary>
/// Tests for <see cref="EfAuditEntryRepository"/>, the durable store behind Servyx's cross-cutting
/// accountability trail: a row must be listable in reverse-chronological order and must survive a simulated
/// restart (a disposed context replaced by a brand-new one) exactly like every other row in this database.
/// </summary>
public class EfAuditEntryRepositoryTests
{
    private static AuditEntry NewEntry(
        DateTimeOffset? timestamp = null, string actor = "alice", string action = AuditActions.UserCreated,
        string? targetType = "user", string? targetId = "bob", string? details = null) => new()
    {
        Id = Guid.NewGuid(),
        TimestampUtc = timestamp ?? DateTimeOffset.UnixEpoch,
        Actor = actor,
        Action = action,
        TargetType = targetType,
        TargetId = targetId,
        Details = details,
    };

    [Fact]
    public async Task AddAsync_then_ListRecentAsync_finds_the_row_through_a_new_context()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfAuditEntryRepository(new FixtureDbContextFactory(fixture));
        var entry = NewEntry();

        await repository.AddAsync(entry);

        var recent = await repository.ListRecentAsync(10);
        recent.Should().ContainSingle(e => e.Id == entry.Id && e.Actor == "alice" && e.Action == AuditActions.UserCreated);
    }

    [Fact]
    public async Task AddAsync_persists_every_field_including_the_optional_ones()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfAuditEntryRepository(new FixtureDbContextFactory(fixture));
        var entry = NewEntry(
            timestamp: new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero),
            actor: "operator",
            action: AuditActions.HostRegistered,
            targetType: "host",
            targetId: "prod-host",
            details: "ssh:steam@10.0.0.4:22");

        await repository.AddAsync(entry);

        var loaded = (await repository.ListRecentAsync(10)).Single();
        loaded.TimestampUtc.Should().Be(new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero));
        loaded.Actor.Should().Be("operator");
        loaded.Action.Should().Be(AuditActions.HostRegistered);
        loaded.TargetType.Should().Be("host");
        loaded.TargetId.Should().Be("prod-host");
        loaded.Details.Should().Be("ssh:steam@10.0.0.4:22");
    }

    [Fact]
    public async Task AddAsync_persists_the_optional_fields_as_null_when_omitted()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfAuditEntryRepository(new FixtureDbContextFactory(fixture));
        var entry = NewEntry(targetType: null, targetId: null, details: null);

        await repository.AddAsync(entry);

        var loaded = (await repository.ListRecentAsync(10)).Single();
        loaded.TargetType.Should().BeNull();
        loaded.TargetId.Should().BeNull();
        loaded.Details.Should().BeNull();
    }

    [Fact]
    public async Task ListRecentAsync_returns_the_newest_entries_first()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfAuditEntryRepository(new FixtureDbContextFactory(fixture));
        var oldest = NewEntry(timestamp: new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero));
        var middle = NewEntry(timestamp: new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));
        var newest = NewEntry(timestamp: new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero));

        // Added out of chronological order, so the read path — not insertion order — is what is under test.
        await repository.AddAsync(middle);
        await repository.AddAsync(oldest);
        await repository.AddAsync(newest);

        var recent = await repository.ListRecentAsync(10);

        recent.Select(e => e.Id).Should().ContainInOrder(newest.Id, middle.Id, oldest.Id);
    }

    [Fact]
    public async Task ListRecentAsync_honours_the_limit()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfAuditEntryRepository(new FixtureDbContextFactory(fixture));
        for (var i = 0; i < 5; i++)
        {
            await repository.AddAsync(NewEntry(timestamp: DateTimeOffset.UnixEpoch.AddMinutes(i)));
        }

        var recent = await repository.ListRecentAsync(2);

        recent.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListRecentAsync_with_no_rows_returns_an_empty_list()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfAuditEntryRepository(new FixtureDbContextFactory(fixture));

        (await repository.ListRecentAsync(10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Rows_are_never_updated_or_removed_by_this_repository()
    {
        // AuditEntry's own remarks: the trail is append-only. There is deliberately no UpdateAsync/RemoveAsync
        // on IAuditEntryRepository to exercise here — this test documents that absence rather than a behavior.
        typeof(Servyx.Domain.Auditing.IAuditEntryRepository).GetMethods()
            .Select(m => m.Name)
            .Should().BeEquivalentTo(["AddAsync", "ListRecentAsync", "SearchAsync"]);
    }

    [Fact]
    public async Task SearchAsync_with_no_filter_returns_the_newest_entries_first_and_the_total_count()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfAuditEntryRepository(new FixtureDbContextFactory(fixture));
        var oldest = NewEntry(timestamp: new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero));
        var newest = NewEntry(timestamp: new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero));

        await repository.AddAsync(oldest);
        await repository.AddAsync(newest);

        var page = await repository.SearchAsync(AuditEntryFilter.None, pageNumber: 1, pageSize: 50);

        page.TotalCount.Should().Be(2);
        page.Entries.Select(e => e.Id).Should().ContainInOrder(newest.Id, oldest.Id);
    }

    [Fact]
    public async Task SearchAsync_filters_by_exact_actor()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfAuditEntryRepository(new FixtureDbContextFactory(fixture));
        await repository.AddAsync(NewEntry(actor: "alice"));
        await repository.AddAsync(NewEntry(actor: "bob"));

        var page = await repository.SearchAsync(new AuditEntryFilter(Actor: "alice"), pageNumber: 1, pageSize: 50);

        page.TotalCount.Should().Be(1);
        page.Entries.Should().ContainSingle(e => e.Actor == "alice");
    }

    [Fact]
    public async Task SearchAsync_filters_by_action_prefix()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfAuditEntryRepository(new FixtureDbContextFactory(fixture));
        await repository.AddAsync(NewEntry(action: AuditActions.UserCreated));
        await repository.AddAsync(NewEntry(action: AuditActions.HostRegistered));

        var page = await repository.SearchAsync(
            new AuditEntryFilter(ActionPrefix: "user."), pageNumber: 1, pageSize: 50);

        page.TotalCount.Should().Be(1);
        page.Entries.Should().ContainSingle(e => e.Action == AuditActions.UserCreated);
    }

    [Fact]
    public async Task SearchAsync_filters_by_an_inclusive_timestamp_range()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfAuditEntryRepository(new FixtureDbContextFactory(fixture));
        var before = NewEntry(timestamp: new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        var inRange = NewEntry(timestamp: new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));
        var after = NewEntry(timestamp: new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));

        await repository.AddAsync(before);
        await repository.AddAsync(inRange);
        await repository.AddAsync(after);

        var page = await repository.SearchAsync(
            new AuditEntryFilter(
                FromUtc: new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero),
                ToUtc: new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero)),
            pageNumber: 1,
            pageSize: 50);

        page.TotalCount.Should().Be(1);
        page.Entries.Should().ContainSingle(e => e.Id == inRange.Id);
    }

    [Fact]
    public async Task SearchAsync_pages_the_filtered_result_set()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfAuditEntryRepository(new FixtureDbContextFactory(fixture));
        for (var i = 0; i < 5; i++)
        {
            await repository.AddAsync(NewEntry(timestamp: DateTimeOffset.UnixEpoch.AddMinutes(i)));
        }

        var firstPage = await repository.SearchAsync(AuditEntryFilter.None, pageNumber: 1, pageSize: 2);
        var secondPage = await repository.SearchAsync(AuditEntryFilter.None, pageNumber: 2, pageSize: 2);

        firstPage.TotalCount.Should().Be(5);
        firstPage.Entries.Should().HaveCount(2);
        secondPage.Entries.Should().HaveCount(2);
        firstPage.Entries.Select(e => e.Id).Should().NotIntersectWith(secondPage.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task SearchAsync_with_no_matches_returns_an_empty_page_and_zero_total()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfAuditEntryRepository(new FixtureDbContextFactory(fixture));
        await repository.AddAsync(NewEntry(actor: "alice"));

        var page = await repository.SearchAsync(new AuditEntryFilter(Actor: "nobody"), pageNumber: 1, pageSize: 50);

        page.TotalCount.Should().Be(0);
        page.Entries.Should().BeEmpty();
    }
}
