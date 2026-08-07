# Console and logs

## Reading the console

The Console tab on a server's detail page streams that server's log output, each line stamped with the time it was produced (`HH:mm:ss`).

![A server's Console tab, showing timestamped log lines and the locked command input](../images/server-console.png)

## Timestamps and warning highlighting

Log lines can be visually flagged as warnings so a significant line doesn't get lost while scrolling past routine output. The demonstration data set (`Servyx:DataSource=Mock`) illustrates this with the classic case: a `401 Unauthorized` line from a container's own healthcheck is highlighted, rather than blending in as routine noise — see "Correlating a log warning with the health badge" below for why that specific line matters.

## The RCON command panel

Below the log view is a command panel that sends catalogued RCON commands to the server — a `<select>` of known commands, not a free-text box. Read-only commands (`info`, `players`) work regardless of write mode; mutating commands (`save`, `broadcast`, `kick`, `ban`, `shutdown`, `doexit`) require the server's write mode to be `Enabled` and a second, explicit confirmation step before anything is sent. See [The RCON console](rcon-console.md) for the full picture — the command catalogue, the confirmation step, and how Servyx reaches RCON at all.

## Correlating a log warning with the health badge

Container health and game readiness are tracked as **separate signals**, deliberately, because they can and do disagree. The motivating case: the standard Palworld Docker image's own healthcheck calls an API endpoint without supplying admin credentials, so it receives `401 Unauthorized` on every single check — and Docker marks the container **unhealthy** as a result, even though the game server is running perfectly normally and players are connected. If you see a server's Health badge reporting unhealthy, check the console for repeated `401 Unauthorized` (or similar authentication-failure) lines around the same time — that combination is the signature of this exact case, not a real outage. See [Troubleshooting](troubleshooting.md) for the full walkthrough.

---
**Next:** [Troubleshooting](troubleshooting.md) · **See also:** [The RCON console](rcon-console.md) · [Architecture — Readiness vs. Container Health](../architecture.md)
