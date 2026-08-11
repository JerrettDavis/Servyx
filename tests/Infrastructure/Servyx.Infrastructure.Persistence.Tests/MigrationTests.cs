using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;

namespace Servyx.Infrastructure.Persistence.Tests;

public class MigrationTests
{
    /// <summary>The last migration applied before <c>CreatedAtTicks</c> existed.</summary>
    private const string BeforeCreatedAtTicks = "20260811181810_AddChangePlanActionRevertEvidence";

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
        context.ChangePlans.Should().BeEmpty();
        context.ChangePlanActions.Should().BeEmpty();
    }

    /// <summary>
    /// Pins <c>AddChangePlanCreatedAtTicks</c>'s backfill against rows that existed before the column did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other test in this repo runs against a database migrated in one go from empty, where the backfill
    /// statement matches zero rows and could be deleted without anything noticing. The failure it prevents is
    /// silent by construction: an unbackfilled plan sorts as though it were created in the year 1, which
    /// throws nothing, logs nothing, and simply shows an operator their existing change history in the wrong
    /// order underneath everything created afterwards.
    /// </para>
    /// <para>
    /// The rows are inserted through <see cref="SqliteParameter"/> carrying real
    /// <see cref="DateTimeOffset"/> values, NOT through pre-formatted strings, so the on-disk text is
    /// whatever Microsoft.Data.Sqlite really writes rather than what this test assumes it writes — which is
    /// the whole assumption the backfill's text parsing rests on.
    /// </para>
    /// </remarks>
    [Theory]
    // Sub-tick-resolution fraction, seven significant digits: the case strftime('%f') alone would truncate.
    [InlineData(1234567, 0)]
    // A whole second, so the stored text carries no '.' at all and the fraction branch must yield zero.
    [InlineData(0, 0)]
    // Trailing zeros trimmed from the fraction ('.123'), which must read as 1230000 ticks and not as 123.
    [InlineData(1230000, 0)]
    // A non-UTC offset: ordering is by absolute instant, so the stored offset must be applied, not ignored.
    [InlineData(4560000, -5)]
    public async Task Migrate_BackfillsCreatedAtTicks_ForPlansWrittenBeforeTheColumnExisted(
        int extraTicks, int offsetHours)
    {
        var createdAt = new DateTimeOffset(2026, 8, 9, 12, 34, 56, TimeSpan.FromHours(offsetHours))
            .AddTicks(extraTicks);

        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ServyxDbContext>().UseSqlite(connection).Options;

        var serverId = ServerId.New();
        var planId = ChangePlanId.New();

        await using (var old = new ServyxDbContext(options))
        {
            await old.GetService<IMigrator>().MigrateAsync(BeforeCreatedAtTicks);

            await ExecuteAsync(
                connection,
                """
                INSERT INTO Servers
                    (Id, Name, ContainerId, GameDefinitionId, DefinitionContentHash, AdoptionMode, WriteMode, CreatedAt)
                VALUES ($id, 'palworld-eu-1', 'container-1', 'palworld', 'sha256:4f2c', 'Adopted', 'ReadOnly', $created);
                """,
                ("$id", serverId.Value.ToString()),
                ("$created", createdAt));

            await ExecuteAsync(
                connection,
                """
                INSERT INTO ChangePlans
                    (Id, ServerId, Status, CreatedAt, CreatedBy, ExpiresAt, DefinitionId, DefinitionVersion,
                     ConsequencesJson, SurfaceHashesJson, BlockedJson, DiagnosticsJson, RowVersion)
                VALUES ($id, $serverId, 'Applied', $created, 'operator@servyx', $expires, 'palworld',
                        'sha256:4f2c', '[]', '{}', '[]', '[]', $rowVersion);
                """,
                ("$id", planId.Value.ToString()),
                ("$serverId", serverId.Value.ToString()),
                ("$created", createdAt),
                ("$expires", createdAt + ChangePlanRecord.DefaultTtl),
                ("$rowVersion", Guid.NewGuid().ToString()));
        }

        await using var migrated = new ServyxDbContext(options);
        await migrated.Database.MigrateAsync();

        var plan = await migrated.ChangePlans.SingleAsync();

        plan.CreatedAt.Should().Be(createdAt, "the pre-existing value must survive the migration untouched");
        plan.CreatedAtTicks.Should().Be(createdAt.UtcTicks);
        plan.CreatedAtTicks.Should().NotBe(0, "a row left at the column default sorts as the year 1");
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
