using Servyx.Domain.Provisioning;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Rcon;

/// <summary>
/// Everything about a control channel that comes from the server's definition and configuration rather than
/// from the provider: which port, which credential, which commands, and what the server's write posture is.
/// </summary>
/// <remarks>
/// <para>
/// The split is the same one <see cref="ServiceCollectionExtensions.AddServyxRcon"/> already draws. The
/// provider knows <em>where</em> the workload is; it does not know that 25575 is the RCON port, that the
/// admin password lives at a particular URN, or which commands the definition declares read-only. Those are
/// host knowledge, and a plausible default for any of them would point a write-capable control channel at
/// the wrong thing.
/// </para>
/// <para>
/// <strong>The port is not verified against the provider, and that is deliberate.</strong> If the resource
/// was provisioned without publishing this port, the channel fails at connect time with
/// <see cref="RconUnreachableException"/> — loudly, naming the endpoint. The alternative, teaching the
/// address source to enumerate published ports, would move a definition-level fact into the provider
/// adapters and would still not know which published port is the control one.
/// </para>
/// </remarks>
/// <param name="Port">The TCP port the workload's RCON listener is published on.</param>
/// <param name="PasswordUrn">Where the RCON credential lives. A locator, never a value.</param>
/// <param name="Catalog">The definition's declared control commands, carrying each one's <c>readOnly</c> flag.</param>
/// <param name="Mode">
/// The owning server's write posture, enforced by <see cref="WriteGuardedRconSession"/>. Set per server and
/// never globally.
/// </param>
public sealed record RconControlChannelSpec(
    int Port,
    SecretUrn PasswordUrn,
    RconCommandCatalog Catalog,
    WriteMode Mode);

/// <summary>
/// Thrown when a control channel was asked for against a resource that has no address it may be pinned to.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="RconUnreachableException"/>, which means "there is an endpoint and the socket
/// did not open". This means there was never an endpoint worth opening: the provider either exposes no
/// address for the workload at all, or exposes only one that stops being correct the next time it replaces
/// the workload. Conflating the two would make a permanent, structural refusal look like a transient network
/// failure, which is exactly the misreading <see cref="ResourceReachability.NoTransport"/> exists to prevent
/// one layer up.
/// </para>
/// <para>
/// The message states three things in order, because an operator holding only the last of them cannot act:
/// that the resource is unreachable by transport and why (the provider's own permanent reason), that the
/// control channel is the remaining route, and what specifically is missing for that route to work.
/// </para>
/// </remarks>
public sealed class ControlChannelUnavailableException : RconException
{
    /// <summary>Creates a <see cref="ControlChannelUnavailableException"/> with a default message.</summary>
    public ControlChannelUnavailableException()
        : base("The resource has no address a control channel may be opened on.")
    {
    }

    /// <summary>Creates a <see cref="ControlChannelUnavailableException"/> with the given message.</summary>
    public ControlChannelUnavailableException(string message) : base(message) { }

    /// <summary>Creates a <see cref="ControlChannelUnavailableException"/> with the given message and inner exception.</summary>
    public ControlChannelUnavailableException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="ControlChannelUnavailableException"/> carrying the address that was refused.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="address">The address the refusal was made on.</param>
    public ControlChannelUnavailableException(string message, ControlChannelAddress address)
        : base(message) => Address = address;

    /// <summary>
    /// The address the refusal was made on — <see cref="ControlChannelAddress.Ephemeral"/> or
    /// <see cref="ControlChannelAddress.NoAddress"/>, never <see cref="ControlChannelAddress.Durable"/>.
    /// </summary>
    public ControlChannelAddress? Address { get; }
}

