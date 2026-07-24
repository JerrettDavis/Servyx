using NSubstitute;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;

namespace Servyx.Infrastructure.Docker.Tests;

public class DockerEndpointResolverTests
{
    private static TargetDescriptor Target(string endpoint = "", IReadOnlyDictionary<string, string>? options = null) =>
        new("docker", endpoint, null, null, options ?? new Dictionary<string, string>());

    private static IDockerEnvironment Environment(string? dockerHost = null, bool isWindows = true)
    {
        var env = Substitute.For<IDockerEnvironment>();
        env.GetEnvironmentVariable("DOCKER_HOST").Returns(dockerHost);
        env.IsWindows.Returns(isWindows);
        return env;
    }

    [Fact]
    public void Resolve_uses_explicit_endpoint_when_given()
    {
        var env = Environment(dockerHost: "unix:///should/not/be/used");

        var result = DockerEndpointResolver.Resolve(Target("npipe://./pipe/dockerDesktopLinuxEngine"), env);

        result.Should().Be(new Uri("npipe://./pipe/dockerDesktopLinuxEngine"));
    }

    [Fact]
    public void Resolve_falls_back_to_DOCKER_HOST_when_no_explicit_endpoint()
    {
        var env = Environment(dockerHost: "unix:///var/run/docker.sock");

        var result = DockerEndpointResolver.Resolve(Target(), env);

        result.Should().Be(new Uri("unix:///var/run/docker.sock"));
    }

    [Fact]
    public void Resolve_falls_back_to_windows_default_when_nothing_else_is_set()
    {
        var env = Environment(dockerHost: null, isWindows: true);

        var result = DockerEndpointResolver.Resolve(Target(), env);

        result.Should().Be(new Uri(DockerEndpointResolver.DefaultWindowsEndpoint));
    }

    [Fact]
    public void Resolve_falls_back_to_unix_default_on_non_windows_when_nothing_else_is_set()
    {
        var env = Environment(dockerHost: null, isWindows: false);

        var result = DockerEndpointResolver.Resolve(Target(), env);

        result.Should().Be(new Uri(DockerEndpointResolver.DefaultUnixEndpoint));
    }

    [Theory]
    [InlineData("npipe://./pipe/dockerDesktopLinuxEngine")]
    [InlineData("unix:///var/run/docker.sock")]
    [InlineData("tcp://localhost:2375")]
    [InlineData("http://localhost:2375")]
    [InlineData("https://remote-host:2376")]
    public void ParseEndpoint_accepts_supported_schemes(string endpoint)
    {
        var uri = DockerEndpointResolver.Resolve(Target(endpoint));

        uri.Should().Be(new Uri(endpoint));
    }

    [Theory]
    [InlineData("not a uri at all with spaces and no scheme")]
    [InlineData("ftp://somewhere")]
    [InlineData("just-a-plain-string")]
    public void ParseEndpoint_rejects_malformed_or_unsupported_input(string malformed)
    {
        var act = () => DockerEndpointResolver.ParseEndpoint(malformed);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseEndpoint_rejects_empty_or_whitespace_input(string malformed)
    {
        var act = () => DockerEndpointResolver.ParseEndpoint(malformed);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Resolve_treats_whitespace_only_explicit_endpoint_as_absent()
    {
        var env = Environment(dockerHost: "tcp://from-env:2375");

        var result = DockerEndpointResolver.Resolve(Target("   "), env);

        result.Should().Be(new Uri("tcp://from-env:2375"));
    }

    [Fact]
    public void Resolve_treats_whitespace_only_DOCKER_HOST_as_absent()
    {
        var env = Environment(dockerHost: "   ", isWindows: false);

        var result = DockerEndpointResolver.Resolve(Target(), env);

        result.Should().Be(new Uri(DockerEndpointResolver.DefaultUnixEndpoint));
    }
}
