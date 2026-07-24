@e2e
Feature: Backup safety
  As an operator
  I want backups Servyx does not own clearly labelled foreign, with no destructive control anywhere near them
  And a single estate-wide view of every archive across every server
  So Servyx can never be mistaken for owning, and therefore being able to prune, a backup it did not create

  Background:
    Given Servyx is running against the demonstration host

  Scenario: A server's own backups are labelled foreign with no destructive control present
    When I open the server detail page for "Palygondwanaland"
    And I open the "Backups" tab
    Then every backup on this server is labelled "Foreign"
    And no delete, prune or restore control is present anywhere on the panel
    And I capture the screen as "server-backups-foreign"

  Scenario: The estate-wide backups page lists every archive across every server
    When I open the backups overview page
    Then 5 backups are listed, each showing its server, filename, created time and size
    And I capture the screen as "backups-overview"
