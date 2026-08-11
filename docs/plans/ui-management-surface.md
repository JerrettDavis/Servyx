# UI Management Surface — Implementation Plan

Status: proposed. Author: architecture pass, 2026-08-09.

This plan makes a fresh Servyx install usable from the browser: adopt servers, view and
configure them, grant writes, onboard game definitions. It is phased so each phase is
independently shippable and independently testable.

Facts below were verified by reading source unless explicitly marked
**[VERIFY]**, which means the claim is load-bearing but was not confirmed before writing.
An implementer must check every **[VERIFY]** before relying on it.

---

## 1. Diagnosis — why the app is inert today

- **The write grant is an immutable startup snapshot.** In
  `ServyxCoreCompositionExtensions.AddServyxCoreCore`, grants are registered inside
  `if (provisioningGate.Enabled)` as `foreach (var writeGrant in ServerWriteModes.ReadGrants(...))
  builder.Services.AddSingleton(writeGrant);` (~line 366), where `ServerWriteModes` is a static
  utility class, and `GrantedWriteModeResolver` is built from `sp.GetServices<WriteModeGrant>()`.
  A fresh install has no grants, and nothing that happens at runtime can add one. The only way to
  grant write access is to edit a config file and restart.
- **Server adoption does not exist.** Repo-wide, `Servers.Add` appears only in
  `tests\Infrastructure\Servyx.Infrastructure.Persistence.Tests\EntityRoundTripTests.cs:21,63`;
  `Servers.Remove` has zero matches; no `new Server{...}` exists in any `src/` project.
  `IServerDiscovery` surfaces `DiscoveredServer` candidates but nothing ever persists one as a
  `Server` row. "Add a server" is not a wired-up-but-gated feature — it was never built.
- **The settings tab is disabled by construction, not by policy.**
  `ServerSettingsTab.razor` takes exactly one `[Parameter]`, `IReadOnlyList<SettingRow> Settings`.
  Its `<GatedControl>` and its Reveal `<GatedButton>` are never passed `Enabled`, so they fall back
  to the parameter default of `false` and are unconditionally locked. Separately, there is no
  persistence for per-server desired setting values at all.
- **Hiding `/deploy` contradicts the product's own stated invariant.** The README states that
  "every mutating control is VISIBLE BUT LOCKED until you explicitly enable writes, one server at a
  time." `NavCatalog` adds `DeployEntry` only when `ProvisioningGate.Enabled`, so the single most
  important write surface vanishes exactly when a new user needs to learn it exists. This is a
  defect against documented intent, not a feature request.
- **Definition onboarding is read-only.** `/games` lists definitions loaded from
  `Servyx:Definitions:Path`. `GameDefinitionYamlParser.Parse` already returns a `ValidationReport`
  with accurate line/column diagnostics and never throws — ideal raw material for an import UI that
  does not exist.

---

## 2. Write-grant decision

### The options

**(a) First-run setup UI that writes config files.** Rejected. It converts a fail-closed,
process-level, admin-owned switch into something the web tier can rewrite, which means a web-tier
vulnerability escalates directly to "grant myself writes on every server." It also needs a process
restart to take effect (grants are startup singletons), so the UX is poor even when it works.

**(b) Keep grants config-only; make the UI explain itself.** Safest, and genuinely valuable — but it
does not solve the user's problem. The app stays inert until someone edits JSON on the host and
restarts. Its best ideas (always-visible `/deploy`, self-explaining gated controls) are worth doing
regardless, and are folded into Phase 3.

**(c) Per-server grant in the database, master switch in config. CHOSEN.**
`Servyx:Provisioning:Enabled` remains config-only, fail-closed, and process-level: nothing in the UI
can change it. Within that boundary, an authenticated operator flips the per-server grant, persisted
to the existing `Server.WriteMode` column with `WriteModeChangedBy` / `WriteModeChangedAt`
attribution.

### Security argument

The master switch keeps its full value: on a host where the operator has not opted in, the entire
write surface is dead and no amount of UI interaction revives it. What (c) changes is only *which
already-opted-in servers* are writable — a decision the operator was already making, but previously
had to make in a text editor with a restart, and now makes in the UI with attribution and an audit
record. Two properties get strictly *better*: today a grant is a startup snapshot that cannot be
revoked without a restart, whereas the design below makes revocation take effect on the next
command; and today a grant leaves no trace of who made it, whereas the DB path records actor and
timestamp. Enforcement is untouched — `WriteGuardedExecutionTarget`, `WritesDisabledException`,
`CommandIntent.Mutating` as the default, and the plan-hash protocols all stay exactly as they are.
This change moves where a grant comes *from*, never whether it is enforced.

### Reconciling with `docs/user-guide/enabling-writes.md`

That doc states verbatim: **"There is deliberately no single global 'enable writes' switch."**
This design *preserves* that promise — the master switch stays config-only and grants stay
per-server. Only the mechanism for the per-server grant moves. Required doc edits are therefore
narrow, and listed in Phase 2.

### Resolver design (the hard part)

Three constraints collide: `IWriteModeResolver.Resolve` is **synchronous**; the resolver and
`WriteGuardedTransport` are **singletons**; `ServyxDbContext` is **scoped**.

`IDbContextFactory<ServyxDbContext>` is already registered as a Singleton at
`src\Infrastructure\Servyx.Infrastructure.Persistence\ServiceCollectionExtensions.cs:45`, with a
Scoped `ServyxDbContext` derived from it at :49. This same pattern is used by `EfServerDefinitionBindingStore`
(Definitions\EfServerDefinitionBindingStore.cs:22).

- **`WriteGrantCache`** (new, singleton): a `ConcurrentDictionary` of server key →
  `ServerWriteMode`, populated at startup and on invalidation from a short-lived context obtained via
  `IDbContextFactory<ServyxDbContext>`. Do **not** make the resolver depend on a scoped context.
