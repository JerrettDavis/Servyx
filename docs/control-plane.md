# The Control Plane: Graduated Control

## Framing

Servyx talks to game servers over wildly different pipes: a bare-metal box reachable by SSH and SFTP with a live RCON port; a Docker container adopted read-only over a socket with no filesystem access at all; a Docker container Servyx itself created, with full compose control. These deployments do not sit on a single line from "less capable" to "more capable." A bare-metal SSH box with direct `.ini` write and RCON access is *more* capable than a socket-only adopted container that cannot write anything — and a socket-only container with a writable bind mount can do things the SSH box cannot.

The four control mechanisms Servyx has available — direct `.ini` write, `.env` write, `compose.yaml` edit, live socket/RCON — are **alternative mechanisms for the same user intent, not an ordered scale**. "Change the max player count" can be satisfied by any one of them, on any given deployment, depending on what that deployment happens to expose. There is no canonical order in which a server "unlocks" these; a server can have the third and not the first.

What the user sees is still a slider — Blind, Observe, Configure, Operate, Provision — because that is the right mental model for "how much can Servyx do for me right now." The slider is real, but it is not built by ranking mechanisms. It is built from **tiers**, and a tier is satisfied by **any** qualifying mechanism among the ones available on this deployment. That is the whole "take what we can get" semantic: Servyx does not require a specific mechanism to reach a specific tier, it requires *some* mechanism that gets the same job done.

## Two layers

Servyx already has `TransportCapabilities`, and it stays exactly what it is: an answer to "what can this pipe physically do?" It is a property of the transport *class* — static, and identical for every Docker server on the box, regardless of which container, which user, or which file permissions are in play. `TransportCapabilities` never changes at runtime and is never mutated by anything downstream.

This document adds a second layer, `ControlCapability`, which answers a different question: "what may Servyx do to *this* server, *right now*, given this host's file permissions, this socket's ACL, this container's entrypoint, and this game definition?" That answer is per-server, dynamic, evidence-backed, and it expires. It depends on facts `TransportCapabilities` cannot see — the owning uid of a config file, whether a bind mount is read-only, whether RCON is enabled in this particular `.env`.

`TransportCapabilities` is an *input* to the `ControlCapability` evaluator, never an output of it, and evaluating control capability never writes back to the transport layer.

**Flags for the math, records for the meaning.** Capability *identity* is a `[Flags] enum : ulong`, so that `(granted & required) == required` stays a hot-path bitmask operation usable in every capability check on the UI thread. Capability *justification* — why does the panel believe this, what would unlock more — is a rich record keyed by that enum. The two never merge: the enum answers "yes or no, fast," the record answers "why, and what next."

```csharp
[Flags]
public enum ControlCapability : ulong
{
    None                     = 0,
    ReadRuntimeState         = 1UL << 0,
    StreamLogs               = 1UL << 1,
    ReadMetrics              = 1UL << 2,
    ReadDerivedConfig        = 1UL << 3,
    ReadAuthoritativeConfig  = 1UL << 4,
    ReadEnvFile              = 1UL << 5,
    ReadComposeFile          = 1UL << 6,
    WriteAuthoritativeConfig = 1UL << 7,
    WriteEnvFile              = 1UL << 8,
    WriteComposeFile          = 1UL << 9,
    StartWorkload             = 1UL << 10,
    StopWorkloadGraceful      = 1UL << 11,
    SignalProcess             = 1UL << 12,
    KillWorkload              = 1UL << 13,
    RecreateWorkload          = 1UL << 14,
    CreateWorkload            = 1UL << 15,
    DestroyWorkload           = 1UL << 16,
    ExecInWorkload            = 1UL << 17,
    AttachStdin               = 1UL << 18,
    ControlChannelRead        = 1UL << 19,
    ControlChannelWrite       = 1UL << 20,
    PortForward               = 1UL << 21,
    ReadSaveData              = 1UL << 22,
    WriteSaveData             = 1UL << 23,
    CreateBackup              = 1UL << 24,
    RestoreBackup             = 1UL << 25,
    InstallMods               = 1UL << 26,
}
```

27 members, bit positions 0–26. Room remains up to bit 63 without a breaking change to the underlying type.

## Evidence and remediation

Every capability grant carries a confidence level, not a boolean:

```csharp
public enum CapabilityConfidence
{
    Denied,
    Unknown,
    Inferred,
    Verified,
}
```

