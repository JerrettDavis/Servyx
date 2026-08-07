# Deploying a server

Every other page in this guide is about a server Servyx already knows about — one you (or someone else)
started, and Servyx **adopted** by matching an existing container against a bundled game definition. The
Deploy page (`/deploy`) is different: it is where Servyx would **create** infrastructure from nothing, on
your local Docker daemon or at a cloud provider, rather than attach to something that already exists. See
[Adopting servers](adopting-servers.md) for the read-only counterpart this page is not.

Provisioning is the newest and least-exposed capability in Servyx, and the whole page reflects that: it is
gated behind its own configuration key, off by default, and every consequence of turning it on is spelled
out on the page itself before you can act on anything.

## The provisioning gate

`Servyx:Provisioning:Enabled` is a process-wide flag, and it defaults to **`false`**. With it off, no
`IProvisioner` of any kind is registered in the running process — there is no object capable of creating
infrastructure, regardless of what any page renders. This mirrors the fail-closed rule `AuthenticationGate`
uses in the opposite direction: a misconfiguration must never widen what an anonymous or unauthenticated
caller can do, so an absent, empty, or unparseable value for this key leaves provisioning **off**.

With the gate closed, the Deploy page shows only an explanation — there is nothing to disable, because
nothing is registered to drive:

![The Deploy page with provisioning disabled: it names Servyx:Provisioning:Enabled as the key that would turn it on, and — because this demonstration instance runs with authentication switched off — warns that doing so would hand anyone who can reach the web port the ability to create real infrastructure](../images/provisioning-gate-closed.png)

The exact warning you see here depends on whether authentication is on. On the demonstration host this
screenshot comes from, `Servyx:Authentication:Enabled` is explicitly `false`, so the page says turning on
provisioning would let **anyone who can reach this web port** create infrastructure, with no login in the
way. On a normal installation — authentication on by default — the same warning instead says the
capability would belong to **whoever holds the one operator password**, since reaching this page at all
already required it. See [Enabling writes](enabling-writes.md) for the equivalent gate and warning that
apply to mutating an *existing* server, and [Operator administration](operator-administration.md) for what
that one operator password actually is.

If the gate is turned on but nothing registers an `IProvisioningDashboard`, the page says so plainly instead
of rendering an empty provisioner list as if that were normal — "provisioning is enabled but not wired" is a
different fact from "provisioning is off", and the page does not collapse the two.

## What provisioning targets exist

With the gate open and a dashboard registered, the page lists every provisioner the running process knows
about, each identified by a stable, permanent id. As of this build, Servyx ships eight:

| Provisioner id | Target | Cost estimates | Update in place | Drift detection |
|---|---|---|---|---|
| `docker-container` | A container on the connected Docker daemon | no — unknown | no (recreate only) | yes |
| `local-process` | A plain OS process, no container runtime involved | no — unknown | yes | yes |
| `aws-ec2` | An AWS EC2 instance | yes | yes | yes |
| `aws-ecs-fargate` | An AWS ECS Fargate task/service | yes | no (recreate only) | no |
| `aws-lightsail` | An AWS Lightsail instance | yes | yes | yes |
| `azure-vm` | An Azure virtual machine | yes | yes | yes |
| `azure-container-instance` | An Azure Container Instance | yes | no (recreate only) | no |
| `digitalocean-droplet` | A DigitalOcean droplet | yes | yes | yes |

Capabilities are per-adapter facts, not a promise about what the underlying provider can do in the
abstract — a provisioner only claims a capability its Servyx adapter actually implements. `docker-container`
and `local-process` are the two that never estimate cost, for the same reason: there is no bill to
estimate. Every row the Deploy page renders states its capabilities as a set of chips, plus a plain
"yes"/"no — costs render as unknown" answer for cost estimation specifically, so you never have to infer it
from the chip list.

## Cost estimates never fabricate a number

Wherever a cost appears on this page — in a plan preview or an update preview — it goes through one single
formatter (`CostEstimateView`), and that formatter has one hard rule: an estimate with
`CostConfidence.Unknown`, or no estimate at all, always renders as the literal word **"unknown"**, never as
a zero or a blank. A fabricated `$0.00` would be indistinguishable from a real free-tier resource to whoever
is reading it; "we do not know what this costs" is the only truthful thing to say when a provisioner has no
`EstimatesCost` capability or genuinely cannot price the request. Where a figure is known, it renders as an
hourly and/or monthly amount in the estimate's own currency.

