# Connectors

## `ITransport` vs `IConnector`

`ITransport` stays exactly as it is today. This document adds `IConnector` as a layer above it, because the two answer different questions and conflating them has already caused problems in similar systems: a transport is a stateless *kind* of pipe, a connector is a specific, user-configured *instance* of one.

| | `ITransport` | `IConnector` |
|---|---|---|
| Lifetime | Singleton, keyed by `TransportId` | Per-user-configured host, persisted |
| State | None | Endpoint, credentials, host-key trust, pooled sessions |
| Capabilities | Static (`TransportCapabilities`) | Observed, degradable (`ConnectorHealth`) |
| Identity example | `"docker"` | `"ssh:steam@10.0.0.4:22"` |

Folding credentials into `ITransport` would make `Capabilities` instance-varying — the whole point of `TransportCapabilities` being static and shared is lost the moment two different credentialed sessions against the same transport kind can answer the capability question differently — and it would drag secret resolution into what is supposed to be a stateless singleton, which is a lifetime mismatch waiting to leak a credential into a cache keyed by the wrong thing.

```csharp
[Flags]
public enum ConnectorChannel
{
    None            = 0,
    Exec            = 1 << 0,
    FileRead        = 1 << 1,
    FileWrite       = 1 << 2,
    DirectoryList   = 1 << 3,
    DockerApi       = 1 << 4,
    ProcessApi      = 1 << 5,
    PortForward     = 1 << 6,
    Stdin           = 1 << 7,
}

public sealed record ConnectorDescriptor(
    string ConnectorId,
    string Kind,
    string DisplayName,
    string TransportId,
    string Endpoint,
    IReadOnlyList<string> CredentialRefs,
    TrustPolicy Trust,
    TimeoutPolicy Timeouts,
    ConnectorChannel DeclaredChannels);

public interface IConnector
{
    ConnectorDescriptor Descriptor { get; }
    ConnectorChannel AvailableChannels { get; }   // observed, always a subset of DeclaredChannels

    Task<ConnectorHealth> CheckAsync(CancellationToken ct);
    Task<IConnectorSession> OpenAsync(CancellationToken ct);
    Task<string> ResolveIdentityAsync(CancellationToken ct);
}

public sealed record ConnectorHealth(
    bool Reachable,
    ConnectorChannel Working,
    ConnectorChannel Degraded,
    IReadOnlyList<string> Issues,
    TimeSpan Latency,
    DateTimeOffset CheckedAt);
```

`AvailableChannels` is what was actually observed working, and it is always a subset of `DeclaredChannels` — a descriptor can declare `FileRead | FileWrite` because that's what the connector kind normally supports, while a specific instance's `AvailableChannels` comes back missing `FileWrite` because this particular host has the sftp subsystem disabled.

**`Degraded` is the partial-availability answer**, and it is the field that makes `ConnectorHealth` worth having instead of a bool. Consider an SSH connector where exec works fine but the sftp subsystem is disabled on the remote `sshd`: `Working = Exec | ProcessApi`, `Degraded = FileRead | FileWrite`, with an issue entry naming `sshd_config` as the place to look. This must be sharply distinguished from a `ControlCapability` denial (see `docs/control-plane.md`): "can I reach the host and talk to it at all" and "may I write this specific file" are different questions, fail for different reasons, and are fixed by different actors. Collapsing them produces the classic bad-panel behavior of blaming the network for what is actually a permissions problem three layers up the stack, or vice versa — telling a user to check their SSH config when the real problem is a chmod on one directory.

## SSH and SFTP are independent

SSH exec and SFTP file access are declared as two separate connector kinds, not one connector that assumes both. They compose:

```csharp
public interface ICompositeConnector : IConnector
{
    IConnector ExecTarget { get; }
    IConnector FileTarget { get; }
}

public interface ICompositeExecutionTarget
{
    // exec routed to ExecTarget, file operations routed to FileTarget
}
```

`CompositeConnector` / `CompositeExecutionTarget` route exec operations to one underlying connector and file operations to another, which may or may not be the same physical connection. This composition is what makes the following four real-world configurations representable without special-casing any of them:

