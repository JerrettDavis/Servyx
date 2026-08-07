# Connecting a host

Servyx reaches a game server through a **transport** — the pipe it uses to talk to the machine the server runs on. There are two: Docker (local or remote-over-TCP) and `ssh+docker` — a remote host reached over SSH, running Docker, that Servyx drives the `docker` CLI against. This page covers the transport model shared by both; for the concrete steps to declare an `ssh+docker` host in configuration, see [Adopting a remote host](adopting-a-remote-host.md).

## Local Docker

By default Servyx connects to the Docker engine on the machine it is running on: the standard Unix socket on Linux, or the Docker Desktop named pipe on Windows. This needs no configuration — if Docker is running and reachable, Servyx uses it.

## Remote Docker

Servyx's Docker transport also understands `tcp://` (and `http(s)://`) endpoints, and honours the standard `DOCKER_HOST` environment variable, so it can point at a Docker engine on another machine. There is currently no in-dashboard wizard for this — it is set once, at the process level, before Servyx starts, not per-server from within the UI.

## SSH and SFTP are independent channels

Servyx's design treats SSH exec (running commands) and SFTP (reading and writing files) as **two separate channels that happen to often travel over the same connection**, not one channel that assumes the other. This matters in practice: it is entirely normal for a host to allow you to run commands over SSH while its SFTP subsystem is disabled, or vice versa. When that happens, Servyx reports the exec channel as working and the file channel as degraded (or the reverse) — it does not collapse "I can't read your files" into "I can't reach your host," because those are different problems with different fixes. See [Connectors](../connectors.md) for the full model, including how Docker-over-SSH composes both channels together, since the Docker API itself cannot read `.env` or `compose.yaml` — those live on the host filesystem, not inside anything the Docker API exposes.

**What works today:** the SSH transport and its exec/SFTP composition are wired into the running `Servyx.Web` dashboard — a host declared under `Servyx:Hosts:<name>` with `Transport: ssh+docker` replaces the dashboard's probe target, and the server it observes appears in the servers list with its **Host** column reading `ssh+docker` instead of a local Docker socket/pipe description. This milestone wires exactly one remote host: only the first configured entry under `Servyx:Hosts` is connected to anything, and there is still no in-dashboard wizard for declaring it — it's config, set before Servyx starts, not a form in the UI. See [Adopting a remote host](adopting-a-remote-host.md) for the full walkthrough: the config keys, host-key pinning, and getting the SSH credential into the secret store.

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
