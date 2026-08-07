using NSubstitute;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;
using Servyx.Infrastructure.Ssh.Tests.Provisioning;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Unit tests for <see cref="SshDockerTransport"/>. Follows the house pattern: the SSH host is a substituted
/// <see cref="ITransport"/>/<see cref="IExecutionTarget"/> pair (see <see cref="SshHostDouble"/>), so no live
/// SSH server or docker daemon is involved anywhere.
/// </summary>
public class SshDockerTransportTests
{
    private static readonly IReadOnlyDictionary<string, string> Options =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["container"] = "palworld" };

    private static readonly TargetDescriptor Descriptor = new(
        TransportId: "ssh+docker",
        Endpoint: "steam@palworld-host.internal:22",
        CredentialUrn: "urn:servyx:secret:ssh-key",
        DockerContext: "desktop-linux",
        Options: Options);

    private const string DockerVersionJson =
        """{"Client":{"Version":"27.3.1"},"Server":{"Platform":{"Name":"Docker Engine - Community"},"Version":"27.3.1"}}""";

    [Fact]
    public void Transport_id_is_ssh_plus_docker()
    {
        var host = new SshHostDouble();
        var transport = new SshDockerTransport(host.Transport);

        transport.TransportId.Should().Be("ssh+docker");
    }

    [Fact]
    public async Task Connect_delegates_to_the_inner_ssh_transport_with_the_transport_id_rewritten()
    {
        var host = new SshHostDouble();
        var transport = new SshDockerTransport(host.Transport);

        await transport.ConnectAsync(Descriptor);

        host.Connected.Should().ContainSingle();
        var connected = host.Connected[0];
        connected.TransportId.Should().Be("ssh");
        connected.Endpoint.Should().Be(Descriptor.Endpoint);
        connected.CredentialUrn.Should().Be(Descriptor.CredentialUrn);
        connected.DockerContext.Should().Be(Descriptor.DockerContext);
        connected.Options.Should().BeEquivalentTo(Descriptor.Options);
    }

    [Fact]
    public async Task Connect_wraps_the_inner_session_in_a_lifecycle_capable_decorator()
    {
        // The session is no longer the bare inner session: it is an SshDockerLifecycleSession decorating it,
        // adding an IContainerLifecycle channel while still forwarding every IExecutionTarget call through
        // (see Rewriting_the_descriptor_preserves_command_intent, which proves the forwarding half).
        var host = new SshHostDouble();
        var transport = new SshDockerTransport(host.Transport);

        var session = await transport.ConnectAsync(Descriptor);

        session.Should().BeOfType<SshDockerLifecycleSession>();
        session.Should().BeAssignableTo<IContainerLifecycle>();
    }

    [Fact]
    public async Task Probe_reports_healthy_when_docker_version_exits_zero()
    {
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(0, DockerVersionJson, string.Empty, TimeSpan.FromMilliseconds(5)),
        };
        var transport = new SshDockerTransport(host.Transport);

        var health = await transport.ProbeAsync(Descriptor);

        health.Reachable.Should().BeTrue();
        health.Detail.Should().Contain("27.3.1");
    }

    [Fact]
    public async Task Probe_reports_unusable_when_docker_version_exits_127()
    {
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(127, string.Empty, "bash: docker: command not found", TimeSpan.Zero),
        };
        var transport = new SshDockerTransport(host.Transport);

        var health = await transport.ProbeAsync(Descriptor);

        health.Reachable.Should().BeFalse();
        health.Detail.Should().Contain("not found");
    }

    [Fact]
    public async Task Probe_reports_unusable_when_docker_version_exits_126()
    {
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(126, string.Empty, "docker: permission denied", TimeSpan.Zero),
        };
        var transport = new SshDockerTransport(host.Transport);

        var health = await transport.ProbeAsync(Descriptor);

        health.Reachable.Should().BeFalse();
        health.Detail.Should().Contain("permission denied");
        health.Detail.Should().Contain("docker");
        health.Detail.Should().NotContain("not found");
    }

    [Fact]
    public async Task Probe_reports_unreachable_when_the_inner_transport_throws()
    {
        var host = new SshHostDouble();
        host.Transport
            .ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns<IExecutionTarget>(_ => throw new InvalidOperationException("connection refused"));
        var transport = new SshDockerTransport(host.Transport);

        var health = await transport.ProbeAsync(Descriptor);

        health.Reachable.Should().BeFalse();
        health.Detail.Should().Contain("SSH host unreachable");
        health.Detail.Should().Contain("connection refused");
    }

    [Fact]
    public async Task Probe_truncates_long_stderr()
    {
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(1, string.Empty, new string('e', 5000), TimeSpan.Zero),
        };
        var transport = new SshDockerTransport(host.Transport);

        var health = await transport.ProbeAsync(Descriptor);

        health.Reachable.Should().BeFalse();
        health.Detail.Should().NotBeNull();
        health.Detail!.Length.Should().BeLessThan(300);
    }

    [Fact]
    public async Task Rewriting_the_descriptor_preserves_command_intent()
    {
        var host = new SshHostDouble();
        var transport = new SshDockerTransport(host.Transport);
        var session = await transport.ConnectAsync(Descriptor);

        await session.ExecuteAsync(DockerCli.Stop("palworld", 10));

        var recorded = host.Commands.Should().ContainSingle().Subject;
        recorded.Arguments.Should().Contain("stop");
        recorded.Intent.Should().Be(CommandIntent.Mutating);
    }

    // ── container-scoped file access ────────────────────────────────────────────────────────────────
    //
    // This transport's exec plane is container-correct (DockerCli names the container in the argv) while its
    // FILE plane is SFTP against the SSH host's own root — SftpFileChannel resolves a TargetPath to
    // "/" + path.Value and has no notion of a container at all. A descriptor carrying 'rootPath' asks for
    // paths relative to a root INSIDE the container, which this transport therefore cannot serve; honouring
    // it silently strips the container root, so a Docker-backup capture reads nothing (and reports success)
    // while a restore writes the archive's bytes onto real host paths.

    private static TargetDescriptor ContainerRooted(string rootPath = "/palworld") => Descriptor with
    {
        Options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["containerName"] = "palworld-server",
            ["rootPath"] = rootPath,
        },
    };

    [Fact]
    public void Capabilities_do_not_advertise_container_scoped_files()
    {
        var transport = new SshDockerTransport(new SshHostDouble().Transport);

        transport.Capabilities.Should().NotHaveFlag(TransportCapabilities.ContainerScopedFiles,
            "files reached through this transport are the SSH HOST's, not the container's — declaring "
            + "otherwise is what would let the Docker backup pipeline misroute onto the host filesystem");

        transport.Capabilities.Should().HaveFlag(TransportCapabilities.ContainerApi,
            "the control plane genuinely is container-addressed; ContainerApi and ContainerScopedFiles are "
            + "independent claims and this transport holds exactly the first");
    }

    [Fact]
    public async Task Connect_refuses_a_container_rooted_descriptor_instead_of_serving_host_paths()
    {
        var host = new SshHostDouble();
        var transport = new SshDockerTransport(host.Transport);

        var act = async () => await transport.ConnectAsync(ContainerRooted());

        var assertion = await act.Should().ThrowAsync<ContainerScopedFilesNotSupportedException>(
            "a session rooted inside the container cannot be served over SFTP on the host, and silently "
            + "serving it is how an empty capture or a host-filesystem restore happens");

        assertion.Which.TransportId.Should().Be("ssh+docker");
        assertion.Which.ContainerRef.Should().Be("palworld-server");
        assertion.Which.ContainerRootPath.Should().Be("/palworld");

        host.Connected.Should().BeEmpty("the refusal must happen before any SSH connection is opened");
    }

    [Fact]
    public async Task Connect_still_serves_a_lifecycle_descriptor_that_names_no_container_root()
    {
        // The refusal is scoped to the file plane and nothing else. Every descriptor this transport is
        // actually wired for — lifecycle (ServyxServerLifecycles), discovery, logs, metrics, and
        // RCON-over-'docker exec' — names its container in the argv and carries no 'rootPath'.
        var host = new SshHostDouble();
        var transport = new SshDockerTransport(host.Transport);

        var session = await transport.ConnectAsync(Descriptor);

        session.Should().BeAssignableTo<IContainerLifecycle>();
        host.Connected.Should().ContainSingle();
    }

    [Fact]
    public async Task An_empty_root_path_option_is_not_treated_as_a_container_root()
    {
        var host = new SshHostDouble();
        var transport = new SshDockerTransport(host.Transport);

        await transport.ConnectAsync(ContainerRooted(rootPath: "   "));

        host.Connected.Should().ContainSingle(
            "only a descriptor that genuinely names an in-container root is refused");
    }

    [Fact]
    public async Task Probe_of_a_container_rooted_descriptor_reports_unreachable_rather_than_throwing()
    {
        // ProbeAsync connects, so the refusal reaches it too. Health reporting must degrade to an honest
        // "unusable" rather than propagating — but it must never report reachable.
        var host = new SshHostDouble();
        var transport = new SshDockerTransport(host.Transport);

        var health = await transport.ProbeAsync(ContainerRooted());

        health.Reachable.Should().BeFalse();
    }

    [Fact]
    public async Task Probe_executes_a_read_only_command()
    {
        var host = new SshHostDouble();
        var transport = new SshDockerTransport(host.Transport);

        await transport.ProbeAsync(Descriptor);

        var recorded = host.Commands.Should().ContainSingle().Subject;
        recorded.Executable.Should().Be("docker");
        recorded.Arguments.Should().Contain("version");
        recorded.Intent.Should().Be(CommandIntent.ReadOnly);
    }
}
