# Servyx Roadmap

Servyx ships in nine milestones, M1 through M9. The MVP is **M1–M7**; M8 and
M9 extend the platform to remote/non-Docker targets and to provisioning and
mods respectively.

**Where things actually stand today:** this document is the original,
milestone-by-milestone plan, and each section below still describes that
milestone's original goal and acceptance criteria — it is not rewritten
after the fact. But several milestones have shipped substantially more than
their position in this list suggests: real per-server write access (M4),
Servyx-owned backup creation/restore/retention (M5), the `ssh+docker`
remote transport (M8), and infrastructure provisioning via the Deploy page
(M9) are all real and in the running dashboard today, each still gated
behind its own explicit, off-by-default switch. See the **Status today**
note under each of those four milestones for exactly what shipped and what
didn't. M6 has also shipped, and substantially more than its original scope:
see the **Status today** note under M6. Config editing (M3/M4's
`.env`/`compose.yaml` write path) has now shipped in part — see the **Status
today** note under M4 for exactly what an operator can and cannot do with it.
Byte-exact revert now has a shipped engine (`IPlanExecutor.RevertAsync`) but
no operator surface calls it yet — see the **Status today** note under M4.
Identity/RBAC/audit UI (M7), and mod installation and the plugin SDK (M9)
remain as originally planned: not yet built.

## M1 — Read-Only Observation

**Goal:** Connect to and fully observe an existing, live Palworld deployment
without the ability to change anything.

**Projects touched:** `Servyx.Domain`, `Servyx.Application`, `Servyx.Config`,
`Servyx.Infrastructure.Docker`, `Servyx.Web`.

**Acceptance criteria:**

- Connects over `npipe` using the `desktop-linux` Docker context.
- Adopts an existing `palworld-server` container by image repo and the
  `/palworld` mount.
- Dashboard shows state, uptime, CPU, memory, ports, mounts, and network.
- Live log streaming, with backscroll that survives socket drops.
- `.env` (150 vars) and `compose.yaml` both parse and round-trip byte-exact.
- The rendered INI parses, and `OptionSettings` decodes into named members.
- The settings page renders all four `SettingState` columns
  (Desired/Authoritative/Rendered/Runtime) with drift badges.
- The saves page lists the world directory, read-only.
- The backups page lists the image's own `*.tar.gz` files as `Foreign`, with
  no delete/prune/restore control exposed for them.
- All power controls and settings fields are disabled, showing a lock badge
  and a reason.

**Container health must not be conflated with game readiness.** The
`thijsvanloef/palworld-server-docker` image's own `HEALTHCHECK` calls
`http://localhost:8212/v1/api/info` without admin credentials, so it gets
`401 Unauthorized` on every probe (observed `FailingStreak: 293`) and reports
`unhealthy` while the server runs normally — `/v1/api/players` returns `OK`
on the same polling cycle. Servyx therefore derives readiness from its own
declared detectors (log-regex plus an authenticated control-probe fallback),
never from Docker's health status, and displays Docker health only as a
separate, clearly labelled signal. Any control-probe used for readiness must
itself carry authentication, or it is no better than the healthcheck it is
meant to replace.

**Negative tests are first-class in this milestone:**

- An architecture test asserts every transport is write-guarded.
- Integration tests assert `ReplaceAsync`, `DeleteAsync`, `StartAsync`,
  `StopAsync`, and every non-`readOnly` control command throw
  `WritesDisabledException` before any I/O occurs.
- A Docker API call-recorder asserts zero mutating Docker calls occur across
  the entire M1 test run.

## M2 — Read-Only Control Channels

**Goal:** Reach live game state over RCON/REST/A2S without any write path.

**Acceptance criteria:**

- Reachability probing correctly reports `direct-tcp` unavailable for
  25575/8212 and selects `docker-exec-tool` instead.
- `ShowPlayers` is parsed into a live player list on a 30-second poll.
- REST is preferred over RCON when both are available.
- A2S is queried on 27015.
- Non-`readOnly` commands are absent from the UI and rejected server-side if
  attempted.
- Secrets never appear in logs, console output, audit records, or diffs
  (asserted by test).

## M3 — Dry-Run Diff Engine

**Goal:** Produce accurate, previewable change plans without ever applying
them.

**Acceptance criteria:**

- Editing "Max players" produces a plan whose diff touches exactly one line
  of `.env`, with all 149 other lines byte-identical, plus a consequence
  list.
