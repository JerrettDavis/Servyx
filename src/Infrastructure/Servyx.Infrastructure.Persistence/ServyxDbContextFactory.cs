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
/// <para>
/// <strong>NEVER pass <c>--no-build</c> to a <c>dotnet ef</c> command after changing the model.</strong> The
/// tooling reads the COMPILED assembly, not the source. With a stale build:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>migrations add</c> silently scaffolds an EMPTY migration — it compares the new model against itself,
/// because it never saw the change. This fails no build and throws nothing.
/// </description></item>
/// <item><description>
/// <c>migrations remove</c> then DELETES THE WRONG MIGRATION. It believes the newest migration is whatever
/// the stale assembly knows about, so it removes the previous, already-good one and reverts
/// <c>ServyxDbContextModelSnapshot.cs</c> along with it. This has actually happened here, and it is
/// destructive to committed files rather than merely unhelpful.
/// </description></item>
/// </list>
/// <para>
/// Build first, then run the tooling: <c>dotnet build</c> this project, and only then
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/Infrastructure/Servyx.Infrastructure.Persistence</c>.
/// Afterwards, confirm the result with <c>dotnet ef migrations has-pending-model-changes</c> and check that
/// the snapshot diff contains only what you intended.
/// </para>
/// <para>
/// One more scaffolder default worth overriding by hand: adding a required <c>string</c> column emits
/// <c>defaultValue: ""</c> to backfill existing rows. For a column holding JSON that value is invalid and
/// will throw on the first read — see <c>AddChangePlanDiagnostics</c>, which uses <c>"[]"</c> instead.
/// </para>
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
