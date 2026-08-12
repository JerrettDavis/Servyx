# Adopting a remote host

Servyx's headline capability is watching a game server that runs on a machine you never log into directly — a box reachable only over SSH, running Docker, somewhere that is not the machine Servyx itself is running on. This is the `ssh+docker` transport: Servyx opens an SSH session to the remote host and drives the `docker` CLI over it, the same way you would by hand, but read-only and continuously.

![The servers list showing a local server and a second server adopted over ssh+docker, distinguished by its Host column](../images/servers-list-remote-host.png)

## Connecting the host

A remote host needs to be connected to Servyx before there is anything for the adoption steps below to find. There are two ways to do that:

- **The `/hosts` page (recommended once Servyx is running).** Go to **Hosts** in the sidebar, enter the SSH endpoint, probe it, confirm the fingerprint Servyx shows you, then supply the SSH credential and register. See [Connecting a host from the UI](connecting-a-host.md#connecting-a-host-from-the-ui) for the full step-by-step. This is faster for a host added after Servyx is already up, and it verifies the host key interactively as part of the flow — no manual `ssh-keygen` step required.
- **Configuration (`Servyx:Hosts:<name>`).** Still fully supported, and still the only option if the host needs to be present the moment Servyx starts — before the dashboard is reachable, `/hosts` isn't an option yet. The rest of this section walks through the config keys.

Both kinds of host — config-declared and UI-registered — are wired concurrently for discovery and adoption; see [Multiple hosts, wired together](connecting-a-host.md#multiple-hosts-wired-together) for how they combine and the precedence rule on a name collision. The container-adoption steps later on this page ([What adoption looks like](#what-adoption-looks-like) onward) are identical regardless of which way the host was connected.

## Declaring a remote host in configuration

A remote host can be declared in configuration, under `Servyx:Hosts:<name>`, where `<name>` is a label you choose for it:

```json
{
  "Servyx": {
    "Hosts": {
      "my-remote-box": {
        "Enabled": true,
        "Transport": "ssh+docker",
        "Endpoint": "ssh:user@<REMOTE_HOST>:22",
        "CredentialUrn": "secret://connector/my-remote-box/ssh/private-key",
        "TrustPolicy": "requirePinned",
        "PinnedFingerprints": "SHA256:REPLACE_ME",
        "Container": "palworld-server"
      }
    }
  }
}
```

| Field | Meaning |
|---|---|
| `Enabled` | Must be an explicit, parseable `true`. Missing, empty, or unparseable is treated as a misconfiguration and logged — it does not silently default to on or off. |
| `Transport` | Optional; when present it must be `ssh+docker` for this host to be picked up at all. Leaving it unset also works — it exists so this same section can later host other, non-Docker host kinds without them being misread as `ssh+docker` entries. |
| `Endpoint` | `ssh:user@host:port` — the `ssh:` prefix and the port are both optional; a bare `host` defaults to port 22. |
| `CredentialUrn` | A `secret://{scope}/{scopeId}/{category}/{name}` locator for the SSH private key (see [Getting the key into the secret store](#getting-the-key-into-the-secret-store) below). It names *where* the credential lives — never the credential itself. |
| `TrustPolicy` | `requirePinned` (the default if omitted) or `trustOnFirstUse`. See [Host-key pinning](#host-key-pinning-fails-closed) — in practice this field rarely matters, because `PinnedFingerprints` below, when set, is checked first regardless of what this says. |
| `PinnedFingerprints` | A comma-separated list of `SHA256:...` fingerprints. If set, this is compared directly against the key the host presents on every connection attempt, ahead of anything else. |
| `Container` | The name of the container on that host Servyx should observe. Required. |

## Multiple hosts, and the precedence rule

You can declare more than one host under `Servyx:Hosts`, and you can also register hosts through the `/hosts` page — every config-declared host and every enabled UI-registered host is wired concurrently for discovery, and each server that turns up is tagged with the host it came from.

If a config-declared host and a UI-registered host share the same name, the config-declared one wins: it is authoritative, and the UI-registered entry with that name is silently shadowed (logged as a warning) rather than treated as a conflict. See [Multiple hosts, wired together](connecting-a-host.md#multiple-hosts-wired-together) for the full rule and rationale.

**Scope limitation that still applies:** discovery and adoption are multi-host-aware, but once a server from a UI-registered host is adopted, its other management surfaces — settings, live logs, backups — still resolve against the primary configured host only. Full multi-host support for those surfaces has not shipped yet; that is a genuine, deliberate limitation of the current milestone, not a bug to work around.

## Host-key pinning fails closed

Before Servyx runs a single command against a remote host, it verifies the SSH host key the box presents. This check is enforced structurally, not as a step someone can forget: SSH.NET's own connection handshake is wired to abort unless the verifier reports the key as **Trusted** — no exec session, no file-read session, nothing is ever constructed for any other outcome (unknown host, changed key, or explicitly revoked key). There is no configuration path that skips this, and there is no UI path that skips it either.

This section describes fingerprint verification for a **config-declared** host, where you obtain and pin the fingerprint yourself before Servyx ever connects. If you're connecting through the `/hosts` page instead, the probe-and-confirm step there does the same job interactively: Servyx shows you the fingerprint it actually observed and requires you to confirm it before the credential step even appears — see [Connecting a host from the UI](connecting-a-host.md#connecting-a-host-from-the-ui). Neither path trusts a host key without an explicit confirmation; they just collect that confirmation differently.

A key becomes Trusted one of two ways:

- **`PinnedFingerprints` matches directly.** If you set this field, Servyx compares the presented key's SHA-256 fingerprint against your list on every connection, and nothing else is consulted. This is the most explicit, most auditable option, and the one shown in the example above.
- **The key was already pinned in Servyx's persisted host-key store** (`servyx-data/host-keys.json` by default — see [Connecting a host](connecting-a-host.md)), from an earlier trust-on-first-use confirmation.

Absent either of those, the verdict is `Unknown` — never `Trusted` — and the connection is refused outright. There is no "accept any key" setting anywhere in Servyx; that is intentional (see [Connecting a host](connecting-a-host.md) for why).

### Obtaining a fingerprint

To fill in `PinnedFingerprints` correctly, get the fingerprint from a source you trust independently of Servyx — never invent one or copy one from an untrusted channel. One straightforward way: connect to the host once with a plain `ssh` client, confirming the key you're shown against your host provider's console or a fingerprint you saved when you built the box, then read it back out of your local `known_hosts`:

```bash
ssh-keygen -F <REMOTE_HOST> -l
```

This prints the SHA-256 fingerprint of whatever key is on record for `<REMOTE_HOST>` in your `known_hosts` file. Paste that value — the whole `SHA256:...` string — into `PinnedFingerprints`.

## Getting the key into the secret store

`CredentialUrn` only *names* a secret; it does not carry one. Servyx's own secret store is where the actual private-key bytes have to end up before a connection can use them, and the supported way to get a key in is a config-driven, startup-only import: `Servyx:Secrets:Import`.

```json
{
  "Servyx": {
    "Secrets": {
      "Import": {
        "secret://connector/my-remote-box/ssh/private-key": "/path/to/plaintext-key-file"
      }
    }
  }
}
```

Each entry maps a `secret://...` URN to a file path. On startup, Servyx reads that file's bytes exactly as they are — no trimming, no encoding changes, since a private key is whitespace- and newline-sensitive — and writes them into the encrypted secret store under that URN.

Two behaviors are worth knowing before you rely on this:

- **It never overwrites an existing secret.** If a secret already exists at that URN, the import is skipped and logged, which is what makes it safe to leave `Servyx:Secrets:Import` in your configuration permanently — every later restart is a no-op for a key that's already imported.
- **A named-but-unreadable file is fatal at startup, by design.** If the source path doesn't exist, can't be read, or is empty, Servyx refuses to start rather than come up with a connector missing the credential it needs. This is intentionally loud: a confusing authentication failure discovered later, with no obvious cause, is worse than a startup crash naming the exact missing file.

**Once the import has run once, delete the plaintext key file.** The secret store holds the only copy Servyx needs from that point on; a plaintext private key left on disk after import is pure liability with no further purpose.

## What adoption looks like

Once a host is connected — either declared, enabled, its key pinned, and its credential imported in configuration, or registered and fingerprint-confirmed through `/hosts` — the server it observes shows up in the servers list exactly like a local one — same columns, same State/Health badges — with one visible difference: its **Host** column reads `ssh+docker` instead of a local Docker socket/pipe description.

## Reading the remote server's Overview tab

![A remote server's Overview tab, showing published game/query ports, the RCON port marked not published to host, the data mount, and network details](../images/remote-server-overview.png)

The Overview tab reads exactly the same for a remote server as a local one — Servyx does not know or care, once connected, that the container lives on another machine. Two things worth calling out on the Palworld deployment specifically:

- **Published vs. unpublished ports.** The game port (8211/UDP) and query port (27015/UDP) are published to the host and reachable from outside the container. RCON (25575/TCP) is **not published to the host** — Servyx labels it plainly as such. This is not a misconfiguration to fix; it is why a direct-TCP RCON connection can never work against this container, and it's the reason Servyx's control channel falls back to running commands *inside* the container over the same SSH/Docker connector instead (see [Diagnostics](diagnostics.md)).
- **The data mount, network, and resource limits** are read the same way as for a local container — through Docker's own inspect data, carried back over the SSH/Docker connector rather than a local socket.

## The Palworld "unhealthy" false negative, on a remote host too

![The Overview tab's Status card, showing the Health badge whose tooltip carries the false-negative explanation](../images/remote-server-health-explanation.png)

The same false-negative health signal documented for the local server (see [Troubleshooting](troubleshooting.md)) applies identically here, because it is a property of the container image, not of which transport reached it: the bundled Palworld image's own `HEALTHCHECK` calls an internal REST endpoint without admin credentials and gets `401 Unauthorized` on every probe, so Docker reports the container `unhealthy` while the game itself runs fine. Servyx surfaces this as an explicit explanation on the Health badge's tooltip — quoted here since a tooltip's text is not something a screenshot can show on its own:

> The container's own HEALTHCHECK calls http://localhost:8212/v1/api/info without admin credentials and receives 401 Unauthorized on every probe. The Palworld server itself is healthy — /v1/api/players returns OK on the same polling cycle. Servyx derives readiness from its own authenticated detectors, never from this signal.

Hover or focus the badge to read it in full; the screenshot above shows the badge itself, in place, on the remote server's Status card.

---
**Next:** [Diagnostics](diagnostics.md) · **See also:** [Connecting a host](connecting-a-host.md) · [Secrets](secrets.md) · [Troubleshooting](troubleshooting.md)
