using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// <see cref="ITransport"/> implementation that manages a remote Docker container by running <c>docker</c>
/// CLI commands over an existing SSH exec channel. This is how Servyx views and manages a game server
/// container on a remote box without a direct Docker Engine API endpoint (TCP or Unix socket) exposed.
/// </summary>
/// <remarks>
/// This transport is deliberately a thin skin over <see cref="ITransport"/> "ssh": it does not open its own
/// connection, does not speak the Docker Engine API, and does not wrap the session it hands back from
/// <see cref="ConnectAsync"/>. All of the actual "docker-ness" lives in <see cref="DockerCli"/>, which builds
/// <see cref="CommandSpec"/> values (argv plus a declared <see cref="CommandIntent"/>) that are executed
/// exactly like any other SSH command. <see cref="ProbeAsync"/> is the one place this class does real work:
/// it runs <c>docker version</c> and turns the exit code and output into an honest <see cref="TargetHealth"/>.
/// </remarks>
public sealed class SshDockerTransport : ITransport
{
    private const int StderrTruncateLength = 200;

    private readonly ITransport _ssh;
    private readonly ILogger<SshDockerTransport> _logger;

    /// <summary>Creates an <see cref="SshDockerTransport"/> wrapping an inner SSH transport.</summary>
    /// <param name="sshTransport">
    /// The transport that actually opens the SSH connection. Expected to have <see cref="ITransport.TransportId"/>
    /// equal to <c>"ssh"</c>; if it is not, this is a wiring error but not a fatal one — a warning is logged
    /// and the transport is used as-is, since <see cref="ConnectAsync"/> only cares that it can honor a
    /// <see cref="TargetDescriptor"/> whose <see cref="TargetDescriptor.TransportId"/> has been rewritten to
    /// <c>"ssh"</c>.
    /// </param>
    /// <param name="loggerFactory">Optional logger factory; defaults to a no-op logger.</param>
    public SshDockerTransport(ITransport sshTransport, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(sshTransport);

        _ssh = sshTransport;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SshDockerTransport>();

        if (!string.Equals(sshTransport.TransportId, "ssh", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "SshDockerTransport was constructed with an inner transport whose TransportId is \"{InnerTransportId}\" " +
                "instead of \"ssh\"; connections will still be attempted with TransportId rewritten to \"ssh\".",
                sshTransport.TransportId);
        }
    }

    /// <inheritdoc />
    public string TransportId => "ssh+docker";

