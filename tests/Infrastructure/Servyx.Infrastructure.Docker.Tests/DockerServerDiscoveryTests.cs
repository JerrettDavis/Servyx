using Docker.DotNet;
using Docker.DotNet.Models;
using NSubstitute;
using Servyx.Infrastructure.Docker;

namespace Servyx.Infrastructure.Docker.Tests;

public class DockerServerDiscoveryTests
{
    private const string ExpectedRepo = "thijsvanloef/palworld-server-docker";
    private const string RequiredMount = "/palworld";

    [Theory]
    [InlineData("thijsvanloef/palworld-server-docker:latest")]
    [InlineData("thijsvanloef/palworld-server-docker")]
    [InlineData("thijsvanloef/palworld-server-docker:v1.2.3")]
    public void ImageRepositoryMatches_ignores_tag(string reference)
    {
        DockerServerDiscovery.ImageRepositoryMatches(reference, ExpectedRepo).Should().BeTrue();
    }

    [Fact]
    public void ImageRepositoryMatches_matches_digest_pinned_reference()
    {
        const string reference = "thijsvanloef/palworld-server-docker@sha256:" + "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9";

        DockerServerDiscovery.ImageRepositoryMatches(reference, ExpectedRepo).Should().BeTrue();
    }

    [Fact]
    public void ImageRepositoryMatches_matches_tag_and_digest_combined()
    {
        const string reference = "thijsvanloef/palworld-server-docker:latest@sha256:" + "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9";

        DockerServerDiscovery.ImageRepositoryMatches(reference, ExpectedRepo).Should().BeTrue();
    }

    [Fact]
    public void ImageRepositoryMatches_respects_registry_host_port_without_treating_it_as_a_tag()
    {
        const string reference = "myregistry.example.com:5000/thijsvanloef/palworld-server-docker:latest";

        DockerServerDiscovery.ImageRepositoryMatches(reference, "myregistry.example.com:5000/thijsvanloef/palworld-server-docker")
            .Should().BeTrue();
    }

    [Fact]
    public void ImageRepositoryMatches_rejects_a_different_repository()
    {
        DockerServerDiscovery.ImageRepositoryMatches("someoneelse/other-image:latest", ExpectedRepo).Should().BeFalse();
    }

    [Fact]
    public void ImageRepositoryMatches_rejects_null_or_empty_reference()
    {
        DockerServerDiscovery.ImageRepositoryMatches(null, ExpectedRepo).Should().BeFalse();
        DockerServerDiscovery.ImageRepositoryMatches("", ExpectedRepo).Should().BeFalse();
    }

    [Fact]
    public void Matches_accepts_container_with_correct_image_and_required_mount()
    {
        var container = ContainerWith(image: "thijsvanloef/palworld-server-docker:latest", mountDestination: "/palworld");

        DockerServerDiscovery.Matches(container, ExpectedRepo, RequiredMount).Should().BeTrue();
    }

    [Fact]
    public void Matches_rejects_correct_image_but_missing_required_mount()
    {
        var container = ContainerWith(image: "thijsvanloef/palworld-server-docker:latest", mountDestination: "/some/other/path");

        DockerServerDiscovery.Matches(container, ExpectedRepo, RequiredMount).Should().BeFalse();
    }

    [Fact]
    public void Matches_rejects_correct_mount_but_wrong_image()
    {
        var container = ContainerWith(image: "someoneelse/other-image:latest", mountDestination: "/palworld");

        DockerServerDiscovery.Matches(container, ExpectedRepo, RequiredMount).Should().BeFalse();
    }

    [Fact]
    public void Matches_rejects_container_with_no_mounts_at_all()
    {
        var container = new ContainerListResponse
        {
            ID = "abc123",
            Image = "thijsvanloef/palworld-server-docker:latest",
            Mounts = new List<MountPoint>(),
        };

        DockerServerDiscovery.Matches(container, ExpectedRepo, RequiredMount).Should().BeFalse();
    }

    [Fact]
    public async Task DiscoverAsync_returns_only_matching_containers_among_multiple_candidates()
    {
        var matching = ContainerWith(
            id: "match-1",
            image: "thijsvanloef/palworld-server-docker:latest",
            mountDestination: "/palworld",
            names: ["/palworld-server"]);

        var wrongImage = ContainerWith(id: "no-match-1", image: "someoneelse/other-image:latest", mountDestination: "/palworld");
        var wrongMount = ContainerWith(id: "no-match-2", image: "thijsvanloef/palworld-server-docker:latest", mountDestination: "/data");

        var secondMatching = ContainerWith(
            id: "match-2",
            image: "thijsvanloef/palworld-server-docker@sha256:deadbeef",
            mountDestination: "/palworld",
            names: ["/palworld-server-2"]);

        var (client, containers) = CreateClientSubstitute();
        containers.ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<ContainerListResponse>>([matching, wrongImage, wrongMount, secondMatching]));

