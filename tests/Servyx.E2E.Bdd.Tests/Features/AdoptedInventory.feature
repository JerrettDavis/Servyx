@e2e
Feature: Adopted inventory
  As an operator who has adopted an existing Palworld container into Servyx
  I want the dashboard and server list to summarise my whole estate
  So I can tell at a glance what is running and how to reach it

  Background:
    Given Servyx is running against the demonstration host

  Scenario: The dashboard summarises the whole estate at a glance
    When I open the dashboard
    Then the "Servers online" tile shows "2 / 2"
    And the "Total players" tile shows "10 / 64"
    And the "Foreign backups" tile shows "5"
    And all 10 sidebar entries are reachable
    And I capture the screen as "dashboard-overview"

  Scenario: The server list shows where each server lives and how to reach it
    When I open the servers list
    Then the server "Palygondwanaland" is listed for game "Palworld"
    And its state is shown as "Running" and its health as "Unhealthy"
    And its players are shown as "3 / 32"
    And its uptime is shown
    And its published ports "8211/udp" and "27015/udp" are listed
    And I capture the screen as "servers-list"
