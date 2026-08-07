@e2e
Feature: Diagnostics
  As an operator whose host or a server's control channel might not be reachable
  I want Servyx to tell me exactly what it tried and why, not just "something is wrong"
  So I know whether to fix Docker, fix SSH, fix RCON, or wait

  Background:
    Given Servyx is running against the demonstration host

  Scenario: The top bar's connection status reports the transport's own probe detail, not a hardcoded claim
    When I open the dashboard
    Then the connection status shows "Connected"
    And the connection tooltip reports the transport's own probe detail
    And I capture the screen as "connection-status-healthy", focused on the connection status

  Scenario: A server with no RCON control channel configured says so plainly on its Console tab
    When I open the server detail page for "Palygondwanaland"
    And I open the "Console" tab
    Then the console reports that no RCON control channel is configured for this server
    And I capture the screen as "console-no-rcon-channel", focused on the command panel
