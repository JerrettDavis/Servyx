using System.Collections.Concurrent;
using System.Net;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Tests.Integration;

/// <summary>
/// The gate every write-mutation test must pass through before a <see cref="TargetDescriptor"/> may be used
/// to obtain a mutating <see cref="Servyx.Domain.Transport.IExecutionTarget"/> in the mutation test suite.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why four layers, each independently sufficient:</b> this guard exists because a single mistake in a
/// mutation test — a copy-pasted container name, a stale endpoint, a fixture that outlived its container —
/// must never be able to reach a real, running server. No single check is trusted to carry that guarantee
/// alone; each of the four below refuses on its own, using a different fact about the target, so a bug that
/// defeats one layer still meets the other three.
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Generated names only.</b> The target's container name must start with <see cref="RequiredPrefix"/>.
/// <see cref="RequiredPrefix"/> is never a literal any human would type for a real workload, and
/// <see cref="DisposableWorkloadContainer"/> is the only code in this assembly that generates a name
/// carrying it.
/// </description></item>
/// <item><description>
/// <b>Live process-local registry.</b> Even a correctly-prefixed name must be currently registered by a
/// live <see cref="DisposableWorkloadContainer"/> in this process. This defeats a stale or copy-pasted
/// literal that happens to carry the right prefix — passing layer 1 is necessary but never sufficient.
/// </description></item>
/// <item><description>
/// <b>Endpoint pinning.</b> The target's endpoint must resolve to a loopback/localhost host, and its port
/// must equal the Testcontainers-mapped ephemeral port the same live fixture registered. A production
/// endpoint is structurally unreachable through this layer: it is never loopback.
/// </description></item>
/// <item><description>
/// <b>Environment exclusion.</b> If any <c>SERVYX_REMOTE_*</c> environment variable is set anywhere in the
/// process, every approval is refused outright, regardless of the other three layers. Those variables are
/// read exclusively by <c>tests/Servyx.Remote.Tests</c> to point at the real production host; the mutation
/// fixture and that suite must never be able to coexist in one process.
/// </description></item>
/// </list>
/// <para>
/// <b>Thread safety:</b> the registry is a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by
/// container name. Registration and removal are single atomic operations
/// (<see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> /
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove"/>), and <see cref="Approve"/> only ever reads via
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryGetValue"/>, so concurrent registration, approval, and
/// disposal across parallel xUnit collections cannot corrupt it or race into a false approval.
/// </para>
/// </remarks>
internal static class MutationTargetGuard
{
    /// <summary>The only prefix a mutation target's container name may carry.</summary>
    internal const string RequiredPrefix = "servyx-mutation-test-";

    private const string RemoteEnvironmentPrefix = "SERVYX_REMOTE_";
    private const string ContainerNameOptionKey = "containerName";

    private const string NamesLayer = "MutationTargetGuard layer 1 (generated names only)";
    private const string RegistryLayer = "MutationTargetGuard layer 2 (live process-local registry)";
    private const string EndpointLayer = "MutationTargetGuard layer 3 (endpoint pinning)";
    private const string EnvironmentLayer = "MutationTargetGuard layer 4 (environment exclusion)";

