# Testing Servyx

This document explains the test pyramid, what belongs where, and exactly how
to run each layer. All commands assume the repository root as the working
directory.

## The pyramid

| Layer | Project(s) | Speed | Needs Docker? | What belongs here |
|---|---|---|---|---|
| Plain unit tests | `Servyx.Domain.Tests`, `Servyx.Application.Tests`, `Servyx.Infrastructure.Tests`, `Servyx.Infrastructure.Docker.Tests`, `Servyx.Config.Tests`, and the non-`Integration` tests in `Servyx.Infrastructure.Ssh.Tests` | Milliseconds | No | Pure logic and algorithms: value-object behavior, a single method's edge cases, calculations (e.g. `DockerCpuPercentCalculator`), parsing. `[Fact]`/`[Theory]` + AwesomeAssertions `.Should()`. No behavioral narrative needed — these test *how*, not *why*. `Servyx.Infrastructure.Docker.Tests` talks to a substituted `IDockerClient`/`IDockerEnvironment`, never a real daemon. `Servyx.Infrastructure.Tests`' secret-store and host-key-store tests write only under a per-test, GUID-named directory beneath `Path.GetTempPath()` — never outside it (note: those temp files/directories are not deleted afterwards, so they do accumulate in the OS temp folder over many runs; this is a hygiene nit, not a hermeticity violation). |
| BDD scenarios | `Servyx.Bdd.Tests` | Milliseconds | No | **Product guarantees** expressed as `Given/When/Then` behavior, grouped by `[Feature]`: read-only safety, path sandboxing, container adoption, configuration drift, secret protection, graceful degradation, observability correctness. Fast and NSubstitute-backed like unit tests, but the point is the *readable scenario*, not the assertion mechanics — this is where you'd point a new contributor to understand what Servyx promises and why. Uses TinyBDD (see below). |
| bUnit component tests | `Servyx.Web.Tests` | Milliseconds | No | Blazor component rendering in isolation: does this component render the right markup for these inputs, does a masked secret ever reach rendered HTML, is a gated control actually disabled. Fast because there's no real browser for almost all of these — but cannot exercise real user interaction (clicks that require a live SignalR circuit), real navigation, or anything CSS/layout-dependent. **One exception**: `Integration/InteractiveRenderModeTests.cs` in this project is not a bUnit test — it launches the real `Servyx.Web` app as a subprocess and issues a real loopback HTTP request to it (no Docker, no external network, just `127.0.0.1`), as a regression guard for a real outage bUnit structurally cannot detect (see the doc comment on that class). It runs by default; it is fast (well under a second) and does not warrant opt-in gating, but it is the one test in the default run that is not purely in-process. |
| Container-backed integration tests | `Servyx.Infrastructure.Ssh.Tests` (the `[Trait("Category", "Integration")]` tests under `Integration/`) | Tens of seconds | **Yes** — starts a real `linuxserver/openssh-server` container per test via Testcontainers | Real, non-mocked round trips against an actual SSH/SFTP server: password and key auth, host-key TOFU pinning and rejection, atomic file writes, exec argument quoting, the exec-only shell-fallback file channel. `SshDockerIntegrationTests.cs` additionally proves the `ssh+docker` transport against a planted stub `/usr/local/bin/docker` inside the same container (see "Testing the ssh+docker transport" below). These need a genuine SSH server on the other end and are deliberately excluded from the default run — see "Container-backed integration tests" below. |
| E2E (Playwright) | `Servyx.E2E.Tests` | Seconds | No (runs against `Servyx:DataSource=Mock`) | Whole-page, real-browser flows: does the sidebar actually render, does clicking a real nav link actually navigate, does the whole page assembled from many components hang together. The layer of last resort — slow and heavier to maintain, so reserved for flows that specifically need a real browser and a real socket. Requires Playwright's browser binaries to be installed locally (see below); self-skips cleanly with an explanatory message if they aren't. |
| Live remote smoke (read-only) | `Servyx.Remote.Tests` | Seconds | Real Docker on a real remote host | A strictly read-only smoke suite against the actual, live, production Palworld host over SSH — not a test double or a container anywhere. Excluded from `Servyx.sln` and from CI; quadruple-gated behind explicit environment variables. See "Testing the ssh+docker transport" below for the gating and the read-only guarantee. |

