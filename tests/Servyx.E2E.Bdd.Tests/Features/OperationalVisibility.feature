@e2e
Feature: Operational visibility
  As an operator
  I want to read the live console output and inspect what is actually on disk for a world
  So I can diagnose a problem without shelling into the container

  Background:
    Given Servyx is running against the demonstration host

  Scenario: The console shows timestamped log lines with a warning highlighted
    When I open the server detail page for "Palygondwanaland"
    And I open the "Console" tab
    Then the console shows 15 timestamped log lines
    And the line mentioning "401 Unauthorized" is highlighted as a warning
    And I capture the screen as "server-console"

  Scenario: Saves show the world id, level file size, and per-player saves
    When I open the server detail page for "Palygondwanaland"
    And I open the "Saves" tab
    Then the world id "F1FA89C5D3A74636A42816EBE4370739" is shown
    And the level file size "2.4 MB" is shown
    And 5 player saves are listed
    And I capture the screen as "server-saves"
