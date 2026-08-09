using System.Globalization;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Backups;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;

namespace Servyx.Composition;

/// <summary>
/// One SSH-hosted server, as the host understands it: which machine to reach, which credential opens it,
/// and which paths under which root a backup captures.
/// </summary>
/// <param name="ServerKey">The <c>Servyx:Servers:&lt;key&gt;</c> configuration key — this server's id.</param>
/// <param name="Endpoint">
/// The address <c>SshTransport</c> connects to, in <c>SshEndpoint</c>'s <c>[user@]host[:port]</c> form.
/// Carried verbatim, because it is also what the server's <see cref="WriteModeGrant"/> is scoped to and the
/// two must match exactly for a write to be permitted.
/// </param>
/// <param name="CredentialUrn">
/// Where the SSH password or private key lives. A locator only; the value is resolved through
/// <see cref="ISecretStore"/> when a connection is opened and is never held in configuration.
/// </param>
/// <param name="Root">The absolute host directory every include, exclude and archive path is relative to.</param>
/// <param name="Include">Root-relative literal paths to capture. Never globs — see <c>SshBackupContext.Include</c>.</param>
/// <param name="Exclude">Glob patterns handed to <c>tar --exclude</c>.</param>
/// <param name="StoreDirectory">Root-relative directory Servyx writes (and deletes) its own archives in.</param>
/// <param name="DeploymentKind">The definition's deployment kind, used by adopters' <c>Supports</c>.</param>
/// <param name="DefaultRetention">Retention applied when a caller supplies none.</param>
/// <param name="Writable">Whether the operator granted this server <c>WriteMode = Enabled</c>.</param>
/// <param name="ForeignDirectory">
/// Root-relative directory some other mechanism (a distro cron job, the game's own scheduled export)
/// writes archives into, or <see langword="null"/> when the operator declared none. Named here, not
/// guessed: see <see cref="SshBackupWiringOptions"/>'s remarks for why Servyx will not infer this path.
/// </param>
/// <param name="ForeignPattern">
/// Filename glob identifying an archive inside <paramref name="ForeignDirectory"/> (e.g. <c>*.tar.gz</c>).
/// Meaningless when <paramref name="ForeignDirectory"/> is <see langword="null"/>.
/// </param>
public sealed record SshBackupServer(
    string ServerKey,
    string Endpoint,
    SecretUrn? CredentialUrn,
    string Root,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    string StoreDirectory,
    string DeploymentKind,
    RetentionPolicy DefaultRetention,
    bool Writable,
    string? ForeignDirectory = null,
    string ForeignPattern = SshBackupWiringOptions.DefaultForeignPattern);

/// <summary>
/// The SSH-hosted servers the operator has configured for backups, read from
/// <c>Servyx:Servers:&lt;name&gt;:Ssh:*</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Empty by default, and empty is the whole point.</strong> There is no host, no root, and no
/// capture set that Servyx could guess for a stranger's machine, so an operator who configures nothing gets
/// <see cref="None"/> — no SSH transport is constructed, no secret is resolved, no
/// <c>SshBackupProvider</c> is registered, and the container is byte-for-byte what it was before this type
/// existed. That is the same shape <see cref="RconWiringOptions.Disabled"/> takes and for the same reason.
/// </para>
/// <para>
/// <strong>Opt-in per server, on top of the provisioning gate.</strong> With
/// <c>Servyx:Provisioning:Enabled</c> off this returns <see cref="None"/> outright. With it on, a server
/// still has to name itself under <c>Servyx:Servers:&lt;name&gt;:Ssh:Enabled</c> <em>and</em> supply the two
/// values nothing can be inferred from — <c>Host</c> and <c>Root</c>. A server missing either is skipped
/// rather than defaulted: a plausible-looking default would archive the wrong machine's wrong directory.
/// </para>
/// <para>
/// <strong>What is deliberately not validated here.</strong> <c>Include</c> is passed through as configured,
/// including when it is empty or contains a wildcard. <c>SshBackupProvider</c> already refuses both, by name,
/// with a message that explains why (its includes reach the host's <c>tar</c> as argv members with no shell
/// to expand them). Re-stating that rule here would give the operator two places to read it and two chances
/// for them to disagree — and listing, inspecting and dry-run pruning all keep working on a server whose
/// capture set is not yet usable.
/// </para>
/// </remarks>
public sealed class SshBackupWiringOptions
{
    /// <summary>The configuration section per-server settings are read from.</summary>
    public const string SectionKey = ServerWriteModes.SectionKey;

    /// <summary>The per-server child key holding the SSH block.</summary>
    public const string SshKey = "Ssh";

