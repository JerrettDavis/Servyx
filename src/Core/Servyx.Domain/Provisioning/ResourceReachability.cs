using Servyx.Domain.Transport;

namespace Servyx.Domain.Provisioning;

/// <summary>
/// Whether a resource a provisioner created can be reached by a transport, and if so by which one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this type exists.</strong> <c>ProvisionedResource.Target</c> used to be a
/// non-nullable <see cref="TargetDescriptor"/>, which encoded an assumption that turned out to be false:
/// that every shape of provisionable resource terminates in something one of Servyx's transports can
/// reach. Shape H terminates in a container reachable by <c>docker</c>; shape I terminates in a host
/// reachable by <c>ssh</c>. A managed container service (shape M — Azure Container Instances) terminates
/// in a workload reachable by <em>nothing</em>: it exposes no Docker daemon, no sshd, and is not the
/// Servyx host. See <c>docs/provisioning.md</c> §11.
/// </para>
/// <para>
/// With no way to say that, an adapter for such a provider had exactly two options, and both are
/// forbidden: fabricate a transport id — which does not fail at the adapter, it fails much later and
/// somewhere else as "no transport for id" — or throw for a capability reason from
/// <c>IProvisioningOperation.CreateAsync</c>, which <see cref="IProvisioner"/>'s own remarks rule out.
/// This type is the third option: the adapter declines to name a transport, in the return type, where
/// the compiler makes every consumer acknowledge it.
/// </para>
/// <para>
/// <strong>Why a closed hierarchy rather than a nullable <c>TargetDescriptor?</c>.</strong> Both make the
/// gap expressible; only one makes it unignorable. A nullable field is checked when a caller remembers to
/// check it, and the cost of forgetting is a <see cref="NullReferenceException"/> — or, under
/// <c>Nullable=enable</c>, a single <c>!</c> that silences the compiler and restores the original bug
/// with no trace in the diff. A closed hierarchy has no <c>!</c>: reaching the descriptor means naming
/// <see cref="ViaTransport"/>, and naming it is an assertion that the resource is reachable. The two
/// options also cost the same at the call site — every existing <c>resource.Target.Endpoint</c> stops
/// compiling either way, as <c>CS8602</c> under the nullable option — so the weaker shape buys nothing.
/// It further carries a <em>reason</em> for unreachability, which a null cannot: "there is no target"
/// and "here is why there will never be one, for this provider, permanently" are different facts, and
/// the second is the one an operator needs on screen.
/// </para>
/// <para>
/// This follows the same closed-taxonomy pattern <c>Servyx.Domain</c> already uses for low-cardinality
/// domain questions — see <see cref="OrphanScope"/>, <c>ProvisioningApplyResult</c>, and
/// <c>UpdateExecutionResult</c> — for the same reason each of those gives: an adapter adding a genuinely
/// new shape should be a deliberate, reviewed act rather than a value slipped past a caller.
/// </para>
/// <para>
/// <strong>Unreachable is not "down".</strong> This says no <c>ITransport</c> in the system can address
/// the resource <em>by construction of the provider</em>, which is a permanent, static fact about the
/// shape. It is not a liveness answer, not a health probe result, and not a transient failure: see the
/// remarks on <see cref="IProvisioner.RefreshAsync"/>, which draw the same distinction for the reachable
/// case. A resource that is <see cref="NoTransport"/> may be running perfectly and serving players.
/// </para>
/// </remarks>
public abstract record ResourceReachability
{
    private ResourceReachability()
    {
    }

    /// <summary>
    /// The resource is reachable through an existing transport, named by <see cref="Target"/>.
    /// </summary>
    /// <remarks>
    /// This is the shape every adapter in the codebase produced before <see cref="NoTransport"/> existed,
    /// and the shape all of them still produce. The descriptor is handed on unchanged — see the remarks on
    /// <see cref="ProvisionedResource"/>.
    /// </remarks>
    public sealed record ViaTransport : ResourceReachability
    {
        /// <summary>Creates a reachable state naming <paramref name="target"/>.</summary>
        /// <param name="target">The transport target the rest of Servyx should use to reach the resource.</param>
        /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
        public ViaTransport(TargetDescriptor target)
        {
            ArgumentNullException.ThrowIfNull(target);

            Target = target;
        }

        /// <summary>The transport target the rest of Servyx should use to reach the resource.</summary>
        public TargetDescriptor Target { get; }
    }

    /// <summary>
    /// The resource exists at the provider but no transport in this system can address it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resource is real, it may be billing, and it is fully destroyable and reconcilable — everything
    /// <see cref="IProvisioner"/> promises still holds. What does not hold is the assumption made by the
    /// machinery <em>downstream</em> of provisioning: there is no <c>IExecutionTarget</c> to connect,
    /// nothing to probe, no file to read, no command to run. A caller holding this must reach the workload
    /// some other way (for a game server, a control channel such as RCON) or not at all.
    /// </para>
    /// <para>
    /// <strong><see cref="Reason"/> is for a human, and it is mandatory.</strong> An operator looking at a
    /// resource Servyx created but cannot connect to will otherwise conclude something is broken. Nothing
    /// is broken; the provider has no daemon to connect to. Saying so is the entire value of carrying a
    /// reason instead of a null.
    /// </para>
    /// </remarks>
    public sealed record NoTransport : ResourceReachability
    {
        /// <summary>Creates an unreachable state explaining itself with <paramref name="reason"/>.</summary>
        /// <param name="reason">
        /// Why no transport can address this resource, phrased for the person reading it on a screen. A
        /// property of the provider's shape, not of this particular resource's health.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="reason"/> is null, empty, or whitespace.</exception>
        public NoTransport(string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            Reason = reason;
        }

        /// <summary>Why no transport can address this resource.</summary>
        public string Reason { get; }
    }
}
