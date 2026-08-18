@e2e
Feature: Operator administration
  As an operator reading the docs
  I want to see exactly what the Users and Audit pages show today
  So the user guide can be honest about what each page actually does

  Background:
    Given Servyx is running against the demonstration host

  # Audit is Admin-gated the same way Users is (see AuditPage.razor, UsersPage.razor) — unconditionally, not
  # only when Servyx:Authentication:Enabled is on — so reaching it needs a real sign-in even against this
  # otherwise-anonymous demonstration host. See AdminSessionSteps for why.

  Scenario: The Audit page requires an authenticated Admin and lists the accountability trail
    Given I am signed in as an administrator
    When I open the audit page
    Then the audit page lists the accountability trail
    And I capture the screen as "audit-page"
