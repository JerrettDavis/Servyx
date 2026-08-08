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
/// shared by the whole process, but write permission is granted per server via
/// <c>Servyx:Servers:&lt;name&gt;:WriteMode</c>. Wrapping <c>LocalProcessTransport</c> in a
/// <c>WriteGuardedTransport</c> carrying a static, unconditional <see cref="WriteMode.Enabled"/> grant for
/// the compose directory would let a restore overwrite a <see cref="WriteMode.ReadOnly"/> server's
/// <c>.env</c>/<c>compose.yaml</c> — the write guard's whole promise is per-server, and a directory-scoped
/// grant with no server check breaks it. This resolver closes that gap by re-asking the SAME
/// <see cref="IWriteModeResolver"/> the shared Docker <see cref="ITransport"/> already consults, for the
/// specific server named on the descriptor it is handed.
/// </para>
/// <para>
/// <see cref="ServyxBackupContextSource.GetAsync"/> stamps a <c>containerName</c> option onto the compose
/// session's <see cref="TargetDescriptor"/> for exactly this reason — the same option key
/// <c>ServerWriteModes</c> emits Docker grants against — so this resolver can translate "is the compose
/// session for server X writable" into "is server X's Docker session writable" without a second,
/// independently-configured knob. A descriptor carrying no <c>containerName</c> resolves
/// <see cref="WriteMode.ReadOnly"/>, the same fail-closed default <see cref="IWriteModeResolver"/> documents
/// for any target it cannot identify.
/// </para>
/// </remarks>
public sealed class ComposeWriteModeResolver : IWriteModeResolver
{
    /// <summary>The Docker transport id per-server grants (<c>ServerWriteModes</c>) are registered against.</summary>
    public const string DockerTransportId = "docker";

    /// <summary>The descriptor option key naming the server, mirroring <c>ServerWriteModes.ContainerOptionKeys</c>.</summary>
    public const string ContainerNameOption = "containerName";

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

        if (!target.Options.TryGetValue(ContainerNameOption, out var containerName) || string.IsNullOrWhiteSpace(containerName))
        {
            return WriteMode.ReadOnly;
        }

        return _perServerGrants.Resolve(new TargetDescriptor(
            DockerTransportId,
            target.Endpoint,
            target.CredentialUrn,
            target.DockerContext,
            new Dictionary<string, string>(StringComparer.Ordinal) { [ContainerNameOption] = containerName }));
    }
}
