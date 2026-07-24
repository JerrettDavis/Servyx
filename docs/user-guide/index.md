# Servyx user guide

Servyx is a self-hosted control panel for game servers. It runs alongside servers you already have — on local Docker, a remote Docker host, or a bare-metal box reachable over SSH — and gives you one dashboard to see their state, health, configuration, and backups.

![The Servyx dashboard, showing server count, player count, foreign backups, and alerts](../images/dashboard-overview.png)

This guide is for **operators**: the person running Servyx to look after one or more game servers. If you want to read or change Servyx's own source code, see the [developer documentation](#developer-documentation) instead.

## Servyx is pre-alpha

Servyx is under active development. The current milestone (M1) is **strictly read-only**: Servyx can see everything about a server it has adopted, but it cannot yet change anything. Every button that would mutate a server is visibly present and visibly disabled, with a reason. Later milestones add writes, backups, remote hosts, and more — see the [roadmap](../roadmap.md) for what is planned and when.

Because of this, several pages in the sidebar — Mods, Plugins, Settings, Users, Audit — are placeholders today. This guide says so plainly wherever that is the case, rather than describing behaviour that does not exist yet.

## The read-only-first philosophy

Servyx's core design choice is to show you the truth about a server before it ever asks to change it:

- **It adopts, it doesn't own.** Servyx attaches to containers and hosts you already run. It never assumes it created something, and it never takes destructive control over something it didn't.
- **It starts blind and earns trust.** A server's control tier only rises when Servyx has evidence it may act — not because you toggled a setting.
- **A disabled button still tells you why.** Rather than hiding an action Servyx can't perform, it shows the action and explains, in plain language, what's missing.
- **Configuration is compared, never guessed.** Servyx reads your intent, your `.env`, your rendered config file, and the live server side by side, and tells you when they disagree.

## Map of this guide

| Page | What it covers |
|---|---|
| [Installation](installation.md) | Prerequisites, running Servyx, the mock demonstration mode, where data lives. |
| [Connecting a host](connecting-a-host.md) | Local vs remote Docker, SSH/SFTP as independent channels, host-key trust. |
| [Adopting servers](adopting-servers.md) | What "adoption" means, what discovery inspects, reading the server list. |
| [Control tiers](control-tiers.md) | The Blind → Observe → Configure → Operate → Provision ladder, in plain terms. |
| [Configuration](configuration.md) | The four-column model, drift, and why the derived file is the wrong place to edit. |
| [Secrets](secrets.md) | What's masked, where secrets live, and what that means for backups. |
| [Backups and saves](backups-and-saves.md) | Foreign vs Servyx-owned archives, the Backups and Saves tabs. |
| [Console and logs](console-and-logs.md) | Reading console output and why the command box is locked. |
| [Troubleshooting](troubleshooting.md) | Common "why is this happening" questions, answered directly. |

## Developer documentation

If you want to understand or extend how Servyx works internally, start with [`docs/architecture.md`](../architecture.md), which links onward to the abstractions, control-plane, connectors, schema, roadmap, and testing documents.

---
**Next:** [Installation](installation.md) · **See also:** [Architecture](../architecture.md)