    /// <summary>
    /// Names currently backed by a live <see cref="DisposableWorkloadContainer"/> in this process, each
    /// mapped to the Testcontainers-assigned host port that fixture registered.
    /// </summary>
    private static readonly ConcurrentDictionary<string, int> Registry = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers <paramref name="name"/> as a live mutation fixture, callable only by
    /// <see cref="DisposableWorkloadContainer.StartAsync"/>. Returns a handle whose
    /// <see cref="IDisposable.Dispose"/> removes the registration; the fixture disposes it unconditionally,
    /// including on a failed start, so a name can never outlive the container it named.
    /// </summary>
    /// <param name="name">The generated container name. Must already carry <see cref="RequiredPrefix"/>.</param>
    /// <param name="port">The Testcontainers-mapped host port this fixture's endpoint will pin to.</param>
    internal static IDisposable Register(string name, int port)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Refusing to register '{name}': it does not start with the required prefix " +
                $"'{RequiredPrefix}'. Only names generated by DisposableWorkloadContainer.StartAsync may be " +
                "registered.",
                nameof(name));
        }

        if (!Registry.TryAdd(name, port))
        {
            throw new InvalidOperationException(
                $"A mutation fixture named '{name}' is already registered. Names are process-unique GUIDs; " +
                "this should never happen.");
        }

        return new RegistrationHandle(name);
    }

    /// <summary>
    /// Approves <paramref name="target"/> for use by the mutation test suite, or throws a
    /// <see cref="MutationTargetRefusedException"/> naming exactly which layer refused it and why. Returns
    /// <paramref name="target"/> unchanged on success, so this can sit inline in a fixture's construction
    /// path.
    /// </summary>
    public static TargetDescriptor Approve(TargetDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Layer 4 first: it is process-wide and independent of everything else about this target, so a
        // developer who set SERVYX_REMOTE_* by accident (or left a shell exporting it) is told that
        // immediately rather than after also being told their otherwise-fine target passed layers 1-3.
        RefuseIfRemoteEnvironmentIsPresent();

        var name = ExtractContainerName(target);

        // Layer 1: generated names only.
        if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
        {
            throw new MutationTargetRefusedException(
                NamesLayer,
                $"container name '{name}' does not start with the required prefix '{RequiredPrefix}'. Only " +
                "names generated by DisposableWorkloadContainer.StartAsync are ever approved — a literal " +
                "name (including a real production container name) is refused outright.");
        }

        // Layer 2: live process-local registry.
        if (!Registry.TryGetValue(name, out var registeredPort))
        {
            throw new MutationTargetRefusedException(
                RegistryLayer,
                $"'{name}' is correctly prefixed but is not currently registered by a live " +
                "DisposableWorkloadContainer in this process. It may be stale, copy-pasted, or its container " +
                "has already been disposed. Only a container this process itself started, right now, is " +
                "approved.");
        }

        // Layer 3: endpoint pinning.
        var (host, port) = ParseEndpoint(target.Endpoint);
        if (!IsLoopback(host))
        {
            throw new MutationTargetRefusedException(
                EndpointLayer,
                $"endpoint '{target.Endpoint}' resolves to host '{host}', which is not loopback/localhost. " +
                "A production endpoint is structurally unreachable through this guard.");
        }

        if (port != registeredPort)
        {
            throw new MutationTargetRefusedException(
                EndpointLayer,
                $"endpoint '{target.Endpoint}' uses port {port}, which is not the Testcontainers-mapped port " +
                $"({registeredPort}) that '{name}' registered when its container started.");
        }

        return target;
    }

    private static void RefuseIfRemoteEnvironmentIsPresent()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key ||
                !key.StartsWith(RemoteEnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            throw new MutationTargetRefusedException(
                EnvironmentLayer,
                $"environment variable '{key}' is set. The mutation fixture and the SERVYX_REMOTE_* remote " +
                "suite (tests/Servyx.Remote.Tests, which points at the real production host) may never " +
                "coexist in one process; every mutation target is refused while any SERVYX_REMOTE_* " +
                "variable is present.");
        }
    }

    private static string ExtractContainerName(TargetDescriptor target)
    {
        if (target.Options.TryGetValue(ContainerNameOptionKey, out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        throw new MutationTargetRefusedException(
            NamesLayer,
            $"target descriptor carries no '{ContainerNameOptionKey}' option. Only a descriptor naming a " +
            "container may be approved.");
    }

    /// <summary>
    /// Splits an endpoint of the form <c>[scheme:][user@]host:port</c> — the shapes actually produced
    /// across this codebase's descriptors, e.g. <c>"servyx@127.0.0.1:54321"</c> or
    /// <c>"ssh:operator@203.0.113.10:22"</c> — into its host and port. Only the host and port matter to
    /// this guard; scheme and user are discarded.
    /// </summary>
    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new MutationTargetRefusedException(EndpointLayer, "target endpoint is null or blank.");
        }

        var lastColon = endpoint.LastIndexOf(':');
        if (lastColon < 0 || lastColon == endpoint.Length - 1 ||
            !int.TryParse(endpoint.AsSpan(lastColon + 1), out var port))
        {
            throw new MutationTargetRefusedException(
                EndpointLayer, $"endpoint '{endpoint}' has no parseable trailing ':<port>'.");
        }

        var hostPart = endpoint[..lastColon];
        var atIndex = hostPart.LastIndexOf('@');
        var host = atIndex >= 0 ? hostPart[(atIndex + 1)..] : hostPart;

        // A scheme with no "user@" (e.g. "ssh:127.0.0.1") leaves a stray "ssh:" glued to the host — strip it.
        var strayColon = host.IndexOf(':');
        if (strayColon >= 0)
        {
            host = host[(strayColon + 1)..];
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new MutationTargetRefusedException(EndpointLayer, $"endpoint '{endpoint}' has no host component.");
        }

        return (host, port);
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static void Unregister(string name) => Registry.TryRemove(name, out _);

    private sealed class RegistrationHandle(string name) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Unregister(name);
            }
        }
    }
}

/// <summary>
/// Thrown by <see cref="MutationTargetGuard.Approve"/> when a target is refused. <see cref="Layer"/> names
/// exactly which of the four independent layers refused it, so a developer hitting this at 2am does not
/// have to guess.
/// </summary>
internal sealed class MutationTargetRefusedException : Exception
{
    public MutationTargetRefusedException(string layer, string reason)
        : base($"{layer} refused this mutation target: {reason}")
    {
        Layer = layer;
    }

    /// <summary>The name of the layer that refused, e.g. "MutationTargetGuard layer 3 (endpoint pinning)".</summary>
    public string Layer { get; }
}
