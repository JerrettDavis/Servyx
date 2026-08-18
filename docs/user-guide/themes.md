# Themes

Servyx renders in light or dark colours, chosen from a three-state toggle in the top bar, next to the page
title.

![The dashboard rendered in dark theme, showing server count, player count, foreign backups, and alerts](../images/dashboard-overview-dark.png)

## The toggle: System, Light, Dark

The toggle offers exactly three choices, each shown as a small icon:

- **System** (monitor icon) — follow the operating system's own light/dark preference, live. If your OS
  switches from light to dark (for example, at sunset, on a schedule your OS controls), Servyx follows
  automatically without a page reload.
- **Light** (sun icon) — always light, regardless of what the OS prefers.
- **Dark** (moon icon) — always dark, regardless of what the OS prefers.

Click a choice to switch immediately — there's no separate "save" step, and no page reload. Whichever option
is currently selected is highlighted; **System** is the default for a browser that has never made a choice.

## The choice persists per browser

Once you pick **Light** or **Dark**, that choice is written to the browser's own local storage and applied on
every later visit, from any page — including the sign-in screen, before you've even reached a dashboard.
It's a per-browser preference, not a per-account one: a different browser, or the same browser in a private
window, starts back at **System** until it's told otherwise. Switching back to **System** clears the
explicit choice and returns to following the OS.

An explicitly stored **Light** or **Dark** choice always wins over whatever the OS currently prefers. This is
deliberate: once you've told Servyx what you want, it stops guessing.

## No flash on load

The resolved theme is applied to the page before the first pixel paints — there's no visible flip from light
to dark (or back) as a page loads, including on the very first request of a session.

## Every screen, in both themes

Every screenshot in this guide that shows the light interface has a dark-theme counterpart below, so you know
what to expect either way you run Servyx.

### Dashboard and servers

![The server list, in dark theme](../images/servers-list-dark.png)

![The servers list showing a local server and a remote one adopted over ssh+docker, in dark theme](../images/servers-list-remote-host-dark.png)

![A remote server's Overview tab, in dark theme](../images/remote-server-overview-dark.png)

![The Overview tab's Status card and its health tooltip, in dark theme](../images/remote-server-health-explanation-dark.png)

![A server's Overview tab showing state and health as separate indicators, in dark theme](../images/server-overview-dark.png)

![A gated, disabled power control with its lock icon and tooltip, in dark theme](../images/control-tier-read-only-dark.png)

### Console, saves, and backups

![A server's Console tab, in dark theme](../images/server-console-dark.png)

![The Console tab's command panel with no RCON control channel configured, in dark theme](../images/console-no-rcon-channel-dark.png)

![A server's Saves tab, in dark theme](../images/server-saves-dark.png)

![A server's Backups tab showing foreign backups with no destructive controls, in dark theme](../images/server-backups-foreign-dark.png)

![The estate-wide Backups page, in dark theme](../images/backups-overview-dark.png)

### Configuration

![The four-column settings view, in dark theme](../images/settings-four-columns-dark.png)

![A secret-typed setting shown masked across all four columns, in dark theme](../images/settings-secret-masking-dark.png)

### Diagnostics and the games catalogue

![The top bar's connection status pill, in dark theme](../images/connection-status-healthy-dark.png)

![The Games page listing a bundled definition and its deployment profiles, in dark theme](../images/games-catalogue-dark.png)

![The Audit page's accountability trail, in dark theme](../images/audit-page-dark.png)

### Deploying and write access

![The provisioning gate explaining its own configuration key, in dark theme](../images/provisioning-gate-closed-dark.png)

![The Power card under WriteMode.PreviewOnly, in dark theme](../images/preview-only-stop-plan-dark.png)

![The Power card with Start, Restart, Stop, and Kill rendered live and clickable, in dark theme](../images/lifecycle-controls-enabled-dark.png)

### First run

![The first-run sign-in page, in dark theme](../images/operator-first-run-login-dark.png)

### Placeholder pages and unknown routes

Mods, Plugins, Users, and the application-level Settings page — see
[Operator administration](operator-administration.md) for what each of these placeholders says today — and
the page Servyx shows for a route it doesn't recognise, all render in dark theme exactly the same way as
everything else: the toggle is a global, whole-application setting, not something individual pages opt into.

![The Mods placeholder page, in dark theme](../images/mods-dark.png)

![The Plugins placeholder page, in dark theme](../images/plugins-dark.png)

![The Users placeholder page, in dark theme](../images/users-dark.png)

![The application-level Settings placeholder page, in dark theme](../images/settings-dark.png)

![The Not Found page, in dark theme](../images/not-found-dark.png)

The Error page — directly routable at `/Error`, reporting the request's own trace id — is no exception: see
[Troubleshooting](troubleshooting.md#i-ended-up-on-the-error-page) for what it means.

![The Error page, in dark theme](../images/error-page-dark.png)

---
**Next:** [Back to the guide hub](index.md) · **See also:** [Operator administration](operator-administration.md) · [Installation](installation.md)