1. **SFTP-only** — shared game hosting with no shell access at all. Config editing works (rung 1 of the write ladder), but there is no lifecycle control: no start/stop, no exec, no live control channel. This is a genuinely common and genuinely useful configuration, and it is currently unrepresentable in a model that assumes exec and file access travel together.
2. **SSH exec-only** — the sftp subsystem is disabled on the remote host, but a shell is available. File operations are synthesized over exec: `cat > path` for writes, `base64 -w0 path` for reads (to survive the shell's text-mode assumptions), `stat -c '%f %u %g %s %Y'` for metadata. This path is capped at 8 MiB per file, refuses writes to any file it detects as binary (synthesizing a binary-safe write over a text shell channel is not attempted), and reports `FileWrite` at `CapabilityConfidence.Inferred` rather than `Verified`, with evidence explicitly naming the exec-based fallback so the user understands why this file-write path behaves differently — slower, size-capped — from a native SFTP write.
3. **Docker over SSH** — this is the configuration that most strongly argues for the composite model, because of a hard structural fact: **the Docker API cannot read `compose.yaml` or `.env` — they live on the host filesystem, not inside the API surface Docker exposes.** Rungs 2 and 3 of the write ladder (`.env`, `compose.yaml`) are simply unreachable through the Docker API alone, no matter how much access that API grants. A "Docker over SSH" connector template must configure a parallel SFTP channel to reach those files, or it must accept — explicitly, visibly — a hard capability ceiling at whatever the Docker API and RCON can do without them.
4. **Local + local Docker** — the degenerate case, everything on one machine, no composition needed but the same interface used.

Future connectors slot into this model without changing it:

| Connector | Channels | Notes |
|---|---|---|
| Kubernetes | `Exec \| PortForward` | No `FileWrite` — ConfigMaps are a distinct surface kind, not a filesystem write |
| Proxmox | varies by guest | — |
| FTP | `FileRead \| FileWrite` | No atomic rename primitive — forces the write ladder's SFTP fallback (truncate durability) plus a loud warning |
| SMB | `FileRead \| FileWrite` | No ownership preservation — rung 1's "apply original owner" step cannot be satisfied |

## Discovery must stop being Docker-shaped

The current discovery interface is:

```csharp
Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(
    string imageRepository,
    string requiredMountContainerPath,
    CancellationToken ct);
```

Both parameters are Docker-specific, and the signature has no way to express "find a Palworld install under `/srv` on this SSH host" — there is no image repository and no container mount path on a bare-metal box. This needs replacing with a spec hierarchy that describes *what a match looks like* independent of the transport doing the looking:

```csharp
public abstract record DetectSpec
{
    public sealed record DockerImage(string Repository) : DetectSpec;
    public sealed record ProcessName(string Pattern) : DetectSpec;
    public sealed record FilesystemMarker(string RelativePath, string? ContentPattern) : DetectSpec;
    public sealed record SystemdUnit(string UnitPattern) : DetectSpec;
    public sealed record ListeningPort(int Port, string? Protocol) : DetectSpec;
}

public sealed record DiscoveryLimits(
    int MaxDepth,
    int MaxEntries,
    TimeSpan Budget);

public interface IDiscoveryStrategy
{
    string DiscoveryId { get; }
    bool CanHandle(DetectSpec spec, TransportCapabilities transportCapabilities);
    Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(
        DetectSpec spec, DiscoveryLimits limits, CancellationToken ct);
}
```

Each strategy declares what kind of spec it can act on and what transport it needs (`CanHandle`); the discovery pipeline dispatches specs to whichever strategies can handle them for the connector at hand, rather than assuming Docker is the only shape a "find my server" query can take.

`DiscoveredServer` needs new fields to match: deployment kind, resolved data and compose directories, the `DetectSpec` that matched, and — critically — **match evidence**. The wizard shows the user *why* a candidate was proposed ("found `PalWorldSettings.ini` under `/srv/palworld/config`, port 8211 listening"), not just that it was. Adopting the wrong container or the wrong directory is the most damaging mistake this product can make on a user's behalf — it can point Servyx's write path at the wrong install — so discovery owing the user a legible reason for every candidate is not a nice-to-have.

`DiscoveryLimits` is load-bearing, not garnish. `FilesystemMarker` discovery over SFTP is a recursive *remote* directory walk — every `stat` and every directory listing is a network round trip, and an unbounded walk starting from `/` over a 60ms link is a self-inflicted denial-of-service against the user's own host, not a hypothetical edge case. `MaxDepth`, `MaxEntries`, and `Budget` bound that walk unconditionally, and the walk itself must skip `/proc`, `/sys`, `/dev`, and refuse to cross a filesystem boundary — all three of which are exactly the kind of directory a naive recursive walk wanders into and never returns from.

## Secrets

```
secret://{scope}/{scopeId}/{category}/{name}
```

`SecretUrn` is the only thing a descriptor ever holds for a credential. Resolution to an actual value happens nowhere except inside the connector implementation that needs it, at the moment it needs it:

```csharp
public interface ISecretStore
{
    Task<SecretLease> ResolveAsync(SecretUrn urn, CancellationToken ct);
    Task SetAsync(SecretUrn urn, ReadOnlySpan<byte> value, string actor, CancellationToken ct);
    Task DeleteAsync(SecretUrn urn, string actor, CancellationToken ct);
}

public sealed class SecretLease : IDisposable
{
    // wraps the resolved bytes; Dispose() zeroes them
}
```

`SecretLease` wraps bytes and zeroes them on dispose — **deliberately not a string**, because a .NET string is immutable and interned in ways that make guaranteed zeroing impossible; a secret that was ever materialized as a `string` cannot be reliably scrubbed from memory.

Rules, without exception:

- Secrets are never a `string` anywhere in a domain model. A field that holds a credential is a `SecretUrn`.
- Descriptors hold URNs only. Only the connector implementation that actually opens the session resolves the URN to a value, and it does so as late as possible.
- `ToString()` on any type that holds a secret returns the URN, never the resolved value — this is what stops a credential from leaking into a log line via an innocent interpolation.
- `SetAsync` and `DeleteAsync` both take an `actor` parameter, because a secret write or deletion is an audit event, not a silent state change.

The default `ISecretStore` implementation is ASP.NET Core Data Protection backed by a file-backed key ring — adequate for a self-hosted single-box deployment, and replaceable later (a real KMS-backed store, for instance) without touching anything above the interface.

## Host key trust

```csharp
public enum HostKeyVerdict
{
    Trusted,
    Unknown,
    Changed,
    Revoked,
}

public sealed record HostKeyRecord(
    string Host,
    string Algorithm,
    string FingerprintSha256,
    DateTimeOffset FirstSeen,
    DateTimeOffset? PinnedAt);

public sealed record TrustPolicy(
    bool RequirePinned,           // default for automation
    bool TrustOnFirstUse,         // human confirms fingerprint, then it is pinned
    IReadOnlyList<string> PinnedFingerprints);
```

State this plainly, because it is a deliberate constraint on the type system, not an oversight: **there is no `AcceptAny` member on `HostKeyVerdict` or `TrustPolicy`, and there is no bypass flag anywhere in this model.** A previous project reached `StrictHostKeyChecking=no` in production not because anyone decided that was acceptable, but because the type system allowed a config value to express it. The fix here is to make that state unrepresentable — there is no boolean or enum value in this model that means "skip verification" — rather than to add the value and write a comment asking people not to set it.

`RequirePinned` is the default posture for anything automated: a connector with no pinned fingerprint and `RequirePinned = true` simply cannot connect. `TrustOnFirstUse` is the human-in-the-loop path: a person is shown the SHA-256 fingerprint, confirms it out of band (or accepts the risk knowingly), and it is pinned from that point forward — this is a one-time interactive decision, not a standing policy that silently trusts every new host.

`Changed` — the host presented a fingerprint that doesn't match what was pinned — refuses the connection outright, evicts the connector from the pool immediately, and requires an explicit re-pin action that records the actor and both the old and new fingerprints in the audit log. There is no auto-heal path and no "trust this key" checkbox bundled into the error dialog that reports the mismatch; re-pinning is a deliberate, separately-initiated action, specifically so that a user under pressure to "just make the error go away" cannot do so by reflex.

## Pooling

```csharp
public sealed record ConnectorKey(
    string Kind,
    string EndpointKey,
    string CredentialKey,
    string TrustKey);

public interface IConnectorPool
{
    Task<IConnectorLease> LeaseAsync(ConnectorKey key, CancellationToken ct);
}

public interface IConnectorLease : IAsyncDisposable
{
    IConnector Connector { get; }
}

public sealed record TimeoutPolicy(
    TimeSpan Connect,              // 10s
    TimeSpan Command,              // 30s
    TimeSpan FileTransfer,         // 10m
    TimeSpan IdleEviction,         // 5m
    int MaxConcurrentSessions);    // 4
```

One pooled SSH connection is maintained per `ConnectorKey`, multiplexing multiple logical channels (exec, sftp, port-forward) over that single connection rather than opening a new TCP/SSH handshake per operation. Long-lived consumers — log streaming, a metrics poll loop — hold a lease for their entire lifetime and are exempt from `IdleEviction`, since "idle" from the pool's perspective would otherwise incorrectly describe a connection that is quietly streaming log lines every few seconds.

`CredentialKey` is a **hash of the resolved credential URN(s), never the secret value itself** — the pool key must be computable without holding a live `SecretLease` open just to key a dictionary. This has a useful side effect beyond avoiding secret exposure in a cache key: rotating a credential naturally produces a new `ConnectorKey` and therefore a new pool entry, rather than silently reusing a session that was authenticated under the credential that just got rotated out. A credential rotation and a connection rotation happen together, by construction, instead of needing to be coordinated by hand.
