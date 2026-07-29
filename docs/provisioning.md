# Provisioning Architecture

**Status: Proposal — not yet accepted.** This document describes a design for extending Servyx from adoption-only to create-and-maintain. It is a starting point for discussion, not shipped design. Nothing here has been implemented.

## 1. The central abstraction

`ITransport` answers *"how do I reach and act on a thing that exists."* Provisioning answers *"how do I bring a thing into existence."* These are two interfaces. Provisioning is not a peer of transport — it runs upstream of it, and its output is the input to it.

A provisioner's job is finished when it can hand back a `TargetDescriptor` (plus the `ConnectorDescriptor` that owns it). From that moment the existing machinery takes over unchanged. That single rule is the core of this architecture.

The second structural claim: Servyx already has an execution engine, and provisioning must not introduce a second one. `IPlanExecutor`, `ConfigChangePlan`, `PlanStep`, `PlanFeasibility`, `CapabilityFingerprint`, `ChangeReceipt` and `JobProgress` are existing code. Provisioning is therefore new step kinds and new capability requirements inside the existing plan model, plus one new contract that produces those plans.

```csharp
public interface IProvisioner
{
    string ProvisionerId { get; }
    ProvisioningCapabilities Capabilities { get; }

    bool CanHandle(DeploymentProfile profile, ConnectorDescriptor? host);

    Task<ProvisioningPlan> PlanAsync(ProvisioningRequest request, CancellationToken ct);
    Task<ProvisionedResource?> RefreshAsync(ResourceHandle handle, CancellationToken ct);
    Task<IReadOnlyList<ResourceHandle>> ReconcileAsync(OrphanScope scope, CancellationToken ct);
}

public sealed record ProvisionedResource(
    ResourceHandle Handle,
    ConnectorDescriptor Connector,   // how Servyx reaches it from now on
    TargetDescriptor Target,         // handed to ITransport unchanged
    DetectSpec Identity,             // inverse discovery: what to look for later
    ResourceFacts Facts);

public sealed record ResourceHandle(
    string ProvisionerId, string ProviderResourceId,
    string? Region, IReadOnlyDictionary<string, string> Tags);

public sealed record ResourceFacts(
    string? PublicAddress, string? PrivateAddress,
    CostEstimate Cost, DateTimeOffset CreatedAt);

[Flags]
public enum ProvisioningCapabilities
{
    None = 0, Create = 1, Destroy = 2, Resize = 4, Snapshot = 8,
    StaticAddress = 16, FirewallRules = 32, EstimatesCost = 64,
    TagQuery = 128,   // load-bearing: without it, orphans are unfindable
}
```

Note what `IProvisioner` deliberately lacks: an `ApplyAsync`. Application goes through `IPlanExecutor`. This keeps *"no code beneath `IPlanExecutor` throws for a capability reason"* enforceable — a provisioner that cannot do something returns a `BlockedChange` in its plan, never an exception, and there is no override because there is no second execution path. The "no force flag, anywhere" non-goal therefore survives contact with provisioning by construction rather than by discipline.

`ProvisionedResource.Identity` closes the loop on `adoptionMode: Provisioned`. Every `DetectSpec` variant today is retrospective — it describes finding an existing workload. A provisioner emits the `DetectSpec` that will later find what it just created. A provisioned server is one whose `DetectSpec` Servyx authored rather than inferred; it then flows through the same discovery path as any adopted server. Adoption and provisioning converge one step after creation.

### Where the existing seams must widen

