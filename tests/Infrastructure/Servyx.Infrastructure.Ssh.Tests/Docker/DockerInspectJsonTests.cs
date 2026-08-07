using System.Reflection;
using Servyx.Domain.Discovery;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Verifies <see cref="DockerInspectJson"/> against real <c>docker</c> CLI output captured from the
/// production Palworld host (see <c>TestData/</c>), plus a couple of hand-built minimal-JSON fixtures for
/// edge cases the real capture doesn't exercise (a container with no healthcheck, an empty inspect array).
/// </summary>
public class DockerInspectJsonTests
{
    private static readonly string TestDataDirectory = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "TestData");

    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(TestDataDirectory, fileName));

    private static DiscoveredServer ParsePalworldInspect() =>
        DockerInspectJson.ParseInspect(ReadFixture("palworld-inspect.json"));

    [Fact]
    public void Inspect_reports_rcon_25575_as_exposed_but_not_published()
    {
        var server = ParsePalworldInspect();

        var rconPort = server.Ports.Should().ContainSingle(p => p.ContainerPort == 25575 && p.Protocol == "tcp").Subject;

        rconPort.HostPort.Should().BeNull("25575/tcp is exposed but has no host binding in the fixture");
    }

    [Fact]
    public void Inspect_maps_published_udp_ports_with_both_ipv4_and_ipv6_bindings()
    {
        var server = ParsePalworldInspect();

        var queryPortBindings = server.Ports.Where(p => p.ContainerPort == 27015 && p.Protocol == "udp").ToList();
        var gamePortBindings = server.Ports.Where(p => p.ContainerPort == 8211 && p.Protocol == "udp").ToList();

        queryPortBindings.Should().HaveCount(2, "the fixture carries one binding for 0.0.0.0 and one for [::]");
        queryPortBindings.Should().OnlyContain(p => p.HostPort == 27015);

        gamePortBindings.Should().HaveCount(2);
        gamePortBindings.Should().OnlyContain(p => p.HostPort == 8211);
    }

    [Fact]
    public void Inspect_strips_the_leading_slash_from_the_container_name()
    {
        var server = ParsePalworldInspect();

        server.Name.Should().Be("palworld-server");
    }

    [Fact]
    public void Inspect_reports_the_unhealthy_health_status()
    {
        var server = ParsePalworldInspect();

        server.HealthStatus.Should().Be("unhealthy");
    }

    [Fact]
    public void Inspect_maps_the_bind_mount_source_and_destination()
    {
        var server = ParsePalworldInspect();

        var mount = server.Mounts.Should().ContainSingle().Subject;
        mount.Source.Should().Be("/opt/palworld/data");
        mount.Destination.Should().Be("/palworld");
        mount.ReadWrite.Should().BeTrue();
    }

    [Fact]
    public void Inspect_maps_restart_policy_memory_and_cpu_limits()
    {
        var server = ParsePalworldInspect();

        server.RestartPolicy.Should().Be("unless-stopped");
        server.MemoryLimitBytes.Should().Be(8589934592L);
        server.CpuLimit.Should().Be(4.0);
    }

    [Fact]
    public void Inspect_parses_nanosecond_precision_started_at()
    {
        var server = ParsePalworldInspect();

        server.StartedAt.Should().Be(DateTimeOffset.Parse("2026-08-05T05:00:30.817174837Z"));
    }

    [Fact]
    public void Inspect_reports_the_image_and_container_id()
    {
        var server = ParsePalworldInspect();

        server.ServerId.Should().StartWith("1cae202fb534");
        server.Image.Should().Be("thijsvanloef/palworld-server-docker:latest");
        server.State.Should().Be("running");
    }

    [Fact]
    public void Inspect_without_a_health_block_does_not_throw()
    {
        const string json = """
            [
                {
                    "Id": "deadbeefcafe",
                    "Name": "/no-healthcheck",
                    "Created": "2026-01-01T00:00:00Z",
                    "State": {
                        "Status": "running",
                        "StartedAt": "2026-01-01T00:00:01Z"
                    },
                    "Config": {
                        "Image": "alpine:latest",
                        "Env": []
                    },
                    "HostConfig": {},
                    "Mounts": [],
                    "NetworkSettings": {
                        "Ports": {},
                        "Networks": {}
                    }
                }
            ]
            """;

        var act = () => DockerInspectJson.ParseInspect(json);

        act.Should().NotThrow().Which.HealthStatus.Should().Be("none");
    }

    [Fact]
    public void Inspect_with_an_empty_array_throws_a_clear_error()
    {
        var act = () => DockerInspectJson.ParseInspect("[]");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty*");
    }

    [Fact]
    public void Container_list_parses_one_object_per_line()
    {
        var entries = DockerInspectJson.ParseContainerList(ReadFixture("palworld-container-ls.jsonl"));

        var entry = entries.Should().ContainSingle().Subject;
        entry.Id.Should().Be("1cae202fb5341a59ec34c72e63fbad9c33e054ce88f5b05e37bd756b729fa81e");
        entry.Name.Should().Be("palworld-server");
        entry.Image.Should().Be("thijsvanloef/palworld-server-docker:latest");
        entry.HealthStatus.Should().Be("unhealthy");
        entry.State.Should().Be("running");
    }

    [Fact]
    public void Container_list_tolerates_blank_lines_and_a_trailing_newline()
    {
        var jsonl = "\n  \n" + ReadFixture("palworld-container-ls.jsonl").TrimEnd('\n') + "\n\n   \n";

        var entries = DockerInspectJson.ParseContainerList(jsonl);

        entries.Should().ContainSingle();
    }

    [Fact]
    public void Version_reports_the_server_version()
    {
        var version = DockerInspectJson.ParseVersion(ReadFixture("docker-version.json"));

        version.ServerVersion.Should().Be("29.7.0");
        version.ClientVersion.Should().Be("29.7.0");
        version.ApiVersion.Should().Be("1.55");
    }

    [Fact]
    public void Stats_reports_cpu_and_memory()
    {
        var stats = DockerInspectJson.ParseStats(ReadFixture("palworld-stats.json"));

        stats.Container.Should().Be("palworld-server");
        stats.CpuPercent.Should().Be(138.51);
        stats.MemoryLimitBytes.Should().Be(8L * 1024 * 1024 * 1024);
        stats.MemoryUsageBytes.Should().Be((long)(2.141 * 1024 * 1024 * 1024));
    }

    [Fact]
    public void Image_repository_matching_ignores_tag_and_digest()
    {
        DockerInspectJson.ImageRepositoryMatches(
            "thijsvanloef/palworld-server-docker:latest",
            "thijsvanloef/palworld-server-docker").Should().BeTrue();

        DockerInspectJson.ImageRepositoryMatches(
            "thijsvanloef/palworld-server-docker@sha256:401d3eb5c053bcd72949e1ede8c4e38be5e5ad66be7272ac37940706df0aeb2f",
            "thijsvanloef/palworld-server-docker").Should().BeTrue();

        DockerInspectJson.ImageRepositoryMatches(
            "registry.example.com:5000/thijsvanloef/palworld-server-docker:latest",
            "registry.example.com:5000/thijsvanloef/palworld-server-docker").Should().BeTrue();

        DockerInspectJson.ImageRepositoryMatches(
            "thijsvanloef/palworld-server-docker:latest",
            "some/other-image").Should().BeFalse();
    }

    /// <summary>
    /// Regression guard: this repo has a known history of secrets accidentally landing in git via
    /// captured fixtures. If a future re-capture of the inspect fixture forgets to scrub, this test
    /// fails loudly instead of the leak going unnoticed.
    /// </summary>
    [Fact]
    public void Inspect_fixture_contains_no_unscrubbed_secrets()
    {
        var raw = ReadFixture("palworld-inspect.json");

        raw.Should().NotContain("185.126.158.41");
        raw.Should().Contain("ADMIN_PASSWORD=SCRUBBED");
        raw.Should().Contain("SERVER_PASSWORD=SCRUBBED");
    }
}