If a behavior can be proven with a plain `[Fact]`, prove it there — don't
reach for BDD narrative or a real browser just because they exist. Reach for
BDD when the *point* is a product guarantee worth naming and reading back as
a sentence. Reach for bUnit when the point is "does this component render
correctly." Reach for Playwright only when the point genuinely requires a
real browser exercising several components together.

## Testing the `ssh+docker` transport: four layers

The `ssh+docker` transport (`src/Infrastructure/Servyx.Infrastructure.Ssh/Docker/`) — which manages
a Docker container on a remote host by running the `docker` CLI over an SSH exec channel — is
tested at four progressively more realistic layers. Each catches a class of bug the layer below it
structurally cannot.

| Layer | Where | Real SSH? | Real Docker? | Real production host? |
|---|---|---|---|---|
| 1. Unit | `Servyx.Infrastructure.Ssh.Tests` (non-`Integration`) | No | No | No |
| 2. Hermetic container integration | `Servyx.Infrastructure.Ssh.Tests/Integration/SshDockerIntegrationTests.cs` | Yes | Stubbed | No |
| 3. bUnit / composition-root | `Servyx.Web.Tests` | No | No | No |
| 4. Live read-only smoke | `Servyx.Remote.Tests` | Yes | Yes | **Yes** |

**Layer 1** covers `DockerCli`'s `CommandSpec` construction (argv shape, declared
`CommandIntent`), `DockerInspectJson` parsing edge cases, and `SshDockerWiringOptions`'
configuration-reading rules — all against in-memory fixtures or substituted collaborators, exactly
like every other unit tier in the pyramid above.

**Layer 2** is `SshDockerIntegrationTests`, tagged `[Trait("Category", "Integration")]` alongside
the rest of `Servyx.Infrastructure.Ssh.Tests`' container-backed tests (same opt-in gating — see
"Hermeticity policy" above; a bare `dotnet test` runs zero of these). It starts a throwaway
`linuxserver/openssh-server` Testcontainer and plants a **stub** `/usr/local/bin/docker` shell
script that dispatches on argv and echoes canned fixture JSON captured from a real production
Palworld container (secrets scrubbed, public IP rewritten to `203.0.113.10`), then drives the real
transport, discovery, log stream, and metrics source against it exactly as production code would.

*Why a planted stub instead of mocking `IExecutionTarget` or `ITransport`:* a mocked target proves
nothing about the actual wire path. What this transport depends on for correctness is that
`docker ... --format {{json .}}` — including the literal `{{` `}}` template syntax and any
argument containing spaces or special shell characters — survives `PosixArgv`'s quoting, gets
carried faithfully over a *real* SSH exec channel, and comes back out the other side as bytes
`DockerInspectJson` can parse. None of that is exercised by a mock; a mock's `docker` command never
gets quoted, never gets shipped over a socket, and never gets echoed back by anything. The stub is
the cheapest fixture that is still honest about the thing that actually breaks in practice: quoting
across a real transport boundary. Verified live details worth knowing if you touch this fixture:
a non-executable stub (`CopyAsync`'s default file mode) yields exit 126, a missing stub yields exit
127 — both match `SshDockerTransport.ProbeAsync`'s branches exactly, and both were confirmed against
a live container before being encoded as test expectations rather than assumed.

**Layer 3** is ordinary bUnit/composition-root coverage in `Servyx.Web.Tests` — e.g. the
`AddServyxSshDocker` registration shape (write-guarded `ITransport`, `LazyConnectingExecutionTarget`
deferring the actual connect) — nothing here opens a socket.

**Layer 4**, `Servyx.Remote.Tests`, is the only test project in this repository that talks to a
real, live, production game server, and it is isolated accordingly:

- **Not in `Servyx.sln`** — `dotnet build Servyx.sln` never builds it, and no IDE "run all tests"
  reaches it.
- **Not in `.github/workflows/ci.yml`**'s run list, for the same reason.
- The project's own `VSTestTestCaseFilter` is `Category!=Integration` by default (same mechanism as
  the hermeticity fix above), and every test additionally carries `[Trait("Category",
  "Integration")]` — so even `dotnet test tests/Servyx.Remote.Tests` with no filter runs zero tests.
