# Provisioning Architecture

**Status: Proposal — not yet accepted.** This document describes a design for extending Servyx from adoption-only to create-and-maintain. It is a starting point for discussion, not shipped design. Nothing here has been implemented.

## 1. The central abstraction

`ITransport` answers *"how do I reach and act on a thing that exists."* Provisioning answers *"how do I bring a thing into existence."* These are two interfaces. Provisioning is not a peer of transport — it runs upstream of it, and its output is the input to it.

A provisioner's job is finished when it can hand back a `TargetDescriptor` (plus the `ConnectorDescriptor` that owns it). From that moment the existing machinery takes over unchanged. That single rule is the core of this architecture.

The second structural claim: Servyx already has an execution *contract*, and provisioning must not introduce a second one. Be precise about what that means today, because the distinction is load-bearing and easy to misread:

| Type | Status in `src/` |
|---|---|
| `IPlanExecutor` | **Declared, never implemented.** `Servyx.Domain.Configuration`; zero implementations, zero callers. `PreviewAsync`/`ApplyAsync`/`RevertAsync` are signatures only. |
| `ConfigChangePlan`, `PlannedAction`, `PlannedActionKind`, `Consequence`, `ConsequenceKind`, `ChangeReceipt`, `PlanStaleException` | **Exist as declared types.** Nothing constructs or throws them in production code. |
| `ChangePlanRecord` / `ChangePlanActionRecord` | **Exist, with an applied EF migration.** Persistence only — no production code reads or writes these tables yet. |
| `JobProgress` | **Exists** (`Servyx.Domain.Common`), used by backups. |
| `PlanStep`, `PlanFeasibility`, `CapabilityFingerprint`, `JobStep`, `BlockedChange`, `RestartImpact` | **Do not exist anywhere in `src/` or `tests/`.** They are proposed here and in `control-plane.md`, and are named below as design vocabulary, not as code to call. |

So provisioning is not "new step kinds inside an existing plan model" in the sense of extending running code. It is new step kinds and new capability requirements inside an existing *model sketch*, plus one new contract that produces those plans — and it presumes someone first builds the executor that the sketch has always assumed. Every type in the last row of that table has to be written before anything in this document compiles.

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
| `PlanStep` (**does not exist yet**) | Nothing to widen — the type has never been written. `control-plane.md` sketches it with a non-nullable `SurfaceId`, which presumes an existing surface | Whoever writes it must make `SurfaceId` nullable and add `Produces`. A provisioning step's output *is* the surface. Add `ResourceEffect { None, Creates, Destroys, Resizes }` and a `Billable` flag. |
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

Extend `JobProgress` (which exists) and `IPlanExecutor` (which is declared but unimplemented — extending it here means building it, then keeping provisioning inside it); do not introduce a competing progress or execution abstraction. Add a database-backed queue and an in-process `JobRunner : BackgroundService` using row leases — no Hangfire, no Quartz, with single-instance documented as an assumption.

Intent-before-effect is the critical discipline: before any billable create, commit a `ResourceHandle` with `State = Intended` *and the tags about to be applied*, then call the API, then update to `Created`. A crash between the call and the record leaves an `Intended` row for the sweep to find.

Universal tagging: every cloud resource carries `servyx:instance-id`, `servyx:job-id` and `servyx:managed=true`. The orphan sweep is list-by-tag minus database, run on startup and on a timer. This must be enforced in a base class that refuses to issue a create without tags, covered by a test. Ship it with the first cloud adapter — never retrofit it. A half-created VM that leaks money is the worst failure mode this system has, and it is unrecoverable without tags.

Compensation is a per-step `CompensateAsync` run in reverse. Restart mid-deploy means the lease expires, the runner reclaims the job, and replays from the last completed step by idempotency key. Cancellation is both a token and a persisted flag, so a cancel issued before a restart still lands.