    /// <summary>The <see cref="TargetDescriptor.TransportId"/> these servers are reached through.</summary>
    public const string TransportId = "ssh";

    /// <summary>The deployment kind assumed when configuration names none.</summary>
    public const string DefaultDeploymentKind = "ssh";

    /// <summary>The default directory Servyx writes its own archives into, relative to the data root.</summary>
    public const string DefaultStoreDirectory = BackupWiringOptions.DefaultStoreDirectory;

    /// <summary>
    /// The filename glob assumed for a declared foreign archive directory when the operator names a
    /// directory but no pattern — the shape a plain <c>tar.gz</c> cron job produces.
    /// </summary>
    public const string DefaultForeignPattern = "*.tar.gz";

    /// <summary>The <see cref="SecretUrn"/> scope SSH credentials live under.</summary>
    public const string SecretScope = "server";

    /// <summary>The <see cref="SecretUrn"/> category SSH credentials live under.</summary>
    public const string SecretCategory = "ssh";

    /// <summary>The <see cref="SecretUrn"/> name an SSH credential is stored as when configuration names none.</summary>
    public const string SecretName = "password";

    /// <summary>No server is SSH-hosted. The state of a Docker-only host, and the safe default.</summary>
    public static readonly SshBackupWiringOptions None = new([]);

    private readonly IReadOnlyList<SshBackupServer> _servers;

    /// <summary>Creates options over an explicit set of servers.</summary>
    /// <param name="servers">The configured servers.</param>
    public SshBackupWiringOptions(IEnumerable<SshBackupServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        _servers = [.. servers];
    }

    /// <summary>The configured servers.</summary>
    public IReadOnlyList<SshBackupServer> Servers => _servers;

    /// <summary>Whether any server in this process is SSH-hosted.</summary>
    public bool Any => _servers.Count > 0;

    /// <summary>The configured server ids, for routing a call to the provider that owns it.</summary>
    public IReadOnlyList<string> ServerKeys => [.. _servers.Select(s => s.ServerKey)];

    /// <summary>Finds a configured server by id, or returns <see langword="null"/>.</summary>
    /// <param name="serverId">The server id.</param>
    public SshBackupServer? Find(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return null;
        }

        foreach (var server in _servers)
        {
            if (string.Equals(server.ServerKey, serverId, StringComparison.OrdinalIgnoreCase))
            {
                return server;
            }
        }

