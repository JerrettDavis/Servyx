using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Servyx.Infrastructure.Persistence;

/// <summary>
/// Builds a <see cref="ServyxDbContext"/> for the <c>dotnet ef</c> design-time tooling.
/// </summary>
/// <remarks>
/// Exists because no host project references this one yet, so <c>dotnet ef</c> has no application startup path
/// to borrow a configured context from. A factory living in the persistence project itself is the smallest
/// thing that works and keeps migration generation independent of whatever host eventually composes the app.
/// The connection string below is used only to pick the provider whose SQL the migration is scaffolded
/// against; the tooling never opens it for <c>migrations add</c>, and no file is created by generating one.
/// </remarks>
public sealed class ServyxDbContextFactory : IDesignTimeDbContextFactory<ServyxDbContext>
{
    /// <inheritdoc />
    public ServyxDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ServyxDbContext>()
            .UseSqlite("Data Source=servyx-design-time.db")
            .Options;

        return new ServyxDbContext(options);
    }
}