Drift is *designed*, not solved. The intended mechanism is `CapabilityFingerprint` and `surfaceHashes` captured at preview, re-checked at apply, raising `PlanStaleException` and moving the plan to `Stale`. Of that, `ConfigChangePlan.SurfaceHashes`, `PlanStaleException` and `ChangePlanStatus` exist as declared types; `CapabilityFingerprint` does not exist at all, and nothing re-checks anything at apply because no `IPlanExecutor` implementation exists to have an apply step. Provisioning inherits the design, and would emit a `ChangeReceipt` on completion once there is something to emit it.

For progress reporting, the database is truth and the in-memory channel is liveness: read job-step rows on render, then subscribe, so a circuit drop and reconnect re-reads and then re-subscribes with no gap. (The `JobStep` entity is proposed here; no such table or type exists today, and there is no job runner or durable queue in `src/` at all.) No SignalR hub of our own is needed — the Blazor Server circuit already is one. No HTTP API is needed either, beyond one read-only `GET /api/jobs/{id}` for future CLI use.

## 6. Safety

The plan/preview/confirm flow is a `ChangePlan` variant rather than a second mechanism — though "the mechanism" is at present a set of record definitions and a database table, not a working flow. The shape: `Previewed → Applied`, with `PlanFeasibility` of `FullyAchievable | PartiallyAchievable | Blocked`, and anything an adapter cannot do surfacing as a `BlockedChange` with a remediation hint. `PlanFeasibility` and `BlockedChange` are both proposed types; `RemediationHint` is the only one of the three that exists today.

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

## 11. Shape M: investigated, then implemented on a widened domain