- **`DbBackedWriteModeResolver : IWriteModeResolver`** (new, singleton, replaces
  `GrantedWriteModeResolver`): if the master switch is closed it returns the read-only mode
  immediately without touching the cache or the database. Otherwise it is a dictionary lookup.
- **Missing row fails closed.** A `TargetDescriptor` with no matching `Server` row resolves to
  `ServerWriteMode.ReadOnly`. This is the single most important line in the change and must have a
  dedicated test.

### Revocation: a revoked grant must not survive an open session

`Resolve` is called once per *connect*, and the resolved mode is then held by the returned
`WriteGuardedExecutionTarget`. So the grant is already a per-session snapshot, and **no caching
strategy alone can satisfy the revocation requirement** — a smarter cache only changes what *new*
connections see.

Two ways to fix it. **Chosen: re-resolve per command.** `WriteGuardedExecutionTarget` holds the
`IWriteModeResolver` and its `TargetDescriptor` instead of a captured mode, and calls `Resolve`
inside `ThrowIfWritesDisabled` and `ThrowIfMutatingCommandIsDisabled`. Revocation then takes effect
on the very next command, with no session registry and no teardown race. Backed by the cache this is
a dictionary lookup on the command path.

Rejected alternative: keep the connect-time snapshot and explicitly invalidate live sessions. That
needs a registry of open sessions, and it fails *open* for the window between the flip and the
teardown — the wrong direction for a safety property.

**Invalidation trigger:** `WriteGrantService.SetWriteModeAsync` writes the row and then calls
`WriteGrantCache.Invalidate(serverId)` in the same operation, before returning. A UI flip is
therefore visible to the next command issued anywhere in the process.

**Performance of per-command re-resolve:** Per-command re-resolve reads `WriteGrantCache`, **not
the database**. The cost per guarded command is a `ConcurrentDictionary` lookup — nanoseconds,
against a docker exec or an RCON round-trip measured in milliseconds. There is no added database
round-trip on the command path; the database is touched only at startup and on invalidation.

### The three frozen-snapshot sites

Fixing the resolver alone leaves the UI lying about write state. All three must change together:

1. The grant singletons (`AddSingleton(writeGrant)`, ~line 366) — deleted, replaced by the resolver.
2. `AddSingleton(WritableServers.FromConfiguration(...))` (~line 304) — `WritableServers` becomes a
   live view over `WriteGrantCache` rather than a frozen snapshot. Its existing call shape
   `writableServers.Mode(id, name)` is preserved so call sites do not churn.
3. `ServyxRconChannels` takes `WritableServers` at construction (~line 516) — it captures the
   snapshot too, and must receive the live view.

Call sites that read the label and will start telling the truth: `ServerDetailPage.razor`,
`ServerOverviewTab.razor`, `ServerConsoleTab.razor`, `BackupsPage.razor`, and the `MainLayout` badge.

### Session memoization: re-resolve is necessary but not sufficient

Verified: successfully-connected sessions are memoized **for the life of the process** and never
evicted on success — `ServyxServerLifecycles._sessions` (`ConcurrentDictionary<string, Lazy<Task<IExecutionTarget>>>`,
ServyxServerLifecycles.cs:53, `GetOrAdd` at :146-165), `ServyxRconChannels._sessions`
(ServyxRconChannels.cs:55, :154-193), and the same field shape in `ServyxBackupContextSource.cs:82`
and `ServyxSshBackupContextSource.cs:63`. Only *faulted* tasks are evicted and retried.

Fixing `WriteGuardedExecutionTarget` alone therefore does **not** close the revocation hole. There
is a second, independent capture path: `ServyxRconChannels.BuildAsync` computes the mode once from
`_writable.IsWritable(...)` and bakes it into the returned `WriteGuardedRconSession`
(ServyxRconChannels.cs:190-192), and that session is then cached forever.

**Both paths must re-resolve per command.** `WriteGuardedExecutionTarget` holds the resolver and
its `TargetDescriptor`; `WriteGuardedRconSession` holds the live `WritableServers` view and
re-checks on each command rather than trusting a build-time boolean. Because both then consult
`WriteGrantCache`, the memoized sessions stay memoized — we do **not** need to evict session caches,
and we do not change connection behavior at all. This is why re-resolve was chosen over session
invalidation: eviction would have required reaching into four separate caches on three singleton
services.

Add a test for the RCON path specifically, not just the exec path — the two capture sites fail
independently.

### The two-enum hazard

`ServerWriteMode` (domain entity) and `WriteMode` (transport) are **different enums with identical
members**. Once the DB column is authoritative, a careless cast between them sits directly on the
enforcement path and will fail silently. Mitigation, non-optional:

- Declare the domain `ServerWriteMode` authoritative; the transport enum is a projection.
- Add `WriteModeMapping.ToTransport(ServerWriteMode)` as a `switch` expression with **no default
  arm**, so adding a member to either enum becomes a compile-time error rather than a silent
  fallthrough.
- Add a test asserting the two enums have identical member-name sets, so divergence fails the build.

### Pre-existing `Servyx:Servers:<key>:WriteMode` config: **ignored, with a loud startup warning**

Not honoured as a seed and not honoured as an override.

- *Not an override*, because that would keep two sources of truth for the same decision — the exact
  ambiguity this change exists to remove.
- *Not a seed*, for a correctness reason and a security reason. Correctness: the config key is keyed
  by container name while the DB row is keyed by `ServerId`, and on a fresh install there may be no
  `Server` row to seed at all. Security: a config file can be stale, copied from another host, or
  committed to a repo, and auto-importing it would grant write access that nobody consciously
  re-affirmed.
- Instead, `StartupSafetyWarnings` detects any `Servyx:Servers:*:WriteMode` key and logs a warning
  naming each key and instructing the operator to re-grant in the UI. Failing closed and making the
  operator click once is the correct trade.