    /// <summary>
    /// The <see cref="TargetDescriptor.Options"/> key naming the root every <see cref="TargetPath"/> handed
    /// to the session is relative to. For the Docker Engine transport this is a path <em>inside</em> the
    /// container; this transport cannot honour it as one, which is what <see cref="ConnectAsync"/> refuses.
    /// </summary>
    private const string RootPathOption = "rootPath";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Mirrors <see cref="SshTransport.Capabilities"/> (the exec/file/directory surface is identical — this
    /// transport rides the same SSH session) plus <see cref="TransportCapabilities.ContainerApi"/>, since the
    /// whole point of this transport is to reach a container-oriented API — the <c>docker</c> CLI — rather
    /// than manage a bare process.
    /// </para>
    /// <para>
    /// <strong><see cref="TransportCapabilities.ContainerScopedFiles"/> is deliberately absent, and its
    /// absence is the honest answer, not an oversight.</strong> The <see cref="TransportCapabilities.FileRead"/>
    /// and <see cref="TransportCapabilities.FileWrite"/> declared above are real, but they are SFTP against
    /// the <em>SSH host's</em> filesystem — <see cref="SftpFileChannel"/> resolves a <see cref="TargetPath"/>
    /// to <c>"/" + path.Value</c> and has no notion of a container at all. Only the exec surface is
    /// container-addressed, and only because <see cref="DockerCli"/> names the container in the argv. A
    /// caller needing files inside the container must not be served here; see <see cref="ConnectAsync"/>.
    /// </para>
    /// </remarks>
    public TransportCapabilities Capabilities =>
        TransportCapabilities.ExecuteCommand |
        TransportCapabilities.StreamOutput |
        TransportCapabilities.StreamStdin |
        TransportCapabilities.FileRead |
        TransportCapabilities.FileWrite |
        TransportCapabilities.DirectoryList |
        TransportCapabilities.ProcessApi |
        TransportCapabilities.ContainerApi;

    /// <summary>
    /// Connects via the inner SSH transport and wraps its session in <see cref="SshDockerLifecycleSession"/>,
    /// which adds an <see cref="IContainerLifecycle"/> channel and forwards every other
    /// <see cref="IExecutionTarget"/> member unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This used to return the inner session unchanged, with no wrapper at all.</strong> That
    /// decision still holds for the reason it was made: <see cref="CommandSpec"/> already carries everything
    /// a caller needs to express a docker command safely — <see cref="DockerCli"/> builds the executable and
    /// argv, the declared <see cref="CommandSpec.Intent"/> travels with it untouched, and
    /// <see cref="PosixArgv"/> (used by the inner SSH exec channel) already quotes each argument
    /// individually — so a wrapper that intercepted <see cref="IExecutionTarget.ExecuteAsync"/> or
    /// special-cased docker commands would be exactly the kind of seam declared <see cref="CommandIntent"/>
    /// could quietly get lost or re-derived from argv text at.
    /// </para>
    /// <para>
    /// <strong>What changed is not that risk — it is what the wrapper is for.</strong>
    /// <see cref="SshDockerLifecycleSession"/> does not touch a single byte of the command path: every
    /// <see cref="IExecutionTarget"/> member it exposes forwards to the inner session verbatim, with no
    /// inspection, rewriting, or re-derivation of intent anywhere in it (see its own remarks, and
    /// <c>The_decorator_forwards_every_execution_target_member_unchanged</c>, which pins that by reflection).
    /// It exists purely to ADD a second, independent channel — <see cref="IContainerLifecycle.InvokeAsync"/> —
    /// for verbs (<c>docker start</c> in particular) that have no meaningful <see cref="CommandSpec"/> shape
    /// of their own to launder in the first place. A wrapper that only adds a channel, and never sits between
    /// a caller and the exec path <see cref="CommandIntent"/> already flows through, carries none of the
    /// laundering risk the original no-wrapper decision was guarding against.
    /// <para>
    /// <strong>A container-rooted descriptor is refused outright.</strong> A descriptor carrying
    /// <c>rootPath</c> is asking for a session whose <see cref="TargetPath"/> values are relative to a path
    /// <em>inside</em> the container — the contract <see cref="TransportCapabilities.ContainerScopedFiles"/>
    /// names, and the one the Docker Engine transport's <c>DockerExecutionTarget</c> honours by prefixing
    /// that root as an in-container path. This transport cannot honour it: the session below forwards every
    /// file member to <see cref="SftpFileChannel"/>, which resolves a path against the <em>SSH host's</em>
    /// root and would therefore serve <c>/palworld/Pal/Saved/x</c> as the host's <c>/Pal/Saved/x</c> — a
    /// capture that silently finds nothing, and a restore that silently writes real bytes to real host
    /// paths. Neither the write guard (which asks whether this <em>server</em> may be written to, and a
    /// restore has already answered yes) nor path containment (which keeps a path inside a root that has
    /// itself been lost) is positioned to see that, so the refusal lives here, at the one seam that can.
    /// </para>
    /// <para>
    /// Descriptors with no <c>rootPath</c> pass through untouched, which is every descriptor this transport
    /// is actually wired for: lifecycle, discovery, logs, metrics and RCON-over-<c>docker exec</c> all name
    /// their container in the argv and are container-correct through the exec channel. Only the file plane
    /// is refused, because only the file plane is wrong.
    /// </para>
    /// </remarks>
    /// <exception cref="ContainerScopedFilesNotSupportedException">
    /// <paramref name="target"/> declares a <c>rootPath</c>, i.e. asks for container-scoped file operations
    /// this transport cannot provide.
    /// </exception>
    public async Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        RefuseContainerRootedDescriptor(target);

        var session = await _ssh.ConnectAsync(target with { TransportId = "ssh" }, ct).ConfigureAwait(false);
        return new SshDockerLifecycleSession(session);
    }

