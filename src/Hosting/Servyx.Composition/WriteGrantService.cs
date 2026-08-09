using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Servers;
using Servyx.Domain.Transport;

namespace Servyx.Composition;

/// <summary>
/// Why a <see cref="IWriteGrantService.SetWriteModeAsync"/> call did not take effect, or
/// <see cref="Applied"/> when it did.
/// </summary>
public enum WriteGrantOutcome
{
    /// <summary>The row was updated, the cache invalidated, and the change recorded.</summary>
    Applied,

    /// <summary>
    /// The process-level master switch (<c>Servyx:Provisioning:Enabled</c>) is closed, so no grant can exist
    /// in this process at all. Nothing was written — refusing is the point of a master switch.
    /// </summary>
    MasterSwitchClosed,

    /// <summary>No <c>Server</c> row matches the supplied id, so there is nothing to grant against.</summary>
    ServerNotFound,
}

/// <summary>The result of attempting to change a server's write posture.</summary>
/// <param name="Outcome">Whether the change was applied, and if not, why not.</param>
/// <param name="Mode">The posture the server holds after this call.</param>
/// <param name="ChangedBy">Who the change was attributed to, when one was applied.</param>
/// <param name="ChangedAt">When the change was applied, when one was.</param>
public sealed record WriteGrantResult(
    WriteGrantOutcome Outcome,
    ServerWriteMode Mode,
    string? ChangedBy = null,
    DateTimeOffset? ChangedAt = null)
{
    /// <summary>Whether the posture on the row actually changed.</summary>
    public bool Applied => Outcome == WriteGrantOutcome.Applied;
}

/// <summary>
/// A tracked server's current write grant, as the UI needs to render it.
/// </summary>
/// <param name="Id">The tracked server's own id — what <see cref="IWriteGrantService.SetWriteModeAsync"/> takes.</param>
/// <param name="Name">The container name, for display only.</param>
/// <param name="ContainerId">The durable identity the grant is keyed on.</param>
/// <param name="Mode">The posture currently recorded.</param>
/// <param name="ChangedBy">
/// Who last changed it, or <see langword="null"/> if it has never been changed. Servyx has one shared
/// operator password and no per-operator accounts, so in practice this is a constant — the UI says so
/// rather than implying attribution that does not exist.
/// </param>
/// <param name="ChangedAt">When it was last changed, or <see langword="null"/> if it never has been.</param>
public sealed record WriteGrantState(
    ServerId Id,
    string Name,
    string ContainerId,
    ServerWriteMode Mode,
    string? ChangedBy,
    DateTimeOffset? ChangedAt);

/// <summary>
/// The one sanctioned way an operator's per-server write grant is created, changed, or revoked.
/// </summary>
/// <remarks>
/// Deliberately narrow: there is no method here that grants write access to more than one server, and no
/// method that opens the process-level master switch. Both omissions are the design — a grant names one
/// server, and the switch above it stays a config-only, admin-owned decision the web tier cannot reach.
/// </remarks>
public interface IWriteGrantService
{
    /// <summary>
    /// Records <paramref name="mode"/> as <paramref name="id"/>'s write posture, attributed to
    /// <paramref name="actor"/>, and makes it visible to the write guard before returning.
    /// </summary>
    /// <param name="id">The server whose posture changes.</param>
    /// <param name="mode">The posture to record.</param>
    /// <param name="actor">Who is making the change.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<WriteGrantResult> SetWriteModeAsync(
        ServerId id,
        ServerWriteMode mode,
        string actor,
        CancellationToken ct = default);

    /// <summary>
    /// The grant currently recorded for the container with <paramref name="containerId"/>, or
    /// <see langword="null"/> when Servyx tracks no server for it — the state a page needs before it can
    /// offer to change anything.
    /// </summary>
    /// <remarks>
    /// Keyed on the container id because that is what a page route and a discovery listing carry, and
    /// because it is the identity the grant itself is bound to. A container Servyx has never adopted has no
    /// row and therefore no grant, which the caller must render as "adopt this server first" rather than as
    /// a read-only grant it could flip.
    /// </remarks>
    /// <param name="containerId">The discovery-native container id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<WriteGrantState?> DescribeAsync(string containerId, CancellationToken ct = default);
}