## Preview, then apply — nothing is created by looking

Selecting a provisioner and filling in its form (the fields are driven entirely by that provisioner's own
schema, not hand-coded per target) and clicking **Preview plan** is pure computation: it calls the
provisioner's `PlanAsync`, which the `IProvisioner` contract forbids from changing anything at the provider.
The result shows a plan id, a content hash, the estimated cost, an expiry timestamp, and the ordered list of
stages applying the plan would run.

**Apply this plan** is the one control on this half of the page that can create anything, and three things
protect it:

1. It only exists once a plan has been previewed — there is no way to apply a plan you have not seen.
2. The plan hash shown to you is sent back and **revalidated** when you apply; if anything about the
   request changed since the preview (an edited field, a different provisioner), the hash no longer matches
   and the apply is refused as stale rather than silently running against a different plan than the one you
   reviewed.
3. If the host process registers no `ProvisioningExecutor` (`IProvisioningDashboard.ExecutionConfigured` is
   `false`), the button renders greyed out with a lock icon and a stated reason instead of being hidden —
   the same "always show a locked control, never remove it" rule the rest of Servyx follows for mutating
   actions (see [Enabling writes](enabling-writes.md)).

Applying commits a write-ahead ledger row **before** the provider is contacted, then asks the provider to
create the resource. A failed apply leaves that row in the `Intended` state for reconciliation, and says so
explicitly, including whether Servyx's own attempt to compensate (undo the partial create) completed.

## Updates require a second, separate acknowledgement for risky data impact

An existing ledger row that a provisioner can maintain (`IMaintainer`) can have an update planned against
it, the same preview/apply discipline applying again — but updates add one more gate that fresh creation
does not need: **`DataImpactAcknowledgement`**.

Every update plan states a `DataImpact`:

- **`Preserved`** — every store the resource's persistent data lives in survives, attached the same way it
  was before. No acknowledgement is possible or needed for this value; there is deliberately no factory
  that produces one.
- **`AtRisk`** — the update may separate the workload from some of its state without deleting anything (for
  example, a container whose writable layer does not survive being recreated).
- **`Destroyed`** — the update deletes a store the resource's data lives in. No provisioner in this build
  ever asserts this today; the value exists so a future adapter that genuinely does destroy a volume has a
  truthful way to say so instead of understating it as `AtRisk`.

For any plan that is not `Preserved`, the page renders a **second, distinct checkbox** — "I have read the
above and accept that this update's impact on persistent data is `<impact>`" — separate from the Apply
button itself, and the Apply control stays disabled until it is checked. This is not just a UI nicety: the
application layer independently refuses to run a non-preserving update without a matching
`DataImpactAcknowledgement` token, and that token is impact-specific (a token minted for `AtRisk` does not
authorize a `Destroyed` plan). There is no way to acknowledge "whatever the plan turns out to be" — accepting
one specific, named impact is the only shape the API accepts.

## The provisioning ledger

The bottom of the page lists every ledger row Servyx has recorded — its durable record of provisioning
*intent*, kept independently of whatever the provider itself reports. Three distinct states appear here,
each with its own copy rather than being collapsed into a generic empty state:

- **No ledger configured** — nothing is recording intent in this process at all. This is explicitly *not*
  the same as "nothing has been provisioned"; a resource created with no ledger wired up has no local trace.
- **Ledger configured, no unresolved rows** — every recorded intent has been reconciled.
- **One or more rows** — each shows its row id, provisioner, region, lifecycle state (`Intended` before the
  provider confirms it, `Created` once it does), the provider-assigned resource id (or "not assigned yet"
  for a still-`Intended` row), when it was recorded, and a drift badge.

Drift is checked against the provider-assigned id the ledger learned when the provider confirmed the
resource — never against a guess reconstructed from the row's own tags. An `Intended` row has no such id
yet, so it is never silently reported as matching; it is reported as not-yet-checkable, with the reason
named. A provisioner that does not implement drift detection (`aws-ecs-fargate`, `azure-container-instance`)
answers "unknown — not checkable" for the same reason it never claims the capability in the first place.

---
**Next:** [Enabling writes](enabling-writes.md) · **See also:** [Adopting servers](adopting-servers.md) ·
[Operator administration](operator-administration.md)
