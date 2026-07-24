@e2e
Feature: Game catalogue
  As an operator deciding how to deploy a new game
  I want to see every deployment profile a bundled game definition offers
  So I can pick the one that matches how I already run my containers

  Background:
    Given Servyx is running against the demonstration host

  Scenario: The bundled game definition lists multiple deployment profiles
    When I open the games page
    Then the "Palworld Dedicated Server" game lists 2 deployment profiles
    And I capture the screen as "games-catalogue"