| Seam | Today | Required change |
|---|---|---|
| `Provision` tier | Docker-recreate-shaped; presumes a compose file | Narrow to `Every(Operate, All(CreateWorkload))`. `WriteComposeFile` is a Docker requirement belonging on `PlanStep.Requires`, not the tier — a droplet+systemd deployment has no compose file and must not be locked out. |
| Capability evaluation subject | per-**server** | Widen to `Server \| Host \| ProviderAccount`. Before creation there is no server to evaluate. |
| Capability confidence | probe-based | Pre-creation, `CreateWorkload` is at best `Inferred` — you cannot probe a thing that doesn't exist. "Unknown is not Denied" still holds; the normal probe upgrades to `Verified` post-creation. |
| `DestroyWorkload` | defined, unused, in no tier | Keep it out of every tier. Destroy is per-operation confirmed, never granted by tier membership. |
| `PlanStep` | `SurfaceId` presumes an existing surface | Make `SurfaceId` nullable; add `Produces`. A provisioning step's output *is* the surface. Add `ResourceEffect { None, Creates, Destroys, Resizes }` and a `Billable` flag. |
| `IServerDiscovery` | Docker-shaped signature | Land the `IDiscoveryStrategy` + `DetectSpec` refactor already spec'd in `connectors.md` first. |

`ProvisioningCapabilities` is separate from both `TransportCapabilities` and `ControlCapability` because it answers a third question: "what can this adapter do to *infrastructure*." `TransportCapabilities` is read on execution hot paths, and `BillsHourly` is a category error there. Non-capability facts such as `has-public-ip` belong in `ResourceFacts` as data.

## 2. The target taxonomy — three shapes

| Shape | What the adapter does | Targets | Produces | Reached by |
|---|---|---|---|---|
| **H — Host-executed install** | Given an `IExecutionTarget` on a host, install and start the game — as a container or as a process | local Docker, remote Docker (TCP+TLS), Docker over SSH, local process, remote SSH bare-metal | a **server** | `DockerTransport` / `SshTransport` / local |
| **M — Managed container service** | Hand a container spec to a cloud API, poll an LRO | Azure ACI, Azure Container Apps, AWS ECS/Fargate | a **server**, capability-degraded | cloud API shim |
| **I — Machine provisioner** | Create a VM, wait for boot, inject SSH key, return an address | Azure VM, DigitalOcean Droplet, AWS EC2, AWS Lightsail | a **host** | feeds Shape H |

### Shape I produces a host, not a game server

This is the key structural point. A cloud deployment is a two-stage plan: Shape I creates the machine and registers a `ConnectorDescriptor`; Shape H then runs against that connector identically to any bare-metal SSH box. No cloud adapter contains install logic. Shape I is not a peer of H and M — it is a prefix stage.

```csharp
public sealed record ProvisioningPlan(
    Guid PlanId, string PlanHash,
    IReadOnlyList<ProvisioningStage> Stages,   // [ MachineStage(DigitalOcean), InstallStage(Steam) ]
    CostEstimate EstimatedCost,
    PlanFeasibility Feasibility,
    IReadOnlyList<BlockedChange> Blocked,
    CapabilityFingerprint Fingerprint,
    DateTimeOffset ExpiresAt);
```

`ICompositeConnector` is the primitive this needs. A provisioned VM has precisely the shape the existing docs describe for "Docker over SSH": SSH-exec and SFTP as independent connectors behind one composite, `ExecTarget` and `FileTarget` split. The documented constraint applies directly — the Docker API cannot read `compose.yaml` or `.env`, because they live on the host filesystem rather than in the API surface Docker exposes. A provisioned cloud VM running Docker must therefore be composite, or it hits a capability ceiling and can never reach `Provision` tier. Shape I hands back a composite descriptor, not a bare one.

### Shape H's five targets are one adapter

Local, remote-TCP and over-SSH Docker differ only in which transport resolves the descriptor — work `ITransport` already does. Container-versus-process differ only in installer strategy. Shape H is therefore one provisioner parameterised twice:

```csharp
public interface IInstaller
{
    string InstallerId { get; }                  // "container" | "steamcmd" | "archive"
    Task<IReadOnlyList<PlanStep>> PlanAsync(GameDeployment d, IExecutionTarget t, CancellationToken ct);
}
```

Shape H needs zero new SDKs.

### Shape I's four clouds differ trivially

```csharp
public sealed record MachineSpec(
    string ImageRef, string SizeRef, string Region,
    string SshPublicKey, string? CloudInit,
    IReadOnlyList<FirewallRule> Ingress,
    IReadOnlyDictionary<string,string> Tags);
```

