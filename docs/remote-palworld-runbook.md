# Operator runbook: the remote Palworld host

This is the day-to-day operator guide for the one thing Servyx can do against the Cloudnium
Palworld host today: **observe it, read-only, over SSH.** It complements two other documents rather
than repeating them — read `docs/connectors.md` for the configuration shape and the `ssh+docker`
transport's internals, and `docs/testing.md` for how each test layer proves this surface works. This
document is about *running* it: connecting, viewing, verifying, and knowing exactly where the edges
of "what Servyx can do" currently are.

> **Real coordinates live outside this repo.** Every placeholder below (`<REMOTE_HOST>`,
> `<REMOTE_USER>`, `<REMOTE_KEY_NAME>`, `<REMOTE_FINGERPRINT>`, `<LOCAL_MIRROR_PATH>`) stands in for
> a real production value. The actual values live in `scripts\cloudnium.local.ps1`, which is
> gitignored and never committed. To get set up: copy `scripts\cloudnium.example.ps1` to
> `scripts\cloudnium.local.ps1` and fill in the real host, user, SSH key filename, fingerprint, and
> local mirror path. `scripts\verify-cloudnium-mirror.ps1` reads that file automatically; anywhere
> else you need a real value (e.g. the manual commands in Part 1 and Part 2A below), substitute it
> in from the same file.

## What this host is

| | |
|---|---|
| Host | `<REMOTE_USER>@<REMOTE_HOST>:22` |
| Container | `palworld-server` |
| SSH key | `~/.ssh/<REMOTE_KEY_NAME>`, inside WSL (Servyx itself runs on Windows) |
| Host-key fingerprint | `<REMOTE_FINGERPRINT>` (ED25519) |