- Every test is `[SkippableFact]` and calls `Skip.IfNot` against
  `RemoteTestEnvironment.MissingReason`, which is non-null (and the whole suite skips) unless
  **all** of the following environment variables are present and valid:

  | Variable | Meaning |
  |---|---|
  | `SERVYX_REMOTE_E2E` | Must be exactly `"1"`. The master switch. |
  | `SERVYX_REMOTE_ENDPOINT` | `[ssh:][user@]host[:port]` |
  | `SERVYX_REMOTE_KEY_PATH` | A **Windows-readable** path to the private key |
  | `SERVYX_REMOTE_CONTAINER` | The container name to observe |
  | `SERVYX_REMOTE_FINGERPRINT` | The pinned `SHA256:...` host-key fingerprint(s), comma-separated |

  No production coordinate — endpoint, username, key path, container name, or fingerprint — appears
  anywhere in `Servyx.Remote.Tests`' own source; they exist only in the operator's environment for
  the duration of one run. A missing or blank variable produces a skip reason naming exactly which
  variable is absent, never a connection failure and never a guessed default.

Run it explicitly:

```bash
dotnet test tests\Servyx.Remote.Tests --filter "Category=Integration"
```

**This is four independent gates that must all be satisfied at once** (project exclusion from the
`.sln`, exclusion from CI, the project-level test-case filter, and the `[SkippableFact]` env-var
check) — the same "opt-in must be explicit, not accidental" posture the hermeticity policy above
applies to the container-backed SSH tests, deliberately raised to an even higher bar here because
the blast radius of an accidental run is a real production host, not a throwaway container.

**The read-only guarantee, verified rather than assumed.** Every session
`Servyx.Remote.Tests` opens is wrapped in `WriteGuardedTransport` with its default
`ReadOnlyWriteModeResolver` — every target is `WriteMode.ReadOnly` — with a recording decorator
placed *inside* that guard, so what it records is exactly what production saw and nothing more. The
suite:

- Issues only the read-only docker verbs (`version`, `container ls`, `container inspect`, `logs`,
  `stats`), asserted positively by an end-of-suite audit (`Every_command_this_suite_issues_is_read_only`)
  that fails if any recorded `CommandSpec` is not `CommandIntent.ReadOnly` or uses any verb outside
  that allow-list.
- Builds one real mutating `CommandSpec` — `DockerCli.Stop` — purely to prove it is refused:
  `Stopping_the_container_is_refused_before_any_io` asserts the call throws
  `WritesDisabledException`, asserts the recorder (which sits *inside* the guard) never saw the
  `stop` spec at all — proving the throw happens before the inner target is touched, not merely
  before it succeeds — and then re-inspects the container to assert `State == "running"`, i.e. the
  refusal cost production nothing.

## Hermeticity policy

**The default run — `dotnet test Servyx.sln`, with no extra flags — is
hermetic: no Docker daemon is touched, no container is started, and nothing
reaches beyond the test process's own temp directory or, for the one
exception noted above, `127.0.0.1`.** Any tier that needs a real external
resource (today: the SSH/SFTP container tests, plus
`Servyx.Infrastructure.Docker.Tests`' own Docker-backed pre-start-seeding
integration tests) is opt-in and must be invoked with an explicit command,
documented below.

This used to not be true. The `Servyx.Infrastructure.Ssh.Tests` integration
tests were gated only by `[SkippableFact]` plus a runtime Docker-availability
probe inside `IAsyncLifetime.InitializeAsync` — which skips the *assertions*
if Docker turns out to be unavailable, but unconditionally *attempts to
start* a real `linuxserver/openssh-server` container first, for every one of
the ten integration tests, on every `dotnet test Servyx.sln` run. On a
machine with a running Docker daemon (most developer machines, and this
one), that meant a supposedly-fast default test run silently spent ~33
seconds spinning up and tearing down containers. That was a bug: hermeticity
should be policy, not an accident of whether Docker happens to be running.