Eleven targets reduce to three shapes: one nearly free, one mechanical per cloud, one genuinely different and belonging last.

## 3. Definition schema

Extend the native schema. Do not adopt Pterodactyl Eggs — `schema.md` already rejects that with four sound reasons, and the Palworld case (~150 `.env` vars rendered into one `OptionSettings=(...)` blob) is unanswerable in the Egg model.

The existing `capabilities:` block is already most of a provisioning spec, which is the decisive argument for extending rather than paralleling. Ports with `protocol` and `published` map to a security group, NSG or droplet firewall. `filesystem[].access` maps to volume creation and mount mode. `egress` maps to an egress allowlist. `privileged` and `hostNetwork` are exactly the flags indicating Shape M cannot host something. Written for adoption-verification, it reads forward as "create these rules."

It falls short on: no CPU/memory/disk sizing (needed for every I and M target — add a `sizing:` block, advisory for H, binding for I and M); no region; no image-build spec; no OS/arch constraint. Separately, `egress: []` is dangerously ambiguous — "none" or "unspecified"? A provisioner reading "unspecified" as deny-all will silently break servers. This should be made tri-state before any adapter consumes it.

Provisioning steps extend the allowlisted typed verb list, never shell — the established stance, and more important once verbs spend money. Combinatorial explosion is avoided by keeping placement out of the definition: a profile declares `requires`, a connector declares what it offers, and the planner intersects the two at deploy time. Definitions and targets never multiply.

```yaml
deployments:
  - id: native-linux
    installer: steamcmd
    requires: { transport: [ssh, local], os: linux }
    provision:
      - { verb: ensure-machine, sizeClass: small, ports: from-capabilities }
      - { verb: ensure-dir,     path: "${DATA_DIR}", mode: "0750" }
      - { verb: steamcmd,       appId: 2394010, validate: true }
      - { verb: ensure-service, kind: systemd, user: palworld }
```

`ports: from-capabilities` is the point — the firewall derives from the `capabilities:` block and cannot drift from what the game actually needs.

## 4. Persistence

A new `src/Infrastructure/Servyx.Infrastructure.Persistence` project with `ServyxDbContext`, an `AddServyxPersistence()` extension, and co-located migrations. SQLite with WAL by default, PostgreSQL by connection string — therefore no provider-specific SQL, no SQLite-only defaults, UTC everywhere. The domain stays pure: attribute-free POCOs in `Servyx.Domain`, `IEntityTypeConfiguration<T>` in Persistence.

Persist the entity list already specified in `architecture.md` (`Operation`/`Job`, `ChangePlan`, `ChangeReceipt`, `ConfigSnapshot`, `AuditEvent`, `Host`, `Server`, `Secret`), including the `AuditEvent` hash chain.

Net-new entities: `ProviderAccount` (provider id, default region, credential URNs, scope hint — the subject of provider-scoped capability grants); `ResourceHandle` (provider resource id, region, tags, and `State = Intended | Created | Destroying | Destroyed` — the orphan ledger); `CostSnapshot` (estimated at plan time, observed at refresh, with confidence). `Host` gains `ProvisionedByJobId?` and `ResourceHandle?`.

Secrets never enter the database — only credential URNs, resolved through the existing secret store. Landing this also fixes the connector-pool factory that currently throws, which exists precisely because there is no connector registry.

## 5. Job engine

Extend the existing `JobProgress` and `IPlanExecutor`; do not introduce a competing progress or execution abstraction. Add a database-backed queue and an in-process `JobRunner : BackgroundService` using row leases — no Hangfire, no Quartz, with single-instance documented as an assumption.

Intent-before-effect is the critical discipline: before any billable create, commit a `ResourceHandle` with `State = Intended` *and the tags about to be applied*, then call the API, then update to `Created`. A crash between the call and the record leaves an `Intended` row for the sweep to find.

