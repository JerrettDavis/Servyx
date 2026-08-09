using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Entities;
using Servyx.Infrastructure.Persistence.Configuration;
using Servyx.Infrastructure.Persistence.Servers;

namespace Servyx.Infrastructure.Persistence.Tests;

/// <summary>
/// A minimal <see cref="IDbContextFactory{TContext}"/> over a <see cref="SqliteDatabaseFixture"/>'s
/// already-migrated connection, so <see cref="EfServerSettingsService"/> — which takes a factory rather than
/// a context directly, see its own remarks — can be exercised against the same real, relational, throwaway
/// database every other persistence test uses. Mirrors <c>EfServerRepositoryTests</c>'s own
/// <c>FixtureDbContextFactory</c>.
/// </summary>
file sealed class FixtureDbContextFactory(SqliteDatabaseFixture fixture) : IDbContextFactory<ServyxDbContext>
{
    public ServyxDbContext CreateDbContext() => fixture.CreateContext();
}

/// <summary>
/// Tests for <see cref="EfServerSettingsService"/>: desired values must round-trip with attribution, must be
/// unreachable for an untracked container, and — the property this whole design decision exists for — must
/// NOT survive a server being forgotten and the same container later re-adopted.
/// </summary>
public class EfServerSettingsServiceTests
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
    public async Task SaveDesiredValueAsync_then_LoadAsync_RoundTrips_ThroughANewContext_WithAttribution()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));
        var server = NewServer(containerId: "container-1");
        await repository.AddAsync(server);

        var saveResult = await service.SaveDesiredValueAsync(server.Id, "SERVER_NAME", "A New Name", "operator");

        saveResult.Recorded.Should().BeTrue();

        // Read through a brand-new instance over the same database, so this proves a real write/read cycle
        // rather than a still-live identity map handing back the object just mutated.
        var reloaded = await service.LoadAsync("container-1");

        reloaded.Should().NotBeNull();
        reloaded!.ServerId.Should().Be(server.Id);
        reloaded.Values.Should().ContainKey("SERVER_NAME");
        var value = reloaded.Values["SERVER_NAME"];
        value.Value.Should().Be("A New Name");
        value.UpdatedBy.Should().Be("operator",
            because: "the desired value and its attribution move in one unit of work, so a row can never " +
                "carry a recorded intent with no record of who recorded it");
    }

    [Fact]
    public async Task SaveDesiredValueAsync_called_twice_overwrites_rather_than_duplicating()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));
        var server = NewServer(containerId: "container-1");
        await repository.AddAsync(server);

        await service.SaveDesiredValueAsync(server.Id, "SERVER_NAME", "First", "operator");
        await service.SaveDesiredValueAsync(server.Id, "SERVER_NAME", "Second", "operator");

        var snapshot = await service.LoadAsync("container-1");

        snapshot!.Values.Should().ContainSingle();
        snapshot.Values["SERVER_NAME"].Value.Should().Be("Second");
    }

    [Fact]
    public async Task LoadAsync_for_an_untracked_container_returns_null_not_an_empty_snapshot()
    {
        using var fixture = new SqliteDatabaseFixture();
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));

        var snapshot = await service.LoadAsync("no-such-container");

        snapshot.Should().BeNull(
            because: "an untracked container and a tracked one with nothing recorded yet are different facts");
    }

    [Fact]
    public async Task SaveDesiredValueAsync_for_an_unknown_ServerId_reports_ServerNotFound_and_writes_nothing()
    {
        using var fixture = new SqliteDatabaseFixture();
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));

        var result = await service.SaveDesiredValueAsync(ServerId.New(), "SERVER_NAME", "X", "operator");

        result.Outcome.Should().Be(SaveDesiredValueOutcome.ServerNotFound);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task A_null_value_is_recorded_as_an_explicit_empty_string_not_refused()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));
        var server = NewServer(containerId: "container-1");
        await repository.AddAsync(server);

        var result = await service.SaveDesiredValueAsync(server.Id, "SERVER_NAME", null, "operator");

        result.Recorded.Should().BeTrue();
        result.Value!.Value.Should().Be(string.Empty);
    }

    // ── The property this design decision exists for ────────────────────────────────────────────────

    [Fact]
    public async Task ForgettingAndReadoptingAServer_DoesNotResurrectPreviousDesiredSettingValues()
    {
        // Mirrors the exact defect Phase 2's security review found in container-id-keyed write grants:
        // adopt -> record intent -> forget -> re-adopt the same container must NOT silently inherit the old
        // server's recorded intent. Storage keys on ServerId (never the container id), and
        // ServerSettingValueConfiguration declares a cascade-delete foreign key to Server, so forgetting the
        // first server discards its desired values outright, and the re-adopted container gets a brand new
        // ServerId with nothing recorded against it.
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));

        const string containerId = "container-1";
        var firstAdoption = NewServer(containerId: containerId, name: "palworld-eu-1");
        await repository.AddAsync(firstAdoption);
        await service.SaveDesiredValueAsync(firstAdoption.Id, "ADMIN_PASSWORD", "old-secret-intent", "operator");

        // "Forget": remove the Server row. ServerAdoptionService.ForgetAsync does exactly this and nothing
        // else — no cascading application code needed for the property under test to hold.
        await repository.RemoveAsync(firstAdoption.Id);

        // "Re-adopt the same container": a brand new ServerId, same ContainerId — exactly what
        // ServerAdoptionService.AdoptAsync mints (ServerId.New()).
        var secondAdoption = NewServer(containerId: containerId, name: "palworld-eu-1");
        secondAdoption.Id.Should().NotBe(firstAdoption.Id);
        await repository.AddAsync(secondAdoption);

        var snapshot = await service.LoadAsync(containerId);

        snapshot.Should().NotBeNull();
        snapshot!.ServerId.Should().Be(secondAdoption.Id);
        snapshot.Values.Should().BeEmpty(
            because: "the re-adopted server must start with zero recorded intent, not silently inherit the " +
                "forgotten server's desired values");
    }
}