State this prominently, because it is the single easiest thing to get wrong when wiring this into the UI: **`Unknown` is not `Denied`**. `Unknown` means Servyx has not (yet, or cannot) determine the answer — the relevant probe didn't run, threw, or the required transport capability isn't available. `Denied` means Servyx checked and was told no. Rendering "unknown" as "no" is how a panel ends up telling a user their working server can't do something it demonstrably can, because a probe timed out once. Every surface that displays capability state must have a visually distinct treatment for `Unknown` — never collapse it into the same red/disabled state as `Denied`.

```csharp
public sealed record CapabilityEvidence(
    string ProbeId,
    string Summary,
    string Detail,
    DateTimeOffset ObservedAt);
```

Example `Detail`: `"stat /srv/palworld/config: mode 0755 owner uid=0, connector uid=1000"`. Evidence is meant to be read by a human debugging "why doesn't this work," not just logged.

```csharp
public enum RemediationActor
{
    EndUser,
    HostAdmin,
    Servyx,
}

public sealed record RemediationHint(
    string Code,
    string Summary,
    string? SuggestedCommand,
    RemediationActor Actor,
    ControlCapability Unlocks,
    string? DocsUrl);
```

Codes are stable strings like `SVX-CAP-0041`, so a hint can be linked to from documentation, support threads, and telemetry without the summary text becoming a compatibility surface.

```csharp
public sealed record CapabilityGrant(
    ControlCapability Capability,
    CapabilityConfidence Confidence,
    IReadOnlyList<CapabilityEvidence> Evidence,
    IReadOnlyList<RemediationHint> Remediations);

public sealed record ControlCapabilitySet(
    ControlCapability Granted,     // Verified | Inferred
    ControlCapability Verified,
    ControlCapability Probed,      // capabilities a probe actually ran for
    IReadOnlyList<CapabilityGrant> Grants,
    DateTimeOffset EvaluatedAt,
    string Fingerprint);
```

`Fingerprint` is a SHA-256 hash over the ordered `(capability, confidence, probeId)` triples in `Grants`. It is cheap to compare, so it drives two different things: UI change detection (re-render only when the fingerprint moves) and apply-time staleness checks (a plan captured a fingerprint at preview time; if the fingerprint has moved by apply time, the plan is stale — see "Graceful degradation" below).

## Tiers

```csharp
public enum ControlTier
{
    Blind,
    Observe,
    Configure,
    Operate,
    Provision,
}
```

A plain bitmask cannot express "any write mechanism will do," so tier requirements are a small expression tree rather than a single required mask:

```csharp
public abstract record CapabilityRequirement
{
    public sealed record All(ControlCapability Mask) : CapabilityRequirement;
    public sealed record AnyOf(IReadOnlyList<CapabilityRequirement> Alternatives) : CapabilityRequirement;
    public sealed record Every(IReadOnlyList<CapabilityRequirement> Parts) : CapabilityRequirement;
}
```

`All(mask)` is satisfied when every bit in `mask` is granted. `AnyOf` is satisfied when at least one alternative is satisfied. `Every` is satisfied when all parts are satisfied — it exists mainly so a tier's requirement can nest a previous tier's requirement (`Configure` requires everything `Observe` requires, plus more) without repeating it.

| Tier | Requirement | Recommended (drives `IsDegraded`, not gating) | User summary |
|---|---|---|---|
| **Observe** | `All(ReadRuntimeState)` | `StreamLogs \| ReadMetrics \| ReadDerivedConfig` | "Servyx can see this server is running and watch it." |
| **Configure** | `Every(Observe, All(ReadAuthoritativeConfig), AnyOf(WriteAuthoritativeConfig, WriteEnvFile, WriteComposeFile), All(StartWorkload \| StopWorkloadGraceful))` | `CreateBackup` | "Servyx can change settings and restart this server." |
| **Operate** | `Every(Configure, All(CreateBackup \| RestoreBackup), AnyOf(ExecInWorkload, ControlChannelWrite))` | — | "Servyx can back up, restore, and act on the running server live." |
| **Provision** | `Every(Operate, All(WriteComposeFile \| RecreateWorkload \| CreateWorkload))` | — | "Servyx can create, recreate, and fully manage this deployment." |

Note the shape of `Configure`'s write requirement: `AnyOf(WriteAuthoritativeConfig, WriteEnvFile, WriteComposeFile)`. Any one of the three write mechanisms qualifies. A deployment that can only write `.env` and restart reaches `Configure` exactly as validly as one that can write the `.ini` directly — the *mechanism* differs and that difference is surfaced elsewhere (see "Config write ladder"), but the *tier* does not care which mechanism got there.

```csharp
public sealed record TierGap(
    ControlTier CurrentTier,
    bool IsDegraded,
    IReadOnlyList<RemediationHint> ToNextTier);
```

