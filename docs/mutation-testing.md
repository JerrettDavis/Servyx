# Mutation Testing

This document explains why mutation testing is mandatory for the three
projects that fund every configuration mutation Servyx makes against a live
server, how the tooling is configured, and exactly how to run it. All
commands assume the repository root as the working directory unless stated
otherwise.

## Why this is mandatory here

Line and branch coverage answer "was this code executed by a test." They do
not answer "would this test actually fail if the code were wrong." Mutation
testing does: Stryker rewrites a single operator, condition, or literal in
the compiled code (a "mutant") and reruns the test suite against it. A
mutant that survives — the suite stays green despite the code now doing
something different — is a green checkmark lying to you.

This repository has caught real silent coverage gaps this way that normal
review missed: a self-contradicting ledger row that satisfied every existing
assertion, and an unpinned refusal-precedence bug where two competing
refusal reasons could both be "correct" for a given input and nothing forced
a test to pin which one Servyx actually returns. Both were 100%-covered by
line coverage. Neither would have been caught without mutating the code and
watching a test suite fail to notice.

For that reason, mutation testing is mandatory — not advisory — for the
three projects on the direct path from an operator's intent to a write
against a live server:

- `src/Infrastructure/Servyx.Config` — `PlanExecutor`, the funnel every
  config mutation passes through before it reaches a live server.
- `src/Infrastructure/Servyx.Infrastructure.Persistence` — the change-plan
  store and the retention/purge predicates that decide what history is kept
  or discarded.
- `src/Core/Servyx.Application` — the application-layer orchestration that
  sits above both.

## The isolated-clone rule — and exactly why

**Never invoke `dotnet stryker` directly against `C:\git\Servyx`.**

Stryker mutates source files *in place* during a run: it rewrites a file,
builds, runs the suite against the mutant, restores the file, and moves to
the next mutant. For the duration of a run, the working tree does not
contain the code you think it contains. If anyone else — a teammate, a
concurrent agent, your own editor's background build — reads, builds, or
commits from that tree while a run is in flight, they are working against
mutated source, not real source. A full run over one project alone takes
minutes; over all three it is easily an hour or more. That is an
unacceptably long window to leave the shared tree unstable.

The fix is mechanical: clone, run, delete.

```bash
git clone --local C:\git\Servyx isolated-mutant-test
cd isolated-mutant-test
dotnet tool restore
dotnet stryker -f mutation\servyx-config.json
```

When the run finishes, copy whatever report you need out of
`isolated-mutant-test\StrykerOutput`, then delete the clone:

```bash
cd ..
Remove-Item -Recurse -Force isolated-mutant-test
```

`git clone --local` is fast (a hardlinked local clone, not a full network
clone) and gives Stryker a tree nobody else is touching. `dotnet tool
restore` and `dotnet stryker --help` do **not** rewrite source and are safe
to run directly in `C:\git\Servyx` — only invoking Stryker with a config
(`dotnet stryker -f <config>`) starts an actual run that mutates files in
place. Note there is no dry-run or config-validate-only flag in this
version (`4.16.0`) — `--help` was checked directly to confirm this; the
closest thing to a safe check is `dotnet stryker -f <config>
--mutation-level Basic` against a small target inside an isolated clone
first, before committing to a full run.

## The 5% survival gate

Every config under `mutation/` sets:

```json
"thresholds": { "high": 95, "low": 95, "break": 95 }
```

Stryker enforces `high >= low >= break`; the mandatory gate here is
`break`, so `high` and `low` are pinned to the same value rather than left
at Stryker's defaults (which would carve out a "good but not great" warning
band that doesn't exist as a concept for these three projects). A run whose
mutation score — the percentage of mutants killed — comes in below 95%
exits non-zero. Below 95% killed means more than 5% of mutants survived: the
mandatory gate. This was verified directly, not assumed: a real smoke run
against `Servyx.Config` at `--mutation-level Basic` scored 60.98% and
Stryker exited with code `2` and the message `Final mutation score is below
threshold break. Crashing...` — confirming the gate actually fails the run
rather than merely warning.

## Config shape: one file per target project

Stryker.NET mutates exactly one project under test per config file (1..N
test projects may cover it, but not the reverse — a single config cannot
span multiple *different* target projects with their own test-project
mappings). Because the three mandatory targets each have a distinct
test-project mapping, a single root config could not express this
correctly, so configuration lives as three separate files under
`mutation/`:

| Config | Target project | Test project(s) |
|---|---|---|
| `mutation/servyx-config.json` | `Servyx.Config` | `Servyx.Config.Tests` |
| `mutation/servyx-infrastructure-persistence.json` | `Servyx.Infrastructure.Persistence` | `Servyx.Infrastructure.Persistence.Tests`, `Servyx.Config.Tests` (which also exercises persistence through `PlanExecutor`) |
| `mutation/servyx-application.json` | `Servyx.Application` | `Servyx.Application.Tests`, `Servyx.Bdd.Tests` (which project-references `Servyx.Application` directly) |

Each config's `mutate` glob is written relative to the target project's own
directory (`**/*.cs`, not a repo-rooted path) — Stryker resolves the glob
against the project it was told to mutate via `project`, not against the
current working directory. This was also verified directly: a repo-rooted
glob (`src/Infrastructure/Servyx.Config/**/*.cs`) silently matched zero
files and every mutant was filtered out; a project-relative glob (`**/*.cs`)
matched correctly. Each config excludes `**/obj/**`, `**/bin/**`, and
`**/*.feature.cs` (Reqnroll-generated code — none currently lives under
these three projects, but the exclude is defensive); the persistence config
additionally excludes `**/Migrations/**`, since EF Core migrations are
generated code, not hand-written logic worth mutating.

The tool version is pinned in `.config/dotnet-tools.json` as a local
`dotnet-stryker` tool (version `4.16.0`, confirmed installable via `dotnet
tool restore` against the live NuGet feed at the time this was written).

## Running it

All commands below assume you're inside an isolated clone (see above), with
`dotnet tool restore` already run.

Single target — pick the one config you need:

```bash
dotnet stryker -f mutation\servyx-config.json
dotnet stryker -f mutation\servyx-infrastructure-persistence.json
dotnet stryker -f mutation\servyx-application.json
```

All three targets, sequentially, in the same isolated clone:

```bash
dotnet stryker -f mutation\servyx-config.json
dotnet stryker -f mutation\servyx-infrastructure-persistence.json
dotnet stryker -f mutation\servyx-application.json
```

There is no solution-wide "run everything in one command" mode for this
config shape — each `dotnet stryker` invocation is scoped to one project by
design (see "Config shape" above). Expect the full set to take on the order
of an hour or more; this is exactly why it happens in an isolated clone
rather than blocking the shared tree.

## Reading the report

Each run writes reports under `StrykerOutput` inside whatever directory you
ran it from — in the isolated clone, that's
`isolated-mutant-test\StrykerOutput\reports\mutation-report.html` (verified
directly: this Stryker version does not nest reports under a per-run
timestamp folder). Open the HTML report in a browser. It shows, per file,
every mutant Stryker generated with its status:

- **Killed** — a test failed when this mutant was active. Good; no action.
- **Survived** — every test still passed. This is the signal that matters:
  either no test exercises this code path meaningfully, or an existing test
  asserts too loosely to notice the behavior changed.
- **NoCoverage** — no test executed this code at all.
- **Timeout** — the mutant caused an infinite loop or hang; usually fine,
  but worth a glance if it's unexpected.
- **CompileError** — the mutation didn't produce valid code; not a real
  finding, Stryker discards these automatically.

## Triaging a surviving mutant

For every **Survived** or **NoCoverage** mutant:

1. Read what the mutant actually changed (the report shows the diff inline
   — a flipped comparison operator, a boundary shifted by one, a negated
   condition, a swapped literal).
2. Decide whether that change represents a real behavior a test should
   pin down.
   - **If yes: write or strengthen a test that fails against the mutant.**
     This is almost always the right outcome — a survived mutant on
     `PlanExecutor`, the change-plan store, or application orchestration
     usually means a genuine gap in what's proven, not a false positive.
   - **If no** (the mutant is genuinely equivalent — it changes code that
     provably cannot affect observable behavior, e.g. a mutated branch that
     is unreachable, or two literals that are behaviorally identical for
     every caller): justify the exclusion in writing. Add the exclusion to
     the relevant `mutation/*.json` file's `ignore-mutations` or a targeted
     `mutate` glob exclusion. JSON has no comment syntax, so record the
     rationale in the PR description or a changelog entry instead, naming
     the file and mutant kind excluded. Do not silently exclude — the next
     person to read the config needs to know why a hole is there on
     purpose.

**Never lower a threshold to make a run pass.** The `break: 95` gate is the
point of this whole setup; adjusting it to match whatever score a run
happened to produce defeats it entirely. If a run is genuinely below 95% and
you can't close the gap in the time you have, that's a finding to report
and fix, not a config value to change.
