# Game Definition Schema Reference (`servyx.dev/v1`)

This document is a field-by-field reference for the `servyx.dev/v1`
`GameDefinition` schema, illustrated throughout with the four definitions
shipped in `definitions/`: `palworld-docker.yaml`, `minecraft-itzg.yaml`,
`ark-asa-pok.yaml`, and `factorio-factoriotools.yaml`.

> **Implementation status.** Every definition under `definitions/` is loaded
> by `FileSystemGameDefinitionProvider` and fully parsed and validated by
> `GameDefinitionYamlParser` — there is no longer a single hardcoded loader
> for one game. (An earlier phase of this project had exactly that,
> `PalworldDefinitionLoader`, which read only a handful of top-level blocks
> for a single bundled file; it has since been retired in favor of the
> generic, multi-definition catalog described below.) Servyx can load any
> number of definitions from the configured directory at once, and most of
> the schema described in this document now drives real runtime behavior —
> adoption, settings, lifecycle, control, backups, and saves are all
> definition-driven today, not just Palworld's. A handful of blocks remain
> **declared only**: present in the YAML and described below, but not yet
> read by any runtime code path. Each section below is marked **Parsed** or
> **Declared only** to make this explicit, and where a field within an
> otherwise-parsed block is itself unread, that is called out specifically.

## Loading and adoption

`FileSystemGameDefinitionProvider` enumerates every `.yaml`/`.yml` file (and
`definition.yaml` bundle directory) under the configured definitions
directory, parses each with `GameDefinitionYamlParser`, and exposes the
result as a `GameDefinitionCatalog`. A malformed file, an unrecognized
`apiVersion`, or a duplicate `metadata.id` degrades to a recorded fault
rather than crashing the host or hiding the other, good definitions.

A discovered container is matched against **every** loaded definition's
`deployments[].detect` rule (`imageRepo` plus `requiredMounts`), not just one
hardcoded definition. `ServerBindingResolver` resolves each server to
exactly one governing definition when precisely one candidate matches,
`Ambiguous` when two or more definitions match with equal specificity, or
`NeedsRebind` when a server was previously bound to a definition content
hash no longer resolvable in the catalog. The `/games` page lists every
loaded definition as a card.

Every `LoadedDefinition` is `TrustTier.Unverified` today: `IDefinitionTrustEvaluator`
has no implementation yet, so no definition — signed or not, `shell`-declaring
or not — is evaluated against a trust tier. This matters for the
`capabilities`/`signature` blocks and the `shell: true` validation rule
below.

## Top-Level Blocks

### `metadata` — Parsed

Identifies the definition itself.

| Field | Purpose |
|---|---|
| `id` | Stable identifier for the game (`palworld`, `minecraft-itzg`, `ark-asa-pok`, `factorio-factoriotools`). |
| `name` | Human-readable display name. |
| `version` | Definition version string. Note: servers pin definitions by **content hash**, not by this field — see the Validation Rules section. |
| `license` | License of the definition content. |
| `tags` | Free-form classification tags (`survival`, `steam`, `unreal`, …). |

### `capabilities` — Declared only

The declared blast radius of the deployment, checked against trust tier
restrictions before anything runs. No code path reads this block yet — there
is no trust-tier check that consults it today, matching `signature`'s status
below.

