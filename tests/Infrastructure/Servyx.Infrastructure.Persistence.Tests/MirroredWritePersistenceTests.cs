using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Entities;
using Servyx.Infrastructure.Persistence.Configuration;
using Servyx.Infrastructure.Persistence.Servers;

namespace Servyx.Infrastructure.Persistence.Tests;

/// <summary>
/// A minimal <see cref="IDbContextFactory{TContext}"/> over an already-migrated fixture connection, matching
/// <c>EfServerSettingsServiceTests</c>'s own.
/// </summary>
file sealed class FixtureDbContextFactory(SqliteDatabaseFixture fixture) : IDbContextFactory<ServyxDbContext>
{
    public ServyxDbContext CreateDbContext() => fixture.CreateContext();
}

/// <summary>
/// Covers the two persisted halves of the mirrored-write toggle: <see cref="Server.MirrorDerivedSurfaces"/>
/// (the per-server default, with attribution) and <see cref="ServerSettingValue.MirrorToDerived"/> (the
/// three-valued per-row override).
/// </summary>
/// <remarks>
/// The property worth the most here is the seeded default. Mirroring writes bytes into a file the workload
/// owns, so a server nobody has opted in must read as off — including every server adopted before the column
/// existed, which the migration adds it to.
/// </remarks>
public class MirroredWritePersistenceTests
{
    private static Server NewServer(string containerId = "container-1") => new()
    {
        Id = ServerId.New(),
        Name = "palworld-eu-1",
        ContainerId = containerId,
        GameDefinitionId = "palworld",
        DefinitionContentHash = "sha256:4f2c",
        HostId = null,
        AdoptionMode = AdoptionMode.Adopted,
        WriteMode = ServerWriteMode.ReadOnly,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task ANewlyAddedServer_DefaultsToNotMirroring_WithNoAttribution()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var server = NewServer();

        await repository.AddAsync(server);

        var reloaded = await repository.TryGetAsync(server.Id);

        reloaded!.MirrorDerivedSurfaces.Should().BeFalse(
            because: "mirroring is opt-in — a write into a file the workload owns is not something an " +
                "operator who has not asked for it has consented to");
        reloaded.MirrorDerivedSurfacesChangedBy.Should().BeNull();
        reloaded.MirrorDerivedSurfacesChangedAt.Should().BeNull();
    }