        return null;
    }

    /// <summary>
    /// The <see cref="WriteModeGrant"/>s the SSH write guard consults — one per server the operator marked
    /// <c>WriteMode = Enabled</c>, scoped to that server's endpoint and nothing wider.
    /// </summary>
    /// <remarks>
    /// No <see cref="WriteModeGrant"/> is emitted for the local <c>docker</c> transport any more — an
    /// adopted server's grant is a database row resolved live by <see cref="DbBackedWriteModeResolver"/>,
    /// keyed on container id, and <see cref="ServerWriteModes"/> now only <em>detects</em> the legacy key so
    /// startup can name it as ignored. This is the SSH half, still read from that same
    /// <c>Servyx:Servers:&lt;name&gt;:WriteMode</c> key, because no adoption path mints a row for an SSH
    /// backup endpoint. A server without a grant can still be
    /// listed, inspected, previewed and dry-run pruned; only <c>CreateAsync</c> and <c>RestoreAsync</c> are
    /// refused, at the transport, with <see cref="WritesDisabledException"/>.
    /// </remarks>
    public IReadOnlyList<WriteModeGrant> WriteGrants =>
        [.. _servers.Where(s => s.Writable).Select(s => new WriteModeGrant(WriteMode.Enabled, TransportId, s.Endpoint))];

    /// <summary>
    /// Reads the configured SSH-hosted servers, or returns <see cref="None"/> when <paramref name="gate"/>
    /// is closed.
    /// </summary>
    /// <remarks>
    /// A server whose configuration key is not a legal <see cref="SecretUrn"/> segment is skipped rather
    /// than coerced, exactly as in <see cref="RconWiringOptions.FromConfiguration"/>: the credential's
    /// default location is derived from that key, and a key that cannot address a secret cannot open a
    /// connection.
    /// </remarks>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="gate">The provisioning gate; a closed gate yields no servers at all.</param>
    /// <param name="logger">
    /// Where a declared-but-inert <c>ForeignDirectory</c> is reported (see <see cref="SshBackupServer.ForeignDirectory"/>'s
    /// remarks). Optional so every existing call site keeps compiling; a caller that omits it gets a server
    /// object exactly as before, just without the warning.
    /// </param>
    public static SshBackupWiringOptions FromConfiguration(
        IConfiguration configuration, ProvisioningGate gate, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(gate);

        if (!gate.Enabled)
        {
            return None;
        }

        var servers = new List<SshBackupServer>();

        foreach (var server in configuration.GetSection(SectionKey).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(server.Key) || !SecretUrn.IsValidSegment(server.Key))
            {
                continue;
            }

            var ssh = server.GetSection(SshKey);

            // Fail-closed, exactly like SshDockerWriteModes.ReadGrants and RconWiringOptions: absent,
            // misspelled and explicitly false all mean "not an SSH-hosted server", and are all spelled the
            // same way here.
            if (!bool.TryParse(ssh["Enabled"], out var enabled) || !enabled)
            {
                continue;
            }

            var host = ssh["Host"];
            var root = ssh["Root"];

            // The two values nothing can be inferred from. A default host would archive some other machine;
            // a default root would archive some other directory. Both silently.
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var storeDirectory = string.IsNullOrWhiteSpace(ssh["StoreDirectory"])
                ? DefaultStoreDirectory
                : ssh["StoreDirectory"]!.Trim('/');

            // Root-relative, exactly like StoreDirectory above: nothing can be inferred, so an unset value
            // means "no foreign directory declared" (None below), never a guessed default like "backups".
            var foreignDirectory = string.IsNullOrWhiteSpace(ssh["ForeignDirectory"])
                ? null
                : ssh["ForeignDirectory"]!.Trim('/');
            var foreignPattern = string.IsNullOrWhiteSpace(ssh["ForeignPattern"])
                ? DefaultForeignPattern
                : ssh["ForeignPattern"]!.Trim();

            // Naming a directory is not enough to make its archives appear: SshBackupProvider only ever
            // surfaces what a registered IBackupAdopter discovers inside a declared directory, and this
            // project registers none for SSH (see AddServyxSshBackups()'s remarks — a generic SSH host
            // ships no convention Servyx could adopt on its own say-so). Without this warning, an operator
            // who sets ForeignDirectory would see nothing adopted and no error at all — a silent-failure
            // shape this codebase otherwise goes out of its way to avoid.
            if (foreignDirectory is not null)
            {
                logger?.LogWarning(
                    "'{Section}:{ServerKey}:{SshKey}:ForeignDirectory' names '{Directory}', but no " +
                    "IBackupAdopter is registered for SSH-hosted backups, so nothing in it will be adopted " +
                    "or listed. The directory is declared and ready for a future host-specific adopter; on " +
                    "its own it does nothing.",
                    SectionKey, server.Key, SshKey, foreignDirectory);
            }

            servers.Add(new SshBackupServer(
                server.Key,
                host.Trim(),
                ReadCredentialUrn(ssh["CredentialUrn"], server.Key),
                Normalize(root),
                ReadList(ssh.GetSection("Include")),
                ReadList(ssh.GetSection("Exclude")),
                storeDirectory,
                string.IsNullOrWhiteSpace(ssh["DeploymentKind"]) ? DefaultDeploymentKind : ssh["DeploymentKind"]!.Trim(),
                new RetentionPolicy(
                    ReadCount(ssh["KeepHourly"]) ?? BackupWiringOptions.FallbackRetention.KeepHourly,
                    ReadCount(ssh["KeepDaily"]) ?? BackupWiringOptions.FallbackRetention.KeepDaily,
                    ReadCount(ssh["KeepWeekly"]) ?? BackupWiringOptions.FallbackRetention.KeepWeekly),
                Enum.TryParse<WriteMode>(server[ServerWriteModes.WriteModeKey], ignoreCase: true, out var mode)
                    && mode != WriteMode.ReadOnly,
                foreignDirectory,
                foreignPattern));
        }

        return servers.Count == 0 ? None : new SshBackupWiringOptions(servers);
    }

    /// <summary>
    /// Resolves the credential locator: the configured URN when it parses, otherwise the conventional
    /// <c>secret://server/&lt;key&gt;/ssh/password</c>. A configured value that is not a well-formed
    /// <see cref="SecretUrn"/> yields <see langword="null"/> rather than the convention, because an operator
    /// who named a locator meant a specific one and quietly substituting a different one would authenticate
    /// as somebody else.
    /// </summary>
    private static SecretUrn? ReadCredentialUrn(string? configured, string serverKey)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return SecretUrn.Create(SecretScope, serverKey, SecretCategory, SecretName);
        }

        return SecretUrn.TryParse(configured.Trim(), out var urn) ? urn : null;
    }

    private static string Normalize(string root)
    {
        var normalized = root.Trim().Replace('\\', '/').TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    private static IReadOnlyList<string> ReadList(IConfigurationSection section) =>
    [
        .. section.GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim()),
    ];

    private static int? ReadCount(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0
            ? count
            : null;
}
