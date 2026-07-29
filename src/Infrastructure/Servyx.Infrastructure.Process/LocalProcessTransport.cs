using System.Diagnostics;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process;

/// <summary>
/// <see cref="ITransport"/> implementation reaching a workload that runs directly on the machine Servyx is
/// running on — the "local process execution" implementation <see cref="ITransport"/>'s own remarks name
/// alongside local Docker, SSH, and Docker-CLI-over-SSH.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A local target has nothing to connect to, and that shapes both methods below.</strong>
/// <see cref="ProbeAsync"/> answers the only question a local target can meaningfully be asked — does the
/// configured root exist, and can this process read it — using directory metadata reads alone: it creates no
/// file, opens nothing for write, and starts no process, as <see cref="ITransport.ProbeAsync"/>'s
/// side-effect-free requirement demands. <see cref="ConnectAsync"/> deliberately does <em>not</em> require the
/// root to exist: there is no session to establish, and a provisioner legitimately opens a session against the
/// directory it is about to create. Reachability is <see cref="ProbeAsync"/>'s answer to give, not a
/// precondition of holding a session.
/// </para>
/// <para>
/// <strong>Which root a session is sandboxed to comes from the descriptor.</strong> The <c>rootPath</c>
/// option is the same key <c>DockerTransport</c> and <c>SshProcessProvisioner</c> already use, so a
/// descriptor produced by <see cref="Provisioning.LocalProcessProvisioner"/> is consumed here with no
/// translation step.
/// </para>
/// </remarks>
public sealed class LocalProcessTransport : ITransport
{
    /// <summary>
    /// The stable <see cref="ITransport.TransportId"/> of this transport — one of the four values
    /// <see cref="TargetDescriptor.TransportId"/> documents.
    /// </summary>
    public const string Id = "local";

    /// <summary>The <see cref="TargetDescriptor.Options"/> key naming the directory a session is sandboxed to.</summary>
    public const string RootPathOption = "rootPath";

    private readonly TimeSpan _defaultCommandTimeout;

    /// <summary>Creates a <see cref="LocalProcessTransport"/>.</summary>
    /// <param name="defaultCommandTimeout">
    /// Applied to a <see cref="CommandSpec"/> that does not specify its own <see cref="CommandSpec.Timeout"/>.
    /// Defaults to <see cref="LocalExecutionTarget.DefaultCommandTimeout"/>.
    /// </param>
    public LocalProcessTransport(TimeSpan? defaultCommandTimeout = null) =>
        _defaultCommandTimeout = defaultCommandTimeout ?? LocalExecutionTarget.DefaultCommandTimeout;

    /// <inheritdoc />
    public string TransportId => Id;

    /// <inheritdoc />
    /// <remarks>
    /// Declares only what <see cref="LocalExecutionTarget"/> actually implements.
    /// <see cref="TransportCapabilities.StreamStdin"/> is omitted because
    /// <see cref="IExecutionTarget"/> exposes no way to feed a running command's stdin, so
    /// <see cref="ProcessStartInfo.RedirectStandardInput"/> is left off rather than opening a pipe nothing can
    /// write to. <see cref="TransportCapabilities.ContainerApi"/> is omitted because a host process is not a
    /// container. <see cref="TransportCapabilities.PortForward"/> is omitted because a target on this machine
    /// has nothing to forward through — its ports are already the panel's ports, and claiming the capability
    /// would let a caller believe a tunnel had been established when none had.
    /// </remarks>
    public TransportCapabilities Capabilities =>
        TransportCapabilities.ExecuteCommand |
        TransportCapabilities.StreamOutput |
        TransportCapabilities.FileRead |
        TransportCapabilities.FileWrite |
        TransportCapabilities.DirectoryList |
        TransportCapabilities.ProcessApi;

    /// <inheritdoc />
    /// <remarks>
    /// Side-effect free by construction: the only calls made are <see cref="Directory.Exists(string)"/> and a
    /// single step of <see cref="Directory.EnumerateFileSystemEntries(string)"/>, which reads one directory
    /// entry in order to distinguish "the directory is there" from "the directory is there but this process
    /// cannot read it". Nothing is created, opened for write, or executed.
    /// </remarks>
    public Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var root = Path.GetFullPath(ResolveRootPath(target));

            if (!Directory.Exists(root))
            {
                return Task.FromResult(new TargetHealth(false, null, $"Local root path '{root}' does not exist."));
            }

            using (var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator())
            {
                entries.MoveNext();
            }

            stopwatch.Stop();
            return Task.FromResult(new TargetHealth(true, stopwatch.Elapsed, $"Local root path '{root}' exists and is readable."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return Task.FromResult(new TargetHealth(false, null, $"Local target unreachable: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult<IExecutionTarget>(new LocalExecutionTarget(ResolveRootPath(target), _defaultCommandTimeout));
    }

    /// <summary>
    /// Reads the directory a session against <paramref name="target"/> is sandboxed to: the
    /// <see cref="RootPathOption"/> option when present, otherwise <see cref="TargetDescriptor.Endpoint"/>
    /// when the endpoint is itself a fully-qualified path.
    /// </summary>
    /// <remarks>
    /// Two accepted forms rather than one, for the same reason <c>DockerTransport</c> accepts three container
    /// option spellings: a hand-written local target reads most naturally as an endpoint that simply is the
    /// directory, while a provisioned one carries a machine identifier as its endpoint and the data directory
    /// as <c>rootPath</c> — matching the option key the Docker and SSH adapters already stamp. A descriptor
    /// carrying neither is rejected loudly rather than silently defaulting to the process's current directory,
    /// which would sandbox a session to wherever Servyx happened to be launched from.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="target"/> names no usable root path.</exception>
    public static string ResolveRootPath(TargetDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Options.TryGetValue(RootPathOption, out var configured) && !string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (!string.IsNullOrWhiteSpace(target.Endpoint) && Path.IsPathFullyQualified(target.Endpoint))
        {
            return target.Endpoint;
        }

        throw new ArgumentException(
            $"A '{Id}' target needs a '{RootPathOption}' option naming the directory the session is sandboxed to, " +
            $"or an endpoint that is itself a fully-qualified directory path. '{target.Endpoint}' is neither.",
            nameof(target));
    }
}
