using Servyx.Domain.Transport;

namespace Servyx.Composition;

/// <summary>
/// Resolves a target's write posture from the operator's per-server grant in Servyx's own database, behind
/// the process-level master switch.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What replaced what.</strong> This takes over from the composition root's old
/// <c>foreach (grant) AddSingleton(grant)</c> loop over <c>Servyx:Servers:&lt;key&gt;:WriteMode</c>, which
/// froze the grant set at process start: a fresh install had no grants and nothing at runtime could add one,
/// and a grant could not be revoked without a restart. The decision an operator is making is unchanged — one
/// server at a time, inside a master switch only a host admin can open — but it is now recorded with
/// attribution and takes effect on the next command.
/// </para>
/// <para>
/// <strong>The master switch is checked first and short-circuits everything.</strong> With
/// <c>Servyx:Provisioning:Enabled</c> closed this returns <see cref="WriteMode.ReadOnly"/> without touching
/// the cache, the database, or the configured-grant fallback. A read-only host stays read-only regardless of
/// what any row, any key, or any UI says.
/// </para>
/// <para>
/// <strong>Only the local <c>docker</c> transport is database-backed.</strong> That is the transport Phase 1's
/// adoption path produces <c>Server</c> rows for. Targets on other transports — <c>ssh+docker</c> containers
/// and SSH backup endpoints — are still resolved from the composition-root <see cref="WriteModeGrant"/>s
/// their own wiring emits, because those name a host the operator declared explicitly in configuration and
/// no adoption path mints a row for them. Mixing the two sources for one target was rejected: two sources of
/// truth for one decision is the ambiguity this change exists to remove.
/// </para>
/// <para>
/// <strong>Identity, not name.</strong> A grant is honoured only when the descriptor presents the exact
/// <c>ContainerId</c> the row was written against. A descriptor that names a container only by name resolves
/// <see cref="WriteMode.ReadOnly"/>, because a name can be reassigned to a different workload at any time
/// outside Servyx — that is precisely the "recreated container inherits the old grant" hole this keying
/// closes. Container names are compared against an id-keyed dictionary and simply miss, which is the
/// fail-closed direction.
/// </para>
/// </remarks>
public sealed class DbBackedWriteModeResolver : IWriteModeResolver
{
    /// <summary>The transport whose targets are resolved from the database.</summary>
    public const string DockerTransportId = "docker";

    /// <summary>
    /// The descriptor option keys that may carry a container <em>id</em>, in the order the Docker transport
    /// itself reads them. <c>containerName</c> is deliberately absent: a name is not an identity.
    /// </summary>
    private static readonly string[] ContainerIdOptionKeys = ["containerId", "container"];

    private readonly ProvisioningGate _gate;
    private readonly WriteGrantCache _grants;
    private readonly IWriteModeResolver _configuredGrants;

    /// <summary>Creates the resolver.</summary>
    /// <param name="gate">The process-level master switch; closed means read-only everywhere, with no store read.</param>
    /// <param name="grants">The database-backed per-server grant cache.</param>
    /// <param name="configuredGrants">
    /// The resolver consulted for every target that is not on the local <c>docker</c> transport — in practice
    /// a <see cref="GrantedWriteModeResolver"/> over the composition root's remaining
    /// <see cref="WriteModeGrant"/>s. Never consulted for a Docker target, so a stale configuration key
    /// cannot re-grant an adopted container.
    /// </param>
    public DbBackedWriteModeResolver(
        ProvisioningGate gate,
        WriteGrantCache grants,
        IWriteModeResolver configuredGrants)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(configuredGrants);

        _gate = gate;
        _grants = grants;
        _configuredGrants = configuredGrants;
    }

    /// <inheritdoc />
    public WriteMode Resolve(TargetDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!_gate.Enabled)
        {
            return WriteMode.ReadOnly;
        }

        if (!string.Equals(target.TransportId, DockerTransportId, StringComparison.Ordinal))
        {
            return _configuredGrants.Resolve(target);
        }

        return WriteModeMapping.ToTransport(_grants.ModeFor(ContainerIdOf(target)));
    }

    /// <summary>
    /// The container id a descriptor presents, or <see langword="null"/> when it names its container only by
    /// name (or not at all). Null resolves read-only.
    /// </summary>
    private static string? ContainerIdOf(TargetDescriptor target)
    {
        foreach (var key in ContainerIdOptionKeys)
        {
            if (target.Options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