`IsDegraded` is true when the tier's *requirement* is satisfied but a *recommended* capability for that tier is absent — the tier is genuinely held, just not at full strength. `TierGap` is what drives copy like:

> **Configure** · degraded (no backups)
> To reach **Operate**: enable RCON in `.env` and publish port 25575 — *or* grant the Servyx user access to `/var/run/docker.sock`.

Note the "or" in that copy is not decoration — it is `AnyOf` rendered as prose, offered as two independent, equally valid paths.

## Probing

```csharp
public enum ProbeDepth
{
    Passive,
    Active,
}
```

**Passive** probes read metadata only — `stat`, directory listing, container `inspect`, a read-only API call — and **never mutate** anything. Capability is *inferred* from what that metadata implies: file mode and owner versus the connector's uid/gid, mount flags, entrypoint contents. This inference is correct roughly 95% of the time and wrong under POSIX ACLs, filesystem immutable bits, SELinux/AppArmor contexts, and read-only bind mounts. The read-only-bind-mount case is common enough (it's exactly what a cautious adopted-container setup looks like) that it is detected explicitly rather than left to fall out of a generic permission check — a read-only mount is asserted directly from the mount's flags, not inferred from a write attempt that never happens.

**Active** probing may create and immediately delete a zero-byte probe file **in the target directory** — the same filesystem as the real write would use, not `/tmp`, because a same-filesystem probe is the only one that actually answers the question ("can I write and rename here") rather than a nearby question ("can I write somewhere"). Active probing runs only inside the add-server wizard, behind an explicit "Test write access" button — never in the background, never on a schedule. A successful active probe upgrades a capability's confidence from `Inferred` to `Verified`.

This split keeps every background and scheduled probe genuinely non-mutating — a read-only Servyx installation never touches a byte on disk it wasn't explicitly told to — while still giving a truthful, verified answer at the one moment the user is actually paying attention and has asked for one.

**Evaluator rules:**

- A probe whose `RequiresTransport` capability isn't available on the underlying `TransportCapabilities` is skipped entirely and recorded as `Unknown`, with evidence naming the missing transport capability (e.g. `"probe skipped: transport does not support FileRead"`).
- A probe that throws yields `Unknown` for the capabilities it would have determined, and **every other probe still runs and contributes its result** — one broken probe never fails the whole evaluation. The evaluator isolates probe execution accordingly (independent try/catch per probe, not a single try/catch around the batch).
- Cancellation propagates normally: a cancelled evaluation cancels in-flight probes and does not partially cache a result.

Results are **cached per server with a 5-minute TTL**, invalidated early by: a connector edit, a container recreate, a plan apply (successful or not — capability may have changed either way), and a manual refresh triggered by the user. The cache is keyed by server, not by connector, since two servers behind the same connector can have different capability sets (different file ownership, different container entrypoints).

## Graceful degradation

**The rule, stated once and enforced everywhere below `IPlanExecutor`: no code beneath that boundary throws for a capability reason.** Capability is fully resolved during preview, before the user commits to anything, so the UI has the true picture — what will happen, what won't, why — *before* the "Apply" button is even enabled to be pressed. This inverts the failure mode that capability-unaware code naturally falls into, where a `NotSupportedException` surfaces from deep inside a file writer partway through applying a plan. By the time execution starts, capability is a known quantity; if it changes between preview and apply, that is handled explicitly as staleness (below), not as an exception escaping a plan step.

`ConfigChangePlan` — which already exists — is extended, not replaced:

```csharp
public enum PlanFeasibility
{
    FullyAchievable,
    PartiallyAchievable,
    Blocked,
}

public enum RestartImpact
{
    None,
    RestartRequired,
    RecreateRequired,
    DataLossRisk,
}

public sealed record PlanStep(
    string Id,
    string Description,
    ControlCapability Requires,
    string SurfaceId,
    string TargetRole,
    string Kind,
    RestartImpact Impact);

public sealed record BlockedChange(
    string SettingKey,
    string Reason,
    IReadOnlyList<string> MissingAlternatives,
    IReadOnlyList<RemediationHint> Remediations,
    ControlTier UnlockedAtTier);
```

`ConfigChangePlan` gains `Feasibility`, a list of `PlanStep`, a list of `BlockedChange`, and a `CapabilityFingerprint` captured at preview time.

Three consequences follow from this design:

