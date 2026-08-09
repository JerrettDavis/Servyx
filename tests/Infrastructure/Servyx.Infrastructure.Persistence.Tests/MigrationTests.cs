using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Servyx.Infrastructure.Persistence.Tests;

public class MigrationTests
{
    [Fact]
    public void Migrate_AppliesEveryMigration_ToAFreshDatabase()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ServyxDbContext>().UseSqlite(connection).Options;
        using var context = new ServyxDbContext(options);

        context.Database.GetAppliedMigrations().Should().BeEmpty();

        context.Database.Migrate();

        var applied = context.Database.GetAppliedMigrations().ToList();
        applied.Should().NotBeEmpty();
        applied.Should().BeEquivalentTo(context.Database.GetMigrations());
    }

    [Fact]
    public void Migrate_LeavesNoPendingModelChanges()
    {
        using var fixture = new SqliteDatabaseFixture();
        using var context = fixture.CreateContext();

        // Catches a model edited without a follow-up migration, which would otherwise only surface at
        // runtime as a missing column against a real database.
        context.Database.GetPendingMigrations().Should().BeEmpty();
    }

    [Fact]
    public void MigratedSchema_CreatesEveryMappedTable()
    {
        using var fixture = new SqliteDatabaseFixture();
        using var context = fixture.CreateContext();

        // Queries succeed only if the migration actually produced each table with the mapped column names.
        context.Servers.Should().BeEmpty();
        context.Hosts.Should().BeEmpty();
        context.ProviderAccounts.Should().BeEmpty();
        context.ProvisionedResources.Should().BeEmpty();
        context.ServerSettingValues.Should().BeEmpty();
    }
}