        containers.InspectContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(InspectFor(callInfo.ArgAt<string>(0))));

        var discovery = new DockerServerDiscovery(client);

        var results = await discovery.DiscoverAsync(ExpectedRepo, RequiredMount);

        results.Should().HaveCount(2);
        results.Select(r => r.ContainerId).Should().BeEquivalentTo(["match-1", "match-2"]);
    }

    [Fact]
    public async Task DiscoverAsync_maps_full_container_detail()
    {
        var container = new ContainerListResponse
        {
            ID = "container-id-123",
            Names = ["/palworld-server"],
            Image = "thijsvanloef/palworld-server-docker:latest",
            ImageID = "sha256:resolveddigest",
            State = "running",
            Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Ports =
            [
                new Port { PrivatePort = 8211, PublicPort = 8211, Type = "udp" },
                new Port { PrivatePort = 27015, PublicPort = 27015, Type = "udp" },
                new Port { PrivatePort = 25575, PublicPort = 0, Type = "tcp" },
            ],
            Mounts =
            [
                new MountPoint { Source = @"D:\Games\Palworld\data", Destination = "/palworld", RW = true },
            ],
            Labels = new Dictionary<string, string>
            {
                ["com.docker.compose.project"] = "palworld",
                ["com.docker.compose.project.config_files"] = "D:\\Games\\Palworld\\compose.yaml",
                ["com.docker.compose.project.working_dir"] = "D:\\Games\\Palworld",
                ["com.docker.compose.service"] = "palworld-server",
                ["unrelated.label"] = "ignored",
            },
        };

        var inspect = new ContainerInspectResponse
        {
            ID = "container-id-123",
            Name = "/palworld-server",
            State = new ContainerState { Status = "running", Health = new Health { Status = "unhealthy" }, StartedAt = "2026-01-01T00:05:00Z" },
            HostConfig = new HostConfig { Memory = 8_000_000_000L, NanoCPUs = 4_000_000_000L, RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped } },
            NetworkSettings = new NetworkSettings
            {
                Networks = new Dictionary<string, EndpointSettings>
                {
                    ["palworld_default"] = new EndpointSettings { IPAddress = "172.19.0.2" },
                },
            },
        };

        var (client, containers) = CreateClientSubstitute();
        containers.ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<ContainerListResponse>>([container]));
        containers.InspectContainerAsync("container-id-123", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(inspect));

        var discovery = new DockerServerDiscovery(client);

        var results = await discovery.DiscoverAsync(ExpectedRepo, RequiredMount);

        results.Should().HaveCount(1);
        var result = results[0];
        result.ContainerId.Should().Be("container-id-123");
        result.ContainerName.Should().Be("palworld-server");
        result.Image.Should().Be("thijsvanloef/palworld-server-docker:latest");
        result.ImageDigest.Should().Be("sha256:resolveddigest");
        result.State.Should().Be("running");
        result.HealthStatus.Should().Be("unhealthy");
        result.State.Should().NotBe(result.HealthStatus, "Docker health must be tracked as a signal distinct from container state");
        result.PublishedPorts.Should().Contain(p => p.ContainerPort == 8211 && p.HostPort == 8211 && p.Protocol == "udp");
        result.PublishedPorts.Should().Contain(p => p.ContainerPort == 25575 && p.HostPort == null);
        result.Mounts.Should().ContainSingle(m => m.Destination == "/palworld" && m.ReadWrite);
        result.NetworkName.Should().Be("palworld_default");
        result.ContainerIp.Should().Be("172.19.0.2");
        result.MemoryLimitBytes.Should().Be(8_000_000_000L);
        result.CpuLimit.Should().Be(4.0);
        result.RestartPolicy.Should().Be("unless-stopped");
        result.ComposeLabels.Should().ContainKey("com.docker.compose.project").WhoseValue.Should().Be("palworld");
        result.ComposeLabels.Should().ContainKey("com.docker.compose.service").WhoseValue.Should().Be("palworld-server");
        result.ComposeLabels.Should().NotContainKey("unrelated.label");
    }

    private static ContainerListResponse ContainerWith(
        string image,
        string mountDestination,
        string id = "container-id",
        IList<string>? names = null) =>
        new()
        {
            ID = id,
            Names = names ?? ["/container"],
            Image = image,
            Mounts = [new MountPoint { Destination = mountDestination, Source = "/host/path", RW = true }],
            Labels = new Dictionary<string, string>(),
        };

    private static ContainerInspectResponse InspectFor(string id) => new()
    {
        ID = id,
        Name = "/" + id,
        State = new ContainerState { Status = "running" },
        HostConfig = new HostConfig(),
        NetworkSettings = new NetworkSettings(),
    };

    private static (IDockerClient Client, IContainerOperations Containers) CreateClientSubstitute()
    {
        var containers = Substitute.For<IContainerOperations>();
        var client = Substitute.For<IDockerClient>();
        client.Containers.Returns(containers);
        return (client, containers);
    }
}
