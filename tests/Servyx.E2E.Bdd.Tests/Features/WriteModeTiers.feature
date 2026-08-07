@e2e
Feature: Write mode tiers
  As an operator deciding whether to grant a server write access
  I want the provisioning gate to name its own configuration key and warn me when authentication is off
  So I understand exactly what turning writes on for a server requires, and what it exposes if I misconfigure it

  Background:
    Given Servyx is running against the demonstration host

  # The disabled, lock-iconed Power card (lifecycle controls read-only, with a lock reason) is already
  # captured as "control-tier-read-only" by ServerHealthAndPower.feature — reused by both
  # docs/user-guide/enabling-writes.md and docs/user-guide/lifecycle-control.md rather than re-captured here.

  Scenario: The provisioning gate names its own configuration key and warns when authentication is off
    When I open the deploy page
    Then the page explains that provisioning requires "Servyx:Provisioning:Enabled"
    And the page warns that authentication is disabled
    And I capture the screen as "provisioning-gate-closed"

  # The two scenarios below run against a SECOND, independent app process (see WriteEnabledAppFixture) that
  # grants writes to the two mock servers — the default host every other scenario in this suite runs
  # against never does, and stays closed throughout. Nothing here clicks a lifecycle control: PreviewOnly
  # cannot mutate by construction, and the Enabled capture below shows the rendered controls only, exactly
  # as the docs it illustrates describe.

  @write-enabled-host
  Scenario: PreviewOnly renders the stop-escalation ladder and offers no control at all
    Given Servyx is running with provisioning enabled and per-server write grants
    When I open the server detail page for "Palygondwanaland"
    Then the stop-escalation ladder is shown in order, with no power controls present
    And I capture the screen as "preview-only-stop-plan"

  @write-enabled-host @requires-docker
  Scenario: A fully-enabled server shows live, clickable Start, Restart, Stop, and Kill controls
    Given Servyx is running with provisioning enabled and per-server write grants
    When I open the server detail page for "Example Remote Palworld"
    Then the power controls "Start", "Restart", "Stop" and "Kill" are all present and enabled
    And I capture the screen as "lifecycle-controls-enabled", focused on the power controls