1. **The UI never guesses.** A plan with eight requested settings and two blocked ones renders "Apply 6 of 8," with the blocked rows explained inline. This reuses the *existing* `SettingState.IsWritable` / `NotWritableReason` fields — no new UI-facing columns are needed, because `BlockedChange.Reason` and `.Remediations` slot directly into the existing not-writable explanation slot.
2. **Mechanism selection becomes visible copy**, and this *is* the sliding-control feature made legible to the user, not an internal implementation detail: *"via `PalWorldSettings.ini` (direct, no restart)"* versus *"via `.env` (container restart required)"* versus *"via RCON (live, not persisted)"*. The user is told not just *that* a setting will change but *how*, because the how determines whether it survives a restart, whether the server bounces, and whether it's safe to do while players are online.
3. **Apply re-checks the fingerprint.** If it has moved since preview, apply throws `PlanStaleException`, naming the specific capabilities that were lost since the plan was captured, rather than attempting a subset of the plan under stale assumptions.

**Explicit non-goal: there is no "force" flag, anywhere in this design, and none should be added later.** If the capability isn't there, the write either cannot happen at all, or it would happen and be silently reverted at the next container boot (a `Derived`-surface write — see below). Both outcomes are worse than a refusal that carries a remediation: a refusal at least tells the truth about what will happen next.

## Config write ladder

Four mechanisms, in increasing order of directness and decreasing order of universal availability. A given deployment may offer any subset of these, in any combination — the ladder describes *how each rung works*, not a sequence a deployment climbs.

### 1. Direct `.ini` write (local or SFTP)

1. Read the pre-image of the file.
2. Verify its SHA-256 matches what Servyx last observed (detects concurrent external edits).
3. Write the new content to `<name>.servyx-tmp-{guid}` **in the same directory as the target** — rename is only atomic within a single filesystem, and a `/tmp` staging path silently degrades to a non-atomic copy-and-truncate the moment the target is on a different filesystem or a different SFTP root.
4. Apply the original file's mode and owner to the temp file.
5. `fsync` the temp file.
6. Rename the temp file over the target.
7. `fsync` the containing directory (the rename itself is not durable until the directory entry is synced).
8. Retain one prior version as `<name>.servyx.bak`.

Risks specific to this rung:

- SFTP rename is not universally atomic. Probe for the `posix-rename@openssh.com` extension and downgrade to truncate-and-write with a visible warning when it's absent, rather than silently assuming atomicity the server doesn't provide.
- Ownership handling must never silently produce a root-owned config file that the game process can no longer read after restart — applying "the original owner" is not optional, and if the connector's identity cannot set that owner, the write should be refused rather than proceeding with the wrong owner.
- Files that are memory-mapped or held open by the running game process require writing while the process is stopped; a live rename under an mmap'd file is not something this design assumes is safe.

### 2. `.env` write + regenerating restart

Backup the current `.env` → write the new one → stop the workload (via the stop ladder) → start it → **verify regeneration** by re-reading the *derived* surface (the file the entrypoint templates from the env at startup) and asserting the new value is actually present in it. If it is not, the plan step is marked `RegenerationFailed` and the plan reports partial success rather than claiming victory on the strength of "the restart completed without error." This verification step is the one everyone skips, and it is the difference between "we changed the env var" and "we changed the setting."

### 3. `compose.yaml` write + recreate

Before any bytes are written, a `ComposeMutationValidator` runs against the *rendered post-image* of the file and rejects the change if it detects:

- any named or external volume present in the pre-image and absent from the post-image,
- any bind-mount source path change in a plan that was only supposed to be a settings edit,
- any service or container name change after adoption (the container name is the adoption key — changing it silently orphans the adoption),
- any delta in the rendered diff that isn't explained by the intended edit set.

Recreate is performed with `up -d --no-deps <service>` semantics — targeted at the one service, never touching sibling services in the same compose file — and **never** `down -v`, which would remove volumes.

### 4. Live control channel (RCON, in-game console, etc.)

Changes made this way usually don't persist across a restart; they take effect immediately on the running process and are gone the next time it starts unless separately written to a persisted surface. This is the fastest rung and the least durable one, and the UI's "via RCON (live, not persisted)" copy exists specifically so this tradeoff is never implicit.

### Surface role is per-deployment, not per-file

```csharp
public enum SurfaceRole
{
    Unavailable,
    Derived,
    Authoritative,
}
```

The same file path and the same format can be either role depending on the deployment: `PalWorldSettings.ini` is `Derived` under the standard compose image, because the container's entrypoint templates it from environment variables on every start — any direct edit is overwritten within seconds of the next boot. The identical path is `Authoritative` on a bare-metal SteamCMD install, where nothing regenerates it and a direct edit is the only way the setting is controlled.

