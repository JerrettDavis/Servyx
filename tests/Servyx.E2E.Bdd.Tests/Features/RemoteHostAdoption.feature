@e2e
Feature: Remote host adoption
  As an operator running a game server on a machine I reach only over SSH
  I want Servyx to adopt and label that server the same way it labels a local one
  So I can tell at a glance which servers are local and which live on a remote host

  Background:
    Given Servyx is running against the demonstration host

  Scenario: A server adopted over ssh+docker is listed alongside the local one, labelled by transport
    When I open the servers list
    Then the server "Example Remote Palworld" is listed for game "Palworld"
    And its state is shown as "Running" and its health as "Unhealthy"
    And its published ports "8211/udp" and "27015/udp" are listed
    And the server "Example Remote Palworld" has host "ssh+docker"
    And I capture the screen as "servers-list-remote-host"

  Scenario: The remote server's Overview tab shows which ports actually reach the host
    When I open the server detail page with id "example-remote-palworld"
    Then the port "8211/UDP" is shown as published to host
    And the port "27015/UDP" is shown as published to host
    And the port "25575/TCP" is shown as not published to host
    And the mount "/opt/palworld/data" maps to "/palworld"
    And the network is shown as "bridge"
    And I capture the screen as "remote-server-overview"

  Scenario: The remote server's unhealthy badge explains the Palworld healthcheck false negative
    When I open the server detail page with id "example-remote-palworld"
    Then the health badge shows "Unhealthy"
    And the health badge's tooltip explains the false-negative health signal
    And I capture the screen as "remote-server-health-explanation", focused on the status card
