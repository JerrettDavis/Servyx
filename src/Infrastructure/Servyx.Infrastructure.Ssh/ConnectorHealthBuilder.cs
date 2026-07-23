using Servyx.Domain.Connectors;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// Computes a <see cref="ConnectorHealth"/> from a connector's declared versus actually-working channels.
/// Factored out as a pure function (rather than inlined in <see cref="SshConnector.CheckAsync"/>) so the
/// degraded-channel-and-issue logic is directly unit-testable without a network connection.
/// </summary>
public static class ConnectorHealthBuilder
{
    /// <summary>
    /// Builds a <see cref="ConnectorHealth"/>. When <paramref name="reachable"/> is <see langword="true"/>,
    /// any bit present in <paramref name="declared"/> but absent from <paramref name="working"/> is folded
    /// into <see cref="ConnectorHealth.Degraded"/> with an explanatory issue — file channels and the exec
    /// channel get distinct, specific messages, matching the canonical example in
    /// <c>docs/connectors.md</c>: exec working but sftp disabled yields
    /// <c>Degraded = FileRead | FileWrite</c> with an issue naming <c>sshd_config</c>.
    /// </summary>
    public static ConnectorHealth Build(
        ConnectorChannel declared,
        ConnectorChannel working,
        bool reachable,
        TimeSpan latency,
        DateTimeOffset checkedAt,
        IReadOnlyList<string>? extraIssues = null)
    {
        var issues = new List<string>(extraIssues ?? []);
        var degraded = ConnectorChannel.None;

        if (reachable)
        {
            var missing = declared & ~working;

            const ConnectorChannel fileChannels = ConnectorChannel.FileRead | ConnectorChannel.FileWrite | ConnectorChannel.DirectoryList;
            var missingFile = missing & fileChannels;
            if (missingFile != ConnectorChannel.None)
            {
                degraded |= missingFile;
                issues.Add("File channels declared but not available — check whether the sftp subsystem is enabled in sshd_config.");
            }

            if ((missing & ConnectorChannel.Exec) != ConnectorChannel.None)
            {
                degraded |= ConnectorChannel.Exec;
                issues.Add("Exec channel declared but not available.");
            }
        }

        return new ConnectorHealth(reachable, working, degraded, issues, latency, checkedAt);
    }
}
