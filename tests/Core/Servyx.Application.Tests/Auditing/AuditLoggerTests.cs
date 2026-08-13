using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Application.Auditing;
using Servyx.Domain.Auditing;
using Servyx.Domain.Entities;

namespace Servyx.Application.Tests.Auditing;

/// <summary>
/// Tests for <see cref="AuditLogger"/>: the convenience overload builds a correct <see cref="AuditEntry"/> and
/// persists it, and a repository write failure is swallowed and logged rather than propagated — see this
/// type's own remarks for why the latter is load-bearing (the audited action must never fail because its
/// audit row could not be written).
/// </summary>
public class AuditLoggerTests
{
    private sealed class FakeAuditEntryRepository : IAuditEntryRepository
    {
        public List<AuditEntry> Rows { get; } = [];

        /// <summary>Set to make <see cref="AddAsync"/> fail, exercising the swallow-and-log path.</summary>
        public Exception? AddFailure { get; set; }

        public Task AddAsync(AuditEntry entry, CancellationToken ct = default)
        {
            if (AddFailure is not null)
            {
                return Task.FromException(AddFailure);
            }

            Rows.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> ListRecentAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>(
                Rows.OrderByDescending(r => r.TimestampUtc).Take(limit).ToList());

        public Task<AuditEntryPage> SearchAsync(
            AuditEntryFilter filter, int pageNumber, int pageSize, CancellationToken ct = default)
        {
            // Not exercised by this file's tests — AuditLogger only ever calls AddAsync — but the interface
            // requires an implementation. Kept minimal rather than duplicating the real filter/sort/page
            // semantics EfAuditEntryRepository and FakeAuditEntryRepository (Servyx.Web.Tests) both document.
            var ordered = Rows.OrderByDescending(r => r.TimestampUtc).ToList();
            var page = ordered.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new AuditEntryPage(page, ordered.Count));
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (AuditLogger Logger, FakeAuditEntryRepository Repository) Build()
    {
        var repository = new FakeAuditEntryRepository();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero));
        var logger = new AuditLogger(repository, NullLogger<AuditLogger>.Instance, time);

        return (logger, repository);
    }

    [Fact]
    public async Task RecordAsync_WithParts_PersistsAFullyPopulatedEntry()
    {
        var (logger, repository) = Build();

        await logger.RecordAsync("alice", AuditActions.UserCreated, targetType: "user", targetId: "bob", details: "role Operator");

        repository.Rows.Should().ContainSingle();
        var row = repository.Rows[0];
        row.Actor.Should().Be("alice");
        row.Action.Should().Be(AuditActions.UserCreated);
        row.TargetType.Should().Be("user");
        row.TargetId.Should().Be("bob");
        row.Details.Should().Be("role Operator");
        row.TimestampUtc.Should().Be(new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero));
        row.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task RecordAsync_WithParts_MintsAUniqueIdPerCall()
    {
        var (logger, repository) = Build();

        await logger.RecordAsync("alice", AuditActions.UserCreated);
        await logger.RecordAsync("alice", AuditActions.UserCreated);

        repository.Rows.Should().HaveCount(2);
        repository.Rows[0].Id.Should().NotBe(repository.Rows[1].Id);
    }

    [Fact]
    public async Task RecordAsync_WithParts_OmittedOptionalFields_PersistNull()
    {
        var (logger, repository) = Build();

        await logger.RecordAsync(AuditActors.System, AuditActions.HostDeregistered);

        var row = repository.Rows.Single();
        row.Actor.Should().Be(AuditActors.System);
        row.TargetType.Should().BeNull();
        row.TargetId.Should().BeNull();
        row.Details.Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_WithAnEntity_PersistsItVerbatim()
    {
        var (logger, repository) = Build();
        var entry = new AuditEntry
        {
            Id = Guid.NewGuid(),
            TimestampUtc = DateTimeOffset.UnixEpoch,
            Actor = "operator",
            Action = AuditActions.ChangePlanApplied,
            TargetType = "changeplan",
            TargetId = "plan-1",
        };

        await logger.RecordAsync(entry);

        repository.Rows.Should().ContainSingle().Which.Should().BeSameAs(entry);
    }

    [Fact]
    public async Task RecordAsync_WhenTheRepositoryThrows_IsSwallowedAndLogged_NeverPropagated()
    {
        // The load-bearing guarantee: the action this entry describes has already happened by the time this
        // is called (every call site in this codebase calls it AFTER its own write succeeds), so a database
        // hiccup while writing the audit row must never surface as a failure of that action.
        var repository = new FakeAuditEntryRepository { AddFailure = new InvalidOperationException("db is down") };
        var logger = new AuditLogger(repository, NullLogger<AuditLogger>.Instance);

        var act = async () => await logger.RecordAsync("alice", AuditActions.UserCreated);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecordAsync_WhenCancelled_StillPropagatesCancellation()
    {
        var repository = new FakeAuditEntryRepository
        {
            AddFailure = new OperationCanceledException(),
        };
        var logger = new AuditLogger(repository, NullLogger<AuditLogger>.Instance);

        var act = async () => await logger.RecordAsync("alice", AuditActions.UserCreated);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RecordAsync_WithParts_RejectsABlankActor(string actor)
    {
        var (logger, _) = Build();

        var act = async () => await logger.RecordAsync(actor, AuditActions.UserCreated);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RecordAsync_WithParts_RejectsABlankAction(string action)
    {
        var (logger, _) = Build();

        var act = async () => await logger.RecordAsync("alice", action);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