Universal tagging: every cloud resource carries `servyx:instance-id`, `servyx:job-id` and `servyx:managed=true`. The orphan sweep is list-by-tag minus database, run on startup and on a timer. This must be enforced in a base class that refuses to issue a create without tags, covered by a test. Ship it with the first cloud adapter — never retrofit it. A half-created VM that leaks money is the worst failure mode this system has, and it is unrecoverable without tags.

Compensation is a per-step `CompensateAsync` run in reverse. Restart mid-deploy means the lease expires, the runner reclaims the job, and replays from the last completed step by idempotency key. Cancellation is both a token and a persisted flag, so a cancel issued before a restart still lands.

Drift is already solved: `CapabilityFingerprint` and `surfaceHashes` captured at preview, re-checked at apply, `PlanStaleException`, `ChangePlan.status = Stale`. Provisioning inherits this, and completion emits a `ChangeReceipt`.

For progress reporting, the database is truth and the in-memory channel is liveness: read `JobStep` rows on render, then subscribe, so a circuit drop and reconnect re-reads and then re-subscribes with no gap. No SignalR hub of our own is needed — the Blazor Server circuit already is one. No HTTP API is needed either, beyond one read-only `GET /api/jobs/{id}` for future CLI use.

## 6. Safety

The plan/preview/confirm flow is a `ChangePlan` variant, not a new mechanism: `Previewed → Applied`, with `PlanFeasibility` of `FullyAchievable | PartiallyAchievable | Blocked`, and anything an adapter cannot do surfacing as a `BlockedChange` with a remediation hint.

No force flag — that non-goal is absolute and provisioning gets no exception. Likewise no `AcceptAny` host-key path: a freshly created VM's host key is captured at creation from the provider API or console output and pinned, which is stronger evidence than trust-on-first-use, not a reason to weaken the model.

Cost is disclosed via `CostEstimate(decimal? Hourly, decimal? Monthly, string Currency, CostConfidence Confidence, string Source)` with `CostConfidence { Exact, ListPrice, Estimated, Unknown }`. `Unknown` renders as "unknown," never as a fabricated number. A standing dashboard panel lists every resource currently costing money.

Destroys enumerate exactly what will be deleted, including volumes and disks, default to `PreserveData = true`, and require typed confirmation of the server name. `DestroyWorkload` is never granted by tier.

Credentials layer onto the existing secret store per `ProviderAccount`: Azure service principal or `DefaultAzureCredential`, AWS key pair / assume-role / ambient chain, DigitalOcean PAT — each with a stored scope hint so the UI can warn when a token carries account-wide delete rights.

## 7. Phased delivery

### Prerequisites

0. **CI.** No workflows exist at all. Gate builds and tests before adding this much surface.
1. **Persistence and the connector registry.** Nothing can own a created resource without it, and the throwing connector-pool factory is a live blocker today.
2. **The `IDiscoveryStrategy` / `DetectSpec` refactor** already spec'd in `connectors.md`. Provisioning emits `DetectSpec`s; building against the Docker-shaped signature would fork discovery. Cheap now, expensive later.
3. **Wire the control-tier evaluator into the dashboard.** It is real code currently bypassed by a simpler uniform gate. Provisioning is the most destructive thing the app does; shipping it through a gate that isn't connected is not acceptable. Includes widening the evaluation subject to `Host` and `ProviderAccount`.

Not required first: mods, backups, RCON, or full observation breadth. Provisioning is orthogonal to those.

### First vertical slice: local Docker + Palworld

- A `ContainerInstaller` under Shape H, driven by an extended `definitions/palworld-docker.yaml` — whose docker profile has no `install` block today, though the `native-steamcmd` profile does, giving a worked example of the verb format to follow. Authoring one is part of the slice.
- Emitted as `PlanStep`s, previewed as a `ChangePlan`, executed through `IPlanExecutor`.
- Gated by a real `ControlCapability` check for `CreateWorkload`.
- Producing `Host` and `Server` rows with `adoptionMode = Provisioned` and a Servyx-authored `DetectSpec`.
- Handed to the existing `DockerTransport`, then managed by the first real `IServerLifecycle` implementation.

