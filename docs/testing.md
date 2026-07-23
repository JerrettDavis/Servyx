# Testing Servyx

This document explains the test pyramid, what belongs where, and exactly how
to run each layer. All commands assume the repository root as the working
directory.

## The pyramid

| Layer | Project(s) | Speed | Needs Docker? | What belongs here |
|---|---|---|---|---|
| Plain unit tests | `Servyx.Domain.Tests`, `Servyx.Application.Tests`, `Servyx.Infrastructure.Tests`, `Servyx.Infrastructure.Docker.Tests` | Milliseconds | No | Pure logic and algorithms: value-object behavior, a single method's edge cases, calculations (e.g. `DockerCpuPercentCalculator`), parsing. `[Fact]`/`[Theory]` + FluentAssertions `.Should()`. No behavioral narrative needed — these test *how*, not *why*. |
| BDD scenarios | `Servyx.Bdd.Tests` | Milliseconds | No | **Product guarantees** expressed as `Given/When/Then` behavior, grouped by `[Feature]`: read-only safety, path sandboxing, container adoption, configuration drift, secret protection, graceful degradation, observability correctness. Fast and NSubstitute-backed like unit tests, but the point is the *readable scenario*, not the assertion mechanics — this is where you'd point a new contributor to understand what Servyx promises and why. Uses TinyBDD (see below). |
| bUnit component tests | `Servyx.Web.Tests` | Milliseconds | No | Blazor component rendering in isolation: does this component render the right markup for these inputs, does a masked secret ever reach rendered HTML, is a gated control actually disabled. Fast because there's no real browser or network — but cannot exercise real user interaction (clicks that require a live SignalR circuit), real navigation, or anything CSS/layout-dependent. |
| E2E (Playwright) | `Servyx.E2E.Tests` | Seconds | No (runs against `Servyx:DataSource=Mock`) | Whole-page, real-browser flows: does the sidebar actually render, does clicking a real nav link actually navigate, does the whole page assembled from many components hang together. The layer of last resort — slow and heavier to maintain, so reserved for flows that specifically need a real browser and a real socket. Requires Playwright's browser binaries to be installed locally (see below); self-skips cleanly with an explanatory message if they aren't. |

If a behavior can be proven with a plain `[Fact]`, prove it there — don't
reach for BDD narrative or a real browser just because they exist. Reach for
BDD when the *point* is a product guarantee worth naming and reading back as
a sentence. Reach for bUnit when the point is "does this component render
correctly." Reach for Playwright only when the point genuinely requires a
real browser exercising several components together.

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
- `[Scenario(name, params string[] tags)]` **plus** `[Fact]` (or `[Theory]` + a data source, for outline-style scenarios) on the method. Tags flow into xUnit traits and are filterable via `dotnet test --filter`.
- `Given`/`When`/`Then`/`And`/`But` are instance methods inherited from `TestBase`, not static helpers — no `ScenarioContext` plumbing needed.
- Terminal `.AssertPassed()` runs the chain and asserts every step passed.
- **`[DisableOptimization]` is required on every scenario method.** TinyBDD ships a Roslyn source generator that "optimizes" `[Scenario]` methods; without either marking the containing class `partial` or adding `[DisableOptimization]` to the method, the build fails outright with error `TBDD010`. Marking the class `partial` was tried and rejected: the generator's optimized codegen was buggy for anything beyond a trivial one-line lambda body (multi-statement `When`/`Then` lambdas, `try`/`catch`, local variables all produced invalid generated C#). `[DisableOptimization]` on the method sidesteps the generator entirely and is what every scenario in `Servyx.Bdd.Tests` uses.
- Async lambdas passed to `.When(...)`/`.Then(...)` without an explicit return-type annotation can hit `CS0121` (ambiguous between the `Task<TOut>` and `ValueTask<TOut>` overloads) — annotate explicitly, e.g. `async Task<Exception?> (target) => { ... }`.
- `TinyBDD.ScenarioOutlineBuilder`/`ScenarioCaseAttribute` exist but require an explicit `ScenarioContext` and don't integrate with xUnit's `[Theory]` data-driven discovery — outline-style scenarios in this repo use plain `[Theory]` + `[MemberData]`/`[InlineData]` alongside `[Scenario]` instead, which works cleanly.

## Filter commands

```bash
# Everything (what CI / `dotnet build` + `dotnet test` on the whole solution runs by default)
dotnet test Servyx.sln

# Unit tests only (excludes the BDD project and E2E)
dotnet test tests/Core/Servyx.Domain.Tests
dotnet test tests/Core/Servyx.Application.Tests
dotnet test tests/Infrastructure/Servyx.Infrastructure.Tests
dotnet test tests/Infrastructure/Servyx.Infrastructure.Docker.Tests
dotnet test tests/Presentation/Servyx.Web.Tests

# BDD scenarios only, by project or by tag (every scenario in Servyx.Bdd.Tests is tagged "unit")
dotnet test tests/Servyx.Bdd.Tests
dotnet test Servyx.sln --filter "Category=unit"

# E2E only (NOT included in `dotnet test Servyx.sln` — see below)
dotnet test tests/Servyx.E2E.Tests
dotnet test Servyx.sln --filter "Category=e2e"
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