The fix: `tests/Infrastructure/Servyx.Infrastructure.Ssh.Tests/Servyx.Infrastructure.Ssh.Tests.csproj`
sets the MSBuild property `VSTestTestCaseFilter` to `Category!=Integration`
by default (only when the caller hasn't already supplied their own
`VSTestTestCaseFilter`/`--filter`, so an explicit filter always wins).
`tests/Infrastructure/Servyx.Infrastructure.Docker.Tests/Servyx.Infrastructure.Docker.Tests.csproj`
sets the identical property for the same reason, for its own Docker-backed
`PreStartSeedingIntegrationTests` (exercising `deployments[].files[]` seeding
against a real container) — the same opt-in mechanism, not a second one. This
is what `dotnet test` uses internally to populate vstest's
`/TestCaseFilter:` — setting it as a project default means the exclusion
applies to a bare `dotnet test Servyx.sln` with **no special flags,
settings file, or environment variable required**, while `dotnet test
--filter "Category=Integration"` (or any other explicit filter) still
overrides it and runs the excluded tests. Because the excluded tests are
filtered out of the run at discovery time, xUnit never constructs a
`SshIntegrationTests` instance for them at all, so `InitializeAsync` never
runs and no container is ever started — verified empirically (see below),
not assumed.

A `.runsettings`-based `TestCaseFilter` was considered instead (as the docs
previously implied should be checked) but there is no standard `.runsettings`
node that vstest applies as a *default* filter without the caller passing
`--settings` explicitly, which reintroduces the same "only works if you
remember the extra flag" problem this fix is meant to eliminate. The
project-level `VSTestTestCaseFilter` MSBuild property avoids that: it's part
of the project, not something the caller must remember to pass.

The existing Docker-availability probe in `SshIntegrationTests.InitializeAsync`
is kept as a second, independent layer: when the integration tests **are**
explicitly opted into (via `--filter "Category=Integration"`) but Docker
turns out to be unavailable, each `[SkippableFact]` still skips cleanly with
an explanatory message instead of failing.

### Container-backed integration tests: the explicit command

```bash
# Runs only the ten Docker-backed SSH/SFTP integration tests. Requires a running
# Docker daemon; if Docker is unavailable, they skip cleanly rather than fail.
dotnet test Servyx.sln --filter "Category=Integration"

# Equivalent, scoped to just the project:
dotnet test tests/Infrastructure/Servyx.Infrastructure.Ssh.Tests --filter "Category=Integration"
```

Verified empirically on a machine with Docker running: a bare `dotnet test
Servyx.sln` completed in ~2 seconds with zero containers created (`docker ps
-a` identical before and after); the explicit `--filter "Category=Integration"`
command above took ~33 seconds, ran all 10 tests for real (not skipped), and
every container it created (including the Testcontainers Ryuk reaper) was
gone again within seconds of the run finishing.

## TinyBDD, as verified against the installed package (`TinyBDD`/`TinyBDD.Xunit` 0.19.30)

```csharp
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace Servyx.Bdd.Tests;

[Feature("Read-only safety", "As an operator I trust that Servyx never mutates a workload it manages")]
public class ReadOnlySafetyTests(ITestOutputHelper output) : TinyBddXunitBase(output)
{
    [Scenario("A config-file write is refused before any I/O occurs", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task ConfigWrite_IsRefused_BeforeAnyIOOccurs()
        => await Given("a Docker execution target for an adopted server", () => CreateTarget())
            .When("a write to a config file is attempted", async Task<Exception?> (target) => { /* ... */ return null; })
            .Then("it is refused with WritesDisabledException", ex => Task.FromResult(ex is WritesDisabledException))
            .AssertPassed();
}
```

