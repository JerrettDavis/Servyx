# Game Definition Schema Reference (`servyx.dev/v1`)

This document is a field-by-field reference for the `servyx.dev/v1`
`GameDefinition` schema, illustrated throughout with the Palworld example at
`definitions/palworld-docker.yaml`.

## Top-Level Blocks

### `metadata`

Identifies the definition itself.

| Field | Purpose |
|---|---|
| `id` | Stable identifier for the game (`palworld`). |
| `name` | Human-readable display name. |
| `version` | Definition version string. Note: servers pin definitions by **content hash**, not by this field — see the Validation Rules section. |
| `license` | License of the definition content. |
| `tags` | Free-form classification tags (`survival`, `steam`, `unreal`). |

### `capabilities`

The declared blast radius of the deployment, checked against trust tier
restrictions before anything runs.

| Field | Purpose |
|---|---|
| `network` | List of ports the workload uses, each with `port`, `protocol`, `purpose`, the `.env` variable it is sourced from (`var`), and whether it is `published` to the host by default. |
| `filesystem` | Paths the workload touches, with `access` (`rw`/`ro`) and a human `purpose` string. |
| `egress` | Outbound network destinations the workload is allowed to reach. Empty here because the image self-updates and Servyx downloads nothing for this deployment. |
| `shell` | Whether any step in this definition requires shell execution. `false` for Palworld. |
| `privileged` | Whether the workload requires privileged mode. |
| `hostNetwork` | Whether the workload requires host networking. |

### `deployments`

A definition may describe more than one deployment **profile** for the same
game — Palworld ships two: `docker-thijsvanloef` (the Docker image) and
`native-steamcmd` (bare metal). Each profile independently declares:

- `id`, `kind` (`docker` | `process`), and detection/execution details
  (`detect`, `image`, `dataDir`, `stopTimeout` for Docker; `executable`,
  `install` for process).
- Its own `config.surfaces` list, since the same underlying setting can live
  on a different surface — and even in a different **role** — depending on
  the profile. This is the schema's core departure from a flat egg format:
  see "Configuration surfaces" below.
- An `ignored` list of paths that exist but are deliberately excluded from
  binding and backup, with a `reason` shown in the UI.

#### Configuration surfaces

Each entry under `config.surfaces` describes one place configuration lives:

| Field | Purpose |
|---|---|
| `id` | Surface identifier, referenced from `settings[].bindings`. |
| `role` | `authoritative` (Servyx may write), `derived` (Servyx reads only — the workload regenerates it), or `runtime` (live state over a control channel). |
| `format` | Parser to use: `dotenv`, `yaml`, `ini`, `json`, etc. |
| `codec` / `codecPath` | Optional value codec for a structured payload embedded in a single scalar (`unreal-option-settings` for `OptionSettings`), and the path within the parsed document where it applies. |
| `locator` | Where the surface physically lives — `host-file` with a `path`, or `control-channel` with a `channel` and `query`. |
| `managedSubtree` | For `yaml`/structured formats, restricts writes to a specific subtree (`services.palworld`) rather than the whole document. |
| `mergePolicy` | How unmanaged content is treated on write; `preserve-unknown` is the default and, in practice, non-negotiable. |
| `derivedFrom` | For `derived`/`runtime` surfaces, which upstream surface(s) they are generated from — this is what drift detection compares against. |
| `regeneration` | For `derived` surfaces, how and when the surface gets regenerated (`kind: container-restart` plus a human `description`). |

### `lifecycle`

Describes how Servyx determines the workload is ready, how it is stopped,
and how a crash is detected.

| Field | Purpose |
|---|---|
| `ready` | Ordered list of readiness detectors. `log-regex` matches a pattern in console output; `control-probe` calls a control channel command and matches the response, and exists as a fallback for when upstream changes its log format. |
| `stop` | Ordered escalation ladder: each stage names a `kind` (`control`, `signal`, `kill`), its parameters, and a `timeout`. Servyx proceeds to the next stage only once the current stage's timeout elapses. |
| `crashDetection` | Log patterns that indicate a crash, and the resulting `action`. |

### `control`

Declares the control channels available to the workload and the commands
exposed over each.

| Field | Purpose |
|---|---|
| `channels[].id` / `protocol` | Channel identifier and wire protocol (`source-rcon`, `palworld-rest`, `a2s`). |
| `port` | Port the channel listens on (may reference an `.env` var). |
| `passwordRef` / `auth` | Credential reference — RCON uses `passwordRef` pointing at a secret URN; REST declares an `auth` block (`basic`, referencing the same secret). |
| `enabledWhen` | Expression gating whether the channel is usable, evaluated against surface values (`env.RCON_ENABLED == 'true'`). |
| `reachability` | **Ordered** list of strategies tried in sequence until one succeeds: `direct-tcp`, `docker-exec-tool` (with a `tool` and `argv` template), `docker-exec-network`, `ssh-tunnel`. First available wins. |
| `commands` / `endpoints` | Per-channel operation catalogue. Each entry carries a `template`/`method`+`path`, and — critically — a `readOnly: true|false` flag that the write-mode guard enforces. |
| `players` | Cross-channel player-list configuration: a `preferred` order to try, a `pollInterval`, and per-channel `parsers` for turning raw output into structured player records. |

### `settings`