| Field | Purpose |
|---|---|
| `network` | List of ports the workload uses, each with `port`, `protocol`, `purpose`, the `.env` variable it is sourced from (`var`), and whether it is `published` to the host by default. |
| `filesystem` | Paths the workload touches, with `access` (`rw`/`ro`) and a human `purpose` string. |
| `egress` | Outbound network destinations the workload is allowed to reach. Empty for every shipped definition — each image self-updates or its exact egress hosts could not be confirmed, so nothing is guessed at (see e.g. `ark-asa-pok.yaml`'s comment on this). |
| `shell` | Whether any step in this definition requires shell execution. `false` for every shipped definition. |
| `privileged` | Whether the workload requires privileged mode. |
| `hostNetwork` | Whether the workload requires host networking. |

### `deployments` — Parsed

A definition may describe more than one deployment **profile** for the same
game — Palworld ships two: `docker-thijsvanloef` (the Docker image) and
`native-steamcmd` (bare metal). Each profile independently declares:

- `id`, `kind` (`docker` | `process`), and detection/execution details
  (`detect`, `image`, `dataDir`, `stopTimeout`, `stopGracePeriodSeconds` for
  Docker; `executable`, `install` for process).
- `detect` (`imageRepo` plus `requiredMounts`) drives real adoption matching
  today, across every loaded definition — see "Loading and adoption" above.
- `stopGracePeriodSeconds` (optional, whole seconds) is how long the
  container runtime itself waits before force-killing — Docker's own default
  is **ten seconds**, which truncates the save of any game whose graceful
  shutdown takes longer. It must be at least the sum of the `lifecycle.stop`
  stage timeouts; a shorter value is a validation error, because the runtime
  would force-kill part-way through the ladder and silently defeat it. All
  four shipped definitions set this and size it above their own ladder total
  with headroom (Palworld 100s over a 90s ladder; Minecraft 200s over 180s;
  ARK 240s over 210s; Factorio 300s over 270s). The value reaches a
  provisioned container end to end: `DeployPage.DeriveDockerDefaults` reads
  the selected definition's Docker profile's declared grace period and
  hands it to `ProvisionerFormCatalog` as the `stop-grace-period-seconds`
  field's default (editable, like the image and port fields beside it,
  never invented when the definition declares none); building the request
  writes it into the `stopGracePeriodSeconds` provisioning parameter;
  `DockerContainerProvisioner.BuildSpec` parses that parameter into
  `DockerContainerSpec.StopGracePeriod` — throwing on a malformed or
  non-positive value rather than silently falling back to the daemon's
  10-second default — which becomes the created container's own
  `StopTimeout`. `DeployPageGameSelectionTests.OneDefinition_DeclaredStopGracePeriodReachesTheProvisioningRequest`
  and `DockerContainerProvisionerTests` together pin the whole path.
- `stopTimeout` is parsed and carried on the model (`DeploymentProfile.StopTimeout`)
  and every shipped definition declares one, but **no runtime code path
  reads it today** — it has no consumer anywhere outside the parser and the
  model itself. It is kept, not removed, specifically *because* every
  shipped definition declares it: dropping the key from the accepted schema
  would turn real, working files into hard parse errors, which is worse
  than an inert field. Treat it as reserved documentation of Servyx's
  originally-intended own orchestration budget (distinct from
  `stopGracePeriodSeconds`, which is the container runtime's own kill timer
  and *is* wired through end to end) — the model's own XML doc comment on
  `DeploymentProfile.StopTimeout` says so explicitly, so a reader who lands
  on the field in code sees the same warning a reader of this doc does.
- `files` (optional) seeds file content into the deployment's own storage
  *before* the workload starts for the very first time — see "Seeded files"
  below.
- Its own `config.surfaces` list, since the same underlying setting can live
  on a different surface — and even in a different **role** — depending on
  the profile. This is the schema's core departure from a flat egg format:
  see "Configuration surfaces" below. Surfaces drive real settings bindings,
  drift computation, and (for `authoritative` surfaces) writes.
- An `ignored` list of paths that exist but are deliberately excluded from
  binding and backup, with a `reason` shown in the UI. **Declared only** —
  parsed onto the model, but no runtime code path (UI or otherwise) reads it
  today; `factorio-factoriotools.yaml` uses it for `map-gen-settings.json`/
  `map-settings.json`, which apply only at map-generation time.

#### Seeded files (`deployments[].files`) — Parsed

```yaml
files:
  - path: "${DATA_DIR}/config/rconpw"
    mode: "0600"
    createOnly: true
    contentFrom: "secret:rcon-password"
```
(from `definitions/factorio-factoriotools.yaml`)

Each entry has:

| Field | Purpose |
|---|---|
| `path` | Where the file lands, templated with `${DATA_DIR}` or `${COMPOSE_DIR}` — and it must **start with** one of those two tokens specifically, not just avoid escaping outside the root the way every other path-like field in this schema does. An OS-absolute path, a literal `..` segment, or a percent-encoded `..` (`%2e%2e/`, `..%2f`) are all rejected outright. |
| `mode` | POSIX permission bits as an octal string matching `^0[0-7]{3}$`. Defaults to `0600`. |
| `createOnly` | Whether an already-present file is left untouched. Defaults to `true`. |
| `contentFrom` | A `secret:key` reference whose resolved value becomes the file's content. Mutually exclusive with `content`; exactly one is required. |
| `content` | Literal, checked-in file content. Mutually exclusive with `contentFrom`. |

**Why this exists.** Some images accept no environment variable for a
credential: they generate one themselves on first boot and write it into a
file, only when that file is absent. `factoriotools/factorio` does exactly
this for its RCON password (`/factorio/config/rconpw`) — Servyx cannot learn
a value invented inside a container it does not yet control, so the only way
to make the value knowable in advance is to seed the file with a known value
*before* the entrypoint ever runs, which is what `files[]` and its default
`createOnly: true` behavior are for. The same secret key is then referenced
by the control channel's `passwordRef`, so the seeded value and the
authenticating value can never drift apart.

Seeding a file is a write into the deployment's own storage, so it goes
through the same write-guard machinery as every other mutating operation —
an unwritable server does not get files seeded onto it — and any content
resolved from `contentFrom` is masked in the UI and logs the same way any
other secret-bound value is.

#### Configuration surfaces — Parsed

Each entry under `config.surfaces` describes one place configuration lives:

| Field | Purpose |
|---|---|
| `id` | Surface identifier, referenced from `settings[].bindings`. |
| `role` | `authoritative` (Servyx may write), `derived` (Servyx reads only — the workload regenerates it), or `runtime` (live state over a control channel). |
| `format` | Parser to use: `dotenv`, `yaml`, `ini`, `json`, or `properties`. All five have a shipped `IConfigAdapter` implementation (`Servyx.Config`) and are exercised by at least one shipped definition. |
| `codec` / `codecPath` | Optional value codec for a structured payload embedded in a single scalar (`unreal-option-settings` for `OptionSettings`), and the path within the parsed document where it applies. |
| `locator` | Where the surface physically lives — `host-file` with a `path`, or `control-channel` with a `channel` and `query`. |
| `managedSubtree` | For `yaml`/structured formats, restricts writes to a specific subtree (`services.palworld`, `services.minecraft`, …) rather than the whole document. |
| `mergePolicy` | How unmanaged content is treated on write; `preserve-unknown` is the default and, in practice, non-negotiable. |
| `derivedFrom` | For `derived`/`runtime` surfaces, which upstream surface(s) they are generated from — this is what drift detection compares against. |
| `regeneration` | For `derived` surfaces, how and when the surface gets regenerated (`kind: container-restart` plus a human `description`). |

**JSON surfaces (`format: json`).** `factorio-factoriotools.yaml`'s
`server-settings` surface is the first shipped `json`-format surface,
addressed with RFC 6901 JSON-pointer bindings rather than a flat key or an
ini codec member:

```yaml
- id: server-settings
  role: authoritative
  format: json
  locator: { kind: host-file, path: "${DATA_DIR}/config/server-settings.json" }
  mergePolicy: preserve-unknown
```
```yaml
- key: visibility_public
  label: Advertise publicly
  type: bool
  bindings:
    - { surface: server-settings, direction: write, pointer: "/visibility/public" }
```

`JsonConfigAdapter` (`Servyx.Config`) parses and renders RFC 8259 JSON,
recording the exact character span each scalar occupies rather than
re-serializing the document. That gives it the same round-trip guarantees
the other adapters have: key order, indentation, and every key the tool does
not model survive a write untouched, and a value's native JSON type (number
stays a number, string stays a quoted string) is preserved by construction.
Writing to a pointer whose parent objects do not already exist in the source
**throws** rather than materializing the missing structure — the adapter
refuses to guess where new lines, indentation, or trailing commas should go,
and would rather fail loudly naming the pointer than silently reflow the
operator's file.