### Audit trail

The `/audit` page is a placeholder (`Components\Pages\Audit\AuditPage.razor` is 17 lines of static
markup). **Do not build an audit subsystem in this plan.** Record the grant change through the existing
structured-logging convention `OperatorAuthentication.AuditLogCategory =
"Servyx.Web.Authentication.Audit"`, and persist attribution on the row itself
(`WriteModeChangedBy`, `WriteModeChangedAt`). A persisted, queryable audit store is future work.

**Attribution honesty.** Servyx has one shared password, not per-operator accounts.
Authentication issues a `ClaimsIdentity` under scheme `"ServyxOperator"` whose `ClaimTypes.Name`
is the constant `OperatorAuthentication.OperatorNameClaimValue` (`"operator"` — see
`AuthenticationEndpoints.cs:209-210`). `AuthenticationStateProvider` and `CascadingAuthenticationState`
are wired (`AuthenticationServiceCollectionExtensions.cs:112`, `Components\Routes.razor:13-26`), so
a component can read the principal — it will just always be `"operator"`. `WriteModeChangedBy` will
therefore record a constant until a real user system exists. Say this in the UI copy rather than
implying per-user attribution — a column named `WriteModeChangedBy` that always holds one value
is a trap for whoever reads it in a year.

### Grant key semantics: identity, not name