An implementation attempt for Azure Container Instances (§2's Shape M, "managed container service") was carried out against the domain contracts as they existed at the time. It concluded the shape **could not be implemented honestly** as an `IProvisioner` / `IExecutionTarget` adapter, and nothing was built. §11.1–§11.7 below are that finding, kept verbatim so it is not re-derived. §11.8 named what an honest path would require; both halves of that requirement have now been taken up in part, and §11.10 records what was actually built and what remains true.

**Status: both of §11.8's items are done, and the two adapters end in different places.** `ProvisionedResource` expresses unreachability, two shape-M adapters exist on it — `AzureContainerInstanceProvisioner` and `AwsEcsFargateProvisioner` — and there is now an RCON control-plane path that consumes an unreachable resource. ACI reaches `Operate` through it when the container group carries a `dnsNameLabel`; Fargate does not reach it at all, for a reason that is a property of the shape rather than an implementation gap. See §11.10 for ACI, §11.11 for Fargate, and §11.12 for the control channel and the verdict per adapter.

### 11.1 The resolution that was tried, and why it looked plausible

§7's phased plan and §8's risk list both attribute Shape M's degradation to "no persistent local disk." That symptom is fixable: mounting an Azure Files share onto the container group gives it durable storage, and the fix genuinely works as far as storage goes. Six of the eight `IExecutionTarget` members — `ExistsAsync`, `ListDirectoryAsync`, `OpenReadAsync`, `DeleteAsync`, and largely `WriteFileAsync` — become implementable through the Azure Files REST data plane. This is what made the resolution look promising enough to pursue past the storage question.

### 11.2 Why it fails anyway: the exec members no storage configuration reaches

ACI's exec API (`POST .../containers/{c}/exec`) accepts a `command` string plus a `terminalSize`, and returns only a `webSocketUri` and a `password`. No mount, share, or storage account changes this surface. Consequently:

- `CommandResult.ExitCode` is non-nullable, and **no exit code exists anywhere in the ACI exec surface** — the socket simply closes when the process ends. Any value returned would be fabricated.
- `terminalSize` implies a TTY, and a TTY has one output stream, so `CommandResult.StandardError` and `OutputChunk.Stream` have no truthful value to report.
- ACI's `command` is a **single string** handed to the container's shell, whereas `CommandSpec` carries verbatim argv specifically as the primary defence against injection from definition authors. Honouring `CommandSpec` against this API would mean joining and re-quoting arguments into a shell string — a security regression, not merely a loss of fidelity.

Storage reachability and exec reachability are independent axes. Fixing the first does not move the second.

### 11.3 Two secondary blockers

- **Credential shape.** Mounting Azure Files on ACI requires a storage account key in the container group's ARM request body; managed identity is not supported for SMB mounts on ACI. This conflicts with the project rule that credentials are referenced by secret-store URN and never travel as literals.
- **CIFS semantics.** The mount is CIFS, which has no per-file POSIX ownership, so `FileStat`'s mode/uid/gid fields are structurally null and `PermitsWriteBy` would answer "not writable" for every non-Windows target regardless of the truth. Separately, `WriteFileAsync`'s temp-sibling-then-rename pattern is expressible via the Files REST rename operation in principle, but SMB refuses rename over a file that is open — precisely the case Servyx hits most often, rewriting a config while the game server holds it open.

### 11.4 The orphan consequence

A tag sweep against a Shape M deployment finds the container group but not the storage account or file share backing it. This differs in kind from the VM adapter's known blind spots (resource group, subnet, managed disk), which are either free or die with their tagged parent. The storage account is a **separate billable resource with an independent lifetime holding the customer's save data** — it must outlive the container group by design, so destroying the group leaves the storage account billing with nothing left able to attribute the charge to a resource.

### 11.5 The transport finding

No transport in the codebase can reach an ACI container group. `docker` needs an Engine endpoint, and ACI exposes no daemon. `ssh` needs sshd plus key injection, and ACI has no cloud-init equivalent. `local` is the Servyx host itself. §2's "reached by: cloud API shim" names a transport that does not exist in `src/` and, per §11.2, could not be written truthfully against ACI's exec API.

### 11.6 The deepest point

Shape M does not fail `IProvisioner`'s verbs; it fails its **return type**. `ProvisionedResource.Target` is a non-nullable `TargetDescriptor`, documented as the transport target the rest of Servyx should use from that point on. With no truthful `TargetDescriptor` value available for a container group, the only options are fabricating a transport id — which fails at runtime as "no transport for id" — or throwing for a capability reason from `CreateOperation`, which `IProvisioner`'s own remarks (§1) forbid. Shape H terminates in a server reachable by docker or ssh; Shape I terminates in a host reachable by ssh; Shape M terminates in a workload reachable by nothing Servyx currently has a transport for, and the domain has no way to express that gap.

### 11.7 `Operate`-tier reachability

§7 item 8 and §8's risk list state that Shape M may never reach `Provision` tier; neither states that it can never reach `Operate` tier, so no correction to that existing text is needed. For the record, since it was an open question §8 flagged for a spike: `Operate` requires `AnyOf(ExecInWorkload, ControlChannelWrite)`, and RCON over the container group's public IP satisfies `ControlChannelWrite` — Servyx now has `Servyx.Infrastructure.Rcon`. Shape M is therefore reachable at `Operate` through a game-specific control channel, though never through the generic `IExecutionTarget` exec path described in §11.2. `Provision` remains unreachable regardless, since it requires `WriteComposeFile` and ACI has no compose file — consistent with §7 item 8 as written.

### 11.8 What an honest path would require

Not an `IProvisioner` adapter under the contracts as they stand. It would require, at minimum: (1) widening `ProvisionedResource.Target` to nullable, or introducing an explicit "provisioned but unreachable by transport" state, so a provisioner can decline to name a transport without fabricating one; and (2) reaching the workload through RCON as a control channel rather than through `IExecutionTarget`, accepting a permanent ceiling below `Provision`. That is a domain change plus a control-plane design, not an adapter, and is out of scope for the phased plan in §7 as written.

### 11.9 What carries over

Two pieces of the earlier analysis remain valid if Shape M is revisited: `ServyxTagKeys` needs no new encoding for ACI — it uses the same native ARM tags dictionary as the VM adapter. And any future cost estimate must read "COMPUTE ONLY" rather than "ALL-IN": ACI bills per-second on vCPU and memory, while the storage account and any gateway needed for a stable IP bill separately, and ACI's own documentation warns a container group's IP may change on restart.

### 11.10 What was built, and what is still true

§11.8's item (1) was taken, in its second form. `ProvisionedResource.Target` was **not** widened to nullable; it was replaced by a non-nullable `Reachability` of the closed hierarchy `ResourceReachability`, whose two cases are `ViaTransport(TargetDescriptor)` and `NoTransport(string reason)`. The choice between the two options was not about strength alone — it was that they cost the same. Under `Nullable=enable` plus `TreatWarningsAsErrors`, every existing `resource.Target.Endpoint` stops compiling either way (as `CS8602` under the nullable option), so the weaker shape bought no compatibility. What it would have cost is that the check becomes optional at every site: a single `!` silences it and leaves no trace in a diff. A null also cannot carry a reason, and "there is no target" and "here is why there will never be one for this provider" are different facts — the second is the one an operator needs on screen.

`ProvisionedResource` keeps a `TargetDescriptor`-taking constructor overload, so the six adapters that genuinely have a target are unchanged at their construction sites, and gains `RequireTarget()` (throws, for code that has already established reachability) and `TargetOrNull()` (for rendering). It deliberately exposes no property of type `TargetDescriptor` — that absence is pinned by a reflection test, because a property is exactly the shape that was removed.

`AzureContainerInstanceProvisioner` was then built on it, in `Servyx.Infrastructure.Azure`, reusing `AzureArmApiClient` and its OAuth2 exchange. The one change that file needed is a third entry in `ApiVersionFor` for `Microsoft.ContainerInstance`; ARM versions each resource provider independently and there is no default that works.

Four findings from §11.1–§11.9 survive unchanged and are now enforced rather than merely recorded:

- **The mount is mandatory and unrepresentable otherwise.** `AzureContainerGroupSpec` takes an `AzureFileShareMount` as a required constructor argument.
- **§11.3's credential conflict does not survive contact.** The rule is that a credential is *held* only as a `SecretUrn` and resolved at the point of use — not that it never appears on a wire; `AzureArmApiClient` already puts the resolved client secret into a token-request body on every exchange. The storage account key is resolved from `ISecretStore` inside `CreateAsync`, materialised once into the container group's ARM body, and reaches no tag, handle, plan, plan hash, ledger row or log.
- **§11.4's orphan consequence is unchanged and is not fixable from inside the adapter.** Servyx never creates the storage account, so it never tags it, so the tag sweep cannot see it. The container group carries `servyx.azure-storage-account` and `servyx.azure-file-share` as pointers, which means a sweep that finds the group can name the account it depends on — and means nothing at all once the group is destroyed.
- **§11.2's exec finding stands.** No `IExecutionTarget` was written and none can be. `Capabilities` is `Create | Destroy | TagQuery | EstimatesCost`; `StaticAddress` is absent specifically because ACI's IP may move on restart.

**§11.8's item (2) has since been taken — see §11.12.** At the time this section was written there was no RCON control-plane path that consumed an unreachable `ProvisionedResource`, and the `Operate`-tier claim in §11.7 was a design rather than a code path. It is now a code path, and ACI satisfies it. The adapter remains deliberately unregistered in the web composition (`ProvisionerWiringOptions` / `ProvisionerFormSchema`); that is now a wiring decision rather than a capability one.

### 11.11 The second shape-M adapter: AWS ECS/Fargate

`AwsEcsFargateProvisioner` was built in `Servyx.Infrastructure.Aws` on the same `ResourceReachability` widening, reusing `AwsSigV4` and `AwsRequestSigner` **completely unmodified** — ECS speaks AWS JSON 1.1 with an `X-Amz-Target` header exactly as Lightsail does, and the signer already covers every `x-amz-*` header, so a third service under the same SigV4 machinery needed no signing change at all. That is the strongest available evidence for `Servyx.Infrastructure.Aws.csproj`'s claim that the algorithm was never EC2-specific.

**A Servyx server maps to an ECS *service* at desired count 1, not to a standalone task.** `RunTask` launches one task, and when it stops — host retirement, platform-version rollout, OOM, crash — nothing brings it back; AWS retires Fargate infrastructure underneath running tasks as ordinary maintenance, so a standalone task is a server with a scheduled death rather than a server lacking a supervisor. The mapping also settles identity: a task ARN changes on every replacement, so a `ProviderResourceId` naming one would go stale with nothing having gone wrong. A service ARN does not move.

Six findings, of which two are improvements on ACI and four are not:

- **Persistent storage is mandatory and is enforced by the type, and the argument is *stronger* than ACI's.** `AwsFargateServiceSpec` takes an `EfsVolumeMount` as a required constructor argument (and a non-empty subnet list, since `awsvpc` has no default). Fargate's only durable option for a Linux task is EFS — ephemeral task storage dies with the task, and FSx for Windows is Windows-only. An ACI group loses its writable layer when Azure happens to restart it; an ECS service destroys its task's storage *every time it replaces the task*, which is the service's purpose.
- **§11.3's credential blocker does not exist here at all.** EFS is authorised by network reachability and IAM, not by an account key, so `EfsVolumeMount` carries no `SecretUrn`, resolves nothing from `ISecretStore`, and puts no credential in any request body. This adapter has no second credential.
- **What replaces it is quieter and therefore worse.** EFS needs a mount target in the task's availability zone and an inbound NFS (2049) rule, and Servyx creates, sees and validates none of that. When either is missing *every ECS call still succeeds* — the definition registers, the service is created, ECS reports `ACTIVE` — and the task fails afterwards. A credential mistake is loud; this is not. The create path therefore confirms by reading a task's own `lastStatus` and surfaces `DescribeTasks`'s `stoppedReason` in the failure.
- **§11.4's orphan consequence is unchanged, and is joined by three narrower blind spots.** The sweep lists Fargate services in the configured cluster and keeps the `servyx.managed=true` ones, tags asked for explicitly. It cannot find: services in **any other cluster** (ECS has no cross-cluster listing and no tag filter on `ListServices`; the Resource Groups Tagging API would, and this adapter deliberately does not call it); **task definition revisions**, which every provision adds and which nothing ever deletes — `DeregisterTaskDefinition` only marks a revision `INACTIVE` — but which are **free** and hold nothing, making this the mildest orphan class in the codebase; the **cluster**, also free; and the **EFS file system and access point**, which is §11.4 exactly — separately billed, holds the save data, must outlive the service, never tagged because never created. `servyx.aws-efs-file-system` on the service is the same bounded mitigation `servyx.azure-storage-account` is, and dies the same way.
- **§11.2's exec finding stands, reached independently against a different provider.** ECS Exec returns an AWS Systems Manager session — `streamUrl`, `tokenValue`, `sessionId` — whose WebSocket carries SSM's binary framing rather than a command protocol, reports no exit code, multiplexes one PTY, and requires the SSM agent in the operator's image. `Capabilities` is `Create | Destroy | TagQuery | EstimatesCost`.
- **Addressing is materially worse than ACI's, and is stated as its own plan stage.** ACI's IP may change on restart and a `dnsNameLabel` survives that. A Fargate task's address belongs to its ENI and changes on every replacement, and `DescribeTasks` reports no public address at all — obtaining one means `ec2:DescribeNetworkInterfaces`, a different service this adapter does not call. `PublicAddress` is therefore always `null` and `PrivateAddress` carries the current task's private IPv4. `StaticAddress` is absent, more emphatically than for ACI.

Two things this adapter does that ACI's does not, both consequences of "submission is not success" applied to a provider that is asynchronous at both ends. `CreateService` answers `200 OK` with the service already `ACTIVE` and its running count zero, so a create is not complete until a task reports `RUNNING`. `DeleteService` answers `200 OK` with the service `DRAINING` and its task still running, so `DestroyAsync` polls until `INACTIVE` and **raises rather than returning `true`** if it never gets there.

**Cost is COMPUTE ONLY and is explicitly NOT ALL-IN**, and by a wider margin than ACI's: excluded are EFS storage and throughput, CloudWatch Logs ingestion (the only way a Fargate task's output is readable at all), the hourly public IPv4 charge, a NAT gateway if the task sits in a private subnet, and any load balancer or Cloud Map registration added to obtain a stable address. `AwsLightsailPricing`'s figure genuinely is all-in; these two must never be shown side by side without that being said.

