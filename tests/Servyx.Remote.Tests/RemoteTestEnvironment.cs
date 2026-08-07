using System.Collections.Concurrent;
using Servyx.Domain.Connectors;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Remote.Tests;

/// <summary>
/// Reads the live remote target's coordinates from the environment and decides, per run, whether the
/// remote suite may execute at all. This is gating layer 4 of 4 (see the .csproj header for the other
/// three): <strong>not one production value — endpoint, username, key path, container name, or host-key
/// fingerprint — appears anywhere in this project's source.</strong>
/// </summary>
/// <remarks>
/// <para>
/// A missing or blank required variable produces a <see cref="MissingReason"/> and the tests SKIP. They
/// never fail for want of configuration, and they never fall back to a default: there is no endpoint
/// Servyx could guess for someone else's production box, and guessing one is precisely the failure this
/// type exists to make impossible.
/// </para>
/// <para>
/// <b>Required variables</b>
/// <list type="table">
/// <item><term><c>SERVYX_REMOTE_E2E</c></term><description>Must be exactly <c>"1"</c>. The master switch.</description></item>
/// <item><term><c>SERVYX_REMOTE_ENDPOINT</c></term><description>
/// <c>[ssh:][user@]host[:port]</c>, parsed by <see cref="SshEndpoint.Parse"/> — e.g.
/// <c>ssh:someuser@203.0.113.10:22</c>. The username is taken from the endpoint (there is no separate
/// username variable), which is exactly the <c>usernameHint</c> path <see cref="SshTransport"/> already uses.
/// </description></item>
/// <item><term><c>SERVYX_REMOTE_KEY_PATH</c></term><description>
/// A <em>Windows-readable</em> path to the OpenSSH private key. See <see cref="Servyx.Remote.Tests"/>'s
/// smoke-test class remarks for how an operator whose key lives inside WSL produces one.
/// </description></item>
/// <item><term><c>SERVYX_REMOTE_CONTAINER</c></term><description>The container name to observe.</description></item>
/// <item><term><c>SERVYX_REMOTE_FINGERPRINT</c></term><description>
/// The host's SHA-256 host-key fingerprint in OpenSSH's own <c>SHA256:&lt;base64-no-padding&gt;</c> form —
/// byte-identical to what <see cref="HostKeyFingerprint.ComputeSha256"/> produces, so it can be compared
/// directly. Obtain it read-only with <c>ssh-keygen -F &lt;host&gt; -l</c> (reads the local known_hosts and
/// contacts nothing) or <c>ssh-keyscan -t ed25519 &lt;host&gt; | ssh-keygen -lf -</c> (fetches only the
/// public host key). Note that <c>ssh-keygen -l</c> prints <c>&lt;bits&gt; SHA256:... comment (ALG)</c> —
/// only the <c>SHA256:...</c> token belongs in this variable. Multiple fingerprints may be given
/// comma-separated; <see cref="SshTransport"/> splits them for
/// <see cref="TrustPolicy.PinnedFingerprints"/>.
/// </description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class RemoteTestEnvironment
{
    /// <summary>The master switch. Anything other than exactly <c>"1"</c> skips the whole suite.</summary>
    public const string EnabledVariable = "SERVYX_REMOTE_E2E";

    /// <summary>The <c>[ssh:][user@]host[:port]</c> endpoint variable.</summary>
    public const string EndpointVariable = "SERVYX_REMOTE_ENDPOINT";

    /// <summary>The Windows-readable private key path variable.</summary>
    public const string KeyPathVariable = "SERVYX_REMOTE_KEY_PATH";

    /// <summary>The container name variable.</summary>
    public const string ContainerVariable = "SERVYX_REMOTE_CONTAINER";

    /// <summary>The pinned SHA-256 host-key fingerprint variable.</summary>
    public const string FingerprintVariable = "SERVYX_REMOTE_FINGERPRINT";

    /// <summary>
    /// The URN the private key is stored under in this run's in-memory secret store. The
    /// <see cref="SecretUrn.Name"/> segment must be <c>"private-key"</c> — that is the literal
    /// <see cref="SshCredentialResolver"/> switches on to decide this credential is a key rather than a
    /// password. The scope id is a fixed local label, not a production identifier.
    /// </summary>
    private const string PrivateKeyUrnText = "secret://connector/servyx-remote-readonly/ssh/private-key";

    private RemoteTestEnvironment(string endpoint, string keyPath, string container, string fingerprint)
    {
        Endpoint = endpoint;
        KeyPath = keyPath;
        Container = container;
        Fingerprint = fingerprint;
    }

    /// <summary>The configured endpoint, verbatim from <see cref="EndpointVariable"/>.</summary>
    public string Endpoint { get; }

    /// <summary>The configured Windows-readable private key path.</summary>
    public string KeyPath { get; }

    /// <summary>The container name this suite observes.</summary>
    public string Container { get; }

    /// <summary>The pinned SHA-256 host-key fingerprint(s), comma-separated.</summary>
    public string Fingerprint { get; }

    /// <summary>
    /// Why the suite cannot run, or <see langword="null"/> when it can. Computed once per process and
    /// handed straight to <c>Skip.IfNot</c>, so a skipped run states exactly which variable was absent
    /// rather than failing with a connection error.
    /// </summary>
    public static string? MissingReason { get; } = ComputeMissingReason(out var resolved) ? null : resolved;

    /// <summary>
    /// The resolved environment, or <see langword="null"/> when <see cref="MissingReason"/> is set.
    /// Never dereference this without skipping on <see cref="MissingReason"/> first.
    /// </summary>
    public static RemoteTestEnvironment? Current { get; private set; }

    /// <summary>
    /// Builds the <see cref="TargetDescriptor"/> for the live host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The option keys are exactly the ones <see cref="SshTransport"/>'s connector-descriptor builder
    /// reads, and the values mirror what <see cref="SshDockerWiringOptions.FromConfiguration"/> would have
    /// produced from an operator's <c>Servyx:Hosts:&lt;name&gt;</c> section — this suite goes through the
    /// same seam production configuration does, it just sources the values from the environment instead of
    /// appsettings.
    /// </para>
    /// <para>
    /// <c>trustPolicy: "requirePinned"</c> is set explicitly even though
    /// <c>pinnedFingerprints</c> already wins in <c>BuildTrustPolicy</c>'s ordering, and even though
    /// <see cref="TrustPolicy.RequirePinned"/> is the fallback anyway. It is stated so that deleting the
    /// fingerprint option can never silently downgrade this suite to trust-on-first-use: with both keys
    /// present, the worst outcome of losing the pin is a refused connection, never an unverified one.
    /// <see cref="HostKeyGate"/> fails closed by design and this keeps it that way.
    /// </para>
    /// </remarks>
    public TargetDescriptor BuildDescriptor() => new(
        TransportId: SshDockerWiringOptions.TransportIdValue,
        Endpoint: Endpoint,
        CredentialUrn: PrivateKeyUrnText,
        DockerContext: null,
        Options: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["containerName"] = Container,
            ["declaredChannels"] = SshDockerWiringOptions.DeclaredChannels,
            ["trustPolicy"] = "requirePinned",
            ["pinnedFingerprints"] = Fingerprint,
        });

    /// <summary>
    /// Loads the private key bytes off disk into an in-memory secret store.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <c>DataProtectionSecretStore</c>: that would persist a production private key into
    /// a DataProtection key ring on the machine running the tests, which is a side effect a read-only smoke
    /// suite has no business leaving behind. <see cref="InMemorySecretStore"/> holds the bytes for the
    /// lifetime of the process and nothing longer.
    /// </remarks>
    public async Task<ISecretStore> CreateSecretStoreAsync(CancellationToken ct = default)
    {
        var store = new InMemorySecretStore();
        var bytes = await File.ReadAllBytesAsync(KeyPath, ct).ConfigureAwait(false);
        if (!SecretUrn.TryParse(PrivateKeyUrnText, out var urn))
        {
            throw new InvalidOperationException($"'{PrivateKeyUrnText}' is not a valid SecretUrn.");
        }

        await store.SetAsync(urn, bytes, "servyx-remote-tests", ct).ConfigureAwait(false);
        return store;
    }

    /// <summary>
    /// The host-key verifier this suite connects through: the real <see cref="HostKeyVerifier"/>, backed by
    /// a store that has pinned nothing and revoked nothing.
    /// </summary>
    /// <remarks>
    /// Under <see cref="TrustPolicy.PinnedFingerprints"/> the verifier consults the store only to ask
    /// whether the host is revoked, and compares the presented key against the caller-supplied list — so a
    /// store with no records is not a weakening, it is simply unused for the trust decision. A fingerprint
    /// that does not match still yields <see cref="HostKeyVerdict.Unknown"/> and
    /// <see cref="SshConnector"/> aborts the handshake.
    /// </remarks>
    public static IHostKeyVerifier CreateHostKeyVerifier() => new HostKeyVerifier(new EmptyHostKeyStore());

    private static bool ComputeMissingReason(out string reason)
    {
        var enabled = Environment.GetEnvironmentVariable(EnabledVariable);
        if (!string.Equals(enabled, "1", StringComparison.Ordinal))
        {
            reason =
                $"{EnabledVariable} is not \"1\". This suite talks to a REAL production game server; " +
                "it stays off unless explicitly switched on.";
            return false;
        }

        var endpoint = Read(EndpointVariable);
        var keyPath = Read(KeyPathVariable);
        var container = Read(ContainerVariable);
        var fingerprint = Read(FingerprintVariable);

        var missing = new List<string>();
        if (endpoint is null)
        {
            missing.Add(EndpointVariable);
        }

        if (keyPath is null)
        {
            missing.Add(KeyPathVariable);
        }

        if (container is null)
        {
            missing.Add(ContainerVariable);
        }

        if (fingerprint is null)
        {
            missing.Add(FingerprintVariable);
        }

        if (missing.Count > 0)
        {
            reason = $"{EnabledVariable}=1 but these required variables are unset or blank: {string.Join(", ", missing)}.";
            return false;
        }

        if (!File.Exists(keyPath))
        {
            reason = $"{KeyPathVariable} points at '{keyPath}', which does not exist or is not readable from Windows.";
            return false;
        }

        Current = new RemoteTestEnvironment(endpoint!, keyPath!, container!, fingerprint!);
        reason = string.Empty;
        return true;
    }

    private static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>
