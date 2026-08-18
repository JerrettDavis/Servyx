@e2e
Feature: Coverage
  As an operator relying on the user guide's screenshots
  I want every navigable screen documented, not only the ones a business scenario already exercises
  So the guide never quietly omits a page Servyx actually serves

  Background:
    Given Servyx is running against the demonstration host

  # Mods and Plugins still render the empty-state placeholder pattern described below — a heading and a short
  # paragraph naming the milestone the real feature ships in. Users and application-level Settings graduated
  # out of that pattern (see their own scenarios below, and OperatorAdministration.feature's identical note for
  # Audit) once real functionality shipped for each. Each still gets its own capture here: they are genuinely
  # different pages the sidebar links to.

  Scenario: The Mods placeholder explains mod management is not available yet
    When I open the mods page
    Then the page heading reads "Mod management is not available yet"
    And the page is in "light" theme
    And I capture the screen as "mods"

  Scenario: The Plugins placeholder explains the plugin SDK ships later
    When I open the plugins page
    Then the page heading reads "No plugins installed"
    And the page is in "light" theme
    And I capture the screen as "plugins"

  Scenario: The Users page requires an authenticated Admin and lists accounts
    Given I am signed in as an administrator
    When I open the users page
    Then the page's h2 heading reads "Users"
    And the page is in "light" theme
    And I capture the screen as "users"

  Scenario: The application Settings page shows retention, host connection, and password sections
    When I open the app settings page
    Then the page's h2 heading reads "Settings"
    And the page is in "light" theme
    And I capture the screen as "settings"

  Scenario: An unknown route renders the Not Found page
    When I open a page that does not exist
    Then the page heading reads "Not Found"
    And the page is in "light" theme
    And I capture the screen as "not-found"

  # Error.razor carries its own "@page "/Error"" directive, so it is directly routable — an operator who
  # bookmarks or is redirected to it sees this regardless of whether an exception actually occurred.
  Scenario: The Error page is directly routable and reports a request id
    When I navigate directly to the error page
    Then the error page reports a request id
    And the page is in "light" theme
    And I capture the screen as "error-page"