### `lifecycle` — Parsed

Describes how Servyx determines the workload is ready, how it is stopped,
and how a crash is detected.

| Field | Purpose |
|---|---|
| `ready` | Ordered list of readiness detectors. `log-regex` matches a pattern in console output; `control-probe` calls a control channel command and matches the response, and exists as a fallback for when upstream changes its log format (or, in `factorio-factoriotools.yaml`, as the *primary* detector — an RCON liveness probe rather than a guess at an unconfirmed reply). |
| `stop` | Ordered escalation ladder: each stage names a `kind` (`control`, `signal`, `kill`), its parameters, and a `timeout`. Servyx proceeds to the next stage only once the current stage's timeout elapses. The final stage must be `kind: kill` — a validation error otherwise. |
| `stop[].continueOnError` | Whether a failure of a `control` or `signal` stage's own action is absorbed so the ladder escalates, rather than aborting the stop. **Defaults to `true` for `control`** — an unreachable control channel must never wedge a shutdown, since it is the single most common reason a control stage fails — **and `false` for `signal`** — a runtime that refuses to deliver a signal at all is a real fault worth surfacing, not one to paper over. It never overrides the write-mode guard: a refused stage always aborts the ladder regardless of `continueOnError`. |
| `crashDetection` | Log patterns that indicate a crash, and the resulting `action`. |
| `healthSignal` | Optional. Whether the workload's own container-level health check (e.g. Docker `HEALTHCHECK`) can be trusted: `trust` is `trust` or `ignore`, plus an `explanation` shown to an operator when the workload reports unhealthy and `trust: ignore`. A definition that omits this block gets a generic, game-neutral explanation instead of one written for a different game — `minecraft-itzg.yaml`, `ark-asa-pok.yaml`, and `factorio-factoriotools.yaml` all omit it for exactly this reason (no documented HEALTHCHECK behavior to override); only `palworld-docker.yaml` declares one, for the bundled image's own 401-on-every-probe healthcheck. |

### `control` — Parsed

Declares the control channels available to the workload and the commands
exposed over each.

| Field | Purpose |
|---|---|
| `channels[].id` / `protocol` | Channel identifier and wire protocol (`source-rcon`, `palworld-rest`, `a2s`). |
| `port` | Port the channel listens on (may reference an `.env` var). |
| `passwordRef` / `auth` | Credential reference — RCON uses `passwordRef` pointing at a secret URN; REST declares an `auth` block (`basic`, referencing the same secret). |
| `enabledWhen` | Expression gating whether the channel is usable, evaluated against surface values (`env.RCON_ENABLED == 'true'`). Parsed and shape-validated (`surface.key == 'value'`), but **not yet evaluated by any runtime code path** — every shipped definition declares one, but nothing currently gates channel usability on it. |
| `reachability` | **Ordered** list of strategies tried in sequence until one succeeds: `direct-tcp`, `docker-exec-tool` (with a `tool` and `argv` template), `docker-exec-network`, `ssh-tunnel`. First available wins. This drives the real `RconReachabilityChain` a control session is acquired through. |
| `commands` / `endpoints` | Per-channel operation catalogue. Each entry carries a `template`/`method`+`path`, and — critically — a `readOnly: true|false` flag that the write-mode guard enforces. |
| `players` | Cross-channel player-list configuration: a `preferred` order to try, a `pollInterval`, and per-channel `parsers` for turning raw output into structured player records. |

`lifecycle.stop`'s `control`-kind stages and `backup.quiesce`/`backup.resume`
reference a channel and command declared here, and the reference is checked
at validation time — a stop stage or backup step naming a command the
channel's own `commands` catalogue doesn't declare is a validation error.

#### Player-list parsers (`control.players.parsers`) — Parsed

