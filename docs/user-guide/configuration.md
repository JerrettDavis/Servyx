# Configuration

## The four columns

Every setting Servyx tracks for a server is shown across four columns, side by side:

| Column | What it is |
|---|---|
| **Desired** | Servyx's own record of your intent — what you asked for. |
| **Authoritative** | The current value in the file Servyx is actually allowed to write to (typically `.env`). |
| **Rendered** | The current value in the file the game's own entrypoint generates from that authoritative source at boot — for Palworld, `PalWorldSettings.ini`. |
| **Runtime** | The live value on the running server, read over its control channel (RCON/REST) where available. |

![The four-column settings view: Desired, Authoritative, Rendered, and Runtime, with a drift indicator](../images/settings-four-columns.png)

These columns exist because a game server's "current setting" is not one fact — it's up to four facts that are usually in agreement and occasionally aren't. Each pair disagreeing means something different:

- **Desired vs Authoritative** — you asked for a change that hasn't been written to `.env` yet.
- **Authoritative vs Rendered** — `.env` has the new value, but the server hasn't regenerated its config file from it (usually: needs a restart).
- **Rendered vs Runtime** — the config file has the new value, but the running process hasn't picked it up yet.

Any of these disagreements is called **drift**, and Servyx flags it rather than picking one column and presenting it as "the" value.

## What to do about drift

Drift isn't automatically a problem — a value you just changed will legitimately drift for a moment until a restart catches it up. What matters is whether the drift is expected (you changed something and haven't restarted yet) or not (something changed outside Servyx — someone edited a file by hand, or the container was recreated from an older image). Servyx's job is to tell you which columns disagree and why; deciding what to do about it — restart, re-apply, or investigate an external change — is yours in the current milestone, since Servyx cannot yet write configuration on your behalf.

## Byte-exact round-trip

When Servyx does gain the ability to write configuration, a hard requirement underpins it: reading a file and writing it back out unchanged must reproduce it **byte-for-byte** — comments, blank lines, key order, and quoting style included. An editor that "normalises" a config file as a side effect of touching one value is not acceptable here, because that file is often hand-maintained and shared, and a diff full of incidental reformatting hides the one line that actually changed. See [the schema reference](../schema.md) for how individual settings map onto the files a game definition declares.

## Why editing the rendered file is the classic mistake

It's tempting to edit `PalWorldSettings.ini` directly, because that's the file the game visibly reads. Don't. For the standard Palworld Docker image, that file is **derived**: the container's own entrypoint regenerates it from `.env` on every boot, so a direct edit survives only until the next restart, then vanishes without a trace, and you're left wondering why your change "didn't stick" or silently reverted. The same file path can be authoritative on a different kind of deployment (a bare-metal install with nothing regenerating it) — the role belongs to the deployment, not the file format, which is exactly why Servyx tracks it as a first-class fact rather than assuming from the file's name or format. Change the `.env`/authoritative side; let the entrypoint regenerate the rest.

---
**Next:** [Secrets](secrets.md) · **See also:** [Game definition schema](../schema.md)
