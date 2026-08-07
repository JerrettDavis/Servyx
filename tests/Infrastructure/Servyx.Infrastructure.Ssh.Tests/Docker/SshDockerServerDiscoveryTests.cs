using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;
using Servyx.Infrastructure.Ssh.Tests.Provisioning;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Unit tests for <see cref="SshDockerServerDiscovery"/>. Follows the house pattern: the SSH host is a
/// substituted <see cref="ITransport"/>/<see cref="IExecutionTarget"/> pair (see <see cref="SshHostDouble"/>),
/// so no live SSH server or docker daemon is involved anywhere — every command's stdout is canned from the
/// real fixtures under <c>TestData/</c>.
/// </summary>
public class SshDockerServerDiscoveryTests
{
    private const string ImageRepository = "thijsvanloef/palworld-server-docker";
    private const string RequiredMountPath = "/palworld";
    private const string PalworldContainerId = "1cae202fb5341a59ec34c72e63fbad9c33e054ce88f5b05e37bd756b729fa81e";

    private const string NonMatchingLsLine =
        """{"ID":"deadbeefcafe0123456789abcdef0123456789abcdef0123456789abcdef01","Names":"other-server","Image":"redis:7","State":"running","Status":"Up 1 hour","HealthStatus":"","CreatedAt":"2026-07-31 02:34:13 +0000 UTC","Ports":"","Mounts":"","Networks":"bridge","Command":"\"redis-server\"","Labels":""}""";

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    private static SshHostDouble CreateHost(string lsOutput, Func<string, CommandResult>? inspectHandler = null)
    {
        var palworldInspectJson = ReadFixture("palworld-inspect.json");

        var host = new SshHostDouble
        {
            ExecHandler = command =>
            {
                if (command.Arguments.Contains("ls"))
                {
                    return new CommandResult(0, lsOutput, string.Empty, TimeSpan.FromMilliseconds(5));
                }

                if (command.Arguments.Contains("inspect"))
                {
                    var containerId = command.Arguments[^1];
                    if (inspectHandler is not null)
                    {
                        return inspectHandler(containerId);
                    }

                    return new CommandResult(0, palworldInspectJson, string.Empty, TimeSpan.FromMilliseconds(5));
                }

                throw new InvalidOperationException($"Unexpected command: {command.Executable} {string.Join(' ', command.Arguments)}");
            },
        };

        return host;
    }

    [Fact]
    public async Task Discovery_finds_the_palworld_container()
    {
        var lsOutput = ReadFixture("palworld-container-ls.jsonl");
        var host = CreateHost(lsOutput);
        var discovery = new SshDockerServerDiscovery(host.Session);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        results.Should().ContainSingle();
        var server = results[0];
        server.ServerId.Should().Be(PalworldContainerId);
        server.Name.Should().Be("palworld-server");
        server.Image.Should().Be("thijsvanloef/palworld-server-docker:latest");
        server.Mounts.Should().Contain(m => m.Source == "/opt/palworld/data" && m.Destination == "/palworld");
    }

    [Fact]
    public async Task Discovery_ignores_containers_that_do_not_match_adoption_criteria()
    {
        var lsOutput = NonMatchingLsLine;
        var host = CreateHost(lsOutput);
        var discovery = new SshDockerServerDiscovery(host.Session);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        results.Should().BeEmpty();
        // The non-matching entry's image doesn't match, so it must never even be inspected.
        host.Commands.Should().NotContain(c => c.Arguments.Contains("inspect"));
    }

    [Fact]
    public async Task Discovery_surfaces_rcon_25575_as_exposed_but_not_published()
    {
        var lsOutput = ReadFixture("palworld-container-ls.jsonl");
        var host = CreateHost(lsOutput);
        var discovery = new SshDockerServerDiscovery(host.Session);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        var server = results.Should().ContainSingle().Subject;
        var rcon = server.Ports.Should().ContainSingle(p => p.ContainerPort == 25575 && p.Protocol == "tcp").Subject;
        rcon.HostPort.Should().BeNull();
    }

    [Fact]
    public async Task Discovery_issues_only_read_only_commands()
    {
        var lsOutput = ReadFixture("palworld-container-ls.jsonl");
        var host = CreateHost(lsOutput);
        var discovery = new SshDockerServerDiscovery(host.Session);

        await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        host.Commands.Should().NotBeEmpty();
        host.Commands.Should().OnlyContain(c => c.Intent == CommandIntent.ReadOnly);
    }

    [Fact]
    public async Task Discovery_throws_when_container_ls_fails()
    {
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(1, string.Empty, "Cannot connect to the Docker daemon", TimeSpan.Zero),
        };
        var discovery = new SshDockerServerDiscovery(host.Session);

        var act = async () => await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*docker container ls*");
    }

    [Fact]
    public async Task Discovery_throws_when_container_inspect_fails()
    {
        var lsOutput = ReadFixture("palworld-container-ls.jsonl");
        var host = CreateHost(lsOutput, _ => new CommandResult(1, string.Empty, "No such container", TimeSpan.Zero));
        var discovery = new SshDockerServerDiscovery(host.Session);

        var act = async () => await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*docker container inspect*");
    }
}
