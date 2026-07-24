# Control tiers

Every server Servyx manages sits at one of five **control tiers** — a plain-language answer to "how much can Servyx do for this server right now?" The tiers form a ladder, but what actually unlocks each rung differs from server to server, because it depends on what that particular deployment happens to expose (file permissions, whether the Docker socket is reachable, whether RCON is enabled). The full technical model is in [The Control Plane](../control-plane.md); this page explains what it means for you.

## The five tiers

| Tier | What it means for you |
|---|---|
| **Blind** | Servyx has no reliable information about this server yet. |
| **Observe** | Servyx can see this server is running and watch it — state, health, logs, metrics, and its rendered configuration. |
| **Configure** | Servyx can change settings and restart the server. |
| **Operate** | Servyx can back up, restore, and act on the running server live (for example, sending a console command). |
| **Provision** | Servyx can create, recreate, and fully manage the deployment itself. |

Reaching a tier doesn't require one specific mechanism. "Change the max player count," for instance, can be satisfied by editing `.env` and restarting, by editing a directly-writable config file, or by writing to `compose.yaml` — any one of those is enough to count as reaching **Configure**, and which one a given deployment offers is a separate detail from whether the tier is reached at all.

## Why a disabled button is a feature, not a bug

When an action isn't available, Servyx shows it anyway — greyed out, with a lock icon and a tooltip explaining why — rather than removing it from the page. You'll see this on every power control (Start, Restart, Stop, Kill), every settings field, and the console command box.

![A gated control showing its lock icon and disabled state](../images/control-tier-read-only.png)

This is deliberate. Hiding an action you might reasonably expect to exist looks like a missing feature; showing it disabled, with a reason, tells you the truth about what's possible and what it would take to change that. A refusal that explains itself is more useful than an action that silently isn't there.

## How evidence raises a tier

Servyx doesn't take your word for what it can do — it checks. Two kinds of checks feed into a tier:

- **Passive checks**, run automatically and safely in the background: reading file ownership and permissions, inspecting a container's mount flags, listing a directory. These never write anything.
- **Active checks**, which briefly write and then delete a small test file in the exact location a real write would use, to verify — not infer — that a write will actually work. This is the only kind of check Servyx runs that touches disk, and it only ever runs when you explicitly ask for it.

A check's result isn't a plain yes/no either. Servyx distinguishes **Verified** (an active check proved it), **Inferred** (a passive check strongly suggests it), **Unknown** (Servyx hasn't been able to determine the answer — a check didn't run, or failed), and **Denied** (Servyx checked and was refused). The distinction between Unknown and Denied matters: Unknown means "we don't know yet," not "no" — a server that is fully capable of something shouldn't be told it can't, just because a single check timed out once.

## What read-only mode means today

Servyx's current milestone is **strictly read-only** end to end: every mutating control across the whole product — every power button, every settings field, the console command box — is locked with the same message: *"Servyx is in read-only mode. Writes are enabled per-server in Milestone 4."* This is a simpler, uniform gate rather than the fully per-server, evidence-driven tier described above; the richer per-server evidence, remediation hints, and the interactive "verify a write will work" check are part of the control-plane design but are not yet wired into the running dashboard. Until then, every server behaves the same way from a write perspective, regardless of what the underlying host would actually permit.

---
**Next:** [Configuration](configuration.md) · **See also:** [The Control Plane](../control-plane.md)
