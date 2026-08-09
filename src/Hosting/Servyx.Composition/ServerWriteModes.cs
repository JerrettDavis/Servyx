using Servyx.Domain.Transport;

namespace Servyx.Composition;

/// <summary>
/// The <c>Servyx:Servers:&lt;key&gt;:WriteMode</c> configuration key: no longer a way to grant write access to
/// an adopted server, and detected here only so an operator who still has one is told it is being ignored.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This key used to be the only route to a write grant.</strong> It produced
/// <see cref="WriteModeGrant"/>s that the composition root registered as startup singletons, which meant a
/// fresh install was inert (nothing at runtime could add a grant) and a grant could not be revoked without
/// restarting the process. The per-server grant now lives on the <c>Server.WriteMode</c> column, is flipped
/// from the UI with attribution, and takes effect on the next command — see
/// <see cref="DbBackedWriteModeResolver"/>.
/// </para>
/// <para>
/// <strong>The old key is ignored, not migrated.</strong> Not honoured as an override, because two sources
/// of truth for one decision is exactly the ambiguity this change exists to remove. And not honoured as a
/// seed, for a correctness reason and a security one. Correctness: this key is keyed by container
/// <em>name</em> while the grant is keyed by container <em>id</em>, so importing it would attach a grant to
/// whatever container currently answers to that name — not necessarily the one the operator was thinking
/// of when they wrote it. Security: a configuration file can be stale, copied from another host, or
/// committed to a repository, and silently re-granting write access nobody consciously re-affirmed is the
/// wrong direction for a safety property. Failing closed and making the operator click once is the correct
/// trade.
/// </para>
/// <para>
/// <strong>Scope of "ignored".</strong> This applies to servers reached over the local <c>docker</c>
/// transport — the ones Servyx's adoption path mints <c>Server</c> rows for. The same configuration key is
/// still read by <see cref="SshDockerWriteModes"/> and <see cref="SshBackupWiringOptions"/> for containers
/// and endpoints an operator declared explicitly under <c>Servyx:Hosts</c> / <c>Servyx:Servers:&lt;name&gt;:Ssh</c>;
/// no adoption path produces a row for those, so there is nothing for a database grant to replace there yet.
/// The warning text below says so rather than overclaiming.
/// </para>
/// </remarks>
public static class ServerWriteModes
{
    /// <summary>The configuration section holding per-server settings.</summary>
    public const string SectionKey = "Servyx:Servers";

    /// <summary>The key, within a server's section, holding its write mode.</summary>
    public const string WriteModeKey = "WriteMode";

    /// <summary>The transport the database-backed per-server grant applies to.</summary>
    public const string DockerTransportId = "docker";

    /// <summary>
    /// Returns the full configuration key of every <c>Servyx:Servers:&lt;key&gt;:WriteMode</c> entry present in
    /// <paramref name="configuration"/>, in configuration order, so startup can name each one it is ignoring.
    /// </summary>
    /// <remarks>
    /// Every present entry is reported, including one spelled <c>ReadOnly</c> and one that does not parse at
    /// all. The old code stayed silent about both — correctly, since it granted nothing either way — but
    /// silence means something different now: an operator reading a key that says <c>Enabled</c> and seeing a
    /// read-only server deserves to be told the key stopped being consulted, whatever it says.
    /// </remarks>
    /// <param name="configuration">The application configuration.</param>
    public static IReadOnlyList<string> FindIgnoredLegacyKeys(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var keys = new List<string>();

        foreach (var server in configuration.GetSection(SectionKey).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(server.Key))
            {
                continue;
            }

            if (server[WriteModeKey] is not null)
            {
                keys.Add($"{SectionKey}:{server.Key}:{WriteModeKey}");
            }
        }

        return keys;
    }
}