Four parser shapes are recognized, one per `kind`. All four are compiled by
`RconPlayerListParser`/`CompiledPattern` under `RegexOptions.NonBacktracking`
plus a one-second match timeout — **at definition-validation time**, not at
poll time — so a malformed or catastrophically-backtracking author-supplied
regex is a validation error against the file, and ReDoS from a definition is
structurally impossible. Parsing itself is a **total function**: every
reply, however malformed, produces a `PlayerListSnapshot` (degrading to
`Unresolved`/`CountOnly` rather than throwing), and the parsed result is
consumed **only** by the status/query projection — asserted by an
architecture test (`PlayerListIsolationArchitectureTests`) — so a wrong
guess about a reply format can never affect readiness, the stop ladder, or a
backup.

**`csv-with-header`** — a header row plus comma-separated fields, one row per player:

```yaml
rcon.players: { kind: csv-with-header, columns: [name, playerUid, steamId] }
```
(from `definitions/palworld-docker.yaml`)

| Key | Purpose |
|---|---|
| `columns` | Declared column names, in order. |
| `nameColumn` | Which column holds the player name. Defaults to the first declared column. |
| `idColumn` | Which column holds the primary identifier. Defaults to the first non-name column. |

**`summary-line`** — a single line reporting a count (and, optionally, names):

```yaml
rcon.list:
  kind: summary-line
  pattern: 'There are (?<count>\d+) of a max(?: of)? (?<max>\d+) players online:?(?<names>.*)'
  nameSeparator: ", "
```
(from `definitions/minecraft-itzg.yaml`; **UNVERIFIED** — see "Unverified behaviour" in `docs/games.md`)

Requires a `(?<count>...)` named group; `(?<max>...)` and `(?<names>...)`
are optional. `nameSeparator` (default `, `) splits the names group.

**`lines`** — one line per player, with optional header/ignore patterns:

```yaml
rcon.players:
  kind: lines
  entryPattern: '^\s*\d+\.\s*(?<name>[^,]+),\s*(?<id>\d+)\s*$'
  ignorePatterns:
    - '[Nn]o\s+[Pp]layers\s+[Cc]onnected'
    - '^\s*$'
```
(from `definitions/ark-asa-pok.yaml`; **UNVERIFIED**)

`entryPattern` requires a `(?<name>...)` group and may declare `(?<id>...)`.
`headerPattern` (optional) may declare a `(?<count>...)` group used to
cross-check the number of entry lines actually read. `ignorePatterns` are
matched before `entryPattern` and skip the line entirely (blank lines,
empty-server sentinels).

**`count`** — no roster, just a number, from either a regex or a JSON pointer:

```yaml
rcon.players: { kind: count, jsonPointer: /data/serverGameState/numConnectedPlayers }
```
(verified against `PlayerParserSpecYamlTests`; no shipped definition currently declares this shape — every shipped game's control channel returns a roster, not a bare count)

Declares exactly one of `pattern` (must have a `(?<count>...)` group) or
`jsonPointer` (must be an absolute RFC 6901 pointer, i.e. start with `/`).

### `settings` — Parsed

The user-facing settings catalogue, organized into `group`s of `items`. Each
item declares its `type` (`string`, `text`, `port`, `int`, `float`, `bool`,
`enum`, `secret`, …), validation constraints (`min`/`max`/`step`/`maxLength`/
`values`), and any type-specific rendering hints (`renderFormat: "F6"` for
Unreal's six-decimal floats, `trueValue`/`falseValue` for non-standard
booleans). An item may also declare `requiresRecreate: true` (changing the
value requires the workload's container to be recreated rather than just
restarted) and, for a `port`-typed item, `publishByDefault: false` (Servyx
will not expose the port to the host network by default) — both are
item-level fields, alongside `default`, sitting next to `bindings` rather
than inside any one binding.

Each item's `bindings` list ties it to one or more surfaces:

- Exactly one binding should normally have `direction: write` — the
  authoritative surface Servyx actually edits.
- Any number of bindings may have `direction: read`, used to show
  `Rendered`/`Runtime` values and compute drift.
- A binding may add `unquote: true` (strip quoting when displaying), `member`
  (name within a decoded codec payload), `pointer` (JSON-pointer-style path,
  used for structured `yaml`/`json` surfaces and control-channel responses),
  `sensitive: true` (mask in the UI and logs), and `strategy` (a named
  transform for non-trivial writes, e.g. `publish-udp`/`publish-tcp` for
  compose port publication).

This block is read by `ServerQueryService`, which builds each server's
`Settings` list directly from the loaded definition's parsed
`SettingDescriptor`s — there is no more hand-maintained C# table mirroring
it by hand. A setting's authoritative environment value (used to populate
the `Authoritative` column against a running container's own reported
environment) is resolved from the surface whose `id` is literally `"env"` —
every shipped definition uses that identity for its dotenv surface, which is
also the convention `enabledWhen` examples assume (`env.RCON_ENABLED == 'true'`).
A definition's settings catalogue therefore now has real effect on the
running dashboard the moment the file is loaded.