- `using TinyBDD;` and `using TinyBDD.Xunit;` are required explicitly — they are not implied usings.
- `[Feature(name, narrative)]` on the class; inherit `TinyBddXunitBase(ITestOutputHelper)`; its constructor is `protected`.
- `[Scenario(name, params string[] tags)]` **plus** `[Fact]` (or `[Theory]` + a data source, for outline-style scenarios) on the method. Every scenario in this repo passes `"unit"` as its tag by convention, but **verified empirically, that tag does not surface as a filterable xUnit trait** — `dotnet test --filter "Category=unit"` (and `Tag=unit`, `Tags=unit`) all match zero tests against the installed `TinyBDD.Xunit` 0.19.30. Run `Servyx.Bdd.Tests` by project path instead (see "Filter commands" below); don't rely on a tag-based filter for it.
- `Given`/`When`/`Then`/`And`/`But` are instance methods inherited from `TestBase`, not static helpers — no `ScenarioContext` plumbing needed.
- Terminal `.AssertPassed()` runs the chain and asserts every step passed.
- **`[DisableOptimization]` is required on every scenario method.** TinyBDD ships a Roslyn source generator that "optimizes" `[Scenario]` methods; without either marking the containing class `partial` or adding `[DisableOptimization]` to the method, the build fails outright with error `TBDD010`. Marking the class `partial` was tried and rejected: the generator's optimized codegen was buggy for anything beyond a trivial one-line lambda body (multi-statement `When`/`Then` lambdas, `try`/`catch`, local variables all produced invalid generated C#). `[DisableOptimization]` on the method sidesteps the generator entirely and is what every scenario in `Servyx.Bdd.Tests` uses.
- Async lambdas passed to `.When(...)`/`.Then(...)` without an explicit return-type annotation can hit `CS0121` (ambiguous between the `Task<TOut>` and `ValueTask<TOut>` overloads) — annotate explicitly, e.g. `async Task<Exception?> (target) => { ... }`.
- `TinyBDD.ScenarioOutlineBuilder`/`ScenarioCaseAttribute` exist but require an explicit `ScenarioContext` and don't integrate with xUnit's `[Theory]` data-driven discovery — outline-style scenarios in this repo use plain `[Theory]` + `[MemberData]`/`[InlineData]` alongside `[Scenario]` instead, which works cleanly.

## Filter commands

All of these were run and their output checked as part of writing this
section — none are assumed.

```bash
# Everything except the opt-in container-backed integration tests (what CI /
# `dotnet build` + `dotnet test` on the whole solution runs by default).
# Hermetic: no Docker daemon touched, no container started. ~2s on this machine
# (was ~35s before the SSH integration tests were made opt-in).
dotnet test Servyx.sln

# Unit tests only, by project (excludes the BDD project and E2E)
dotnet test tests/Core/Servyx.Domain.Tests
dotnet test tests/Core/Servyx.Application.Tests
dotnet test tests/Infrastructure/Servyx.Infrastructure.Tests
dotnet test tests/Infrastructure/Servyx.Infrastructure.Docker.Tests
dotnet test tests/Infrastructure/Servyx.Config.Tests
dotnet test tests/Presentation/Servyx.Web.Tests

# BDD scenarios only, by project (the scenario tags are NOT usable as an xUnit
# filter — see the TinyBDD notes above; `--filter "Category=unit"` matches
# nothing, verified)
dotnet test tests/Servyx.Bdd.Tests

# Container-backed integration tests only — opt-in, requires Docker (skips
# cleanly, doesn't fail, if Docker is unavailable). See "Hermeticity policy"
# above for why this is off by default and how the opt-in works. The
# solution-scoped form below runs both opt-in suites (SSH/SFTP and the
# Docker pre-start-seeding tests); the two project-scoped forms run just one.
dotnet test Servyx.sln --filter "Category=Integration"
dotnet test tests/Infrastructure/Servyx.Infrastructure.Ssh.Tests --filter "Category=Integration"
dotnet test tests/Infrastructure/Servyx.Infrastructure.Docker.Tests --filter "Category=Integration"

# E2E only — NOT part of Servyx.sln at all (a solution-scoped filter such as
# `dotnet test Servyx.sln --filter "Category=e2e"` finds zero tests, since the
# project isn't referenced by the .sln; verified). Must be run by project path:
dotnet test tests/Servyx.E2E.Tests

# Live remote smoke tests — NOT part of Servyx.sln, NOT part of CI, and a bare
# `dotnet test tests/Servyx.Remote.Tests` (even with no filter) still runs ZERO
# tests: this project's own VSTestTestCaseFilter default is "Category!=Integration".
# Requires SERVYX_REMOTE_E2E=1 plus every SERVYX_REMOTE_* variable (see "Testing
# the ssh+docker transport" above) — every test skips cleanly, never fails, if any
# are absent. Talks to a REAL production game server: never run this without
# understanding what it does.
dotnet test tests/Servyx.Remote.Tests --filter "Category=Integration"
```