/// <summary>
/// Writes the per-server grant to the <c>Server</c> row, invalidates the in-memory grant cache, and records
/// the change through the existing structured-logging audit convention.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Order is load-bearing, and is not sufficient on its own.</strong> The row is written first, the
/// cache is invalidated second, and only then does this return. Invalidating first would open a window in
/// which the guard had re-read the old row; returning before invalidating would let an operator watch a
/// "revoked" server keep accepting writes. But those two statements only describe this thread: a cache load
/// that had already read the pre-write rows on ANOTHER thread would go on to publish them after the
/// invalidation, and the operator who was just told their revoke landed would be exactly the person it was
/// lost for. What closes that is <see cref="WriteGrantCache.Invalidate"/>'s version counter: an in-flight
/// load either declines to publish a snapshot it now knows to be stale, or — for the narrower window in
/// which the invalidation lands between that decision and the assignment itself — publishes and then
/// immediately retracts it. Either way the cache ends up empty and the next read reloads. With both
/// properties in place, and only with both, a caller that got a success back can rely on the next command
/// issued anywhere in this process seeing the new posture.
/// </para>
/// <para>
/// <strong>The invalidation is belt and braces, not the only one.</strong> Every <c>IServerRepository</c>
/// this process can resolve is a <see cref="GrantInvalidatingServerRepository"/>, so the write on the line
/// above has already dropped the cache by the time control returns here. The explicit call is kept because
/// this type's contract is stated in terms of it and a second invalidation costs one interlocked increment;
/// what it must never become again is the ONLY invalidation, which is how forgetting a server came to leave
/// a live grant behind.
/// </para>
/// <para>
/// <strong>Cross-process caches are not covered, and this is not papered over.</strong> <c>Servyx.Web</c> and
/// <c>Servyx.Mcp.Stdio</c> are separate processes with separate caches and no shared invalidation channel.
/// The dangerous direction is revocation: an agent driving the same server over the stdio MCP host would
/// keep writing to a server the operator believes they just locked. The UI says so at the point of the flip.
/// A short cache TTL was rejected — it would trade a clear, documented limitation for a timing race.
/// </para>
/// <para>
/// <strong>There is no audit table, and none is invented here.</strong> The change is written to the same
/// structured-logging category every other authentication-adjacent decision uses, under the same event id
/// startup already uses to report granted servers. A durable, queryable audit store is genuinely future
/// work; a half-built one that looked durable would be worse than the honest gap.
/// </para>
/// </remarks>
public sealed class WriteGrantService : IWriteGrantService
{
    private readonly ProvisioningGate _gate;
    private readonly IServerRepository _servers;
    private readonly WriteGrantCache _cache;
    private readonly ILogger _audit;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    /// <param name="gate">The master switch. Closed means every call is refused without writing anything.</param>
    /// <param name="servers">Durable storage for the <c>Server</c> row the grant lives on.</param>
    /// <param name="cache">The in-memory grant view invalidated after a successful write.</param>
    /// <param name="audit">
    /// A logger created under <c>WriteGrantAudit.LogCategory</c> by the composition root, so the grant change
    /// lands in the same stream as every other auditable decision this process makes.
    /// </param>
    /// <param name="time">Supplies the change timestamp. Optional; defaults to <see cref="TimeProvider.System"/>.</param>
    public WriteGrantService(
        ProvisioningGate gate,
        IServerRepository servers,
        WriteGrantCache cache,
        ILogger audit,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(audit);

        _gate = gate;
        _servers = servers;
        _cache = cache;
        _audit = audit;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<WriteGrantResult> SetWriteModeAsync(
        ServerId id,
        ServerWriteMode mode,
        string actor,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        if (!_gate.Enabled)
        {
            // Refused before the database is touched, matching the resolver's own short circuit: with the
            // master switch closed there is no grant in this process, so writing one would only create a row
            // that lies about what this process can do.
            _audit.LogWarning(
                WriteGrantAudit.WriteModeGranted,
                WriteGrantAudit.RefusedMasterSwitchClosedMessage,
                id.Value,
                mode,
                actor,
                ProvisioningGate.ConfigurationKey);

            return new WriteGrantResult(WriteGrantOutcome.MasterSwitchClosed, ServerWriteMode.ReadOnly);
        }

        var changedAt = _time.GetUtcNow();
        var updated = await _servers.SetWriteModeAsync(id, mode, actor, changedAt, ct).ConfigureAwait(false);

        if (updated is null)
        {
            return new WriteGrantResult(WriteGrantOutcome.ServerNotFound, ServerWriteMode.ReadOnly);
        }

        // Before returning, always. See this type's remarks on ordering.
        _cache.Invalidate();

        _audit.Log(
            mode == ServerWriteMode.ReadOnly ? LogLevel.Information : LogLevel.Warning,
            WriteGrantAudit.WriteModeGranted,
            WriteGrantAudit.WriteModeChangedMessage,
            updated.Name,
            updated.ContainerId,
            mode,
            actor,
            changedAt);

        return new WriteGrantResult(WriteGrantOutcome.Applied, mode, actor, changedAt);
    }

    /// <inheritdoc />
    public async Task<WriteGrantState?> DescribeAsync(string containerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        var servers = await _servers.ListAsync(ct).ConfigureAwait(false);
        var match = servers.FirstOrDefault(
            server => string.Equals(server.ContainerId, containerId, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return null;
        }

        // Reported straight off the row rather than through the cache: this is what an operator is about to
        // change, so it must be the persisted truth, not a possibly-stale in-memory projection of it.
        return new WriteGrantState(
            match.Id,
            match.Name,
            match.ContainerId,
            match.WriteMode,
            match.WriteModeChangedBy,
            match.WriteModeChangedAt);
    }
}

/// <summary>
/// The logging category and event id a write-grant change is recorded under.
/// </summary>
/// <remarks>
/// <para>
/// These values intentionally MIRROR <c>Servyx.Web</c>'s <c>OperatorAuthentication.AuditLogCategory</c> and
/// <c>AuthenticationAudit.WriteModeGranted</c> rather than defining a second audit stream: a grant change is
/// exactly the kind of decision that already belongs in that stream, and a parallel one would fragment the
/// only record an operator has. They are restated here because <c>Servyx.Composition</c> sits <em>below</em>
/// <c>Servyx.Web</c> and cannot reference it. A test in <c>Servyx.Web.Tests</c> asserts the two stay
/// byte-identical, so the duplication cannot drift silently.
/// </para>
/// </remarks>
public static class WriteGrantAudit
{
    /// <summary>Mirrors <c>Servyx.Web.Authentication.OperatorAuthentication.AuditLogCategory</c>.</summary>
    public const string LogCategory = "Servyx.Web.Authentication.Audit";

    /// <summary>Mirrors <c>Servyx.Web.Authentication.AuthenticationAudit.WriteModeGranted</c> (event id 6009).</summary>
    public static readonly EventId WriteModeGranted = new(6009, nameof(WriteModeGranted));

    /// <summary>
    /// The message logged when a server's write posture changes. Exposed as a constant so a test asserts the
    /// exact text an operator will see.
    /// </summary>
    public const string WriteModeChangedMessage =
        "Write mode for server '{ServerName}' (container {ContainerId}) was set to {Mode} by '{Actor}' at "
        + "{ChangedAt:o}. This process's write guard honours the new posture from the next command onward, "
        + "including on sessions that were already open. Other Servyx processes (e.g. a separate MCP host) "
        + "keep their own cache and must be restarted to observe this change.";

    /// <summary>
    /// The message logged when a grant change is refused because the process-level master switch is closed.
    /// </summary>
    public const string RefusedMasterSwitchClosedMessage =
        "Refused to change the write mode of server {ServerId} to {Mode} on behalf of '{Actor}': "
        + "{ProvisioningKey} is false, so this process holds no write grant for any server and nothing was "
        + "written. Open that key in configuration and restart before granting write access.";
}