    /// <summary>
    /// Refuses a descriptor that asks for file operations rooted inside the container. See
    /// <see cref="ConnectAsync"/>'s remarks for why this is a refusal rather than a best-effort translation.
    /// </summary>
    private void RefuseContainerRootedDescriptor(TargetDescriptor target)
    {
        if (!target.Options.TryGetValue(RootPathOption, out var rootPath) || string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        var containerRef = DescribeContainer(target);
        throw new ContainerScopedFilesNotSupportedException(
            $"The '{TransportId}' transport cannot open a session rooted at '{rootPath}' inside container "
            + $"'{containerRef}': it reaches files over SFTP on the SSH host, not inside the container, so "
            + $"every path under '{rootPath}' would resolve against the host's own filesystem. Refusing "
            + "rather than reading an empty capture set or writing a restore onto the host.",
            TransportId,
            containerRef,
            rootPath);
    }

    /// <summary>The container a descriptor names, for refusal messages only.</summary>
    private static string DescribeContainer(TargetDescriptor target)
    {
        foreach (var key in (string[])["containerId", "containerName", "container"])
        {
            if (target.Options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return target.Endpoint;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Connects (which itself proves SSH reachability), then runs <see cref="DockerCli.Version"/> — a
    /// read-only command — and maps the outcome honestly:
    /// <list type="bullet">
    /// <item><description>The inner connect throwing means the SSH host itself is unreachable.</description></item>
    /// <item><description>
    /// Exit code 127 means the <c>docker</c> executable was not found on the remote <c>PATH</c>.
    /// </description></item>
    /// <item><description>
    /// Exit code 126 means <c>docker</c> was found but could not be invoked — typically the SSH user is not
    /// a member of the <c>docker</c> group and lacks permission to reach the Docker socket.
    /// </description></item>
    /// <item><description>
    /// Any other non-zero exit is reported with a truncated (~200 character) excerpt of stderr, so a probe
    /// failure can never leak an unbounded amount of remote output — or secrets embedded in it — into health
    /// state.
    /// </description></item>
    /// <item><description>
    /// Exit code 0 is healthy; if stdout parses as the JSON <c>docker version</c> emits, the Docker
    /// <c>Server.Version</c> field is folded into the detail message.
    /// </description></item>
    /// </list>
    /// </remarks>
    public async Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var stopwatch = Stopwatch.StartNew();
        IExecutionTarget? session = null;
        try
        {
            session = await ConnectAsync(target, ct).ConfigureAwait(false);
            var result = await session.ExecuteAsync(DockerCli.Version(), ct).ConfigureAwait(false);
            stopwatch.Stop();

            return result.ExitCode switch
            {
                0 => new TargetHealth(true, stopwatch.Elapsed, DescribeHealthy(result.StandardOutput)),
                127 => new TargetHealth(false, stopwatch.Elapsed,
                    "docker CLI not found on remote host (exit 127): the \"docker\" executable is not on PATH."),
                126 => new TargetHealth(false, stopwatch.Elapsed,
                    "docker CLI found but could not be invoked — permission denied (exit 126): the SSH user is " +
                    "likely not a member of the \"docker\" group."),
                _ => new TargetHealth(false, stopwatch.Elapsed,
                    $"docker version failed (exit {result.ExitCode}): {Truncate(result.StandardError)}"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new TargetHealth(false, null, $"SSH host unreachable: {ex.Message}");
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string DescribeHealthy(string standardOutput)
    {
        var serverVersion = TryParseServerVersion(standardOutput);
        return serverVersion is null
            ? "Docker reachable over SSH."
            : $"Docker reachable over SSH. Server version {serverVersion}.";
    }

    /// <summary>
    /// Best-effort extraction of the <c>Server.Version</c> field from <c>docker version --format {{json .}}</c>
    /// output. Deliberately does not depend on the concurrently-authored <c>DockerInspectJson</c> type; this
    /// is a handful of lines and the two files are being written by different workers at the same time.
    /// </summary>
    private static string? TryParseServerVersion(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("Server", out var server) &&
                server.ValueKind == JsonValueKind.Object &&
                server.TryGetProperty("Version", out var version) &&
                version.ValueKind == JsonValueKind.String)
            {
                return version.GetString();
            }
        }
        catch (JsonException)
        {
            // Best-effort: an unparsable or unexpected payload just means no version is surfaced.
        }

        return null;
    }

    /// <summary>Truncates text to a bounded length so a probe failure can never leak unbounded remote output.</summary>
    private static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= StderrTruncateLength
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, StderrTruncateLength), "...");
    }
}
