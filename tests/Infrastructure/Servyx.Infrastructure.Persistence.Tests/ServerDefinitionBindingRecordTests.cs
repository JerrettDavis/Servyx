using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Definitions;
using Servyx.Infrastructure.Persistence.Definitions;
using Servyx.Infrastructure.Persistence.Entities;

namespace Servyx.Infrastructure.Persistence.Tests;

/// <summary>
/// A minimal <see cref="IDbContextFactory{TContext}"/> over a <see cref="SqliteDatabaseFixture"/>'s
/// already-migrated connection, so <see cref="EfServerDefinitionBindingStore"/> — which takes a factory
/// rather than a context directly, see its own remarks — can be exercised against the same real,
/// relational, throwaway database every other persistence test uses.
/// </summary>
file sealed class FixtureDbContextFactory(SqliteDatabaseFixture fixture) : IDbContextFactory<ServyxDbContext>
{
    public ServyxDbContext CreateDbContext() => fixture.CreateContext();
}

/// <summary>
/// Tests for the server-definition binding table: a resolved binding must be writable, findable by server
/// id, and must survive a simulated restart (a disposed context replaced by a brand-new one, per
/// <see cref="SqliteDatabaseFixture"/>'s own remarks) exactly like every other row in this database.
/// </summary>
public class ServerDefinitionBindingRecordTests
{
    [Fact]
    public void BoundRow_RoundTripsEveryField_ThroughANewContext()
    {
        using var fixture = new SqliteDatabaseFixture();
        var updatedAt = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

        using (var write = fixture.CreateContext())
        {
            write.ServerDefinitionBindings.Add(new ServerDefinitionBindingRecord
            {
                ServerId = "abc123containerid",
                State = ServerDefinitionBindingState.Bound,
                DefinitionId = "factorio",
                DefinitionContentHash = "sha256:factorio-v1",
                DefinitionSourceId = "filesystem",
                DefinitionSourcePath = "definitions/factorio-docker.yaml",
                CandidateDefinitionIds = [],
                UpdatedAt = updatedAt,
            });

            write.SaveChanges().Should().Be(1);
        }

        // A fresh context, standing in for a restarted process: nothing here is served from the previous
        // context's identity map.
        using var read = fixture.CreateContext();
        var loaded = read.ServerDefinitionBindings.Single();

        loaded.ServerId.Should().Be("abc123containerid");
        loaded.State.Should().Be(ServerDefinitionBindingState.Bound);
        loaded.DefinitionId.Should().Be("factorio");
        loaded.DefinitionContentHash.Should().Be("sha256:factorio-v1");
        loaded.DefinitionSourceId.Should().Be("filesystem");
        loaded.DefinitionSourcePath.Should().Be("definitions/factorio-docker.yaml");
        loaded.CandidateDefinitionIds.Should().BeEmpty();
        loaded.UpdatedAt.Should().Be(updatedAt);
    }

    [Fact]
    public void AmbiguousRow_HasNoDefinition_ButNamesEveryCandidate()
    {
        using var fixture = new SqliteDatabaseFixture();

        using (var write = fixture.CreateContext())
        {
            write.ServerDefinitionBindings.Add(new ServerDefinitionBindingRecord
            {
                ServerId = "tied-container",
                State = ServerDefinitionBindingState.Ambiguous,
                DefinitionId = null,
                DefinitionContentHash = null,
                DefinitionSourceId = null,
                DefinitionSourcePath = null,
                CandidateDefinitionIds = ["palworld", "palworld-modded"],
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });

            write.SaveChanges();
        }

        using var read = fixture.CreateContext();
        var loaded = read.ServerDefinitionBindings.Single();

        loaded.State.Should().Be(ServerDefinitionBindingState.Ambiguous);
        loaded.DefinitionId.Should().BeNull();
        loaded.CandidateDefinitionIds.Should().BeEquivalentTo(["palworld", "palworld-modded"], o => o.WithStrictOrdering());
    }

    [Fact]
    public void State_IsStoredByName_NotByOrdinal()
    {
        using var fixture = new SqliteDatabaseFixture();

        using (var write = fixture.CreateContext())
        {
            write.ServerDefinitionBindings.Add(new ServerDefinitionBindingRecord
            {
                ServerId = "c1",
                State = ServerDefinitionBindingState.NeedsRebind,
                CandidateDefinitionIds = [],
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            write.SaveChanges();
        }

        using var read = fixture.CreateContext();
        var connection = read.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT State FROM ServerDefinitionBindings";

        (command.ExecuteScalar() as string).Should().Be("NeedsRebind");
    }

    [Fact]
    public async Task Store_TryGetAsync_ReturnsNull_WhenNoBindingRecorded()
    {
        using var fixture = new SqliteDatabaseFixture();
        var store = new EfServerDefinitionBindingStore(new FixtureDbContextFactory(fixture));

        (await store.TryGetAsync("unknown-container")).Should().BeNull();
    }

    [Fact]
    public async Task Store_SaveThenGet_RoundTripsThroughANewContext_SurvivingASimulatedRestart()
    {
        using var fixture = new SqliteDatabaseFixture();
        var factory = new FixtureDbContextFactory(fixture);
        var reference = new GameDefinitionRef("factorio", "sha256:factorio-v1", "filesystem", "definitions/factorio-docker.yaml");
        var updatedAt = new DateTimeOffset(2026, 8, 6, 9, 0, 0, TimeSpan.Zero);

        // The "process" that first resolved and persisted the binding...
        var writer = new EfServerDefinitionBindingStore(factory);
        await writer.SaveAsync(new ServerDefinitionBinding("container-1", ServerDefinitionBindingState.Bound, reference, [], updatedAt));

        // ...and a brand-new store instance, standing in for the process restarting, reading it back.
        var reader = new EfServerDefinitionBindingStore(factory);
        var loaded = await reader.TryGetAsync("container-1");

        loaded.Should().NotBeNull();
        loaded!.ServerId.Should().Be("container-1");
        loaded.State.Should().Be(ServerDefinitionBindingState.Bound);
        loaded.Definition.Should().Be(reference);
        loaded.UpdatedAt.Should().Be(updatedAt);
    }

    [Fact]
    public async Task Store_SaveAsync_OverwritesAnExistingBinding_RatherThanDuplicatingTheRow()
    {
        using var fixture = new SqliteDatabaseFixture();
        var factory = new FixtureDbContextFactory(fixture);
        var store = new EfServerDefinitionBindingStore(factory);

        var original = new GameDefinitionRef("palworld", "sha256:v1", "filesystem", "definitions/palworld-docker.yaml");
        var edited = new GameDefinitionRef("palworld", "sha256:v2", "filesystem", "definitions/palworld-docker.yaml");

        await store.SaveAsync(new ServerDefinitionBinding("container-1", ServerDefinitionBindingState.Bound, original, [], DateTimeOffset.UnixEpoch));
        await store.SaveAsync(new ServerDefinitionBinding("container-1", ServerDefinitionBindingState.NeedsRebind, edited, ["palworld"], DateTimeOffset.UnixEpoch.AddDays(1)));

        using var read = fixture.CreateContext();
        read.ServerDefinitionBindings.Should().ContainSingle();

        var loaded = await store.TryGetAsync("container-1");
        loaded!.State.Should().Be(ServerDefinitionBindingState.NeedsRebind);
        loaded.Definition.Should().Be(edited);
    }
}