- Changing `PORT` also previews the corresponding `compose.yaml` ports edit
  and flags `requiresRecreate`.
- Binding a write to a `Derived` surface is rejected as a definition
  validation error.
- `ApplyAsync` throws for every plan in this milestone.
- Plans transition to `Stale` when any bound surface's hash changes.

## M4 — Writes Enabled

**Status today:** Shipped in part. A per-server write mode
(`ReadOnly`/`PreviewOnly`/`Enabled`) is real, enforced by the write guard at
every transport, and gated behind both a process-wide provisioning flag and
an explicit per-server grant (see [Enabling writes](user-guide/enabling-writes.md)).
Start/Restart/Stop/Kill execute through the stop ladder with two-step
confirmation (see [Lifecycle control](user-guide/lifecycle-control.md)), and
mutating RCON commands reach the target the same way, with their own
confirm step (see [The RCON console](user-guide/rcon-console.md)).

Configuration writes now have both an engine and an operator-facing caller.
The **engine** has shipped: `IPlanExecutor` is implemented (`PlanExecutor`,
`Servyx.Config`) and DI-registered, `.env`/other authoritative-surface writes
are atomic (temp-file-and-rename), pre-image snapshots are recorded,
`PlanStaleException` is real (a pre-flight sweep re-reads every bound surface
and a per-write TOCTOU check backs it up), and every write is verified two
ways after landing (a transport receipt check, and a genuine
read-back-and-rehash). The **settings tab** is now that caller: recording a
desired value only ever touches Servyx's own database, but a `ChangePlanPanel`
below the grid previews a plan built from recorded values (refusing to preview
while unsaved edits exist) and, behind a two-step confirmation, calls
`ApplyAsync` — an operator can, today, make Servyx write configuration to a
live game server through the UI. The **engine** side of byte-exact revert has
also shipped: `RevertAsync` is implemented, all-or-nothing (it preflights
every action's pre-image availability, integrity, reversibility, and
transport reachability before writing anything) and read-back-verified (each
restoring write is re-read off the server and rehashed against the recorded
pre-image, never just trusted from a transport receipt). What **hasn't**
shipped is an operator surface for it: `ChangePlanPanel` previews and applies
but renders no revert affordance, so in practice the only way back is still a
new plan. Container recreation is also still missing its wiring:
`ServerLifecycleService.RecreateAsync` throws `NotSupportedException`
unconditionally — not because config editing doesn't exist (it does, via
`IPlanExecutor`/`ChangePlanPanel`) but because nothing yet lets an approved
plan carrying a `RecreateRequired` consequence invoke it — so that consequence
still means "sits on disk until a human recreates the container by hand",
even after apply.
No REST API, MCP tool, or job runner calls `PreviewAsync`/`ApplyAsync`/`RevertAsync` — MCP
support in particular was deliberately not built, since a tool call cannot
show a human a diff to approve; wiring one up would mean either a model
self-approving a live config write or a plan id that still routes back through
this same web UI. Partial application is real and reachable (a write can land
and a later one in the same plan fail), and there is deliberately no
auto-repair.

**Goal:** Allow real, guarded, reversible writes.

**Acceptance criteria:**

- Per-server write mode can be flipped, gated by typed confirmation, and the
  change is audited and reversible.
- `.env` writes are atomic, take a pre-image snapshot, and support one-click
  byte-exact revert.
- Applying a stale plan throws `PlanStaleException` instead of clobbering the
  surface.
- Power actions execute through the stop ladder.
- Container recreation shows the exact `docker create` argv used and asserts
  volume preservation.

## M5 — Backups

**Status today:** Shipped. Servyx can create, inspect, restore, and prune
its own archives alongside foreign ones — creation quiesces the server
first where the deployment declares a flush step, restore is previewed and
requires a separate acknowledgement before it overwrites live data, and
retention always previews as a dry run and never selects a foreign archive.
All of it is gated behind provisioning plus a per-server write grant — see
[Backups and saves](user-guide/backups-and-saves.md). Not verified in this
pass against the acceptance criteria below word-for-word (e.g. the exact
archive contents and exclusions).

**Goal:** First-class, Servyx-owned backup lifecycle alongside foreign ones.

**Acceptance criteria:**

- On-demand backup quiesces the server via RCON `Save`.
- Archives include saves, `.env`, `compose.yaml`, and the rendered INI.
- Archives exclude `data/backups/**`.
- Retention pruning only ever removes Servyx-owned artifacts; a test asserts
  foreign tarballs survive every prune path.

