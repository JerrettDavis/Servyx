# Connecting a host

Servyx reaches a game server through a **transport** — the pipe it uses to talk to the machine the server runs on. There are two: Docker (local or remote-over-TCP) and `ssh+docker` — a remote host reached over SSH, running Docker, that Servyx drives the `docker` CLI against. This page covers the transport model shared by both; for the concrete steps to declare an `ssh+docker` host in configuration, see [Adopting a remote host](adopting-a-remote-host.md).

## Local Docker

By default Servyx connects to the Docker engine on the machine it is running on: the standard Unix socket on Linux, or the Docker Desktop named pipe on Windows. This needs no configuration — if Docker is running and reachable, Servyx uses it.

## Remote Docker

Servyx's Docker transport also understands `tcp://` (and `http(s)://`) endpoints, and honours the standard `DOCKER_HOST` environment variable, so it can point at a Docker engine on another machine. There is currently no in-dashboard wizard for this — it is set once, at the process level, before Servyx starts, not per-server from within the UI.

## SSH and SFTP are independent channels

Servyx's design treats SSH exec (running commands) and SFTP (reading and writing files) as **two separate channels that happen to often travel over the same connection**, not one channel that assumes the other. This matters in practice: it is entirely normal for a host to allow you to run commands over SSH while its SFTP subsystem is disabled, or vice versa. When that happens, Servyx reports the exec channel as working and the file channel as degraded (or the reverse) — it does not collapse "I can't read your files" into "I can't reach your host," because those are different problems with different fixes. See [Connectors](../connectors.md) for the full model, including how Docker-over-SSH composes both channels together, since the Docker API itself cannot read `.env` or `compose.yaml` — those live on the host filesystem, not inside anything the Docker API exposes.

**What works today:** the SSH transport and its exec/SFTP composition are wired into the running `Servyx.Web` dashboard, and a remote host can be connected two ways — through the **Hosts** page in the UI, or through `Servyx:Hosts:<name>` configuration — with both kinds wired concurrently for discovery. See [Connecting a host from the UI](#connecting-a-host-from-the-ui) below for the recommended path, and [Adopting a remote host](adopting-a-remote-host.md) for the full config-based walkthrough: the config keys, host-key pinning, and getting the SSH credential into the secret store.

## Connecting a host from the UI

The `/hosts` page is the recommended way to connect a remote SSH+Docker host once Servyx is running. It walks through three explicit steps, not one form:

1. **Probe** — enter an endpoint (`ssh:user@host:22`) and Servyx connects just far enough to observe the host key. Nothing is trusted yet.
2. **Confirm the fingerprint** — the SHA-256 fingerprint Servyx actually observed is shown on screen. You verify it out of band (against your provider's console, or a fingerprint you saved when you built the box) and explicitly check a box confirming it. This step is built in and is never skipped — it's the same trust-on-first-use confirmation the config-based path does manually with `ssh-keygen -F`, just surfaced in the UI instead of a terminal.
3. **Submit credentials and register** — name the host, supply its SSH private key (upload or paste), and register. The host's containers immediately become discoverable for adoption; no restart is required.

A host registered this way is stored in Servyx's own database, not in `appsettings`. It is used for discovery and the "Adopt a server" flow exactly like a config-declared host — see [Multiple hosts, wired together](#multiple-hosts-wired-together) below for how the two kinds combine.

The config-based method described in the rest of this page, and in [Adopting a remote host](adopting-a-remote-host.md), still works and remains fully supported. It is also still the *only* way to have a host present at first startup — before the dashboard is reachable, there is no `/hosts` page to visit yet, so a host that must exist from the moment Servyx starts (rather than being registered afterward by an operator) has to be declared in configuration.

## Multiple hosts, wired together

Config-declared hosts (`Servyx:Hosts:<name>`) and UI-registered hosts (via `/hosts`) are both wired **concurrently** for container discovery and the "Adopt a server" flow, and each discovered server is tagged with the host it came from.

If a config-declared host and a UI-registered host share the same name, the config-declared entry wins: it is authoritative, and the UI-registered entry with that name is silently shadowed (logged as a warning) rather than treated as a conflict. This is deliberate — configuration is something an operator can read, diff, and audit outside the running process, while a UI-registered host can be added by anyone who can reach the `/hosts` page; a same-named UI registration must never be able to shadow a trusted, explicitly-declared host.

**Scope limitation that still applies:** once a server discovered on a UI-registered host is adopted, that server's other management surfaces — settings, live logs, backups — still resolve against the primary configured host only. Registering a host through `/hosts` makes its containers discoverable and adoptable; it does not yet make every per-server surface for an adopted server multi-host-aware. Broader multi-host support for those surfaces is future work, not something the `/hosts` page changes today.

## Host-key trust (TOFU)

Servyx pins SSH host keys using **trust on first use**: the first time it connects to a host, you are shown that host's SHA-256 key fingerprint and asked to confirm it — ideally by checking it against a fingerprint you obtained out of band (from the host provider, or by running `ssh-keygen -lf` on the box itself). Once confirmed, the fingerprint is pinned and stored, by default, in `servyx-data/host-keys.json`.

There is deliberately **no "accept any host key" setting anywhere in Servyx** — not a checkbox, not a config flag. That omission is intentional: a system that can express "skip verification" eventually has that value set by someone under time pressure, and host-key checking stops meaning anything at all.

### When a host key changes

If a host presents a different key to the one pinned, Servyx treats this as a security event, not a routine connectivity problem:

- The connector is evicted immediately — no further operations run against it.
- A prominent warning is raised naming both the old and new fingerprints.
- Reconnecting requires an explicit, separate re-pin action.

**Do not blindly re-pin.** A changed host key usually means the host was rebuilt (a genuinely new key, expected after a reinstall) — but it can also mean something is intercepting your connection. Before re-pinning, confirm the new fingerprint against an independent source (the host provider's console, a fingerprint you saved when you first built the box) the same way you did on first connection.

## Connected vs degraded

A host connection isn't simply up or down. Servyx reports, per connector: what's reachable at all, which specific channels are working, and which are degraded — along with the reason. A host can be fully reachable while one file-access channel is unavailable; that's a degraded connector, not an unreachable one, and the two are fixed by different people (a network problem versus, say, a disabled `sftp` subsystem in `sshd_config`).

![The servers list, showing state, health, players, uptime, host, and ports for each adopted server](../images/servers-list.png)

---
**Next:** [Adopting servers](adopting-servers.md) · **See also:** [Adopting a remote host](adopting-a-remote-host.md) · [Connectors](../connectors.md)