One thing is better than every other adapter's plan: Fargate's CPU/memory matrix is discrete, so `AwsFargateSizing` validates the pair at **plan time**. `PlanAsync` issues no HTTP request, so without it a plan built from an impossible reservation would look fine on screen and die at `RegisterTaskDefinition` — the same argument `ServyxEc2Tags.Validate` makes for tags, applied to the other field that can make a deployment unbuildable.

**Like ACI, it is deliberately not registered in the web composition.** The reason is unchanged and, for Fargate, sharper: a target that can be billed for but not operated should not be offered, and this one additionally has no stable address to point a future RCON control channel at.

### 11.12 The control channel: §11.8's item (2), and what each adapter can actually reach

§11.8's item (1) made "provisioned but unreachable by any transport" expressible, and two shape-M adapters were built on it. Neither was **operable**, because nothing in the codebase consumed an unreachable resource: the workload was reachable by nothing at all. Item (2) is now taken.

**The shape it took.** Two additions, deliberately separate from the transport machinery.

`ControlChannelAddress` (`Servyx.Domain.Provisioning`) is a closed hierarchy with three cases — `Durable(host, justification)`, `Ephemeral(host, reason)`, `NoAddress(reason)` — answering a narrower question than `ResourceReachability`: *is there a host a control channel could connect to, and will it still be the right one tomorrow?* `IControlChannelAddressSource` is the optional second interface a provisioner implements to answer it. `RconControlChannel` (`Servyx.Infrastructure.Rcon`) is the consumer: it resolves the address, and on a `Durable` one composes `RconSession` inside `WriteGuardedRconSession` and hands back the guarded session.

