using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Servers;
using Servyx.Infrastructure.Persistence;
using Servyx.Infrastructure.Persistence.Servers;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// A real, relational, throwaway <c>Servers</c> table for one test, plus the
/// <see cref="IDbContextFactory{TContext}"/> and <see cref="IServerRepository"/> the write-grant path is
/// actually composed over.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>Servyx.Infrastructure.Persistence.Tests.SqliteDatabaseFixture</c> deliberately: SQLite over an
/// explicitly-held in-memory connection, migrated with the real DDL, rather than the EF Core InMemory
/// provider — which enforces no unique index, no NOT NULL and no column length, and would therefore accept a
/// <c>Server</c> row production could never store. Hermetic: no file, no daemon, no network.
/// </para>
/// <para>
/// The connection is held open for the fixture's lifetime because a SQLite <c>:memory:</c> database is scoped
/// to its connection, and because <see cref="WriteGrantCacheFactory"/> hands out a NEW context per load —
/// which is exactly the property the cache's invalidation behaviour has to be tested against.
/// </para>
/// </remarks>
public sealed class WriteGrantTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Creates a fresh in-memory database with every migration applied.</summary>
    public WriteGrantTestDatabase()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        using var context = CreateContext();
        context.Database.Migrate();

        Factory = new WriteGrantCacheFactory(this);
        Repository = new EfServerRepository(Factory);
    }

    /// <summary>The factory a <c>WriteGrantCache</c> opens its short-lived read contexts from.</summary>
    public IDbContextFactory<ServyxDbContext> Factory { get; }

    /// <summary>The real EF-backed repository the grant service writes through.</summary>
    public IServerRepository Repository { get; }

    /// <summary>Creates an independent context over this fixture's database.</summary>
    public ServyxDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ServyxDbContext>().UseSqlite(_connection).Options);

    /// <summary>Inserts one adopted <c>Server</c> row and returns its minted <see cref="ServerId"/>.</summary>
    /// <param name="containerId">The container id the grant is keyed on.</param>
    /// <param name="name">The container name. Display-only, and deliberately never a grant key.</param>
    /// <param name="mode">The posture the row starts in.</param>
    public ServerId AddServer(string containerId, string name, ServerWriteMode mode)
    {
        var id = ServerId.New();

        using var context = CreateContext();
        context.Servers.Add(new Server
        {
            Id = id,
            Name = name,
            ContainerId = containerId,
            GameDefinitionId = "palworld",
            DefinitionContentHash = "sha256:test",
            HostId = null,
            AdoptionMode = AdoptionMode.Adopted,
            WriteMode = mode,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        context.SaveChanges();

        return id;
    }

    /// <summary>Reads a row back, so a test can assert what was actually persisted rather than what was returned.</summary>
    /// <param name="id">The server to read.</param>
    public Server Reload(ServerId id)
    {
        using var context = CreateContext();
        return context.Servers.AsNoTracking().Single(row => row.Id == id);
    }

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();

    private sealed class WriteGrantCacheFactory(WriteGrantTestDatabase owner) : IDbContextFactory<ServyxDbContext>
    {
        public ServyxDbContext CreateDbContext() => owner.CreateContext();
    }
}