Phase 1 persists `Server.ContainerId` (required, unique-indexed) as the durable correlation key.
Phase 2's grant is **stored** against `ServerId` and **honoured** only while the recorded identity
still matches the `TargetDescriptor` presented at resolve time. The identity compared is
**`ContainerId` alone**. The pair (`HostId`, `ContainerId`) was considered and rejected for now:
Phase 1 makes `Server.HostId` nullable and sets it to `null` on adoption — deliberately, because a
fabricated `HostId.New()` pointing at no `Host` row is worse than an honest null — and nothing in
`src\` has ever created a `Host` row. A pair-match would therefore compare `null` to `null` on every
server and contribute nothing, while `enabling-writes.md` promised a host guarantee no code enforced.
A documented guarantee with no enforcement is strictly worse than an acknowledged gap.

**Observation, not a guarantee:** container ids are 64-hex random values and are not portable between
hosts, so in practice re-pointing a host will also fail the `ContainerId` match and revoke. This is a
consequence of how container ids work, not a check we perform, and it would not hold for a container
migrated with its id preserved (a restored snapshot, for instance). The plan does not claim it.

**Follow-on scope (not Phase 2):** populating `HostId` at adoption from the host the container was
discovered on — the information exists at discovery time via the `Servyx:Hosts` configuration and the
SSH+Docker transport — and then extending the match to the pair. Only once `HostId` is genuinely
populated may `enabling-writes.md` claim a host guarantee.

Tests: recreated container (new id, same name) → next command refused; plain rename (same id) →
grant survives, asserted deliberately so a future change cannot silently reverse it. **Do not write
a "different host revokes" test yet** — against two null `HostId`s it would pass vacuously and prove
nothing. Add it with the follow-on work above.

---

## 3. Phases

Ordering rationale: adoption first, because a per-server DB grant needs `Server` rows to hang off,
and because adoption writes only to Servyx's own database — it never issues a guarded transport
command, so it ships without touching the write guard at all.

### Phase 1 — Adopt, view, and forget servers *(makes a fresh install non-inert)*

**Goal:** a user with a running game container can adopt it, see it persist in `/servers`, and remove
it again. No write grant required, because adoption is a Servyx-database operation.

Aligns with the README invariant "adopts existing containers rather than owning them" — this is the
product's primary path, and unlike `/deploy` it needs no cloud credentials.

Note: `docs/user-guide/adopting-servers.md` describes only *discovery* and *viewing* — it never
describes a user adopting anything. There is no documented adoption UX to conflict with, so this
phase designs it fresh and the doc gains a new section.

Verified: the `Servers` DbSet is entirely orphaned today — no code in `src\` reads from or writes to
it (`Servers.Add` appears only in `EntityRoundTripTests.cs:21,63`; `Servers.Remove` has zero matches).
Phase 1 is genuinely the first code to touch this table, which is why it needs a factory, a service,
and a UI rather than a wiring change.

Important design note: `Server.HostId` is nullable and `null` on adoption by design; no `Host` row
has ever been created by any code in `src\`. Phase 2's grant matching depends on this — see *Grant
key semantics*.

**Create**
- `src\Core\Servyx.Domain\Entities\Server.cs` *(modify)* — add a `public static Server Adopt(...)`
  factory. The entity currently has `required` members and no constructor or factory, so invariants
  are enforced nowhere; this is the place to add them.
- `IServerAdoptionService` + implementation, in `src\Core\Servyx.Application\Servers\`. Confirmed:
  `ServerQueryService.cs` and `ServerModels.cs` already live there, and `IServerQueryService` is
  registered from `src\Core\Servyx.Application\ServiceCollectionExtensions.cs:34`. Place the adoption
  service alongside them. Shape:
  - `Task<AdoptionResult> AdoptAsync(DiscoveredServer candidate, string gameDefinitionId, CancellationToken ct)`
    — mints a `ServerId`, persists a `Server` row with `AdoptionMode.Adopted` and
    `WriteMode = ServerWriteMode.ReadOnly`, and writes the `ServerDefinitionBinding` with the
    definition's content hash.
  - `Task<ForgetPlan> ForgetPlanAsync(ServerId id, CancellationToken ct)` and
    `Task<ForgetResult> ForgetApplyAsync(ServerId id, string approvedPlanHash, CancellationToken ct)`.
    Forgetting drops the binding and any grant, so it follows the existing plan→hash→apply protocol
    rather than introducing a second confirmation mechanism. Mirror
    `ServerRuntimeTools.StopPlanAsync` / `StopApplyAsync` and reuse the `StopPlanHash.Compute`
    approach for hashing.
- `src\Presentation\Servyx.Web\Components\Pages\Servers\AdoptServerDialog.razor` (+ `.razor.css`) —
  lists discovery candidates, requires a `GameDefinitionId` selection, shows the resulting binding.
- `src\Presentation\Servyx.Web\Components\Pages\Servers\ForgetServerDialog.razor` — renders the plan,
  requires explicit confirmation, submits the hash.
- DTO for an adoption candidate under `src\Presentation\Servyx.Web\Models\`.

**Modify**
- `src\Presentation\Servyx.Web\Components\Pages\Servers\ServersList.razor` — an "Adopt server"
  action and an unadopted-candidates section.
- `src\Presentation\Servyx.Web\Services\LiveDashboardDataService.cs` and `IDashboardDataService` —
  expose discovery candidates not yet adopted.
- `src\Hosting\Servyx.Composition\ServyxCoreCompositionExtensions.cs` — register the adoption
  service inside `AddServyxCore` only. Do not register it from either `Program.cs`.

**EF migration:** Confirmed: no migration required. Per `ServyxDbContextModelSnapshot.cs` (~lines
98-148) the `Servers` table already maps `Id` (Guid/TEXT, key), `AdoptionMode` (TEXT, required, max
32), `CreatedAt` (TEXT, required), `DefinitionContentHash` (TEXT, required, max 128),
`GameDefinitionId` (TEXT, required, max 128, indexed), `HostId` (Guid/TEXT, required, indexed),
`Name` (TEXT, required, max 200), `WriteMode` (TEXT, required, max 32), `WriteModeChangedAt` (TEXT,
nullable), `WriteModeChangedBy` (TEXT, nullable, max 200), via `b.ToTable("Servers", (string)null)`.
`ServerDefinitionBindings` was added by `20260807124715_AddServerDefinitionBindings`. Existing
migrations: `20260728003145_InitialCreate`, `20260807124715_AddServerDefinitionBindings`. The table
is fully mapped and entirely unused — Phase 1 writes the first code to touch it.

Caveat worth recording: `AddServerContainerIdentity` adds `ContainerId` as NOT NULL with
`defaultValue: ""` and then creates a unique index over it. That sequence would fail on any table
already holding two or more rows. It is safe here **only** because the `Servers` table was provably
orphaned before Phase 1 — the sole `Servers.Add` in `src\` is `EfServerRepository.cs:54`, itself
introduced by Phase 1. Do not imitate this migration shape on a populated table.

**Tests**
- `tests\Core\Servyx.Domain.Tests\Entities\ServerAdoptTests.cs` — factory invariants; adopted servers
  start `ReadOnly`.
- Adoption service tests in `tests\Core\Servyx.Application.Tests\` (project
  `Servyx.Application.Tests.csproj`). Persistence round-trip coverage belongs in
  `tests\Infrastructure\Servyx.Infrastructure.Persistence.Tests\`, where `EntityRoundTripTests.cs`
  is currently the only code in the repo that adds a `Server`. Adoption service tests cover
  idempotence/rejection, forget hash matching, and stale hash refusal.
- `tests\Presentation\Servyx.Web.Tests\Pages\AdoptServerDialogTests.cs` — bUnit, class derives from
  `BunitContext`, fakes registered via `Services.AddSingleton(...)` before `Render<T>()`, queried by
  `data-testid` only, asserted with AwesomeAssertions (`.Should().BeTrue(because: "...")`).
- TinyBDD scenario in `Servyx.Bdd.Tests`: discover → adopt → appears in list → forget.

**Files touched: ~12.**

---

### Phase 2 — DB-backed write grant, live everywhere

**Goal:** an authenticated operator flips a server between `ReadOnly` / `PreviewOnly` / `Enabled`
from the UI, with attribution; revocation takes effect on the next command; the badge and every
gated control tell the truth immediately.

**Create**
- `src\Hosting\Servyx.Composition\WriteGrantCache.cs` — singleton, `IDbContextFactory`-backed,
  `Invalidate(ServerId)` / `Reload()`.
- `src\Hosting\Servyx.Composition\DbBackedWriteModeResolver.cs` — `IWriteModeResolver`; returns
  read-only immediately when the master switch is closed; **missing row → `ReadOnly`**.
- `src\Core\Servyx.Domain\Transport\WriteModeMapping.cs` — total mapping, no default arm.
- `IWriteGrantService` + implementation —
  `Task SetWriteModeAsync(ServerId id, ServerWriteMode mode, string actor, CancellationToken ct)`:
  writes row, stamps `WriteModeChangedBy` / `WriteModeChangedAt`, invalidates the cache, logs to the
  audit category.
- `src\Presentation\Servyx.Web\Components\Pages\Servers\WriteModeControl.razor` — the three-tier
  selector plus current attribution. Gated by the master switch: when
  `ProvisioningGate.Enabled` is false the control renders **visible but locked** via `GatedControl`,
  with a `Reason` naming `Servyx:Provisioning:Enabled` and the file to set it in.

**Modify**
- `src\Core\Servyx.Domain\Transport\WriteGuardedExecutionTarget.cs` — hold the resolver and
  descriptor; re-resolve inside `ThrowIfWritesDisabled` and `ThrowIfMutatingCommandIsDisabled`.
  Do not change the exception type or the intent default.
- `src\Core\Servyx.Domain\Transport\WriteGuardedTransport.cs` — pass the resolver through
  `ConnectAsync` instead of a captured mode.
- `src\Hosting\Servyx.Composition\ServerWriteModes.cs` — `ReadGrants` becomes legacy-key *detection*
  returning warnings; it no longer produces grants.
- `src\Hosting\Servyx.Composition\WritableServers.cs` — live view over the cache, same
  `Mode(id, name)` shape, same `WritableServers.None` fallback.
- `src\Hosting\Servyx.Composition\ServyxCoreCompositionExtensions.cs` — remove the grant-singleton
  loop (~366) and the frozen `WritableServers` registration (~304); register cache, resolver, grant
  service; ensure `ServyxRconChannels` (~516) receives the live view; add
  `AddDbContextFactory<ServyxDbContext>` if absent.
- `src\Presentation\Servyx.Web\Services\StartupSafetyWarnings.cs` — legacy-config-key warning.
- `src\Presentation\Servyx.Web\Components\Pages\Servers\ServerOverviewTab.razor` — host the control.
- `docs\user-guide\enabling-writes.md` — the paragraph beginning "**Renaming a container** doesn't carry
  the grant with it" must be rewritten. Replace its claim that the grant is "keyed on the container
  name it was written for" with: the grant is bound to the container's durable identity, so
  **recreating** a container returns it to read-only while a plain rename preserves the grant. The
  existing "**Re-pointing a host**" paragraph must be **removed or marked as not-yet-enforced** — it
  must not be left asserting a guarantee the code does not check.

**EF migration:** Confirmed: Phase 2 needs no migration either. `WriteMode`, `WriteModeChangedBy` and
`WriteModeChangedAt` are all already mapped and migrated. `ServerConfiguration.cs:50-56` configures
`WriteMode` as `.IsRequired().HasConversion<string>().HasMaxLength(32)` and `WriteModeChangedBy` as
`.HasMaxLength(200)`; `WriteModeChangedAt` falls to convention. **Phase 4a's `ServerSettingValue`
table is the only migration in this entire plan** — Phases 1, 2, 3 and 5 are all schema-free, which
is a meaningful de-risking of the sequence.

**Tests**
- Resolver: master switch closed → `ReadOnly` for everything, DB never queried; **missing row →
  `ReadOnly`**; each tier maps correctly.
- **Revocation test (the critical one):** open a session with `Enabled`, flip to `ReadOnly`, assert
  the next mutating command throws `WritesDisabledException`. This is the test that proves the
  requirement.
- Enum parity test: `ServerWriteMode` and `WriteMode` have identical member-name sets.
- `tests\Presentation\Servyx.Web.Tests\Pages\WriteModeControlTests.cs` — bUnit; locked when the
  master switch is closed; the reason names the config key.
- Re-run `tests\Presentation\Servyx.Mcp.Tests\Composition\CompositionRootSingleSourceTests.cs`
  unchanged — it must still pass.
- TinyBDD: grant → command succeeds → revoke → next command refused.

**Files touched: ~15.**

---

### Phase 3 — Visible but locked, consistently *(defect fix)*

**Goal:** honour the README invariant everywhere. Nothing mutating is ever hidden; everything locked
explains itself.

**Modify**
- `src\Presentation\Servyx.Web\Components\Layout\NavCatalog.cs` — `DeployEntry` is always present,
  carrying a locked flag and reason instead of being conditionally added.
- The nav renderer — render locked entries disabled with a lock icon. **Preserve the existing
  `Services.GetService(...)` null-fallback pattern (`WritableServers.None`, `ProvisioningGate.Closed`)
  rather than `@inject`**, so an unregistered service degrades closed.
- `src\Presentation\Servyx.Web\Components\Pages\Backups\BackupsPage.razor` — replace the
  `@if (_writable)` section-hiding and the `data-testid="server-read-only"` empty state with
  `GatedButton`. This page currently contradicts the invariant and diverges from
  `ServerOverviewTab`. New pages must not copy the hiding pattern.
- The `/deploy` page — render gated rather than assuming the gate is open.
- The stub pages (`settings`, `users`, `mods`, `plugins`) — replace blank stubs with honest
  "not yet implemented" states so the nav stops leading nowhere.

**Tests:** bUnit assertions that the deploy nav entry is *present and disabled* when the gate is
closed, and that Backups renders locked controls rather than hiding them.

**Screenshot impact:** likely invalidates `provisioning-gate-closed.png`, `control-tier-read-only.png`,
and possibly `servers-list.png`. See Risks.

**Files touched: ~8.**

---

### Phase 4a — Desired-value persistence *(shippable)*

**Goal:** the settings tab becomes editable and records what the operator *intends*, clearly labelled
as not yet applied.

Verified constraint: `IPlanExecutor` (`src\Core\Servyx.Domain\Configuration\IPlanExecutor.cs`) now
**has an implementation** — `PlanExecutor` (`src\Infrastructure\Servyx.Config\PlanExecutor.cs`) — and
**is DI-registered** at `src\Hosting\Servyx.Composition\ServyxCoreCompositionExtensions.cs:453-465`.
What it still lacks is **a caller from any operator surface**: no UI, no REST API, no MCP tool, and no
job runner invokes `PreviewAsync`/`ApplyAsync` today. `RevertAsync` still throws
`NotImplementedException`. Phase 4a therefore does not touch it.

`ServerSettingsTab.razor` currently takes only `IReadOnlyList<SettingRow> Settings` and never
passes `Enabled` to its `GatedControl`/`GatedButton`, so it is locked *by construction*; making it
editable means changing its parameter surface, not flipping a flag.

**Create:** `ServerSettingValue` entity (`ServerId`, `Key`, `Value`, `UpdatedBy`, `UpdatedAt`) + DbSet
+ configuration + **the one EF migration in this plan**; `IServerSettingsService` (load / save desired
values only); `Components\Shared\SettingEditor.razor` — one editor per `SettingType` (`String`, `Text`,
`Int`, `Float`, `Bool`, `Enum`, `Port`, `Secret`, `Path`, `Duration`), honouring `Constraints`,
`Required`, `RenderFormat`, grouped by `SettingGroup`.

**Modify:** `ServerSettingsTab.razor` — add `WriteMode` parameter and a save handler; pass `Enabled`
through to `GatedControl` (its native `<fieldset disabled>` cascades to nested inputs).

**Honest UI copy — non-negotiable.** A saved value is *desired*, not applied. The tab must label it
that way and show the gap against the authoritative/rendered/runtime values `SettingRow` already
carries. It must never imply the running server changed. This codebase is honest about degraded state
everywhere else — `LiveDashboardDataService` degrades visibly, MCP distinguishes empty from unknown —
and a settings tab that implies an unapplied write took effect would be the single most damaging
inconsistency we could ship. Settings carrying `RequiresRecreate` render locked with a reason pointing
at Phase 4b.

**Files touched: ~12, plus one migration.**

### Phase 4b — Apply (the real M5)

**Goal:** desired values actually reach the server.

This remains a large piece of remaining work, but the apply engine itself is no longer the
build-from-nothing it was: `IPlanExecutor` is implemented and DI-registered (see below), so what's
left is primarily an operator-facing surface to call it from, plus `YamlConfigAdapter`. The
config-surface read/write layer largely exists: four `IConfigAdapter` implementations —
`DotEnvConfigAdapter`, `IniConfigAdapter`, `PropertiesConfigAdapter`, `JsonConfigAdapter` — live in
`src\Infrastructure\Servyx.Config\` and are registered `AddSingleton<IConfigAdapter, X>()` at
`src\Infrastructure\Servyx.Config\ServiceCollectionExtensions.cs:21-24`.

What is missing is **an operator-facing surface above the orchestration**, plus one adapter:
- `IPlanExecutor` — declared at `src\Core\Servyx.Domain\Configuration\IPlanExecutor.cs`, and now
  **implemented** by `PlanExecutor` (`src\Infrastructure\Servyx.Config\PlanExecutor.cs`) and
  DI-registered at `ServyxCoreCompositionExtensions.cs:453-465`. `PreviewAsync` computes a
  `ConfigChangePlan` — including drift comparison, reversibility, and `PlanStaleException` handling —
  entirely in memory and against Servyx's own database; `ApplyAsync` writes the previewed bytes to the
  live server, verified two ways (see below) and gated by write mode. What remains missing is a
  **caller**: no UI, REST API, MCP tool, or job runner invokes either method, so none of this is
  reachable by an operator yet. `RevertAsync` still throws `NotImplementedException`.
- `ApplyAsync`'s fidelity model, briefly, since it is easy to overstate: it hashes bare lowercase hex
  SHA-256 over raw bytes throughout. Two checks guard a write — the transport's own receipt digest
  (which only proves the transport agrees about the bytes it was *handed*; every shipped transport
  computes it from the input buffer with no read-back, so today this check is a tautology kept to
  catch transport bookkeeping bugs) and a genuine read-back-and-rehash (`PostWriteVerification`:
  `Verified` / `Unverifiable` / `Mismatched`). On a mismatch the action is `Failed` with both digests
  recorded, later actions are `Skipped`, and the plan becomes `PartiallyApplied` — including when the
  very first action fails, because the server was touched. There is **no auto-repair**: no rewrite, no
  retry, no rollback.
- **`YamlConfigAdapter` — does not exist, and must be written from scratch.** `DeclaredConfigSurface.Format`
  includes `Yaml`, and shipped game definitions declare YAML surfaces, so this is a hard blocker for
  those games rather than a nice-to-have. It is also the hardest of the five adapters to get right:
  a write path needs byte-exact round-tripping with comment and key-order preservation, which naive
  serialize-then-write destroys. `SafeYamlLoader` and the hand-rolled `GameDefinitionYamlParser`
  exist and deliberately preserve line and column information — **worth investigating for reuse, but
  treat that as an open question, not a solved problem**; a read path that tracks positions is not
  automatically a faithful write path.
- `ServerLifecycleService.RecreateAsync` throws `NotSupportedException` today, documenting that
  recreate only means anything as the applied consequence of an approved plan. `ConfigChangePlan` and
  `IPlanExecutor.ApplyAsync` exist now, but no operator surface produces an approved plan yet, so this
  is unblocked only once Phase 4b's UI/API surface lands.

Do not size this from within this plan. **Phase 4a delivers visible user value without any of it**,
which is the whole reason for the split.

---

### Phase 5 — Game definition onboarding

**Goal:** import a definition from the UI, see validation errors at the right line and column, and
have the catalog pick it up.

Verified: no new engine is required. `GameDefinitionCatalog.RefreshAsync`
(`src\Core\Servyx.Definitions\GameDefinitionCatalog.cs:136`) re-walks every registered
`IGameDefinitionProvider` and builds an entirely new immutable snapshot, published by a single
reference assignment to a `volatile Snapshot _snapshot`. It is not an incremental update over known
ids, so a file that did not exist on the previous pass **is** picked up. `FileSystemGameDefinitionProvider.ListAsync`
enumerates `Servyx:Definitions:Path` (default `{AppContext.BaseDirectory}/definitions`). The import
flow is therefore: parse the uploaded text with `GameDefinitionYamlParser.Parse(string, sourceName)`
→ if `ValidationReport.IsValid`, write the file into the definitions directory → call `RefreshAsync`.
Unlike Phase 4, this phase is **not** underscoped.

**Create** — nothing in `src\` currently writes into `Servyx:Definitions:Path`, so the import path is
entirely new code:
- `src\Presentation\Servyx.Web\Components\Pages\Games\ImportDefinitionDialog.razor` — paste or upload
  YAML, call `GameDefinitionYamlParser.Parse(yaml, sourceName)`, and render every `ValidationIssue`
  with its `Line`, `Column`, `Severity`, and `Message`. The parser never throws, so this is a pure
  render of `ValidationReport` — no exception handling theatre.
- `IDefinitionImportService` (proposed by this plan; no such type exists yet) — writes the file under
  `Servyx:Definitions:Path` and calls `GameDefinitionCatalog.RefreshAsync`. Refuses to write when
  the report contains errors.

**Modify**
- `GamesPage.razor` — an import action, plus surfacing `GameDefinitionCatalog.Faults` so broken
  definitions are visible instead of silently absent.

**Note:** hot-reload watching is Development-only by default, so the explicit `RefreshAsync` call is
required in Production — do not rely on the watcher.

**Tests:** valid YAML imports and appears in the catalog; invalid YAML surfaces issues with exact
line/column and writes nothing; `Faults` render.

**Files touched: ~6.**

### Caveats

- Hot-reload `WatchAsync` is enabled by default **only in Development** (`ResolveWatch`,
  `Servyx.Definitions\ServiceCollectionExtensions.cs`). In production the import path **must** call
  `RefreshAsync` explicitly — never rely on the watcher.
- `DefinitionsByContentHash` only ever **grows**: every successfully loaded hash stays resolvable for
  the process lifetime. Good for pinned `Server.DefinitionContentHash`, but it means an import cannot
  truly *replace* a hash. Do not build an "edit definition" UX on the assumption that the old version
  disappears.
- Cross-provider id collisions resolve by provider order; the loser gets a `DefinitionFault`. Surface
  this — an import that silently loses to a bundled definition would be baffling.
- A failed reload never evicts a good version, **except** `FileNotFoundException` / `DirectoryNotFoundException`,
  which do evict.
- `RecordFaultAsync` appends a fault but cannot add a definition.
- Publishing is serialized behind a `SemaphoreSlim` — concurrent imports are safe, but not parallel.

### Is definition import gated?

Importing writes a file to the host filesystem, so the question is fair. **Decision: not gated by
`Servyx:Provisioning:Enabled`; operator authentication is sufficient** — the same reasoning as adoption
in Phase 1. The provisioning master switch governs Servyx acting on *managed game servers*; a definition
file is Servyx's own catalog data and touches no server, running or otherwise. Gating it behind the
master switch would mean a fresh install cannot teach Servyx about a new game without first enabling
the ability to mutate servers — which inverts the safety story rather than strengthening it.

The write is nonetheless constrained: it is refused unless `ValidationReport.IsValid`; it must not
silently overwrite an existing definition file (require an explicit replace action); and it is confined
to the configured definitions directory, with path traversal in the supplied name rejected. Note that
unlike adoption, an imported definition is **global** — it affects every server that matches it —
so the confirmation copy should say so.

---

## 4. Backend gaps, mapped to phases

| Gap | Phase | Notes |
|---|---|---|
| No adopt/register service | 1 | Built from scratch; includes a `Server.Adopt` factory, since no invariant enforcement exists in-type today. |
| No delete/forget path (`Servers.Remove` has zero matches) | 1 | Via plan→hash→apply, not a bespoke confirm dialog. |
| Grants are startup singletons | 2 | Replaced by cache + resolver. |
| `WritableServers` frozen (3 capture sites) | 2 | Must change together or the UI lies. |
| Two identical enums, no mapping | 2 | Total mapping + parity test. |
| No per-server setting persistence | 4a | The only EF migration in this plan. |
| `IPlanExecutor` has no caller | **4b** | **Now implemented and DI-registered** (`PlanExecutor`, `src\Infrastructure\Servyx.Config\PlanExecutor.cs`). What remains is a caller — no UI, REST API, MCP tool, or job runner invokes it. The four non-YAML config adapters DO exist and are registered — 4b is now a UI/API surface on top of a working engine, not orchestration from scratch. |
| `YamlConfigAdapter` missing | **4b** | No YAML `IConfigAdapter` exists despite `DeclaredConfigSurface.Format` including `Yaml` and shipped definitions declaring YAML surfaces. Hardest adapter of the five: needs byte-exact round-trip with comment and key-order preservation. |
| `RecreateAsync` throws | **4b** | `ServerLifecycleService.RecreateAsync` throws `NotSupportedException`, documenting that recreate only means anything as the applied consequence of an approved plan. `ConfigChangePlan`/`IPlanExecutor.ApplyAsync` exist now, but nothing produces an *approved* plan through an operator surface yet, so this remains unblocked in practice. |
| No persisted audit store | **Deferred** | Structured logging in Phase 2; queryable store is future work. |
| `IServerDefinitionBindingStore` / `EfServerDefinitionBindingStore` | 1 | Real and implemented, registered via `AddServyxServerDefinitionBindingStore()`. This is the singleton-over-`IDbContextFactory` precedent Phase 1 should copy. Phase 1 gains no work here. |

---

## 5. Risks

- **Weakening the write guard.** The mitigation is that Phase 2 makes enforcement *stricter*
  (per-command re-resolve replaces a per-session snapshot). Non-negotiable invariants: missing row →
  `ReadOnly`; master switch closed → no DB read and no grant; `CommandIntent.Mutating` stays the
  default; `WritesDisabledException` is unchanged. Each has a named test above.
- **The two-enum silent bug.** Highest-likelihood subtle defect in the whole plan, because both enums
  have identical members and a cast compiles cleanly. The no-default-arm mapping plus the parity test
  are the mitigation; do not skip them.
- **`CompositionRootSingleSourceTests`.** It source-scans both `Program.cs` files with `File.ReadAllText`
  and ordinal `string.Contains` — so a match inside a comment fails the test too. Every registration
  in Phases 1–2 goes inside `AddServyxCore`. Nothing new may be added to `Servyx.Web\Program.cs` or
  `Servyx.Mcp.Stdio\Program.cs`.
  
  Forbidden factory calls: `ProvisioningGate.FromConfiguration(`, `WritableServers.FromConfiguration(`,
  `ServerWriteModes.ReadGrants(`, `SshDockerWriteModes.ReadGrants(`, `SshDockerWiringOptions.FromConfiguration(`,
  `RconWiringOptions.FromConfiguration(`, `BackupWiringOptions.FromConfiguration(`,
  `SshBackupWiringOptions.FromConfiguration(`, `ProvisionerWiringOptions.FromConfiguration(`,
  `BackupScheduleOptions.FromConfiguration(`.
  
  Forbidden constructions: `new WriteGuardedTransport(`, `new WriteGuardedRconSession(`,
  `new WriteGuardedExecutionTarget(`, `new ServyxRconChannels(`, `new ServyxBackupContextSource(`,
  `new ServyxSshBackupContextSource(`, `new ProvisioningDashboardService(`.
  
  Positive requirement: both files must contain `AddServyxCore(`. Carve-out: `AuthenticationGate.FromConfiguration`
  is **not** forbidden and legitimately appears at `Servyx.Web\Program.cs:42`.
  
  **Phase 2 obligation:** any new grant-related factory must be called only from inside `AddServyxCore`,
  and **Phase 2 must add its new factory and construction names to both forbidden lists in this test**.
  Extending the guard is part of the work, not an afterthought; leaving it unextended would quietly
  make the architecture test weaker than we found it.
- **Hermetic tests.** `dotnet test Servyx.sln` must stay Docker-free and network-free beyond
  127.0.0.1. The `IDbContextFactory` work must be exercised against the existing in-memory/SQLite
  test setup, never a real database. Discovery must be faked in every adoption test.
- **Screenshot integrity.** `DocumentationScreenshotIntegrityTests` verifies doc-referenced
  screenshots exist. Phase 1 likely invalidates `servers-list.png`; Phase 2 invalidates
  `control-tier-read-only.png` and any `enabling-writes.md` imagery; Phase 3 invalidates
  `provisioning-gate-closed.png`; Phase 4 invalidates `settings.png`. Re-capture runs through the
  Playwright/Reqnroll harness, which is **not in CI** — this is a manual step an implementer will
  otherwise miss.
- **Cross-process cache incoherence.** `Servyx.Web` and `Servyx.Mcp.Stdio` are separate processes
  with separate caches. **What an operator actually observes:** they grant a server `Enabled` in the
  web UI, the web UI immediately works, and an agent driving the same server over the stdio MCP host
  keeps getting `WritesDisabledException` — or, worse on revoke, an agent keeps successfully writing
  to a server the operator believes they just locked. The revoke direction is the dangerous one.
  **Mitigation until a shared invalidation channel exists: restart the MCP host after changing a
  grant**, and say so in the grant UI confirmation copy. Do not paper over this with a short cache
  TTL — that trades a clear, documented limitation for a timing race.
- **Attribution is single-operator.** `WriteModeChangedBy` will record a constant until a real user
  system exists. Do not let UI copy imply otherwise.
- **No new NuGet dependencies** are required by this plan. `Microsoft.EntityFrameworkCore`'s
  `AddDbContextFactory` is part of the existing EF package. If an implementer finds they want a new
  package, that is a signal to re-check the design, not to add it.
- **A test assembly can vanish silently, and "tests green" will not catch it.** Observed across
  repeated full-solution runs during Phase 1 verification: one run reported a solution-wide total of
  **4087** while every other run reported **4480**. The 393-test gap is exactly `Servyx.Domain.Tests`,
  which independent re-runs confirm executes correctly at 393 passed / 0 failed / 0 skipped, both under
  `dotnet test Servyx.sln` and in isolation. The danger is the failure *mode*: an assembly that does
  not execute surfaces as a **missing summary line** in a long console log, not as a failure or a skip.
  `Failed: 0` is reported either way, so a run in which an entire assembly disappeared is visually
  indistinguishable from a fully green one, and the aggregate count is the only signal — which nobody
  reads when the run says "Passed!". The stakes are specific: `Servyx.Domain.Tests` holds the
  write-guard tests (`WriteGuardedExecutionTarget`, `WriteGuardedTransport`, `CommandIntent`
  defaults), so an affected run has **zero write-guard coverage while still reporting success**. Phase 2
  modifies exactly those files and its gate is "tests green."
  
  **Mitigation:** assert an expected test-assembly count — or an expected minimum total — rather than
  trusting `Failed: 0`, and fail CI when fewer assemblies report than expected. `dotnet sln Servyx.sln
  list` shows exactly **16** test projects, so 16 is the correct expected figure. Note that three
  further `*.Tests.csproj` exist on disk (`Servyx.E2E.Bdd.Tests`, `Servyx.E2E.Tests`,
  `Servyx.Remote.Tests`) and are deliberately **not** in the solution — anyone wiring this check needs
  to know that, or they will calibrate it to the wrong number and treat a by-design exclusion as drift.
  `.github\workflows\ci.yml` is where this belongs; it already documents which projects CI runs and why.

## 6. Open questions carried forward

- **`ComposeWriteModeResolver` hardcodes the `docker` transport id.** `ComposeWriteModeResolver.Resolve`
  re-asks the shared `IWriteModeResolver` with a descriptor it synthesizes, and stamps
  `DockerTransportId` on it unconditionally — which routes *every* compose session to the
  database-backed grant path in `DbBackedWriteModeResolver`. That is correct while a compose directory
  is always a local one beside the operator's own compose file, which is the only way
  `Servyx:Backups:ComposeDirectory` is used today. It would be wrong for an SSH-hosted compose
  session: that server's grant lives in configuration, not in a `Server` row, so the synthesized
  descriptor would be resolved against a local container id and miss — read-only, i.e. fail-closed,
  but for the wrong reason and invisibly.
  
  Nobody has established which sources can feed this resolver, so **nothing has been changed on
  speculation.** Determining that is the work: enumerate the paths that can reach
  `ServyxBackupContextSource`'s compose transport, and either confirm local-only (and say so in the
  constant's remarks) or carry the descriptor's real transport id through instead of overwriting it.
  Do not "fix" this by merging the two grant sources for one target — see
  `DbBackedWriteModeResolver`'s remarks for why they are deliberately disjoint.