**Why `Ephemeral` is a case rather than a flag, and why it is refused.** Both shape-M adapters can, at any moment, name an address a socket would connect to. Both of those addresses stop being correct the moment the provider does something routine — Azure restarts the group, the ECS scheduler replaces the task — and nothing is raised when it happens. A control channel built on one works in every test, works in the demo, and then quietly points at nothing or at whatever has since been handed the address. There is deliberately no override, no force, and no opt-in past a non-durable address; the address is still carried on the case so a diagnostic can say "here is what you have, here is the single change that fixes it".

**Where the endpoint comes from, per adapter.** This is the question that decides the verdict, and the two answers are not the same.

- **ACI: the container group's FQDN, and it is genuinely durable.** A `dnsNameLabel` was already accepted by the spec, already sent in the ARM body, and already reported back by ARM as `properties.ipAddress.fqdn` — which the adapter modelled and read for nothing. `ResolveControlAddressAsync` reads it back from ARM rather than composing `{label}.{region}.azurecontainer.io`, because that suffix differs across sovereign clouds and a guessed control address is exactly the failure this path exists to avoid. Azure keeps that name pointed at whatever public IP the group currently holds, so the name survives the restart that moves the IP. A group provisioned *without* a label answers `Ephemeral` carrying its public IP — the very address ACI warns may move — and a group with no public address at all answers `NoAddress`. Note the claim being made is narrow: the name survives a restart, which is **not** a static address, and `StaticAddress` remains absent from the adapter's capabilities.
- **Fargate: nothing, and it cannot be salvaged from inside the adapter.** The addressing finding in §11.11 turns out to be fatal here rather than merely worse. The only address that exists at any moment belongs to the current task's ENI, and replacing that task is the service's entire purpose. It is not even usable in the meantime: `DescribeTasks` reports no public address at all — obtaining one means `ec2:DescribeNetworkInterfaces`, a different service this adapter deliberately does not call — so what `ResourceFacts.PrivateAddress` carries is a private IPv4 inside the task's `awsvpc` subnet that Servyx generally cannot route to. So it is `NoAddress`, not `Ephemeral`: reporting an unroutable address as merely non-durable would overstate how close this target is to being operable. A durable control address for Fargate has to be **created rather than discovered** — a load balancer whose DNS name the channel is pinned to, or an AWS Cloud Map service-discovery name — and this adapter creates neither. `IControlChannelAddressSource` is implemented anyway, precisely so "this one still cannot be operated" is a value a caller receives and a test pins rather than an absence a reader has to notice.