/// A minimal in-memory <see cref="ISecretStore"/> holding this run's private key. Mirrors the equivalent
/// double in <c>Servyx.Infrastructure.Ssh.Tests</c>, which is <c>internal</c> to that test assembly and so
/// cannot be referenced from here; it is replicated rather than shared for that reason alone.
/// </summary>
/// <remarks>
/// Leases are independent copies, because <see cref="SecretLease.Dispose"/> zeroes its buffer and must not
/// corrupt the stored value for the next <see cref="GetAsync"/> — the SSH connector resolves the key once
/// per connection, so the store is read more than once per run.
/// </remarks>
internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(SecretUrn urn, CancellationToken ct = default) =>
        Task.FromResult(_values.ContainsKey(urn.Value));

    /// <inheritdoc />
    public Task<SecretLease?> GetAsync(SecretUrn urn, CancellationToken ct = default)
    {
        if (!_values.TryGetValue(urn.Value, out var stored))
        {
            return Task.FromResult<SecretLease?>(null);
        }

        return Task.FromResult<SecretLease?>(new SecretLease((byte[])stored.Clone()));
    }

    /// <inheritdoc />
    public Task SetAsync(SecretUrn urn, ReadOnlyMemory<byte> value, string actor, CancellationToken ct = default)
    {
        _values[urn.Value] = value.ToArray();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(SecretUrn urn, string actor, CancellationToken ct = default)
    {
        _values.TryRemove(urn.Value, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SecretUrn>> ListAsync(string scope, string scopeId, CancellationToken ct = default)
    {
        IReadOnlyList<SecretUrn> result = _values.Keys
            .Select(k => SecretUrn.TryParse(k, out var urn) ? urn : (SecretUrn?)null)
            .Where(u => u is not null && u.Value.Scope == scope && u.Value.ScopeId == scopeId)
            .Select(u => u!.Value)
            .ToList();
        return Task.FromResult(result);
    }
}

/// <summary>
/// An <see cref="IHostKeyStore"/> that has pinned nothing and revoked nothing, and refuses to record
/// anything.
/// </summary>
/// <remarks>
/// This suite trusts the remote host solely via the operator-supplied
/// <see cref="TrustPolicy.PinnedFingerprints"/> list, so the store is consulted only by
/// <see cref="HostKeyVerifier"/>'s revocation check. <see cref="PinAsync"/> and <see cref="RevokeAsync"/>
/// throw rather than silently succeeding: a read-only remote suite must never persist trust state, and a
/// future edit that started pinning from here should fail loudly instead of quietly writing a production
/// host key into a store nobody expected to exist.
/// </remarks>
internal sealed class EmptyHostKeyStore : IHostKeyStore
{
    /// <inheritdoc />
    public Task<HostKeyRecord?> FindAsync(string host, int port, CancellationToken ct = default) =>
        Task.FromResult<HostKeyRecord?>(null);

    /// <inheritdoc />
    public Task PinAsync(HostKeyRecord record, string actor, CancellationToken ct = default) =>
        throw new NotSupportedException("The remote read-only suite never pins a host key; it compares against SERVYX_REMOTE_FINGERPRINT.");

    /// <inheritdoc />
    public Task RevokeAsync(string host, int port, string actor, CancellationToken ct = default) =>
        throw new NotSupportedException("The remote read-only suite never revokes a host key.");

    /// <inheritdoc />
    public Task<bool> IsRevokedAsync(string host, int port, CancellationToken ct = default) =>
        Task.FromResult(false);
}