## E2E: one-time setup

`Servyx.E2E.Tests` is **not** part of the default `dotnet test Servyx.sln` run
and must be invoked explicitly (`dotnet test tests/Servyx.E2E.Tests`), because
it depends on a heavyweight, separately-installed prerequisite: Playwright's
browser binaries. The suite is designed to **stay green regardless**:

1. Build the project once: `dotnet build tests/Servyx.E2E.Tests`.
2. Install Chromium: `pwsh tests/Servyx.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`
   (on Linux, add `--with-deps`).
3. Run `dotnet test tests/Servyx.E2E.Tests`.

If step 2 wasn't run, or fails (no network, restricted environment, etc.),
every E2E scenario detects that Chromium never launched and reports a genuine
xUnit **Skip** (via `[SkippableFact]` + `Skip.IfNot`), with an explanatory
message, instead of failing or silently passing — see
`PlaywrightFixture`/`E2ETestBase.SkipIfBrowsersUnavailable` in
`tests/Servyx.E2E.Tests`. Check the test output to see whether each scenario
ran for real, was skipped (missing browsers — an environment problem), or
failed (a real product defect).

### How the app is hosted for E2E

`Servyx.E2E.Tests` launches the real, unmodified Servyx.Web app as a
**subprocess** (`ServyxAppProcess`) on a dynamically chosen loopback port,
with `Servyx:DataSource=Mock` set via an environment variable so the suite
needs no Docker daemon and always sees the same seeded server
(`Palygondwanaland`). This was chosen over subclassing
`WebApplicationFactory<TEntryPoint>` (the other option this milestone
considered): `WebApplicationFactory` hosts on an in-memory `TestServer` with
no real socket, which cannot carry Blazor Server's SignalR circuit at all —
a real browser process cannot attach to it. Overriding
`WebApplicationFactory.CreateHost` to layer in a real Kestrel server was
tried first and rejected: this version's `WebApplicationFactory` registers
its in-memory `TestServer` as `IServer` in a way that kept winning
dependency-injection resolution even after explicitly removing and
re-registering it from the overridden hooks — fighting framework internals
rather than testing the app. A subprocess sidesteps this entirely: it is
simply the real app, started exactly the way an operator would start it.

Waits use Playwright's auto-waiting `Locator`/`Expect` assertions against
elements that only exist after render — never a fixed `Task.Delay`, and never
`WaitUntil.NetworkIdle` (Blazor Server's persistent WebSocket keeps the
network permanently "busy," making `NetworkIdle` never fire reliably).

### Interactive render mode

Two scenarios (`SettingsTab_...`, `BackupsTab_...`) need to click a detail-page
tab button to switch panels, which requires a live SignalR circuit. `App.razor`
applies `@rendermode InteractiveServer` to both `<HeadOutlet>` and `<Routes>`,
and `Program.cs` maps `.AddInteractiveServerRenderMode()` accordingly, so
`@onclick` handlers across the app — including these tab switchers — are wired
up server-side once the circuit connects. If a tab ever fails to become
selected after several retries, that is treated as a genuine interactivity
regression: the scenario fails loudly (`Assert.Fail`, not a skip or a silent
pass) so a real break in server-side interactivity is never mistaken for an
environment issue.