**Nothing here makes an unreachable resource look reachable.** No member of `ControlChannelAddress`, `IControlChannelAddressSource`, `RconControlChannel`, `RconControlChannelSpec` or `ControlChannelUnavailableException` accepts, returns or constructs a `TargetDescriptor`, and none names a transport id — pinned by reflection tests in both the domain and the RCON suite. Nothing writes to the `ProvisionedResource` it is handed; after a channel has been opened and a command has been run, that same resource's `RequireTarget()` still throws and `TargetOrNull()` still answers null. There is no force path and no fabricated transport id, which is the correct outcome: the operator can talk to the game, and Servyx still cannot read a file, run a command, or reach `Provision`. The ceiling is a property of the shape, not of how hard the control plane tries.

**The `readOnly` discipline is not reimplemented.** Every session `RconControlChannel` hands back is a `WriteGuardedRconSession` over an `RconSession`, applied by construction — there is no branch that returns the inner session. Read-only commands pass on a `ReadOnly` server; mutating ones and the raw escape hatch do not, and the refusal happens before the secret store or the socket is touched. The credential remains a `SecretUrn` resolved at the point of use.

`RconControlChannelSpec.Mode` is a `Func<WriteMode>`, not a `WriteMode`, and the guard is built over `WriteGuardedRconSession`'s live-source constructor. That matters because "guarded by construction" and "guarded against the posture the operator holds *now*" are different promises, and only the second one survives a revocation: an operator's per-server grant is a database row that can be flipped while a session is open, and a channel that had captured its posture at `Open()` would go on accepting `save`/`broadcast`/`shutdown` afterwards. The container-hosted exec path and `ServyxRconChannels` both re-resolve per command; this path now does too, so the parity claimed in the paragraph above holds for revocation as well as for classification.