### `backup` — Parsed

| Field | Purpose |
|---|---|
| `include` / `exclude` | Glob lists defining what a Servyx-created backup archives. `exclude` always removes the image's own backup directory (where one exists) to prevent re-archiving archives, plus any known in-progress/truncated save artifacts. |
| `quiesce` | Control steps run before archiving, e.g. RCON `save`, with a `timeout`. |
| `resume` | Control steps run **after** capture finishes, whatever the outcome — the undo half of `quiesce`. See "Quiesce and resume" below. |
| `adopt` | Declares a foreign backup source to list and make restorable without Servyx ever managing its lifecycle — `adapter`, `path`, `pattern`, `ownership: foreign`, and a human `note`. |
| `defaultRetention` | Default keep-counts (`keepHourly`/`keepDaily`/`keepWeekly`) applied only to Servyx-owned backups. |

`ServyxBackupContextSource.GetAsync` reads this block off the loaded
`GameDefinition` directly. Precedence is config (`Servyx:Backups:*`) >
loaded definition > built-in default. There is deliberately no built-in
fallback for `include` itself, nor for a game-shaped container data root: a
definition that fails to load, with no explicit
`Servyx:Backups:Include`/`ContainerDataRoot` override either, fails backups
loudly rather than silently archiving nothing or another game's paths under
a name that happens to match.

#### Quiesce and resume

`quiesce` alone can only express "stop writing to disk" — a definition that
used it to turn autosave off had no declared way to turn it back on, so the
first backup would leave the workload unable to save for the rest of its
process lifetime. `resume` is the fix: its steps are guaranteed to run —
`DockerBackupProvider.CreateAsync` opens a `try` around the quiesce-and-capture
sequence *before* the quiesce step ever executes, whose `finally` issues
`resume` unconditionally, on every exit path (success, a capture that threw,
a quiesce that failed partway through its own list, and cancellation alike).
The resume steps are deliberately **not** bound to the caller's cancellation
token — an operator cancelling a backup is asking to stop copying files,
never to leave the server unable to save.

`minecraft-itzg.yaml` is the canonical example — the classic Minecraft
`save-off` → `save-all flush` → copy → `save-on` sequence:

```yaml
quiesce:
  - { kind: control, channel: rcon, command: save-off, timeout: 30s }
  - { kind: control, channel: rcon, command: save-all, timeout: 30s }
resume:
  - { kind: control, channel: rcon, command: save-on,  timeout: 30s }
```

`ark-asa-pok.yaml` and `factorio-factoriotools.yaml` declare no `resume`
block at all — their `quiesce` step (`SaveWorld`/`/server-save`) is a
one-shot synchronous write with nothing to undo, so `resume` (empty by
default) is simply omitted. A definition written before `resume` existed
parses and behaves exactly as it did before.

### `saves` — Parsed

Describes the on-disk shape of world saves so the saves page can enumerate
and label them: `worldRoot`, `worldIdPattern` (a regex constraining what
counts as a valid world folder name), `levelFile`, `metaFile`, `playerDir`.