A real person's Palworld save data lives on this box. Everything below is written with that in
mind — every command listed here is read-only, and the one place a mutating command is even
constructed (the live smoke suite's refusal test) exists specifically to prove it *cannot* reach
production.

## Part 1 — Connect and view

### 1. Configure the host, disabled, then enable it deliberately

`appsettings.json` ships a disabled `Servyx:Hosts:example-remote` block with placeholder values —
see `docs/connectors.md` § "The `ssh+docker` transport, as shipped" for the full field reference.
To actually point Servyx at this host:

1. Import the SSH private key (see "The WSL → Windows key import runbook" in `docs/connectors.md`
   — summarized: copy the key to a Windows-readable path with `wsl cp`, point
   `Servyx:Secrets:Import:<urn>` at it via user-secrets or an environment variable, start Servyx
   once, watch for the import log line, then delete the plaintext copy immediately).
2. Set the real values for `Enabled`, `Endpoint`, `CredentialUrn`, `TrustPolicy`, and
   `PinnedFingerprints` via `dotnet user-secrets` or environment variables —
   **never in a tracked `appsettings*.json` file**, including `appsettings.Development.json`,
   which is tracked by git in this repository.
3. Start (or restart) Servyx. `SshDockerWiringOptions.FromConfiguration` reads the section fresh on
   every start and, if the host is enabled with a valid `Endpoint` and `Container`, replaces the
   local Docker observation surface (`ITransport`, discovery, log stream, metrics source) with one
   backed by this remote host — see `AddServyxSshDocker` in
   `src/Infrastructure/Servyx.Infrastructure.Ssh/Docker/SshDockerServiceCollectionExtensions.cs`.

### 2. What "viewing" looks like once connected

With the host wired in, the dashboard's normal read surfaces are backed by the real SSH/Docker
session instead of a local daemon:

- **Reachability** — `docker version` over SSH; a healthy probe folds the Docker server version
  into the detail text. An unreachable SSH host, a missing `docker` binary (exit 127), and a
  permission-denied `docker` invocation (exit 126 — typically the SSH user is not in the `docker`
  group) are each reported with a distinct, honest message rather than a single "not reachable".
- **Discovery / inspect** — `docker container ls` and `docker container inspect` adopt and describe
  the `palworld-server` container: image, state, health, ports (published vs. exposed-only), and
  the save-data bind mount.
- **Logs** — `docker logs --tail N --timestamps`, replayed once per call (see "Known limitations"
  below — this is not a live follow).
- **Metrics** — `docker stats --no-stream`, polled on an interval (see "Known limitations" — this
  is not a push stream, and network I/O is always reported as zero).

Every one of these runs as a declared-`ReadOnly` `CommandSpec` (see `DockerCli` in
`src/Infrastructure/Servyx.Infrastructure.Ssh/Docker/DockerCli.cs`), and the transport is registered
write-guarded with zero `WriteModeGrant`s regardless of what the code above happens to construct —
see "Read-only posture" below for exactly why that makes a mutating call structurally unreachable,
not merely discouraged.

## Part 2 — Verify

Two independent things are worth verifying periodically, and they answer different questions.

### A. Does Servyx itself see the host correctly? — the live smoke suite

`tests/Servyx.Remote.Tests` is a read-only smoke suite that exercises the real `ssh+docker`
transport against this exact host: reachability, discovery, port classification (RCON's 25575/tcp
is exposed but never published — the assertion the suite exists for), the bind mount, health status
surfacing, log tail, metrics, and the write-guard refusal. It is deliberately isolated — not in
`Servyx.sln`, not in CI — and gated behind four independent conditions that must all hold at once.
Full details, including exactly which environment variables it needs and why each is required, are
in `docs/testing.md` § "Testing the `ssh+docker` transport: four layers".

To run it:

```powershell
wsl cp ~/.ssh/<REMOTE_KEY_NAME> /mnt/c/Users/<you>/AppData/Local/Temp/<unique-name>

$env:SERVYX_REMOTE_E2E         = "1"
$env:SERVYX_REMOTE_ENDPOINT    = "ssh:<REMOTE_USER>@<REMOTE_HOST>:22"
$env:SERVYX_REMOTE_KEY_PATH    = "C:\Users\<you>\AppData\Local\Temp\<unique-name>"
$env:SERVYX_REMOTE_CONTAINER   = "palworld-server"
$env:SERVYX_REMOTE_FINGERPRINT = "<REMOTE_FINGERPRINT>"

dotnet test tests\Servyx.Remote.Tests --filter "Category=Integration"

Remove-Item $env:SERVYX_REMOTE_KEY_PATH
```

Delete the temporary key copy the moment the run finishes — it should never outlive the test
session. A missing or blank variable produces a clean skip, never a failure, so it is always safe
to run this command; it either verifies something real or tells you exactly what's not configured.

### B. Is the save-data mirror to this machine healthy? — the mirror verification script

Separately from anything Servyx does, an existing (and out-of-repo) rsync pipeline mirrors this
host's save data to `<LOCAL_MIRROR_PATH>\data\Pal\Saved` hourly via a Windows Scheduled Task. Servyx
does not run, own, or adopt this pipeline — `LiveDashboardDataService.GetAllBackupsAsync` currently
always returns an empty list, and remote backup adoption is explicitly out of scope (see "Known
limitations" below). What this repository *does* provide is a read-only check on that pipeline's
health:

```powershell
pwsh scripts\verify-cloudnium-mirror.ps1
```

This script (see the banner comment in the file itself) is read-only by construction — the rsync it
runs is hard-coded to `--dry-run` with no way to override that, and it never touches the local
mirror, the remote host, the Scheduled Task, or the sync log. It reads its coordinates from
`scripts\cloudnium.local.ps1` automatically if present (see the note at the top of this document);
otherwise pass `-RemoteHost`, `-RemoteUser`, `-LocalPath`, `-SshKey`, and `-LogPath` explicitly, or
the script prints setup instructions and exits non-zero. It reports:

- What a real sync run **would** change (`rsync --dry-run --itemize-changes --stats`), using the
  same source, excludes, and SSH key invocation as the real sync script.
- The `\Palworld Sync From Cloudnium` Scheduled Task's last run time, last result, and next run
  time.
- A tail of the sync log.
- A read-only size/file-count comparison between the local mirror and the remote `Pal/Saved`
  directory (`du -sh` / `find | wc -l` over SSH).

It exits non-zero if the scheduled task's last result was non-zero, or if the dry-run reports any
deletions in the mirror direction — a deletion there can mean the remote pruned an old save
rotation, or it can mean data loss; either way it is worth a human looking at it before the next
hourly run overwrites the evidence.

The real, mutating sync pipeline this verifies lives outside this repository, at
`<LOCAL_MIRROR_PATH>\scripts\sync-from-cloudnium.ps1` (plus a logging wrapper and the Scheduled Task
itself). It quiesces the world via `rcon-cli Save` before rsyncing, and refuses to run if a *local*
Palworld container is up unless forced. This runbook does not change or move that pipeline — it
only gives you a read-only window into whether it's healthy.

## Part 3 — Known limitations, interpreted

Reported values from this transport are honest about what they are, but a few of them need
context to read correctly:

- **Logs are a replay, not a follow.** `SshDockerLogStream.FollowAsync` runs `docker logs --tail`
  once and completes — there is no `--follow` equivalent over a non-streaming SSH exec result. A UI
  or caller that wants "keep watching" must call it again; it will not push new lines on its own.
- **Metrics are polled, not pushed.** `SshDockerMetricsSource.StreamAsync` runs
  `docker stats --no-stream` on a fixed interval (2 seconds by default) rather than receiving a
  push stream from the Docker Engine API. **Network Rx/Tx are always reported as zero** — `docker
  stats`' JSON output has no machine-parsable network I/O field, only a human-formatted string the
  parser deliberately does not attempt to decode.
- **There is no "degraded" health state.** `TargetHealth` is a binary reachable/not-reachable
  signal. "SSH is fine but Docker is unusable" (exit 126 or 127 from `docker version`) surfaces as
  `Reachable: false`, with the *reason* carried in the `Detail` string rather than as a distinct
  status value. Read the detail text, not just the boolean, when triaging a probe failure.
- **The Palworld container's own `unhealthy` status is a false negative.** Docker's healthcheck for
  this deployment probes an internal REST API that returns HTTP 401 (it is not meant to be reached
  the way the healthcheck reaches it), so `docker inspect` reports `unhealthy` even when the game
  server is fully playable. Servyx surfaces the health status honestly (it does not substitute a
  guess or suppress it) and attaches a `PalworldUnhealthyExplanation` alongside it — the presence of
  that explanation is what distinguishes "known false negative, already explained" from "something
  is actually wrong."

## What Servyx cannot do yet against this host

This list is the honest boundary of the current milestone. None of the following exist today —
they are not partially working, silently disabled, or reachable via a workaround:

- **No lifecycle control.** No start, stop, or restart. `Servyx:Provisioning:Enabled` defaults
  `false`, which produces zero `WriteModeGrant`s, which means `GrantedWriteModeResolver` returns
  `ReadOnly` for every target unconditionally. `WriteGuardedExecutionTarget` throws
  `WritesDisabledException` **synchronously, before any I/O** — a mutating `CommandSpec` (e.g.
  `DockerCli.Stop`) never becomes an argv, never reaches the SSH exec channel, and never leaves the
  machine Servyx runs on. This is what the live smoke suite's refusal test proves directly against
  this host, not just against a mock. Write-enablement is milestone M4, not shipped.
- **No RCON writes.** Nothing here sends an RCON command that changes game state (save, kick, ban,
  broadcast, shutdown). The one read-only exec path this assembly exposes (`DockerCli.ExecReadOnly`)
  exists precisely because `docker exec`'s argv can't be verified safe by the machine — the
  guarantee lives entirely in the caller proving the argv is side-effect-free before using it, and
  no caller in this codebase currently exercises it for RCON.
- **No remote backup adoption.** The external rsync mirror pipeline described in Part 2B is
  completely separate from Servyx's own backup abstraction (`IBackupProvider`,
  `PalworldCronBackupAdopter`, `ScheduledBackupService`) and is not wired to it.
  `LiveDashboardDataService.GetAllBackupsAsync` always returns an empty list today. The container
  also writes its own cron backups to `/opt/palworld/data/backups/` (tar.gz, roughly 4 MB each) —
  Servyx does not list, verify, or restore from those either. This is now enforced rather than merely
  documented: the Docker backup pipeline **refuses** to run over `ssh+docker`, because that
  transport's file operations reach the SSH host's filesystem over SFTP, not the container's — see
  `docs/connectors.md`, "Files reached this way are the SSH **host's**, not the container's". Enabling
  backups on a host wired this way produces a clear `ContainerScopedFilesNotSupportedException`, not
  an empty archive and not a restore written onto the host.
- **Docker's `unhealthy` status is a false negative**, covered above — repeated here because it is
  easy to mistake for a missing feature rather than a known, explained characteristic of this
  specific deployment's healthcheck.

None of the above is a bug to fix in this document's scope — they are the explicit edges of what
"read-only observation" means, stated so an operator never has to rediscover them by trying
something and being surprised it's refused.