**Only `direct-tcp` can apply, so no reachability chain is consulted.** Of the four strategies `IRconReachability` names, `docker-exec-tool` and `docker-exec-network` need a Docker daemon and `ssh-tunnel` needs an sshd — precisely the three things each adapter's own `NoTransport` reason says the provider does not have. Running a chain here would probe three strategies that cannot succeed for a reason already known before the first probe.

**Verdict per adapter.**

- **Azure Container Instances — operable, conditionally.** With a `dnsNameLabel` it reaches `Operate` through a durable control channel: `AnyOf(ExecInWorkload, ControlChannelWrite)` is satisfied by the second alternative, exactly as §11.7 predicted. Without one it is not operable, and the refusal names the single change that would make it so. `Provision` is permanently out of reach either way — it needs `WriteComposeFile` and ACI has no compose file.
- **AWS ECS/Fargate — still not operable.** Creatable, plannable, priceable, sweepable, destroyable, and unoperable. This is a well-evidenced "this one still can't", not a missing feature: the address problem is not solvable inside the adapter, and the fix (a load balancer or Cloud Map registration, plus the plan stage, cost line and destroy path each would bring) is a separate piece of work with its own orphan consequences.

Both adapters remain unregistered in the web composition. For ACI that is now a wiring decision; for Fargate the original reason still stands unchanged — a target that can be billed for but not operated should not be offered.
