using Docker.DotNet;
using Docker.DotNet.Models;
using NSubstitute;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;

namespace Servyx.Infrastructure.Docker.Tests;

public class DockerTransportTests
{
    private static TargetDescriptor Target(IReadOnlyDictionary<string, string>? options = null) =>
        new("docker", "npipe://./pipe/dockerDesktopLinuxEngine", null, null, options ?? new Dictionary<string, string>());

    [Fact]
    public void TransportId_is_docker()
    {
        var transport = new DockerTransport();

        transport.TransportId.Should().Be("docker");
    }

    [Fact]
    public void Capabilities_does_not_advertise_unimplemented_write_or_exec_support()
    {
        var transport = new DockerTransport();

        transport.Capabilities.Should().Be(
            TransportCapabilities.FileRead | TransportCapabilities.DirectoryList | TransportCapabilities.ContainerApi);
        transport.Capabilities.Should().NotHaveFlag(TransportCapabilities.ExecuteCommand);
        transport.Capabilities.Should().NotHaveFlag(TransportCapabilities.FileWrite);
        transport.Capabilities.Should().NotHaveFlag(TransportCapabilities.StreamStdin);
        transport.Capabilities.Should().NotHaveFlag(TransportCapabilities.PortForward);
    }

    [Fact]
    public async Task ProbeAsync_returns_reachable_health_when_the_daemon_responds()
    {
        var client = Substitute.For<IDockerClient>();
        var system = Substitute.For<ISystemOperations>();
        client.System.Returns(system);
        system.GetVersionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new VersionResponse
        {
            Version = "27.0.0",
            APIVersion = "1.46",
            Os = "linux",
            Arch = "amd64",
            KernelVersion = "5.15.0",
        }));

        var factory = Substitute.For<IDockerClientFactory>();
        factory.Create(Arg.Any<Uri>()).Returns(client);

        var transport = new DockerTransport(factory);

        var health = await transport.ProbeAsync(Target());

        health.Reachable.Should().BeTrue();
        health.Latency.Should().NotBeNull();
        health.Detail.Should().Contain("27.0.0");
    }

    [Fact]
    public async Task ProbeAsync_never_throws_and_reports_unreachable_on_failure()
    {
        var client = Substitute.For<IDockerClient>();
        var system = Substitute.For<ISystemOperations>();
        client.System.Returns(system);
        system.GetVersionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<VersionResponse>(new HttpRequestException("connection refused")));

        var factory = Substitute.For<IDockerClientFactory>();
        factory.Create(Arg.Any<Uri>()).Returns(client);

        var transport = new DockerTransport(factory);

        var health = await transport.ProbeAsync(Target());

        health.Reachable.Should().BeFalse();
        health.Latency.Should().BeNull();
        health.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ConnectAsync_resolves_container_id_from_options()
    {
        var client = Substitute.For<IDockerClient>();
        var factory = Substitute.For<IDockerClientFactory>();
        factory.Create(Arg.Any<Uri>()).Returns(client);

        var transport = new DockerTransport(factory);
        var target = Target(new Dictionary<string, string> { ["containerId"] = "abc123" });

        await using var executionTarget = await transport.ConnectAsync(target);

        executionTarget.Should().NotBeNull();
    }

    [Fact]
    public void ResolveContainerRef_throws_when_no_container_option_is_present()
    {
        var target = Target();

        var act = () => DockerTransport.ResolveContainerRef(target);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("containerId")]
    [InlineData("containerName")]
    [InlineData("container")]
    public void ResolveContainerRef_accepts_any_recognised_option_key(string key)
    {
        var target = Target(new Dictionary<string, string> { [key] = "my-container" });

        DockerTransport.ResolveContainerRef(target).Should().Be("my-container");
    }

    [Fact]
    public void ResolveContainerRootPath_defaults_to_root_when_absent()
    {
        var target = Target();

        DockerTransport.ResolveContainerRootPath(target).Should().Be("/");
    }

    [Fact]
    public void ResolveContainerRootPath_honours_explicit_option()
    {
        var target = Target(new Dictionary<string, string> { ["rootPath"] = "/palworld" });

        DockerTransport.ResolveContainerRootPath(target).Should().Be("/palworld");
    }
}
