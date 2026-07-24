# Backups and saves

## Foreign vs Servyx-owned archives

An archive Servyx finds on disk that it did not create itself is **foreign**. For the standard Palworld Docker image, that typically means the `.tar.gz` files the container's own daily cron job produces — entirely independent of Servyx. Servyx lists these, shows their name, creation time, and size, and marks them clearly as **Foreign**.

Servyx-owned backup creation — Servyx initiating a backup, retaining it under its own policy, and restoring from it — is not implemented yet; it ships in a later milestone. Today, every backup you see was made by something other than Servyx.

## Why foreign archives get no destructive controls

Foreign archives are listed **read-only, with no delete, prune, or restore control offered at all** — not even a disabled one. This isn't an oversight: Servyx does not own these archives, has no retention policy governing them, and must never appear to offer control over their lifecycle. Presenting even a disabled "Delete" button next to a foreign backup would misrepresent what Servyx is entitled to do to a file it didn't create.

![A server's Backups tab showing foreign backups with no destructive controls](../images/server-backups-foreign.png)

## The per-server Backups tab

Each server's detail page has a Backups tab listing that server's own on-disk archives — file, created time, size, and ownership. This is the same read-only listing described above, scoped to one server.

## The estate-wide Backups page

`/backups` shows the same kind of listing across every adopted server at once — server, file, created time, size, ownership — so you can see backup coverage across your whole estate in one place, rather than clicking into each server individually.

![The estate-wide Backups page listing foreign archives across every adopted server](../images/backups-overview.png)

## The Saves tab

The Saves tab shows the server's world data directly from disk:

![A server's Saves tab showing the world ID, level file, and per-player save files](../images/server-saves.png)

- **World ID** — the world's identifier.
- **Level file** — the main save file and its size (for Palworld, `Level.sav`), alongside its companion metadata file.
- **Player saves** — one entry per player who has joined the world, with file name and size. A world with no players yet shows an empty list rather than an error.

If the world directory can't be read at all, the tab says so plainly rather than showing a blank or misleading page.

---
**Next:** [Console and logs](console-and-logs.md) · **See also:** [Architecture — IBackupProvider / IBackupAdopter](../architecture.md)
