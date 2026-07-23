# Testing Servyx

This document explains the test pyramid, what belongs where, and exactly how
to run each layer. All commands assume the repository root as the working
directory.

## The pyramid

| Layer | Project(s) | Speed | Needs Docker? | What belongs here |
|---|---|---|---|---|
| Plain unit tests | `Servyx.Domain.Tests`, `Servyx.Application.Tests`, `Servyx.Infrastructure.Tests`, `Servyx.Infrastructure.Docker.Tests`, `Servyx.Config.Tests`, and the non-`Integration` tests in `Servyx.Infrastructure.Ssh.Tests` | Milliseconds | No | Pure logic and algorithms: value-object behavior, a single method's edge cases, calculations (e.g. `DockerCpuPercentCalculator`), parsing. `[Fact]`/`[Theory]` + FluentAssertions `.Should()`. No behavioral narrative needed — these test *how*, not *why*. `Servyx.Infrastructure.Docker.Tests` talks to a substituted `IDockerClient`/`IDockerEnvironment`, never a real daemon. `Servyx.Infrastructure.Tests`' secret-store and host-key-store tests write only under a per-test, GUID-named directory beneath `Path.GetTempPath()` — never outside it (note: those temp files/directories are not deleted afterwards, so they do accumulate in the OS temp folder over many runs; this is a hygiene nit, not a hermeticity violation). |
| BDD scenarios | `Servyx.Bdd.Tests` | Milliseconds | No | **Product guarantees** expressed as `Given/When/Then` behavior, grouped by `[Feature]`: read-only safety, path sandboxing, container adoption, configuration drift, secret protection, graceful degradation, observability correctness. Fast and NSubstitute-backed like unit tests, but the point is the *readable scenario*, not the assertion mechanics — this is where you'd point a new contributor to understand what Servyx promises and why. Uses TinyBDD (see below). |
| bUnit component tests | `Servyx.Web.Tests` | Milliseconds | No | Blazor component rendering in isolation: does this component render the right markup for these inputs, does a masked secret ever reach rendered HTML, is a gated control actually disabled. Fast because there's no real browser for almost all of these — but cannot exercise real user interaction (clicks that require a live SignalR circuit), real navigation, or anything CSS/layout-dependent. **One exception**: `Integration/InteractiveRenderModeTests.cs` in this project is not a bUnit test — it launches the real `Servyx.Web` app as a subprocess and issues a real loopback HTTP request to it (no Docker, no external network, just `127.0.0.1`), as a regression guard for a real outage bUnit structurally cannot detect (see the doc comment on that class). It runs by default; it is fast (well under a second) and does not warrant opt-in gating, but it is the one test in the default run that is not purely in-process. |
| Container-backed integration tests | `Servyx.Infrastructure.Ssh.Tests` (the `[Trait("Category", "Integration")]` tests under `Integration/`) | Tens of seconds | **Yes** — starts a real `linuxserver/openssh-server` container per test via Testcontainers | Real, non-mocked round trips against an actual SSH/SFTP server: password and key auth, host-key TOFU pinning and rejection, atomic file writes, exec argument quoting, the exec-only shell-fallback file channel. These need a genuine SSH server on the other end and are deliberately excluded from the default run — see "Container-backed integration tests" below. |
| E2E (Playwright) | `Servyx.E2E.Tests` | Seconds | No (runs against `Servyx:DataSource=Mock`) | Whole-page, real-browser flows: does the sidebar actually render, does clicking a real nav link actually navigate, does the whole page assembled from many components hang together. The layer of last resort — slow and heavier to maintain, so reserved for flows that specifically need a real browser and a real socket. Requires Playwright's browser binaries to be installed locally (see below); self-skips cleanly with an explanatory message if they aren't. |

If a behavior can be proven with a plain `[Fact]`, prove it there — don't
reach for BDD narrative or a real browser just because they exist. Reach for
BDD when the *point* is a product guarantee worth naming and reading back as
a sentence. Reach for bUnit when the point is "does this component render
correctly." Reach for Playwright only when the point genuinely requires a
real browser exercising several components together.

## Hermeticity policy

**The default run — `dotnet test Servyx.sln`, with no extra flags — is
hermetic: no Docker daemon is touched, no container is started, and nothing
reaches beyond the test process's own temp directory or, for the one
exception noted above, `127.0.0.1`.** Any tier that needs a real external
resource (today: the SSH/SFTP container tests) is opt-in and must be invoked
with an explicit command, documented below.

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
`VSTestTestCaseFilter`/`--filter`, so an explicit filter always wins). This
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

# Container-backed SSH/SFTP integration tests only — opt-in, requires Docker
# (skips cleanly, doesn't fail, if Docker is unavailable). See "Hermeticity
# policy" above for why this is off by default and how the opt-in works.
dotnet test Servyx.sln --filter "Category=Integration"
dotnet test tests/Infrastructure/Servyx.Infrastructure.Ssh.Tests --filter "Category=Integration"

# E2E only — NOT part of Servyx.sln at all (a solution-scoped filter such as
# `dotnet test Servyx.sln --filter "Category=e2e"` finds zero tests, since the
# project isn't referenced by the .sln; verified). Must be run by project path:
dotnet test tests/Servyx.E2E.Tests
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
every E2E scenario detects that Chromium never launched and **skips itself
cleanly** with an explanatory message instead of failing — see
`PlaywrightFixture`/`E2ETestBase.SkipIfBrowsersUnavailable` in
`tests/Servyx.E2E.Tests`. The suite reports all scenarios as passed either
way; check the test output for whether they ran for real or skipped.

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

### A real product gap this E2E suite surfaced

Two scenarios (`SettingsTab_...`, `BackupsTab_...`) need to click a detail-page
tab button to switch panels. As of this writing, **no component in
Servyx.Web's render tree currently applies `@rendermode InteractiveServer`**
— `Program.cs` calls `.AddInteractiveServerRenderMode()`, which only makes
the render mode *available*, but nothing opts into it. The whole app
therefore renders as static SSR only, and every `@onclick` handler anywhere
(not just these tabs) is currently inert in a real browser. Both scenarios
detect this (the tab never becomes selected after several retries) and skip
themselves with a message pointing here, rather than failing or — worse —
silently passing on a page that never actually switched tabs. This is a real
finding about the running app, not a test-authoring bug; fixing it (adding
`@rendermode InteractiveServer` somewhere in the render tree, e.g. on
`<Routes>` in `App.razor`) is out of scope for the BDD/E2E harness work that
produced this document.