**Writing to a `Derived` surface is refused unconditionally — no override, no expert mode, no confirmation dialog that bypasses it.** The reasoning is not caution for its own sake: the write would be silently reverted at the next boot, and a change that appears to succeed and then vanishes three hours later, unattended, is a strictly worse user experience than a refusal today. The refusal message names the surface that *is* authoritative for that value on this deployment, so the user has a next step rather than a dead end.

### Drift

New `DriftKind` flags, layered onto the existing drift-detection model:

| Flag | Meaning | Treatment |
|---|---|---|
| `LiveOnly` | Applied at runtime via the control channel, present in no authoritative surface | Amber "will be lost on restart" badge, with a one-click *Persist to `.env`* action |
| `PersistedNotApplied` | Written to an authoritative surface, not yet reflected in the running process | Blue "restart to apply" badge |
| `RegenerationFailed` | `.env` write completed, but the derived surface does not show the expected value after restart | Surfaced as a plan failure, not silently cleared |
| `SurfaceUnreadable` | A surface that should be checked for drift could not be read | See below |

`SurfaceUnreadable` exists to prevent a specific lie: without it, an unreadable surface renders identically to a surface that was read and found to agree — "no drift" — and the panel confidently reports agreement when in fact it simply didn't look. An unreadable surface must render as "unknown," visually distinct from both "drifted" and "in sync," for the same reason `Unknown` confidence must never collapse into `Denied` above.

## Config document fidelity (prerequisite)

The existing `ConfigDocument(object Root, IReadOnlyList<string> RawLines)` type cannot currently guarantee round-trip fidelity, because `Render` has to choose between two lossy options: re-serialize `Root` (which loses comments, key order, and quoting style) or echo `RawLines` back unchanged (which ignores any edits that were made). This has not mattered yet because nothing in the codebase writes configuration back out — it becomes a real problem the moment rung 1 of the write ladder exists.

The fix is to stop treating `Root` as the thing that gets rendered:

```csharp
public sealed record ConfigSpan(
    string Pointer,
    int LineIndex,
    int ValueStart,
    int ValueLength,
    char? QuoteStyle);
```

`RawLines` becomes authoritative for rendering, full stop. `ConfigSpan` records exactly where in `RawLines` each parsed value lives, so an edit is applied as a text splice against `RawLines` at the recorded span, not as a re-serialization of `Root`. `Root` remains useful for reading and for diffing, but it is never the source of a write. An explicit `LineEnding` field is required rather than inferred, because guessing line endings on write is a reliable way to corrupt a file that was, say, checked out on Windows and deployed on Linux.

With this change, fidelity holds **by construction**, even for parsers that are individually lossy — the parser only has to locate spans correctly, not preserve everything it didn't understand, because the splice never touches anything the parser didn't explicitly change.

One more piece is needed for Palworld specifically: `MergeAll(document, edits, policy)`. Palworld stores roughly 90 settings inside a single `OptionSettings=(...)` scalar. Folding N individual setting changes into that file one at a time means decoding and re-encoding the entire `OptionSettings` blob N times — and each of those N round-trips has to perfectly reproduce every key it didn't touch, in original order, or fidelity degrades one edit at a time even though each individual edit "worked." `MergeAll` applies every pending edit to the blob in a single decode/merge/encode pass, so there is exactly one re-encoding per plan apply, not one per setting.

## Capability watchdog

Beyond the 5-minute TTL cache, a passive re-probe runs every 15 minutes unconditionally, and immediately after: a plan apply, a container recreate, a connector edit, and a manual refresh. Each re-probe compares the new `Fingerprint` against the stored baseline for that server.

- **Downgrade** (capability lost): the tier badge turns to "degraded," annotated with a timestamp and a plain-language cause — e.g. "Operate → Observe at 14:02 — Docker socket permission denied." Every control that depended on the lost capability is disabled, with the relevant remediation surfaced as its tooltip. Any plan that was queued and depended on the lost capability moves to `Blocked` and is **never silently dropped** — it stays visible, in a blocked state, until the user acknowledges it or the capability returns.
- **Upgrade** (capability gained): a dismissible toast offers the user a review of what's newly possible. **Write mode is never escalated automatically** — gaining a capability changes what the UI offers, never what a pending or scheduled plan is allowed to do without the user re-confirming.
- **Host key changed**: this is not treated as a capability downgrade at all — it is a security event. A red banner appears, the connector is evicted immediately, and all operations against that connector halt pending an explicit re-pin (see `docs/connectors.md`, "Host key trust").

All of these transitions — downgrade, upgrade, host key change — append an entry to the audit log, so that "why did this stop working on Tuesday" always has a concrete, timestamped answer rather than requiring the user to reconstruct it from memory.
