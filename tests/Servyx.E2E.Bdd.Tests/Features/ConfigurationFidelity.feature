@e2e
Feature: Configuration fidelity
  As an operator
  I want every setting shown across the four values Servyx tracks for it
  And any secret setting masked rather than shown in the clear
  So I can spot drift between what Servyx wants, the .env, the rendered INI and the live server
  Without ever leaking a credential onto the screen

  Background:
    Given Servyx is running against the demonstration host

  Scenario: A setting shows its four tracked values and is flagged as drifted
    When I open the server detail page for "Palygondwanaland"
    And I open the "Settings" tab
    Then the "Max players" setting shows Desired "32", Authoritative (.env) "32", Rendered (INI) "16" and Runtime "16"
    And the "Max players" setting is flagged as drifted
    And I capture the screen as "settings-four-columns"

  Scenario: A secret setting is masked rather than shown in the clear
    When I open the server detail page for "Palygondwanaland"
    And I open the "Settings" tab
    Then the "Admin / RCON password" setting's authoritative value is masked as "********"
    And the "Admin / RCON password" setting's desired-value field is a password field
    And I capture the screen as "settings-secret-masking", focused on the masked setting row