## M6 — Minecraft (Proves the Abstraction)

**Status today:** Shipped, and then some. `definitions/minecraft-itzg.yaml`
adopts, reads settings, and controls lifecycle/backups entirely through the
generic, definition-driven code paths — no Minecraft-specific C# outside
format adapters and the RCON dialect, exactly as this milestone's own
acceptance criterion demanded. The abstraction has since been proven twice
more: `definitions/ark-asa-pok.yaml` (ARK: Survival Ascended) and
`definitions/factorio-factoriotools.yaml` (Factorio) both ship as full
fourth/fifth definitions, and an architecture test
(`GameNameLiteralSourceScanTests`) enforces the "no C# changes outside
format adapters and the RCON dialect" rule going forward, not just at M6's
own review. See [Supported games](games.md) for what each one covers,
including which player-list reply shapes remain unverified pending a
real-server capture.

**Goal:** Validate that the role-based configuration model generalizes,
using a deployment where the truth direction is mirrored relative to
Palworld.

**Acceptance criteria:**

- An `itzg/minecraft-server` container adopts cleanly — this is the
  mirror-image case, where env vars are authoritative **and**
  `server.properties` is directly writable.
- **No C# changes outside format adapters and the RCON dialect.** If more is
  required, the abstraction has failed and M6 becomes a refactor milestone
  instead.

## M7 — Identity, RBAC, Secrets, Audit UI

**Goal:** Everything required before Servyx is bound to anything other than
loopback.

## M8 — Remote and Non-Docker Targets

**Status today:** The `ssh+docker` transport has shipped: SSH exec and SFTP
compose as independent channels, host keys are pinned (trust-on-first-use
or a configured fingerprint) and fail closed, and a declared remote host's
Docker calls route over it — see [Connecting a host](user-guide/connecting-a-host.md)
and [Adopting a remote host](user-guide/adopting-a-remote-host.md). Only the
first configured `Servyx:Hosts` entry is wired to anything at present. A
local-process target exists for provisioning and for backups
(`Servyx.Infrastructure.Process`), but full bare-process-host parity with
the Docker adoption/lifecycle path was not independently re-verified in
this pass.

**Goal:** Extend beyond local Docker to bare process hosts and SSH targets.

**Acceptance criteria:**

- Bare process hosts and SSH hosts are supported targets.
- An injection test registers a host literally named `; rm -rf /` and proves
  the name is never interpreted as a shell fragment.

## M9 — Provisioning, Mods, Plugin SDK

**Status today:** Provisioning has shipped; mods and the plugin SDK have
not. The Deploy page (`/deploy`) is gated behind its own
`Servyx:Provisioning:Enabled` flag and offers eight registered provisioners
(`aws-ec2`, `aws-ecs-fargate`, `aws-lightsail`, `azure-vm`,
`azure-container-instance`, `digitalocean-droplet`, `docker-container`,
`local-process`), cost estimation, and a preview → apply → ledger → drift
pipeline with an explicit data-impact acknowledgement — see
[Deploying a server](user-guide/deploying-a-server.md). Mods and Plugins
remain the placeholder pages this milestone originally planned to replace.

**Goal:** Provision new servers, install mods, and open the platform to
third-party plugin authors.

**Acceptance criteria:**

- Installer runs in a sandboxed, non-root container with an egress
  allowlist.
- Definitions requesting `shell` are refused at `Unverified` trust.

## Open Questions

1. **Does Servyx get authority to recreate containers?** This blocks M4.
2. **Does Servyx own `compose.yaml`, or only `.env`?** Recommendation: `.env`
   is fully managed; `compose.yaml` is restricted to each definition's own
   declared `managedSubtree` (e.g. `services.palworld`, `services.minecraft`),
   with per-change confirmation.
3. **Should the plaintext `.env` passwords be mirrored into the secret
   store?**
4. **Is Windows a production platform, or dev-only?** Recommendation: Linux
   is the production target; Windows + Docker Desktop is supported for the
   Docker transport, but treated as a development environment.
5. **Where does Servyx itself run?** Recommendation: as a host process, since
   running inside a container would require a root-equivalent Docker socket
   mount.

Questions 1 and 2 block **M4**, but do **not** block M1–M3.

## Resolved

- The REST API is already enabled in the target's `.env`
  (`REST_API_ENABLED=True`, port 8212), so M2 can prefer the REST path
  without requiring any write.
- Persistence is SQLite with WAL mode by default, with PostgreSQL available
  as an opt-in alternative.
