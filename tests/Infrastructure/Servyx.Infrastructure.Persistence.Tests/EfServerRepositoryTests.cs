using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Infrastructure.Persistence.Servers;

namespace Servyx.Infrastructure.Persistence.Tests;

/// <summary>
/// A minimal <see cref="IDbContextFactory{TContext}"/> over a <see cref="SqliteDatabaseFixture"/>'s
/// already-migrated connection, so <see cref="EfServerRepository"/> — which takes a factory rather than a
/// context directly, see its own remarks — can be exercised against the same real, relational, throwaway
/// database every other persistence test uses. Mirrors <c>ServerDefinitionBindingRecordTests</c>' own
/// <c>FixtureDbContextFactory</c>.
/// </summary>
file sealed class FixtureDbContextFactory(SqliteDatabaseFixture fixture) : IDbContextFactory<ServyxDbContext>
{
    public ServyxDbContext CreateDbContext() => fixture.CreateContext();
}

/// <summary>
/// Tests for <see cref="EfServerRepository"/>, the durable store behind Servyx's own server adoption/forget
/// bookkeeping: a row must be listable, findable by id, and must survive a simulated restart (a disposed
/// context replaced by a brand-new one, per <see cref="SqliteDatabaseFixture"/>'s own remarks) exactly like
/// every other row in this database.
/// </summary>
public class EfServerRepositoryTests
{
    private static Server NewServer(ServerId? id = null, string name = "palworld-eu-1", string? containerId = null) => new()
    {
        Id = id ?? ServerId.New(),
        Name = name,
        ContainerId = containerId ?? $"container-{name}",
        GameDefinitionId = "palworld",
        DefinitionContentHash = "sha256:4f2c",
        HostId = null,
        AdoptionMode = AdoptionMode.Adopted,
        WriteMode = ServerWriteMode.ReadOnly,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task AddAsync_then_ListAsync_finds_the_row_through_a_new_context()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var server = NewServer();

        await repository.AddAsync(server);

        var all = await repository.ListAsync();
        all.Should().ContainSingle(s => s.Id == server.Id && s.Name == "palworld-eu-1");
    }

    [Fact]
    public async Task TryGetAsync_finds_a_tracked_row_by_id()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);

        var loaded = await repository.TryGetAsync(server.Id);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("palworld-eu-1");
        loaded.AdoptionMode.Should().Be(AdoptionMode.Adopted);
        loaded.WriteMode.Should().Be(ServerWriteMode.ReadOnly);
    }

    [Fact]
    public async Task TryGetAsync_an_unknown_id_returns_null()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));

        var loaded = await repository.TryGetAsync(ServerId.New());

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_removes_a_tracked_row_and_reports_true()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);

        var removed = await repository.RemoveAsync(server.Id);

        removed.Should().BeTrue();
        (await repository.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SetWriteModeAsync_persists_the_posture_and_its_attribution_together()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);
        var changedAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        var updated = await repository.SetWriteModeAsync(server.Id, ServerWriteMode.Enabled, "operator", changedAt);

        updated.Should().NotBeNull();

        // Read through a NEW context, so this proves a real write/read cycle rather than a still-live
        // identity map handing back the object that was just mutated.
        var reloaded = await repository.TryGetAsync(server.Id);
        reloaded!.WriteMode.Should().Be(ServerWriteMode.Enabled);
        reloaded.WriteModeChangedBy.Should().Be("operator",
            because: "the posture and its attribution move in one unit of work, so a row can never carry a " +
                "grant with no record of who made it");
        reloaded.WriteModeChangedAt.Should().Be(changedAt);
    }

    [Fact]
    public async Task SetWriteModeAsync_an_unknown_id_reports_null_and_writes_nothing()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);

        var updated = await repository.SetWriteModeAsync(
            ServerId.New(), ServerWriteMode.Enabled, "operator", DateTimeOffset.UnixEpoch);

        updated.Should().BeNull();
        (await repository.TryGetAsync(server.Id))!.WriteMode.Should().Be(ServerWriteMode.ReadOnly);
    }

    [Fact]
    public async Task SetWriteModeAsync_refuses_a_blank_actor_rather_than_recording_one()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);

        var act = async () => await repository.SetWriteModeAsync(
            server.Id, ServerWriteMode.Enabled, "  ", DateTimeOffset.UnixEpoch);

        await act.Should().ThrowAsync<ArgumentException>();
        (await repository.TryGetAsync(server.Id))!.WriteMode.Should().Be(ServerWriteMode.ReadOnly);
    }

    [Fact]
    public async Task RemoveAsync_an_unknown_id_reports_false_and_changes_nothing()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);

        var removed = await repository.RemoveAsync(ServerId.New());

        removed.Should().BeFalse();
        (await repository.ListAsync()).Should().ContainSingle();
    }
}