    [Fact]
    public async Task SetMirrorDerivedSurfacesAsync_RecordsTheFlagAndItsAttributionTogether()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);
        var when = new DateTimeOffset(2026, 8, 14, 3, 0, 0, TimeSpan.Zero);

        await repository.SetMirrorDerivedSurfacesAsync(server.Id, true, "operator", when);

        var reloaded = await repository.TryGetAsync(server.Id);

        reloaded!.MirrorDerivedSurfaces.Should().BeTrue();
        reloaded.MirrorDerivedSurfacesChangedBy.Should().Be("operator");
        reloaded.MirrorDerivedSurfacesChangedAt.Should().Be(when);
    }

    [Fact]
    public async Task SetMirrorDerivedSurfacesAsync_LeavesTheWriteGrantUntouched()
    {
        // The two postures are independent facts and must not be able to move each other: this flag decides
        // what an eligible setting does, never whether Servyx may write at all.
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);

        await repository.SetMirrorDerivedSurfacesAsync(server.Id, true, "operator", DateTimeOffset.UnixEpoch);

        var reloaded = await repository.TryGetAsync(server.Id);
        reloaded!.WriteMode.Should().Be(ServerWriteMode.ReadOnly);
        reloaded.WriteModeChangedBy.Should().BeNull();
    }

    [Fact]
    public async Task SetMirrorDerivedSurfacesAsync_ForAnUnknownServer_ReturnsNull()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));

        var updated = await repository.SetMirrorDerivedSurfacesAsync(
            ServerId.New(), true, "operator", DateTimeOffset.UnixEpoch);

        updated.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_CarriesTheServerDefault_SoAPreviewNeedsNoSecondRead()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);
        await repository.SetMirrorDerivedSurfacesAsync(server.Id, true, "operator", DateTimeOffset.UnixEpoch);

        var snapshot = await service.LoadAsync("container-1");

        snapshot!.MirrorDerivedSurfaces.Should().BeTrue(
            because: "IPlanExecutor.PreviewAsync reads the default off this snapshot rather than growing a " +
                "parameter or a second dependency for it");
    }

    [Fact]
    public async Task ANewlyRecordedDesiredValue_InheritsRatherThanOverriding()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);

        await service.SaveDesiredValueAsync(server.Id, "DIFFICULTY", "Hard", "operator");

        var snapshot = await service.LoadAsync("container-1");

        // Null is the third state, not a missing value: "never expressed an opinion" has to keep following
        // the server default wherever it later moves.
        snapshot!.Values["DIFFICULTY"].MirrorToDerived.Should().BeNull();
        snapshot.Values["DIFFICULTY"].MirrorsToDerived(serverDefault: true).Should().BeTrue();
        snapshot.Values["DIFFICULTY"].MirrorsToDerived(serverDefault: false).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetMirrorToDerivedAsync_RoundTripsBothDirectionsOfOverride(bool overrideValue)
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);
        await service.SaveDesiredValueAsync(server.Id, "DIFFICULTY", "Hard", "operator");

        var result = await service.SetMirrorToDerivedAsync(server.Id, "DIFFICULTY", overrideValue, "operator");

        result.Recorded.Should().BeTrue();

        var snapshot = await service.LoadAsync("container-1");
        var value = snapshot!.Values["DIFFICULTY"];

        value.MirrorToDerived.Should().Be(overrideValue);

        // An override wins over the server default in BOTH directions — off on a mirror-on server, and on on
        // a mirror-off one. A one-directional override would make the nullable column pointless.
        value.MirrorsToDerived(serverDefault: !overrideValue).Should().Be(overrideValue);
    }

    [Fact]
    public async Task SetMirrorToDerivedAsync_WithNull_ReturnsTheRowToInheriting()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);
        await service.SaveDesiredValueAsync(server.Id, "DIFFICULTY", "Hard", "operator");
        await service.SetMirrorToDerivedAsync(server.Id, "DIFFICULTY", false, "operator");

        await service.SetMirrorToDerivedAsync(server.Id, "DIFFICULTY", null, "operator");

        var snapshot = await service.LoadAsync("container-1");
        snapshot!.Values["DIFFICULTY"].MirrorToDerived.Should().BeNull();
    }

    [Fact]
    public async Task SetMirrorToDerivedAsync_DoesNotRewriteTheRecordedValue()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);
        await service.SaveDesiredValueAsync(server.Id, "DIFFICULTY", "Hard", "operator");

        await service.SetMirrorToDerivedAsync(server.Id, "DIFFICULTY", true, "operator");

        var snapshot = await service.LoadAsync("container-1");
        snapshot!.Values["DIFFICULTY"].Value.Should().Be("Hard");
    }

    [Fact]
    public async Task SaveDesiredValueAsync_DoesNotSilentlyResetAnExistingOverride()
    {
        // The reason the override is set through its own method rather than as a parameter on the value save:
        // correcting a typo in a value must not quietly discard a preference the operator set separately.
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);
        await service.SaveDesiredValueAsync(server.Id, "DIFFICULTY", "Hard", "operator");
        await service.SetMirrorToDerivedAsync(server.Id, "DIFFICULTY", false, "operator");

        await service.SaveDesiredValueAsync(server.Id, "DIFFICULTY", "Casual", "operator");

        var snapshot = await service.LoadAsync("container-1");
        snapshot!.Values["DIFFICULTY"].Value.Should().Be("Casual");
        snapshot.Values["DIFFICULTY"].MirrorToDerived.Should().BeFalse();
    }

    [Fact]
    public async Task SetMirrorToDerivedAsync_WithNoDesiredValueRecorded_SaysSo_RatherThanInventingARow()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);

        var result = await service.SetMirrorToDerivedAsync(server.Id, "DIFFICULTY", true, "operator");

        // A blank row created purely to hold the flag would be indistinguishable from an operator who
        // genuinely blanked the field, and would record a preference about a write that cannot happen.
        result.Outcome.Should().Be(SaveDesiredValueOutcome.NoDesiredValueRecorded);
        result.Recorded.Should().BeFalse();

        var snapshot = await service.LoadAsync("container-1");
        snapshot!.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task SetMirrorToDerivedAsync_ForAnUnknownServer_SaysServerNotFound()
    {
        using var fixture = new SqliteDatabaseFixture();
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));

        var result = await service.SetMirrorToDerivedAsync(ServerId.New(), "DIFFICULTY", true, "operator");

        result.Outcome.Should().Be(SaveDesiredValueOutcome.ServerNotFound);
    }

    [Fact]
    public async Task ForgettingAServer_DiscardsItsMirrorOverridesWithItsDesiredValues()
    {
        // Inherits the cascade delete ServerSettingValueConfiguration already declares — the override lives
        // on the same row, so a re-adopted container cannot silently resurrect a mirroring preference nobody
        // re-entered.
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfServerRepository(new FixtureDbContextFactory(fixture));
        var service = new EfServerSettingsService(new FixtureDbContextFactory(fixture));
        var server = NewServer();
        await repository.AddAsync(server);
        await service.SaveDesiredValueAsync(server.Id, "DIFFICULTY", "Hard", "operator");
        await service.SetMirrorToDerivedAsync(server.Id, "DIFFICULTY", true, "operator");

        await repository.RemoveAsync(server.Id);

        await using var context = fixture.CreateContext();
        (await context.ServerSettingValues.CountAsync()).Should().Be(0);
    }
}