`LiveDashboardDataService.GetServerSavesAsync` reads this block off the
server's resolved definition — there is no hardcoded Palworld path. A server
whose definition declares no `saves` block, or has no definition resolved
at all, reports `SavesAvailability.NotConfigured` rather than an empty list
pretending nothing was found. Note the schema limitation `ark-asa-pok.yaml`'s
own comments call out: `levelFile`/`metaFile` are parsed as plain strings,
not run through the same `${VAR}` template-token resolution `worldRoot`
gets, so they cannot reference a settings key — a glob (`*_WP.ark`) is the
documented workaround.

### `mods` — Declared only

Declares whether mod management is supported for this definition at all
(`supported: true`/`false`).

This block is not read by any runtime code path today; the Mods page is
still a placeholder unrelated to this declaration, regardless of what a
given definition sets.

### `signature` (not present in any shipped definition) — Declared only

Reserved for definitions that carry a cryptographic signature establishing
provenance, used by `IDefinitionTrustEvaluator` to help assign a trust tier
above `Unverified`. `IDefinitionTrustEvaluator` is an interface with no
implementation wired up today, so no definition — signed or not — is
evaluated against it; every loaded definition is `TrustTier.Unverified`
regardless of what it declares here or under `capabilities`.

## Validation Rules

- **Schema version compatibility.** `apiVersion` is checked against the
  versions Servyx's validator understands; an unrecognized major version is
  rejected outright rather than best-effort parsed.
- **Required fields.** `metadata.id`, `metadata.name`, `metadata.version`,
  and at least one `deployments` entry are mandatory; a deployment missing
  its `kind`-specific required fields (`image.default` for `docker`,
  `executable` for `process`) fails validation.
- **No absolute or traversal paths outside the declared root.** Every
  path-like field (`locator.path`, `backup.include`/`exclude`,
  `saves.worldRoot`, `capabilities.filesystem[].path`,
  `backup.adopt[].path`, `install`'s `ensure-dir` path, `config.ignored[].path`,
  …) is checked for a literal `..` segment or an OS-absolute path — this
  restriction **is implemented**, not aspirational, via
  `GameDefinitionYamlParser`'s shared `ValidateContainedPath` helper.
  `deployments[].files[].path` goes further still, since it names a
  *destination Servyx writes bytes to* rather than something only read or
  listed: it must be rooted specifically at `${DATA_DIR}` or `${COMPOSE_DIR}`
  (not any other declared variable), and a `..` hidden behind one layer of
  percent-encoding (`%2e%2e/`, `..%2f`) is rejected too, not just a literal
  `..` segment.
- **`stopGracePeriodSeconds` must be at least the `lifecycle.stop` ladder's
  stage-timeout total.** A shorter value is a validation error naming both
  numbers, because the container runtime would force-kill the workload
  part-way through the ladder, quite possibly mid-save.
- **`lifecycle.stop`'s final stage must be `kind: kill`.** A ladder that can
  end without a forced kill would leave a server that can never be brought
  down; this is a validation error, not a convention left to authors.
- **A `control`/`backup.quiesce`/`backup.resume`/readiness `control-probe`
  entry's channel and command must exist.** Referencing a channel `control.channels`
  never declares, or a command that channel's own `commands` catalogue
  doesn't list, is a validation error.
- **Secrets must never carry literal defaults.** A `type: secret` setting
  item may not declare a `default` value in the definition — secrets always
  originate from the secret store, never from checked-in definition content.
- **A `deployments[].files[]` entry's `contentFrom`/`content` are mutually
  exclusive, and exactly one is required.** Declaring both, or neither, is a
  validation error — the former because which one wins would otherwise be
  the reader's guess, the latter because a seeded file with no content would
  place an empty file where the workload expects real content.
