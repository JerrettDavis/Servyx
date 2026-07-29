namespace Servyx.Domain.Provisioning;

/// <summary>
/// The address a control channel — RCON or another game-specific protocol — can be pinned to for a
/// provisioned resource, and whether that address outlives the provider's own replacement of the workload.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is not <see cref="ResourceReachability"/> and must never be read as it.</strong>
/// <see cref="ResourceReachability"/> answers "can an <c>ITransport</c> address this resource", and for a
/// managed container service the answer is permanently <see cref="ResourceReachability.NoTransport"/>. This
/// type answers a different, narrower question that only becomes interesting once that answer is "no":
/// <em>is there a host name or IP that a control channel could connect to, and will it still be the right
/// one tomorrow?</em> A <see cref="Durable"/> answer here does not make the resource reachable, does not
/// produce a <c>TargetDescriptor</c>, does not name a transport id, and does not move the resource's
/// <c>ControlTier</c> ceiling above <c>Operate</c> — the <c>Provision</c> tier needs
/// <c>WriteComposeFile</c>, and a shape with no compose file cannot acquire it by acquiring an address.
/// See <c>docs/provisioning.md</c> §11.7 and §11.8.
/// </para>
/// <para>
/// <strong>Why <see cref="Ephemeral"/> exists as a case of its own, rather than collapsing into
/// <see cref="Durable"/> or <see cref="NoAddress"/>.</strong> It is the case that would otherwise be a
/// silent bug. Both of Servyx's shape-M adapters can, at any given moment, name an address that a socket
/// would successfully connect to: an Azure container group without a <c>dnsNameLabel</c> has a public IP
/// right now, and an ECS service's current task has a private IPv4 right now. Both of those addresses stop
/// being correct the moment the provider does something entirely routine — Azure restarts the group, the
/// ECS scheduler replaces the task — and neither the provider nor Servyx raises anything when it happens.
/// A control channel built on one works in every test, works in the demo, and then one day quietly points
/// at nothing, or worse at somebody else's workload that has since been handed the address. Folding it into
/// <see cref="Durable"/> would ship exactly that. Folding it into <see cref="NoAddress"/> would throw away
/// the address and, with it, the operator's ability to see how close the deployment is to working and what
/// single change would fix it.
/// </para>
/// <para>
/// <strong>Every case carries prose, and the prose is the point.</strong> <see cref="Durable"/> states
/// <em>why</em> the address survives, because "trust me" is not evidence and a claim of durability is the
/// one claim here that can be wrong in the expensive direction. <see cref="Ephemeral"/> and
/// <see cref="NoAddress"/> state what would have to change, because an operator reading "no control channel"
/// with no next step will conclude Servyx is broken. Same argument as
/// <see cref="ResourceReachability.NoTransport.Reason"/>, applied one layer down.
/// </para>
/// <para>
/// A closed hierarchy for the same reason <see cref="ResourceReachability"/> is one: the distinction has to
/// survive contact with a caller in a hurry. A <c>string? Host</c> plus a <c>bool IsDurable</c> is checked
/// when somebody remembers to check it; naming <see cref="Durable"/> is an assertion, and there is no
/// <c>!</c> that skips it.
/// </para>
/// </remarks>
public abstract record ControlChannelAddress
{
    private ControlChannelAddress()
    {
    }

    /// <summary>
    /// The resource has an address that survives the provider replacing or restarting the workload.
    /// </summary>
    /// <remarks>
    /// The only case a control channel may be opened on. "Durable" is a claim about the <em>name</em>
    /// outliving a replacement, not about the workload being up, the port being published, or the credential
    /// being right — those fail loudly at connect time, which is the correct place for them to fail.
    /// </remarks>
    public sealed record Durable : ControlChannelAddress
    {
        /// <summary>Creates a durable address.</summary>
        /// <param name="host">The host name or IP a control channel should connect to.</param>
        /// <param name="justification">
        /// Why this address outlives a replacement of the workload, phrased for the person reading it. Not
        /// decorative: it is the evidence for the one claim in this type that is expensive to get wrong.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="host"/> or <paramref name="justification"/> is null, empty, or whitespace.</exception>
        public Durable(string host, string justification)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(host);
            ArgumentException.ThrowIfNullOrWhiteSpace(justification);

            Host = host;
            Justification = justification;
        }

        /// <summary>The host name or IP a control channel should connect to.</summary>
        public string Host { get; }

