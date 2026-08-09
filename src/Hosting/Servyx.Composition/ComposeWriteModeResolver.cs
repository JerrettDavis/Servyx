using Servyx.Domain.Transport;

namespace Servyx.Composition;

/// <summary>
/// Resolves a compose-directory session's write posture from the same, per-server grants that already
/// govern that server's Docker session — never an independent decision scoped only to the compose
/// directory.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> <see cref="BackupWiringOptions.ComposeDirectory"/> is one directory
/// shared by the whole process, but write permission is granted per server — today from the operator's
/// per-server grant row, resolved live by <see cref="DbBackedWriteModeResolver"/>; the old
/// <c>Servyx:Servers:&lt;name&gt;:WriteMode</c> key no longer grants anything to a server Servyx tracks.
/// Wrapping <c>LocalProcessTransport</c> in a
/// <c>WriteGuardedTransport</c> carrying a static, unconditional <see cref="WriteMode.Enabled"/> grant for
/// the compose directory would let a restore overwrite a <see cref="WriteMode.ReadOnly"/> server's
/// <c>.env</c>/<c>compose.yaml</c> — the write guard's whole promise is per-server, and a directory-scoped
/// grant with no server check breaks it. This resolver closes that gap by re-asking the SAME
/// <see cref="IWriteModeResolver"/> the shared Docker <see cref="ITransport"/> already consults, for the
/// specific server named on the descriptor it is handed.
/// </para>
/// <para>
/// <see cref="ServyxBackupContextSource.GetAsync"/> stamps <c>containerId</c> and <c>containerName</c>
/// options onto the compose session's <see cref="TargetDescriptor"/> for exactly this reason — the same
/// option keys the Docker session's own descriptor carries — so this resolver can translate "is the compose
/// session for server X writable" into "is server X's Docker session writable" without a second,
/// independently-configured knob. A descriptor carrying neither resolves <see cref="WriteMode.ReadOnly"/>,
/// the same fail-closed default <see cref="IWriteModeResolver"/> documents for any target it cannot
/// identify; one carrying only a name is attributable but still cannot satisfy a database-backed grant,
/// which is keyed on the container id alone.
/// </para>
/// </remarks>
public sealed class ComposeWriteModeResolver : IWriteModeResolver
{
    /// <summary>
    /// The transport id the re-asked descriptor is stamped with — the one
    /// <see cref="DbBackedWriteModeResolver"/> resolves per-server grants for.
    /// </summary>
    /// <remarks>
    /// Hardcoded, which is an open question rather than a known-correct choice: it routes every compose
    /// session to the database-backed path. That is right while compose sessions are always local, and would
    /// be wrong for an SSH-hosted one, which would then be resolved against a local container id. Nobody has
    /// established which sources can feed this resolver, so nothing is being changed on speculation — see
    /// <c>docs/plans/ui-management-surface.md</c>'s open-questions section.
    /// </remarks>
    public const string DockerTransportId = "docker";

    /// <summary>The descriptor option key naming the server by name. Carried for refusal messages and for the
    /// configured-grant path; a name alone can never satisfy a database-backed grant — see <see cref="ContainerIdOption"/>.</summary>
    public const string ContainerNameOption = "containerName";

    /// <summary>
    /// The descriptor option key naming the server by its container <em>id</em> — the identity a per-server
    /// write grant is actually keyed on. <c>ServyxBackupContextSource</c> stamps it onto the compose
    /// session's descriptor for exactly this reason; a compose session that carries only a container name
    /// resolves read-only, which is the fail-closed direction.
    /// </summary>
    public const string ContainerIdOption = "containerId";

    private readonly IWriteModeResolver _perServerGrants;

    /// <summary>Creates a resolver over <paramref name="perServerGrants"/>.</summary>
    /// <param name="perServerGrants">
    /// The resolver holding every registered <c>WriteModeGrant</c> — the same instance handed to the shared
    /// Docker <see cref="ITransport"/>, so the two never disagree about one server's posture.
    /// </param>
    public ComposeWriteModeResolver(IWriteModeResolver perServerGrants)
    {
        ArgumentNullException.ThrowIfNull(perServerGrants);
        _perServerGrants = perServerGrants;
    }

    /// <inheritdoc />
    public WriteMode Resolve(TargetDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        if (target.Options.TryGetValue(ContainerIdOption, out var containerId) && !string.IsNullOrWhiteSpace(containerId))
        {
            options[ContainerIdOption] = containerId;
        }

        if (target.Options.TryGetValue(ContainerNameOption, out var containerName) && !string.IsNullOrWhiteSpace(containerName))
        {
            options[ContainerNameOption] = containerName;
        }

        // Fail closed: a compose session this resolver cannot attribute to a server must never be writable.
        // Note that carrying only a name is enough to be *attributed*, but not enough to satisfy a
        // database-backed grant — the inner resolver decides that, exactly as it would for the Docker session
        // itself, so the two can never disagree.
        if (options.Count == 0)
        {
            return WriteMode.ReadOnly;
        }

        return _perServerGrants.Resolve(new TargetDescriptor(
            DockerTransportId,
            target.Endpoint,
            target.CredentialUrn,
            target.DockerContext,
            options));
    }
}
