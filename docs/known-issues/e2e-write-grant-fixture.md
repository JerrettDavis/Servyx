# Known issue: the write-enabled E2E fixture cannot grant writes

Four scenarios in `Servyx.E2E.Bdd.Tests` are currently **red**:

- Both `@write-enabled-host` scenarios in
  `tests/Servyx.E2E.Bdd.Tests/Features/WriteModeTiers.feature`:
  *"PreviewOnly renders the stop-escalation ladder and offers no control at all"*
  (~line 27) and *"A fully-enabled server shows live, clickable Start, Restart,
  Stop, and Kill controls"* (~line 34, also tagged `@requires-docker`).
- Their dark-theme twins in `tests/Servyx.E2E.Bdd.Tests/Features/Theming.feature`,
  tagged at ~line 139 and ~line 147 respectively.

**This is pre-existing test-infrastructure breakage, not a regression from any
recent change, and not a product bug.** The product's container-identity and
write-grant path — a grant bound to `Server.ContainerId`, matched through real
discovery, documented in [`docs/user-guide/enabling-writes.md`](../user-guide/enabling-writes.md)
and enforced by `DbBackedWriteModeResolver` — is correct under the app's
default `Live` data source. This is purely a gap in how the E2E harness tries
to fake write access under `Servyx:DataSource=Mock`.

## Root cause

Three pieces of test infrastructure disagree with each other about how a
write grant gets attached to a server:

1. **`MockDashboardDataService.cs`** mints two fully synthetic servers for
   display purposes — `palygondwanaland` at line 14
   (`private const string ServerId = "palygondwanaland";`) and
   `example-remote-palworld` at line 55
   (`private const string RemoteServerId = "example-remote-palworld";`).
   These ids exist only in this mock's in-memory data; by construction they
   match no real `Server.ContainerId` in Servyx's database.

2. **`ServyxAppProcess.cs`** forces the E2E app into mock mode. Note: the task
   that produced this document described this file as living at
   `tests/Servyx.E2E.Bdd.Tests/Support/ServyxAppProcess.cs`; that path does not
   exist. The actual file is
   `tests/Servyx.E2E.Tests/ServyxAppProcess.cs`, shared into the BDD suite via
   `using Servyx.E2E.Tests;` from `WriteEnabledAppFixture.cs`. Line 74 sets
   `startInfo.Environment["Servyx__DataSource"] = "Mock"` unconditionally for
   every E2E app process, including the write-enabled one.

3. **`WriteEnabledAppFixture.cs`** tries to grant write mode through
   environment variables at lines 49–50:

   ```csharp
   ["Servyx__Servers__palygondwanaland__WriteMode"] = "PreviewOnly",
   ["Servyx__Servers__example-remote-palworld__WriteMode"] = "Enabled",
   ```

   But `ServerWriteModes.cs` documents, in its own class remarks, that
   `Servyx:Servers:<key>:WriteMode` is **deliberately ignored** for the
   `docker` transport now that write grants moved from a config-time
   singleton to a per-server database column (`Server.WriteMode`), flipped
   from the UI with attribution. The fixture predates that migration and was
   never updated to seed a grant the new way — there being no old, config-only
   way to reach it anymore. `ServyxCoreCompositionExtensions.cs` even logs a
   startup warning naming every such ignored key
   (`ServerWriteModes.FindIgnoredLegacyKeys`), so the failure is quiet only in
   the sense that the E2E assertions themselves don't inspect startup logs.

`WriteEnabledAppFixture.cs` does correctly provision a *real* Docker container
(`docker create`, never started) named `example-remote-palworld` so that real
discovery can find something for the "Enabled" scenario to render controls
against — see the fixture's own remarks around lines 20–36. That solves the
*discovery* half of the problem for one of the two mock servers. It does not
solve the *grant* half for either: even when discovery succeeds, there is no
database row carrying a non-`ReadOnly` `WriteMode` for that container, because
the only mechanism the fixture uses to try to set one is the ignored config
key. `palygondwanaland` fails on both counts — no real container is
provisioned under that name at all, and the grant it tries to set is ignored
just the same.

## Why the obvious shortcut is wrong

**Do not fix this by loosening the write-grant lookup to accept a display
name or a mock id instead of a real container id.** The grant is deliberately
keyed by `Server.ContainerId` — the discovery-native id a container's own
Docker daemon assigns — specifically because a name is not a safe substitute:
two different hosts can each run a container with the same name, and matching
a grant by name would let one container's write access silently apply to an
unrelated container that happens to share it. That would be a security
regression, not a fix. See `ServerAdoptionService.cs` (~line 202,
`discovered.FirstOrDefault(s => string.Equals(s.ServerId, containerId, ...))`)
and `docs/user-guide/enabling-writes.md`'s "Grants are doubly narrow" section
for why identity-by-id is load-bearing, not incidental.

## Blocked follow-up: the expanded ChangePlanPanel screenshot

A `change-plan-panel-expanded` screenshot could not be captured for the same
underlying reason. `ChangePlanPanel.razor`'s `PreviewEnabled` property
(~line 330) requires `WriteMode != WriteMode.ReadOnly`:

```csharp
private bool PreviewEnabled =>
    _executor is not null
    && WriteMode != WriteMode.ReadOnly
    && !HasUnsavedEdits
    && !string.IsNullOrWhiteSpace(ServerId)
    && _state != PanelState.Previewing;
```

The current mock-based E2E harness cannot reach any server with a real,
non-`ReadOnly` write mode, so this panel state is unreachable until the
fixture below is fixed.

## Remediation plan (future session — not attempted here)

1. Point the E2E app at a throwaway database via
   `Servyx__Persistence__ConnectionString` instead of the default SQLite file
   under `AppContext.BaseDirectory` — see
   `ServyxCoreCompositionExtensions.cs` ~line 520
   (`builder.Configuration["Servyx:Persistence:ConnectionString"]`).
2. Create a second stub Docker container actually named `palygondwanaland`
   (mirroring what `WriteEnabledAppFixture` already does for
   `example-remote-palworld`), so discovery can find both mock servers.
3. Seed adoption and write grants through the app's own APIs, not a direct
   database insert. `IServerAdoptionService.AdoptAsync` requires a genuinely
   discovered container id (`ServerAdoptionService.cs` ~line 202), so there is
   no shortcut that bypasses real discovery.
4. Drop the now-dead `Servyx__Servers__<id>__WriteMode` environment variables
   from `WriteEnabledAppFixture.cs` once the database-backed grant path
   replaces them.
5. Re-tag the currently-untagged `PreviewOnly` scenario as `@requires-docker`
   in both `WriteModeTiers.feature` and `Theming.feature`, since after this
   fix it would need a real container the same way the `Enabled` scenario
   already does.
6. Re-capture the `preview-only-stop-plan`, `lifecycle-controls-enabled`, and
   both `-dark` twin screenshots once the fixture is fixed — the versions
   currently committed under `docs/images/` predate this gap and are not
   regenerated by the current (red) scenarios.

## Status

Until the plan above lands, treat these four scenarios as a known, tracked
gap in E2E coverage — not something to "fix" by weakening product code, and
not evidence of a write-grant regression in the product itself. Everything
else in the suite, including the default read-only host every other scenario
runs against,
continues to pass.