The user-facing settings catalogue, organized into `group`s of `items`. Each
item declares its `type` (`string`, `text`, `port`, `int`, `float`, `bool`,
`enum`, `secret`, …), validation constraints (`min`/`max`/`step`/`maxLength`/
`values`), and any type-specific rendering hints (`renderFormat: "F6"` for
Unreal's six-decimal floats, `trueValue`/`falseValue` for non-standard
booleans).

Each item's `bindings` list ties it to one or more surfaces:

- Exactly one binding should normally have `direction: write` — the
  authoritative surface Servyx actually edits.
- Any number of bindings may have `direction: read`, used to show
  `Rendered`/`Runtime` values and compute drift.
- A binding may add `unquote: true` (strip quoting when displaying), `member`
  (name within a decoded codec payload), `pointer` (JSON-pointer-style path,
  used for structured `yaml`/`json` surfaces and control-channel responses),
  `sensitive: true` (mask in the UI and logs), `strategy` (a named transform
  for non-trivial writes, e.g. `publish-udp` for compose port publication),
  and `requiresRecreate: true` / `publishByDefault: false` as deployment
  hints.

### `backup`

| Field | Purpose |
|---|---|
| `include` / `exclude` | Glob lists defining what a Servyx-created backup archives. `exclude` always removes the image's own backup directory to prevent re-archiving archives. |
| `quiesce` | Control commands run before archiving (RCON `save`, with a `timeout`). |
| `adopt` | Declares a foreign backup source to list and make restorable without Servyx ever managing its lifecycle — `adapter`, `path`, `pattern`, `ownership: foreign`, and a human `note`. |
| `defaultRetention` | Default keep-counts (`keepHourly`/`keepDaily`/`keepWeekly`) applied only to Servyx-owned backups. |

### `saves`

Describes the on-disk shape of world saves so the saves page can enumerate
and label them: `worldRoot`, `worldIdPattern` (a regex constraining what
counts as a valid world folder name), `levelFile`, `metaFile`, `playerDir`.

### `mods`

Declares whether mod management is supported for this definition at all
(`supported: false` for Palworld in this version).

### `signature` (not present in the Palworld example)

Reserved for definitions that carry a cryptographic signature establishing
provenance, used by `IDefinitionTrustEvaluator` to help assign a trust tier
above `Unverified`.

## Validation Rules

- **Schema version compatibility.** `apiVersion` is checked against the
  versions Servyx's validator understands; an unrecognized major version is
  rejected outright rather than best-effort parsed.
- **Required fields.** `metadata.id`, `metadata.name`, `metadata.version`,
  and at least one `deployments` entry are mandatory; a deployment missing
  its `kind`-specific required fields fails validation.
- **No absolute or traversal paths outside the server root.** Every path
  field (`locator.path`, `backup.include`/`exclude`, `saves.worldRoot`, …) is
  resolved and checked to stay within the server's data root; `..` segments
  or absolute paths that escape it are rejected.
- **Secrets must never carry literal defaults.** A `type: secret` setting
  item may not declare a `default` value in the definition — secrets always
  originate from the secret store, never from checked-in definition content.
- **`shell: true` requires an explicit consent flag plus a hash pin.** A
  definition that declares shell capability is unusable at `Unverified`
  trust under any circumstance, and even where permitted, the operator must
  explicitly consent to that specific content hash before it will run.
- **Port `purpose` values must be unique within a deployment.** Two network
  entries with the same `purpose` inside one deployment profile is a
  validation error, since `purpose` is used to disambiguate bindings and UI
  labels.
- **Unknown fields are rejected, not warned.** A definition containing a
  field the validator does not recognize fails to load, full stop. This is
  deliberately stricter than "warn and continue": silently ignoring a
  misspelled security-relevant key — `privleged` instead of `privileged`,
  for instance — is far more dangerous than a hard failure at import time,
  because a warning is easy to miss and the mistyped field would otherwise
  simply have no effect, leaving the operator to believe a restriction was
  in force when it never was.

## Departures from Pterodactyl's Egg Format

1. **Surfaces carry roles.** Pterodactyl/Pelican's `config.files` model
   assumes every configuration file is directly writable. It has no way to
   express that a file is generated by the workload itself and must never be
   written — which is exactly the situation with Palworld's rendered INI.
   Servyx surfaces are typed `authoritative` / `derived` / `runtime` from the
   start, so this distinction is structural rather than worked around.
2. **Install is an allowlisted step list, not a shell script.** Eggs run an
   arbitrary shell install script; Servyx's `install` block is a list of
   named, allowlisted verbs (`steamcmd`, `ensure-dir`, …) with typed
   parameters, so an `Unverified` definition simply has no shell surface to
   exploit.
3. **Ready-detection is regex plus a control-probe fallback,** not a single
   startup line match. This matters in practice: Docker's own container
   health status is not a reliable readiness signal (see
   `docs/architecture.md`, "Readiness vs. Container Health"), so Servyx
   layers an authenticated control-probe behind the log pattern rather than
   trusting either the log line alone or Docker's `HEALTHCHECK`.
4. **Control channels are first-class,** with declared reachability
   strategies tried in order and a per-command `readOnly` flag enforced by
   the write-mode guard — rather than leaving RCON/REST access as an
   unmodelled implementation detail the panel author has to bolt on
   separately, as Pelican's Palworld egg does with its bundled
   `PalworldServerConfigParser` binary.
