using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Servyx.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Puts every SQLite connection Servyx opens into write-ahead-logging journal mode.
/// </summary>
/// <remarks>
/// <para>
/// WAL matters here because the control plane reads constantly (dashboards, pollers, discovery sweeps) while
/// writing occasionally, and the default rollback journal takes a database-wide exclusive lock for the whole
/// of every write. Under WAL, readers do not block the writer and the writer does not block readers, which is
/// the difference between a background provisioning write being invisible and it stalling the UI.
/// </para>
/// <para>
/// <strong>Why an interceptor rather than model configuration.</strong> <c>journal_mode</c> is a property of a
/// SQLite database, not of the Servyx schema, and PostgreSQL has no equivalent — so expressing it anywhere in
/// <see cref="ServyxDbContext"/>'s model would make the shared entity model provider-specific for no benefit.
/// Applying it against the connection keeps the SQLite-only knowledge in the SQLite-only wiring.
/// </para>
/// <para>
/// <strong>Why per-connection rather than once at registration.</strong> Setting the pragma at DI-registration
/// time would mean opening (and therefore creating) the database file as a side effect of building the service
/// collection, which is both surprising and fragile if the directory does not exist yet. Running it on
/// connection-open is idempotent, costs one trivial statement, and cannot leave a pooled connection in the
/// wrong mode. On a <c>:memory:</c> database SQLite simply reports <c>memory</c> and the statement is a no-op,
/// so the in-memory test path needs no special case.
/// </para>
/// </remarks>
public sealed class SqliteWriteAheadLogInterceptor : DbConnectionInterceptor
{
    private const string EnableWalSql = "PRAGMA journal_mode=WAL;";

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Apply(connection);

        base.ConnectionOpened(connection, eventData);
    }

    /// <inheritdoc />
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection is SqliteConnection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = EnableWalSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private static void Apply(DbConnection connection)
    {
        // Guarded rather than assumed: this interceptor is only ever registered by the SQLite path, but a
        // pragma issued against a future PostgreSQL connection would be a hard syntax error at open time,
        // and failing to open the database is a far worse outcome than not being in WAL mode.
        if (connection is not SqliteConnection)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = EnableWalSql;
        command.ExecuteNonQuery();
    }
}