This exercises every seam — plan, capability gate, persist, execute, compensate, emit receipt, hand off to transport, then rediscover what was created — at zero cloud cost with no new SDK.

### Subsequent order

4. **Remote Docker (TCP+TLS, then over SSH).** No new provisioning shape. Forces the missing TLS/client-cert configuration and the composite Docker+SFTP connector, which everything cloud depends on.
5. **Shape H, process variant — SSH bare-metal + steamcmd.** First non-container installer; first long, resumable, genuinely failure-prone install; first real load on the allowlisted verb list. Unlocks all of Shape I.
6. **First Shape I: DigitalOcean Droplet.** Deliberately first among clouds: simplest API, flat pricing so cost estimates are honest, no SDK to curate, and it proves the I-to-H composition.
7. **Azure VM, AWS EC2, Lightsail.** Mechanical repeats — one `MachineSpec` mapping and a price table each.
8. **Shape M (ACI, Container Apps, ECS/Fargate).** Last, possibly never. No persistent local disk without extra plumbing, awkward UDP exposure, always-on billing worse than a droplet, and limited shell access — so a large part of `IExecutionTarget` fails and such a deployment may never reach `Provision` tier.

### Plugin model

Keep cloud adapters in-tree for now. `Servyx.Plugins.Abstractions` is strict-semver with major-version mismatch refused at load; `ProvisioningCapabilities` will churn, and every churn is a major bump that breaks every third-party adapter. Move Shape I behind the plugin boundary once two or three adapters have shipped without changing the interface — that is the real stability signal. Ship them as separate optional projects so their SDK pins stay visible and auditable.

## 8. Risks and open questions

**Positioning.** The README currently promises Servyx *"never creates one on your behalf or assumes control it wasn't given,"* and roadmap Open Question 1 asks whether Servyx gets authority to *recreate* containers. Provisioning inverts that stated promise. That is a decision the project is entitled to make, but it should be made deliberately, and the README should be rewritten in the same commit that ships the first provisioner.

**Scope.** Build the taxonomy so eleven targets are possible; ship four — local Docker, remote Docker, SSH bare-metal, and DigitalOcean Droplet. Each further cloud adapter is a permanent subscription to another vendor's release cadence. Announce the shape, not the matrix; publish the Shape I interface so contributors can add clouds.

**Where this could be wrong, and how to find out cheaply:**

- *Shape M may not fit the model at all.* Two-day spike: run Palworld on ACI, then try `IExecutionTarget` against it. If it cannot reach `Operate`, Shape M is permanently degraded or dropped.
- *I-to-H composition may be leakier than claimed.* Cloud-init timing, host-key capture on a brand-new box, "machine is up but sshd isn't." Half-day spike: droplet, wait-for-ssh, run one command. Highest value per hour in this document.
- *Capability-subject widening may cascade.* The evaluator is real code with real tests; changing its subject from `Server` to a union may touch more than expected. Size this before committing to the sequence.
- *Orphan cleanup is only as good as tagging discipline.* One adapter that forgets tags creates unfindable leaks. Base-class enforcement plus a test mitigates; it remains a standing risk with a money-shaped blast radius.
- *`egress: []` ambiguity* will silently break servers the first time a provisioner reads it as deny-all. Resolve before any adapter consumes the block.
- *In-process job runner* couples provisioning liveness to the web process. Acceptable now; the database-backed queue makes extracting a worker mechanical later.
- *Blazor Server plus multi-minute jobs plus circuit drops.* Database-as-truth mitigates it; verify with the existing Playwright setup rather than assuming.

## 9. Defects found during implementation

Recorded here are defects discovered while building the first vertical slice against this proposal. Each entry states the observed behaviour, why it matters, and current status.

### 9.1 `TargetDescriptor` lacks the value equality its own doc claims

