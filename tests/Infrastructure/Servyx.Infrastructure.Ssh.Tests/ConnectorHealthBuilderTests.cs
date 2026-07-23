using FluentAssertions;
using Servyx.Domain.Connectors;
using Servyx.Infrastructure.Ssh;

namespace Servyx.Infrastructure.Ssh.Tests;

/// <summary>
/// <see cref="ConnectorHealth"/> degraded semantics: the canonical case from <c>docs/connectors.md</c> is
/// an SSH connector where exec works but the sftp subsystem is disabled — <c>Working = Exec</c>,
/// <c>Degraded = FileRead | FileWrite | DirectoryList</c>, with an issue naming <c>sshd_config</c>. This is
/// deliberately distinct from a bare "unreachable" result.
/// </summary>
public class ConnectorHealthBuilderTests
{
    private static readonly DateTimeOffset CheckedAt = DateTimeOffset.UtcNow;

    [Fact]
    public void Exec_working_but_file_channels_disabled_reports_degraded_file_channels_with_sshd_config_issue()
    {
        var declared = ConnectorChannel.Exec | ConnectorChannel.FileRead | ConnectorChannel.FileWrite | ConnectorChannel.DirectoryList;
        var working = ConnectorChannel.Exec;

        var health = ConnectorHealthBuilder.Build(declared, working, reachable: true, TimeSpan.FromMilliseconds(50), CheckedAt);

        health.Reachable.Should().BeTrue();
        health.Working.Should().Be(ConnectorChannel.Exec);
        health.Degraded.Should().Be(ConnectorChannel.FileRead | ConnectorChannel.FileWrite | ConnectorChannel.DirectoryList);
        health.Issues.Should().ContainSingle(i => i.Contains("sshd_config"));
    }

    [Fact]
    public void Fully_working_connector_reports_no_degraded_channels_and_no_issues()
    {
        var declared = ConnectorChannel.Exec | ConnectorChannel.FileRead;
        var working = ConnectorChannel.Exec | ConnectorChannel.FileRead;

        var health = ConnectorHealthBuilder.Build(declared, working, reachable: true, TimeSpan.FromMilliseconds(10), CheckedAt);

        health.Degraded.Should().Be(ConnectorChannel.None);
        health.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Unreachable_connector_reports_no_degraded_channels_since_degradation_is_a_different_failure_than_unreachability()
    {
        var declared = ConnectorChannel.Exec | ConnectorChannel.FileRead;

        var health = ConnectorHealthBuilder.Build(
            declared, ConnectorChannel.None, reachable: false, TimeSpan.Zero, CheckedAt,
            extraIssues: ["Connection refused."]);

        health.Reachable.Should().BeFalse();
        health.Degraded.Should().Be(ConnectorChannel.None, "an unreachable host is a connectivity failure, not a partial-capability one");
        health.Issues.Should().Contain("Connection refused.");
    }

    [Fact]
    public void Exec_disabled_but_files_working_reports_degraded_exec_with_specific_issue()
    {
        var declared = ConnectorChannel.Exec | ConnectorChannel.FileRead;
        var working = ConnectorChannel.FileRead;

        var health = ConnectorHealthBuilder.Build(declared, working, reachable: true, TimeSpan.FromMilliseconds(5), CheckedAt);

        health.Degraded.Should().Be(ConnectorChannel.Exec);
        health.Issues.Should().Contain(i => i.Contains("Exec channel"));
    }

    [Fact]
    public void Working_and_degraded_are_always_disjoint()
    {
        var declared = ConnectorChannel.Exec | ConnectorChannel.FileRead | ConnectorChannel.FileWrite;
        var working = ConnectorChannel.Exec | ConnectorChannel.FileRead;

        var health = ConnectorHealthBuilder.Build(declared, working, reachable: true, TimeSpan.FromMilliseconds(1), CheckedAt);

        (health.Working & health.Degraded).Should().Be(ConnectorChannel.None);
    }
}
