@e2e
Feature: Operator administration
  As an operator reading the docs before Milestone 7 ships
  I want to see exactly what the Users, Audit, and Settings pages show today
  So the user guide can be honest about what is a real feature and what is a placeholder

  Background:
    Given Servyx is running against the demonstration host

  # Users and Settings render the same empty-state pattern as Audit (see UsersPage.razor,
  # AppSettingsPage.razor, AuditPage.razor) — a heading and a paragraph naming Milestone 7, nothing
  # interactive. One representative capture is enough to illustrate the pattern the guide describes;
  # capturing all three would be three near-identical screenshots of the same empty-state layout.

  Scenario: The Audit page is a placeholder until Milestone 7
    When I open the audit page
    Then the audit page explains it has no dedicated UI yet
    And I capture the screen as "audit-page-placeholder"