- **A regex is compiled at validation time, under `NonBacktracking` plus a
  match timeout.** Every author-supplied pattern in this schema —
  `lifecycle.ready`'s `log-regex`, `lifecycle.crashDetection`, and every
  `control.players.parsers` shape — is compiled when the definition loads,
  not when it is first matched against live output. A malformed or
  catastrophically-backtracking pattern is therefore a load-time validation
  error against the file, and ReDoS sourced from a definition file is
  structurally impossible rather than merely unlikely.
- **`shell: true` is parsed, but carries no enforced consequence today.**
  `docs/schema.md` previously stated that a `shell: true` definition is
  unusable at `Unverified` trust, and that even a permitted one requires an
  explicit operator consent plus a content-hash pin. **Neither half of that
  rule is implemented.** There is no trust-tier check anywhere in the
  codebase that reads `capabilities.shell` at all — trust evaluation itself
  has no implementation (`IDefinitionTrustEvaluator` is unimplemented; every
  definition loads as `Unverified` regardless of what it declares), so a
  `shell: true` definition parses and loads exactly like any other today.
  Treat this as a **planned, not-yet-enforced** control, not a shipped
  security boundary — do not rely on it to keep an untrusted definition's
  shell capability from taking effect.
- **Port `purpose` values must be unique within `capabilities.network`.** Two
  entries with the same `purpose` inside one definition is a validation
  error, since `purpose` is used to disambiguate bindings and UI labels.
- **Unknown fields are rejected, not warned.** A definition containing a
  field the validator does not recognize — at the top level, or within any
  known block — fails to load, full stop. This is deliberately stricter than
  "warn and continue": silently ignoring a misspelled security-relevant key
  — `privleged` instead of `privileged`, for instance — is far more
  dangerous than a hard failure at import time, because a warning is easy to
  miss and the mistyped field would otherwise simply have no effect, leaving
  the operator to believe a restriction was in force when it never was. The
  one exception is `signature`: recognized as a legal top-level key (so
  declaring one is not itself an unknown-field error) but not parsed at all,
  with a Warning noting the block is present but unverified.

## Departures from Pterodactyl's Egg Format

1. **Surfaces carry roles.** Pterodactyl/Pelican's `config.files` model
   assumes every configuration file is directly writable. It has no way to
   express that a file is generated by the workload itself and must never be
   written — which is exactly the situation with Palworld's rendered INI or
   Minecraft's `server.properties`. Servyx surfaces are typed
   `authoritative` / `derived` / `runtime` from the start, so this
   distinction is structural rather than worked around, and each shipped
   definition genuinely differs in which surface plays which role: ARK's
   `GameUserSettings.ini` is `authoritative` (nothing regenerates it),
   Palworld's and Minecraft's equivalent surfaces are `derived`.
2. **Install is an allowlisted step list, not a shell script.** Eggs run an
   arbitrary shell install script; Servyx's `install` block is a list of
   named, allowlisted verbs (`steamcmd`, `ensure-dir`) with typed
   parameters, so an `Unverified` definition simply has no shell surface to
   exploit through this path (see the `shell: true` validation-rule note
   above for the one place this project's own enforcement of that intent is
   still incomplete).
3. **Ready-detection is regex plus a control-probe fallback,** not a single
   startup line match. This matters in practice: Docker's own container
   health status is not a reliable readiness signal (see
   `docs/architecture.md`, "Readiness vs. Container Health"), so Servyx
   layers an authenticated control-probe behind (or, for Factorio, ahead of)
   the log pattern rather than trusting either the log line alone or
   Docker's `HEALTHCHECK`.
4. **Control channels are first-class,** with declared reachability
   strategies tried in order and a per-command `readOnly` flag enforced by
   the write-mode guard — rather than leaving RCON/REST access as an
   unmodelled implementation detail the panel author has to bolt on
   separately.
5. **Player-list parsing is closed, isolated, and total.** Four recognized
   reply shapes (`csv-with-header`, `summary-line`, `lines`, `count`)
   compiled at validation time rather than a game-specific binary or an
   open-ended template the author hand-writes; a wrong guess about a reply
   format degrades to an unresolved/count-only result and is structurally
   incapable of affecting anything outside the player-count/roster display.