/// <summary>
/// Opens a write-guarded RCON session against a resource a provisioner created, including — and especially —
/// one whose <see cref="ProvisionedResource.Reachability"/> is
/// <see cref="ResourceReachability.NoTransport"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is <c>docs/provisioning.md</c> §11.8's item (2).</strong> Item (1) made "provisioned but
/// unreachable by any transport" expressible, and two shape-M adapters were built on it; both could then be
/// planned, costed, created, swept and destroyed, and neither could be <em>operated</em>, because nothing in
/// the codebase consumed an unreachable resource. This type is what consumes one. It reaches the workload
/// through its game control channel instead of through <see cref="IExecutionTarget"/>, which satisfies
/// <c>ControlCapability.ControlChannelWrite</c> and therefore <c>ControlTier.Operate</c> — §11.7's claim,
/// turned from a design into a code path.
/// </para>
/// <para>
/// <strong>It does not make an unreachable resource reachable, and is built so that it cannot.</strong> No
/// member of this type, of <see cref="RconControlChannelSpec"/>, or of
/// <see cref="ControlChannelAddress"/> accepts, returns, or constructs a <see cref="TargetDescriptor"/>;
/// none names a transport id; and nothing here writes to the <see cref="ProvisionedResource"/> it is handed —
/// it is a record whose <see cref="ProvisionedResource.Reachability"/> is set at construction by the adapter
/// and never afterwards. After a channel has been opened, <c>RequireTarget()</c> on that same resource still
/// throws and <c>TargetOrNull()</c> still answers null, which is the correct outcome: an operator can talk to
/// the game, and Servyx still cannot read a file, run a command, or reach the <c>Provision</c> tier. The
/// ceiling is a property of the shape, not of how hard the control plane tries.
/// </para>
/// <para>
/// <strong>Only <see cref="ControlChannelAddress.Durable"/> is opened.</strong> An
/// <see cref="ControlChannelAddress.Ephemeral"/> address would produce a channel that connects today and
/// silently points at nothing — or at a replacement workload's neighbour — after a routine restart or task
/// replacement, with no error raised at the moment it stopped being true. There is deliberately no override,
/// no "force", and no opt-in: a channel that appears to work and does not is worse than a refusal that names
/// the one change that would fix it. See <see cref="ControlChannelAddress"/>.
/// </para>
/// <para>
/// <strong>The <c>readOnly</c> discipline is not reimplemented here.</strong> Every session this type hands
/// back is a <see cref="WriteGuardedRconSession"/> over a <see cref="RconSession"/>, in that order, so the
/// definition's per-command <c>readOnly</c> flag gates the channel exactly as it does for a container-hosted
/// server — read-only commands pass on a <see cref="WriteMode.ReadOnly"/> server, mutating ones and the raw
/// escape hatch do not, and the refusal happens before the secret store or the socket is touched. There is
/// no unguarded return path from this type: the guard is applied by construction rather than by a caller
/// remembering to wrap.
/// </para>
/// <para>
/// <strong>Only <c>direct-tcp</c> can apply, so no reachability chain is consulted.</strong> Of the four
/// strategies <see cref="IRconReachability"/> names, <c>docker-exec-tool</c> and <c>docker-exec-network</c>
/// both need a Docker daemon and <c>ssh-tunnel</c> needs an sshd — which are precisely the three things the
/// adapter's own <see cref="ResourceReachability.NoTransport"/> reason says the provider does not have.
/// Running a chain here would try three strategies that cannot possibly succeed for a structural reason
/// already known before the first probe. This type therefore does what
/// <see cref="DirectTcpRconReachability.AcquireAsync"/> does, and skips the probe for the same reason that
/// strategy's own acquire does: a probe is a liveness question, and answering it here would only move the
/// connect failure earlier while doubling the connections made.
/// </para>
/// </remarks>
public sealed class RconControlChannel
{
    private readonly IRconClient _client;
    private readonly ISecretStore _secrets;
    private readonly IRconAuditSink? _audit;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// The only reachability strategy that can serve a resource with no transport, named so a caller can
    /// report which one was used without inferring it.
    /// </summary>
    public const string StrategyId = DirectTcpRconReachability.Id;

    /// <summary>Creates a control-channel opener.</summary>
    /// <param name="client">The protocol client every exchange goes through.</param>
    /// <param name="secrets">The store the RCON credential is resolved from, at the point of use.</param>
    /// <param name="audit">
    /// The audit sink the raw escape hatch writes to. When null the escape hatch is unavailable and says so —
    /// see <see cref="RconSession"/>.
    /// </param>
    /// <param name="timeProvider">Clock used to stamp player snapshots.</param>
    public RconControlChannel(
        IRconClient client,
        ISecretStore secrets,
        IRconAuditSink? audit = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(secrets);

        _client = client;
        _secrets = secrets;
        _audit = audit;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Resolves where <paramref name="resource"/>'s control channel should connect and opens a guarded
    /// session on it.
    /// </summary>
    /// <param name="resource">The resource to operate. Its reachability is read for diagnostics only, and is never changed.</param>
    /// <param name="addresses">
    /// The adapter that owns <paramref name="resource"/>, answering where — if anywhere — a control channel
    /// should connect. Resolved on every call rather than cached: see
    /// <see cref="IControlChannelAddressSource"/>.
    /// </param>
    /// <param name="spec">The definition-level facts: port, credential URN, command catalogue, write mode.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="WriteGuardedRconSession"/> bound to the resource's durable control address.</returns>
    /// <exception cref="ControlChannelUnavailableException">
    /// The resource has no durable control address. The message names the provider's own reason for
    /// unreachability and the address source's reason for the absence.
    /// </exception>
    public async Task<IRconSession> OpenAsync(
        ProvisionedResource resource,
        IControlChannelAddressSource addresses,
        RconControlChannelSpec spec,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(addresses);

        var address = await addresses.ResolveControlAddressAsync(resource.Handle, ct).ConfigureAwait(false);
        return Open(resource, address, spec);
    }

    /// <summary>
    /// Opens a guarded session on an address that has already been resolved.
    /// </summary>
    /// <remarks>
    /// The synchronous half of <see cref="OpenAsync"/>, for a caller that resolved the address itself — a
    /// diagnostic that has already rendered the address to a screen, for instance, and should not ask the
    /// provider a second time.
    /// </remarks>
    /// <param name="resource">The resource to operate. Its reachability is read for diagnostics only, and is never changed.</param>
    /// <param name="address">Where the control channel should connect.</param>
    /// <param name="spec">The definition-level facts: port, credential URN, command catalogue, write mode.</param>
    /// <returns>A <see cref="WriteGuardedRconSession"/> bound to the resource's durable control address.</returns>
    /// <exception cref="ControlChannelUnavailableException"><paramref name="address"/> is not durable.</exception>
    public IRconSession Open(ProvisionedResource resource, ControlChannelAddress address, RconControlChannelSpec spec)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Catalog);

