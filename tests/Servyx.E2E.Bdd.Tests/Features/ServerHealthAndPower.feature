@e2e
Feature: Server health and power
  As an operator
  I want a server's run state and container health reported as separate signals
  And every power action visibly present but gated in read-only mode
  So I never confuse "the container is running" with "Docker's own healthcheck is happy"
  And I never mistake an absent control for a missing feature

  Background:
    Given Servyx is running against the demonstration host

  Scenario: The run state and container health of a server are reported as separate indicators
    When I open the server detail page for "Palygondwanaland"
    Then the state badge shows "Running"
    And the health badge shows "Unhealthy"
    And the state and health badges are distinct elements
    And I capture the screen as "server-overview"

  Scenario: Every power action is present but disabled while Servyx is read-only
    When I open the server detail page for "Palygondwanaland"
    Then the power controls "Start", "Restart", "Stop" and "Kill" are all present and disabled
    And each disabled power control explains it is because of read-only mode
    And I capture the screen as "control-tier-read-only", focused on the power controls
