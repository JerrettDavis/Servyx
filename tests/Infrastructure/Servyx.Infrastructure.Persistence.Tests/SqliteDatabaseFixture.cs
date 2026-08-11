using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Servyx.Infrastructure.Persistence.Tests;

/// <summary>
/// A real, relational, throwaway database for one test.
/// </summary>
/// <remarks>
/// <para>
/// Uses the SQLite in-memory provider over an explicitly opened <see cref="SqliteConnection"/>, not the EF
/// Core InMemory provider. That distinction is the point of this fixture: the InMemory provider is a LINQ
/// query engine over dictionaries and enforces no foreign keys, no unique indexes, no NOT NULL, and no column
/// length — so a persistence test written against it passes for schemas that could never be created. SQLite
/// runs the migration's actual DDL and rejects the same writes production would.
/// </para>
/// <para>
/// The connection is held open for the fixture's lifetime because a SQLite <c>:memory:</c> database is scoped
/// to its connection: closing the last one deletes the database. Holding one open is also what lets a test
/// dispose a <see cref="ServyxDbContext"/> and open a brand-new one against the same data, which is how the
/// round-trip tests prove values survive a real write/read cycle rather than being served from the identity
/// map of a still-live context.
/// </para>
/// </remarks>
public sealed class SqliteDatabaseFixture : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Creates a fresh in-memory database and applies every migration to it.</summary>
    public SqliteDatabaseFixture()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        using var context = CreateContext();
        context.Database.Migrate();
    }

    /// <summary>
    /// The open connection this fixture's database lives on, for a test that needs to build its own
    /// <see cref="DbContextOptions"/> over the same data — capturing the SQL EF generates, say.
    /// </summary>
    public SqliteConnection Connection => _connection;

    /// <summary>Creates a new context over this fixture's database. Each call is an independent unit of work.</summary>
    public ServyxDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ServyxDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new ServyxDbContext(options);
    }

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();
}
