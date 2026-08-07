@e2e
Feature: First run
  As someone starting Servyx for the very first time
  I want to see what the operator sign-in / first-run screen looks like before I can reach any dashboard
  So the installation guide can show me what to expect instead of leaving me guessing

  # Every other @e2e scenario in this suite runs against a host started with
  # Servyx__Authentication__Enabled=false (see ServyxAppProcess.StartAsync's documented defaults), so the
  # dashboard is reachable with no sign-in preamble. This is the one scenario that needs authentication ON,
  # against a fresh install with no operator password ever set, to show the actual first thing an operator
  # sees. See Support/AuthenticationEnabledAppFixture and Steps/FirstRunSteps for how it gets its own,
  # separately-configured app instance without touching any other scenario's setup.

  @login-first-run
  Scenario: A fresh install with authentication on asks the first operator to set a password
    Given Servyx is running with authentication enabled and no operator password set
    When I visit Servyx for the first time
    Then I am redirected to the sign-in page
    And the page asks me to set the first operator password
    And I capture the screen as "operator-first-run-login"

  # Dark twin of the scenario above. Lives here, not in Theming.feature, because the capture step it needs
  # is @login-first-run-scoped to this feature's own auth-enabled fixture and page (see FirstRunSteps) —
  # Theming.feature's scenarios all share the ordinary container-registered page, which never reaches this
  # sign-in flow at all.
  @login-first-run @dark
  Scenario: A fresh install with authentication on asks the first operator to set a password, in dark theme
    Given Servyx is running with authentication enabled and no operator password set
    When I visit Servyx for the first time
    Then I am redirected to the sign-in page
    And the page asks me to set the first operator password
    And the page is in "dark" theme
    And I capture the screen as "operator-first-run-login-dark"