        if (spec.Port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(spec),
                spec.Port,
                "A control channel needs the TCP port the workload publishes its RCON listener on.");
        }

        if (string.IsNullOrEmpty(spec.PasswordUrn.Value))
        {
            throw new ArgumentException(
                "A control channel needs the URN its RCON credential lives at. Build one with SecretUrn.Create; a "
                + "default(SecretUrn) is not a valid URN.",
                nameof(spec));
        }

        var host = address.OpenableHostOrNull()
            ?? throw new ControlChannelUnavailableException(RefusalMessage(resource, address), address);

        var endpoint = new RconEndpoint(host, spec.Port);

        var session = new RconSession(
            _client,
            endpoint,
            spec.Catalog,
            _secrets,
            spec.PasswordUrn,
            _audit,
            _timeProvider);

        // Guarded by construction. There is no branch of this method that returns the inner session.
        return new WriteGuardedRconSession(session, spec.Catalog, spec.Mode, Describe(resource));
    }

    /// <summary>
    /// Renders, without connecting to anything, why a resource can or cannot be operated through a control
    /// channel.
    /// </summary>
    /// <remarks>
    /// For an operator staring at a resource Servyx created and will not connect to. It says all three parts
    /// at once — no transport and why, whether a control channel is possible, and what is missing if it is
    /// not — because each part alone reads as a different, and wrong, story.
    /// </remarks>
    /// <param name="resource">The resource being explained.</param>
    /// <param name="address">Its resolved control-channel address.</param>
    /// <returns>A human-readable explanation.</returns>
    public static string Explain(ProvisionedResource resource, ControlChannelAddress address)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(address);

        var transport = resource.Reachability switch
        {
            ResourceReachability.ViaTransport reachable =>
                $"{Describe(resource)} is reachable by the '{reachable.Target.TransportId}' transport, so the control "
                + "channel is an addition to that route rather than the only one.",
            ResourceReachability.NoTransport unreachable =>
                $"{Describe(resource)} is not reachable by any transport: {unreachable.Reason}",
            _ => $"{Describe(resource)} has an unrecognised reachability shape.",
        };

        var control = address switch
        {
            ControlChannelAddress.Durable durable =>
                $"A control channel can be opened on '{durable.Host}' over {StrategyId}, which reaches the Operate "
                + $"tier and no higher. That address is durable because: {durable.Justification}",
            ControlChannelAddress.Ephemeral ephemeral =>
                $"No control channel will be opened. The resource's only address is '{ephemeral.Host}', which is not "
                + $"durable: {ephemeral.Reason}",
            ControlChannelAddress.NoAddress none =>
                $"No control channel will be opened, because there is no address to open one on: {none.Reason}",
            _ => "The resource's control-channel address has an unrecognised shape.",
        };

        return transport + " " + control;
    }

    private static string RefusalMessage(ProvisionedResource resource, ControlChannelAddress address) =>
        "Refusing to open an RCON control channel: " + Explain(resource, address)
        + " Nothing is broken and nothing will be retried - this is a property of how the resource was "
        + "provisioned, not of its health.";

    private static string Describe(ProvisionedResource resource) =>
        $"'{resource.Handle.ProviderResourceId}' (provisioner '{resource.Handle.ProvisionerId}')";
}