`src/Core/Servyx.Domain/Transport/TargetDescriptor.cs` documents: "Immutable; two descriptors with equal values are considered the same target." This is false. `Options` is typed `IReadOnlyDictionary<string, string>`, and the compiler-generated record `Equals` compares it **by reference** — two descriptors with identical contents are not equal. Anything that dedupes or pools on descriptor identity is currently incorrect.

**Status:** pinned by a passing regression test in `Servyx.Infrastructure.Docker.Tests` that documents current behaviour; not fixed, because changing equality semantics warrants a deliberate decision.

### 9.2 The M1 read-only guarantee is thinner than the roadmap describes

`docs/roadmap.md`'s M1 acceptance criteria present two negative tests as first-class: an architecture test asserting every transport is write-guarded, and a Docker API call-recorder asserting zero mutating Docker calls across the entire M1 test run. **Neither exists.** A repo-wide search of `tests/` for a call recorder or write-guard assertion returns no hits.

What actually enforces read-only is narrower: four BDD scenarios in `tests/Servyx.Bdd.Tests/ReadOnlySafetyTests.cs` asserting specific methods throw, plus `DockerTransportTests.Capabilities_does_not_advertise_unimplemented_write_or_exec_support`. `src/Infrastructure/Servyx.Infrastructure.Docker/ServiceCollectionExtensions.cs` carries a `// TODO(M4)` conceding the `WriteGuardedExecutionTarget` decorator does not exist yet.

The consequence: the guarantee cannot be tripped accidentally — there is no shared observer to trip — but nothing would catch a new component quietly issuing writes.

**Status:** reported, not fixed — building it is M4 scope.

### 9.3 Two E2E test projects are absent from the solution

`tests/Servyx.E2E.Tests` and `tests/Servyx.E2E.Bdd.Tests` exist on disk but are not registered in `Servyx.sln`, so a solution-wide build does not include them.

**Status:** pre-existing, reported, not fixed.

### 9.4 Remote Docker has no TLS path

`DockerClientConfiguration`'s optional `Credentials` parameter is never populated anywhere in `src/`, so every remote connection uses `AnonymousCredentials` and is plaintext regardless of an `https://` scheme or port 2376. Docker.DotNet chooses http vs. https from `Credentials.IsTlsCredentials()`, so the scheme alone does nothing.

Fixing it needs no new NuGet package — `Docker.DotNet.Credentials` is public and abstract in the already-referenced core assembly. Cert references belong on `ConnectorDescriptor.CredentialRefs` (a daemon's TLS identity is per-connector, not per-container), following the `secret://connector/{connectorId}/docker/...` shape that mirrors the existing `SshCredentialResolver` convention.

**Status:** now pinned by a test; reported, not fixed.

## 10. Validated architectural claims

Two claims made earlier in this proposal have since been validated against an implementation.

### 10.1 Section 1's central claim: a provisioner's job ends at the `TargetDescriptor`

Validated. The `TargetDescriptor` instance returned by `DockerContainerProvisioner` is passed as the same object into `DockerTransport.ProbeAsync`/`ConnectAsync` with no adapter, copy, or field fix-up.

Two caveats weaken this from "unchanged by construction" to "unchanged by convention":

- There is no shared constant for the transport id — `DockerTransport.TransportId` returns the literal `"docker"`, and a provisioner must reproduce it. This is currently pinned by a test asserting the provisioner's constant equals `DockerTransport.TransportId`, so drift fails a test rather than causing a runtime "no transport for id" failure.
- The descriptor's option keys (`"containerId"`, `"containerName"`, `"rootPath"`) are a stringly-typed convention documented only in `DockerTransport`'s XML remarks, enforced by no shared constant or type.

Both are worth hardening.

### 10.2 Section 2's claim: remote Docker is a second target with zero new provisioner code

Validated. `DockerContainerProvisioner` never inspects the endpoint — it stores it and stamps it onto the descriptor. Proven by asserting identical `PlanHash` and identical `CreateContainerParameters` between a `npipe://` and a `tcp://` provisioner, with a whole-object comparison pinning `Target.Endpoint` as the only differing field.

This supports the proposal's claim that Shape H's Docker variants are one adapter, not several.