        /// <summary>Why this address outlives a replacement of the workload.</summary>
        public string Justification { get; }
    }

    /// <summary>
    /// The resource has an address right now, and it will stop being the right one without warning.
    /// </summary>
    /// <remarks>
    /// Deliberately not openable. Carrying the address anyway is what lets a diagnostic say "this is the
    /// address it has today, here is why pinning to it would break, here is the one change that fixes it"
    /// rather than the far less useful "no address".
    /// </remarks>
    public sealed record Ephemeral : ControlChannelAddress
    {
        /// <summary>Creates an ephemeral address.</summary>
        /// <param name="host">The address the resource holds at this moment.</param>
        /// <param name="reason">
        /// Why the address does not survive, and what would have to change for a durable one to exist.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="host"/> or <paramref name="reason"/> is null, empty, or whitespace.</exception>
        public Ephemeral(string host, string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(host);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            Host = host;
            Reason = reason;
        }

        /// <summary>The address the resource holds at this moment. Not safe to pin a control channel to.</summary>
        public string Host { get; }

        /// <summary>Why the address does not survive, and what would have to change.</summary>
        public string Reason { get; }
    }

    /// <summary>
    /// The resource has no address a control channel could use at all.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Ephemeral"/>: there is nothing to connect to, not merely nothing worth
    /// pinning to. This is the honest answer for a workload the provider does not expose an address for, and
    /// for a handle that names a resource that has since gone.
    /// </remarks>
    public sealed record NoAddress : ControlChannelAddress
    {
        /// <summary>Creates the absent case.</summary>
        /// <param name="reason">
        /// Why no control-channel address exists, and what would have to change for one to. A property of
        /// the provider's shape or of this resource's configuration — never a health verdict.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="reason"/> is null, empty, or whitespace.</exception>
        public NoAddress(string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            Reason = reason;
        }

        /// <summary>Why no control-channel address exists, and what would have to change.</summary>
        public string Reason { get; }
    }

    /// <summary>
    /// The host a control channel may be opened on, or <see langword="null"/> when there is none that may
    /// be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answers <see langword="null"/> for <see cref="Ephemeral"/> as well as for <see cref="NoAddress"/>,
    /// which is the whole reason this is not called <c>HostOrNull</c>: <see cref="Ephemeral"/> <em>has</em> a
    /// host, and this method is deliberately not the way to get it. Reading an ephemeral host is an act that
    /// should name <see cref="Ephemeral"/> at the call site.
    /// </para>
    /// <para>
    /// Deliberately a method rather than a property, for the same reason
    /// <c>ProvisionedResource.TargetOrNull()</c> is: a property named <c>Host</c> on this type is exactly the
    /// shape the three cases exist to prevent.
    /// </para>
    /// </remarks>
    /// <returns>The durable host, or <see langword="null"/>.</returns>
    public string? OpenableHostOrNull() => this is Durable durable ? durable.Host : null;

    /// <summary>
    /// The prose this case carries, whichever case it is — a justification for <see cref="Durable"/>, a
    /// reason otherwise.
    /// </summary>
    /// <remarks>For rendering and for refusal messages. Never empty, for any case.</remarks>
    public string Explanation => this switch
    {
        Durable durable => durable.Justification,
        Ephemeral ephemeral => ephemeral.Reason,
        NoAddress none => none.Reason,
        _ => throw new InvalidOperationException(
            $"Unhandled {nameof(ControlChannelAddress)} shape '{GetType().Name}'."),
    };
}

/// <summary>
/// Implemented by a provisioner that can say where — if anywhere — a control channel to its resources
/// should connect.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="IProvisioner"/> on purpose, and optional on purpose.</strong> The seven
/// adapters whose resources <em>are</em> reachable by a transport have no need of it: their control channels
/// are reached through the target descriptor the transport already carries. This interface exists for the
/// shape that has no descriptor at all, and implementing it is an adapter stating that it has considered
/// the question — including by answering <see cref="ControlChannelAddress.NoAddress"/>, which is a finding
/// rather than a gap and is worth pinning with a test.
/// </para>
/// <para>
/// <strong>Answering does not grant anything.</strong> An implementation returns an address; it does not
/// return a <c>TargetDescriptor</c>, cannot name a transport, and has no way to alter the
/// <see cref="ResourceReachability"/> of the resource it was asked about. That separation is the reason this
/// is a second interface rather than an extra member on <see cref="IProvisioner"/> whose return value could
/// be mistaken for a hand-off.
/// </para>
/// <para>
/// <strong>This is a live read, not a stored fact.</strong> Like <see cref="IProvisioner.RefreshAsync"/> it
/// may issue a provider API call, and its answer may differ between calls — a container group provisioned
/// without a DNS label will answer <see cref="ControlChannelAddress.Ephemeral"/> forever, but one whose
/// address has not been allocated yet answers <see cref="ControlChannelAddress.NoAddress"/> and will later
/// answer something else. Callers should resolve at the point of use rather than cache.
/// </para>
/// </remarks>
public interface IControlChannelAddressSource
{
    /// <summary>Resolves where a control channel to <paramref name="handle"/>'s resource should connect.</summary>
    /// <param name="handle">The resource to resolve an address for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The address, which may be <see cref="ControlChannelAddress.Ephemeral"/> or
    /// <see cref="ControlChannelAddress.NoAddress"/>. Implementations report the absence of an address as a
    /// value rather than by throwing, for the same reason <see cref="IProvisioner"/>'s remarks give: a
    /// capability answer is not an error.
    /// </returns>
    Task<ControlChannelAddress> ResolveControlAddressAsync(ResourceHandle handle, CancellationToken ct = default);
}
