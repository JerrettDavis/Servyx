@e2e @dark
Feature: Theming
  As an operator who prefers a dark interface, or whose OS is set to dark mode
  I want every documented screen available as a dark-theme capture too
  So the user guide never shows only a light interface for an app that ships both

  # Every scenario here is the dark twin of a scenario that already exists elsewhere in this suite (see the
  # comment above each one for its light counterpart), reusing the exact same step definitions — only the
  # capture name changes, with a literal "-dark" suffix, and a "the page is in "dark" theme" guard is added
  # before every capture. That guard is not decorative: without it, a silently-failed dark-theme seed (see
  # ThemedBrowserContextFactory) would produce a light-rendered PNG committed under a "-dark" filename, and
  # nothing else in the suite — least of all the documentation integrity tests, which compare capture NAMES,
  # never pixels — would ever catch it.
  #
  # This feature file is the ONLY viable place for these captures. DocumentationScreenshotIntegrityTests scans
  # feature files as static text for literal `I capture the screen as "..."` names — it never executes
  # anything — so a Scenario Outline with a "<theme>" placeholder, or any capture name assembled at runtime,
  # would satisfy nothing: the regex `[a-z0-9-]+` cannot match a literal "<theme>" placeholder, and a name
  # built in a step definition never appears as text in this file at all.

  Background:
    Given Servyx is running against the demonstration host

  # Light: AdoptedInventory.feature — "The dashboard summarises the whole estate at a glance"
  Scenario: The dashboard summarises the whole estate at a glance, in dark theme
    When I open the dashboard
    Then the page is in "dark" theme
    And I capture the screen as "dashboard-overview-dark"

  # Light: AdoptedInventory.feature — "The server list shows where each server lives and how to reach it"
  Scenario: The server list shows where each server lives and how to reach it, in dark theme
    When I open the servers list
    Then the page is in "dark" theme
    And I capture the screen as "servers-list-dark"

  # Light: RemoteHostAdoption.feature — "A server adopted over ssh+docker is listed alongside the local one..."
  Scenario: A server adopted over ssh+docker is listed alongside the local one, in dark theme
    When I open the servers list
    Then the page is in "dark" theme
    And I capture the screen as "servers-list-remote-host-dark"

  # Light: RemoteHostAdoption.feature — "The remote server's Overview tab shows which ports actually reach the host"
  Scenario: The remote server's Overview tab shows which ports actually reach the host, in dark theme
    When I open the server detail page with id "example-remote-palworld"
    Then the page is in "dark" theme
    And I capture the screen as "remote-server-overview-dark"

  # Light: RemoteHostAdoption.feature — "The remote server's unhealthy badge explains the Palworld healthcheck false negative"
  Scenario: The remote server's unhealthy badge explains the Palworld healthcheck false negative, in dark theme
    When I open the server detail page with id "example-remote-palworld"
    Then the page is in "dark" theme
    And I capture the screen as "remote-server-health-explanation-dark", focused on the status card

  # Light: ServerHealthAndPower.feature — "The run state and container health of a server are reported as separate indicators"
  Scenario: The run state and container health of a server are reported as separate indicators, in dark theme
    When I open the server detail page for "Palygondwanaland"
    Then the page is in "dark" theme
    And I capture the screen as "server-overview-dark"

  # Light: ServerHealthAndPower.feature — "Every power action is present but disabled while Servyx is read-only"
  Scenario: Every power action is present but disabled while Servyx is read-only, in dark theme
    When I open the server detail page for "Palygondwanaland"
    Then the page is in "dark" theme
    And I capture the screen as "control-tier-read-only-dark", focused on the power controls

  # Light: OperationalVisibility.feature — "The console shows timestamped log lines with a warning highlighted"
  Scenario: The console shows timestamped log lines with a warning highlighted, in dark theme
    When I open the server detail page for "Palygondwanaland"
    And I open the "Console" tab
    Then the page is in "dark" theme
    And I capture the screen as "server-console-dark"

  # Light: Diagnostics.feature — "A server with no RCON control channel configured says so plainly on its Console tab"
  Scenario: A server with no RCON control channel configured says so plainly on its Console tab, in dark theme
    When I open the server detail page for "Palygondwanaland"
    And I open the "Console" tab
    Then the page is in "dark" theme
    And I capture the screen as "console-no-rcon-channel-dark", focused on the command panel

  # Light: OperationalVisibility.feature — "Saves show the world id, level file size, and per-player saves"
  Scenario: Saves show the world id, level file size, and per-player saves, in dark theme
    When I open the server detail page for "Palygondwanaland"
    And I open the "Saves" tab
    Then the page is in "dark" theme
    And I capture the screen as "server-saves-dark"

  # Light: BackupSafety.feature — "A server's own backups are labelled foreign with no destructive control present"
  Scenario: A server's own backups are labelled foreign with no destructive control present, in dark theme
    When I open the server detail page for "Palygondwanaland"
    And I open the "Backups" tab
    Then the page is in "dark" theme
    And I capture the screen as "server-backups-foreign-dark"

  # Light: BackupSafety.feature — "The estate-wide backups page lists every archive across every server"
  Scenario: The estate-wide backups page lists every archive across every server, in dark theme
    When I open the backups overview page
    Then the page is in "dark" theme
    And I capture the screen as "backups-overview-dark"

  # Light: ConfigurationFidelity.feature — "A setting shows its four tracked values and is flagged as drifted"
  Scenario: A setting shows its four tracked values and is flagged as drifted, in dark theme
    When I open the server detail page for "Palygondwanaland"
    And I open the "Settings" tab
    Then the page is in "dark" theme
    And I capture the screen as "settings-four-columns-dark"

  # Light: ConfigurationFidelity.feature — "A secret setting is masked rather than shown in the clear"
  Scenario: A secret setting is masked rather than shown in the clear, in dark theme
    When I open the server detail page for "Palygondwanaland"
    And I open the "Settings" tab
    Then the page is in "dark" theme
    And I capture the screen as "settings-secret-masking-dark", focused on the masked setting row

  # Light: Diagnostics.feature — "The top bar's connection status reports the transport's own probe detail..."
  Scenario: The top bar's connection status reports the transport's own probe detail, in dark theme
    When I open the dashboard
    Then the page is in "dark" theme
    And I capture the screen as "connection-status-healthy-dark", focused on the connection status

  # Light: GameCatalogue.feature — "The bundled game definition lists multiple deployment profiles"
  Scenario: The bundled game definition lists multiple deployment profiles, in dark theme
    When I open the games page
    Then the page is in "dark" theme
    And I capture the screen as "games-catalogue-dark"

  # Light: OperatorAdministration.feature — "The Audit page requires an authenticated Admin and lists the
  # accountability trail"
  Scenario: The Audit page requires an authenticated Admin and lists the accountability trail, in dark theme
    Given I am signed in as an administrator
    When I open the audit page
    Then the page is in "dark" theme
    And I capture the screen as "audit-page-dark"

  # Light: WriteModeTiers.feature — "The provisioning gate names its own configuration key and warns when authentication is off"
  Scenario: The provisioning gate names its own configuration key and warns when authentication is off, in dark theme
    When I open the deploy page
    Then the page is in "dark" theme
    And I capture the screen as "provisioning-gate-closed-dark"

  # Light: WriteModeTiers.feature — "PreviewOnly renders the stop-escalation ladder and offers no control at all"
  @write-enabled-host
  Scenario: PreviewOnly renders the stop-escalation ladder and offers no control at all, in dark theme
    Given Servyx is running with provisioning enabled and per-server write grants
    When I open the server detail page for "Palygondwanaland"
    Then the page is in "dark" theme
    And I capture the screen as "preview-only-stop-plan-dark"

  # Light: WriteModeTiers.feature — "A fully-enabled server shows live, clickable Start, Restart, Stop, and Kill controls"
  @write-enabled-host @requires-docker
  Scenario: A fully-enabled server shows live, clickable Start, Restart, Stop, and Kill controls, in dark theme
    Given Servyx is running with provisioning enabled and per-server write grants
    When I open the server detail page for "Example Remote Palworld"
    Then the page is in "dark" theme
    And I capture the screen as "lifecycle-controls-enabled-dark", focused on the power controls

  # The five scenarios below are the dark twins of Coverage.feature's new-coverage light captures — there is
  # no pre-existing light scenario anywhere else in the suite for these five pages.

  Scenario: The Mods placeholder explains mods are not supported for the bundled game, in dark theme
    When I open the mods page
    Then the page is in "dark" theme
    And I capture the screen as "mods-dark"

  Scenario: The Plugins placeholder explains the plugin SDK ships later, in dark theme
    When I open the plugins page
    Then the page is in "dark" theme
    And I capture the screen as "plugins-dark"

  Scenario: The Users page requires an authenticated Admin and lists accounts, in dark theme
    Given I am signed in as an administrator
    When I open the users page
    Then the page is in "dark" theme
    And I capture the screen as "users-dark"

  Scenario: The application Settings placeholder explains there is nothing to configure yet, in dark theme
    When I open the app settings page
    Then the page is in "dark" theme
    And I capture the screen as "settings-dark"

  Scenario: An unknown route renders the Not Found page, in dark theme
    When I open a page that does not exist
    Then the page is in "dark" theme
    And I capture the screen as "not-found-dark"

  # Light: Coverage.feature — "The Error page is directly routable and reports a request id"
  Scenario: The Error page is directly routable and reports a request id, in dark theme
    When I navigate directly to the error page
    Then the page is in "dark" theme
    And I capture the screen as "error-page-dark"
