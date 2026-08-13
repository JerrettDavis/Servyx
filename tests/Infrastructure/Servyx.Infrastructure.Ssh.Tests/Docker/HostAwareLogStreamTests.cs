using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Unit tests for <see cref="HostAwareLogStream"/> — the seam that routes each console read/write to the
/// specific host <c>serverId</c> actually lives on, instead of the single process-wide log stream the
/// composition root used to leave fixed to either local Docker or the one statically-declared ssh+docker
/// host. <see cref="IHostConnectionSource"/>, <see cref="IServerExecutionTargetResolver"/>,
/// <see cref="IExecutionTarget"/> and the local <see cref="ILogStream"/> here are all plain NSubstitute
/// doubles — no real SSH connection or Docker daemon anywhere in this file.
/// </summary>
public class HostAwareLogStreamTests
{
    private const string ServerId = "palworld-1";

    private sealed class FakeHostConnectionSource(IReadOnlyList<HostConnection> connections) : IHostConnectionSource
    {
        public Task<IReadOnlyList<HostConnection>> GetConnectionsAsync(CancellationToken ct = default) =>
            Task.FromResult(connections);
    }

    /// <summary>Builds an <see cref="IExecutionTarget"/> answering <c>docker container inspect</c> and <c>docker logs</c> only.</summary>
    private static IExecutionTarget FakeHostTarget(bool hasServer, string logsOutput = "")
    {
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var spec = callInfo.Arg<CommandSpec>()!;
                if (spec.Arguments.Contains("inspect"))
                {
                    return Task.FromResult(hasServer
                        ? new CommandResult(0, "{}", string.Empty, TimeSpan.Zero)
                        : new CommandResult(1, string.Empty, "No such container", TimeSpan.Zero));
                }

                if (spec.Arguments.Contains("logs"))
                {
                    return Task.FromResult(new CommandResult(0, logsOutput, string.Empty, TimeSpan.Zero));
                }

                throw new InvalidOperationException($"Unexpected command: {string.Join(' ', spec.Arguments)}");
            });
        return target;
    }

    private static IExecutionTarget UnreachableHostTarget()
    {
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns<Task<CommandResult>>(_ => throw new InvalidOperationException("host unreachable"));
        return target;
    }

    [Fact]
    public async Task Zero_hosts_resolves_through_the_local_log_stream_without_probing_anything()
    {
        var connections = new FakeHostConnectionSource([]);
        var local = Substitute.For<ILogStream>();
        local.ReadAsync(ServerId, 0, 200, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConsoleLine>>(
                [new ConsoleLine(0, "hello", DateTimeOffset.UnixEpoch, OutputStream.StdOut)]));
        var resolver = Substitute.For<IServerExecutionTargetResolver>();

        var sut = new HostAwareLogStream(connections, resolver, local);

        var lines = await sut.ReadAsync(ServerId, 0, 200);

        lines.Should().ContainSingle().Which.Text.Should().Be("hello");
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_server_matched_by_a_registered_host_resolves_through_that_hosts_execution_target()
    {
        var matchingTarget = FakeHostTarget(hasServer: true, logsOutput: "2024-01-01T00:00:00.000000000Z remote line\n");
        var connections = new FakeHostConnectionSource([
            new HostConnection("prod-1", matchingTarget),
        ]);
        var local = Substitute.For<ILogStream>();
        var resolver = Substitute.For<IServerExecutionTargetResolver>();
        resolver.ResolveAsync(ServerId, "prod-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(matchingTarget));

        var sut = new HostAwareLogStream(connections, resolver, local);

        var lines = await sut.ReadAsync(ServerId, 0, 200);

        lines.Should().ContainSingle().Which.Text.Should().Be("remote line");
        await resolver.Received(1).ResolveAsync(ServerId, "prod-1", Arg.Any<CancellationToken>());
        await local.DidNotReceive().ReadAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_server_matched_by_no_registered_host_falls_back_to_local()
    {
        var connections = new FakeHostConnectionSource([
            new HostConnection("prod-1", FakeHostTarget(hasServer: false)),
        ]);
        var local = Substitute.For<ILogStream>();
        local.ReadAsync(ServerId, 0, 200, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConsoleLine>>(
                [new ConsoleLine(0, "local line", DateTimeOffset.UnixEpoch, OutputStream.StdOut)]));
        var resolver = Substitute.For<IServerExecutionTargetResolver>();

        var sut = new HostAwareLogStream(connections, resolver, local);

        var lines = await sut.ReadAsync(ServerId, 0, 200);

        lines.Should().ContainSingle().Which.Text.Should().Be("local line");
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A host that throws while being probed (unreachable, mid-registration, etc.) must not stop the search
    /// for a good one — mirrors <c>CompositeServerDiscovery</c>'s own per-host partial-failure handling.
    /// </summary>
    [Fact]
    public async Task A_host_that_throws_while_being_probed_is_treated_as_not_found_there()
    {
        var matchingTarget = FakeHostTarget(hasServer: true, logsOutput: "2024-01-01T00:00:00.000000000Z from-good-host\n");
        var connections = new FakeHostConnectionSource([
            new HostConnection("unreachable-host", UnreachableHostTarget()),
            new HostConnection("good-host", matchingTarget),
        ]);
        var local = Substitute.For<ILogStream>();
        var resolver = Substitute.For<IServerExecutionTargetResolver>();
        resolver.ResolveAsync(ServerId, "good-host", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(matchingTarget));

        var sut = new HostAwareLogStream(connections, resolver, local);

        var lines = await sut.ReadAsync(ServerId, 0, 200);

        lines.Should().ContainSingle().Which.Text.Should().Be("from-good-host");
    }

    [Fact]
    public async Task No_match_and_no_local_fallback_throws_rather_than_degrading_to_a_no_op_stream()
    {
        var connections = new FakeHostConnectionSource([]);
        var resolver = Substitute.For<IServerExecutionTargetResolver>();

        var sut = new HostAwareLogStream(connections, resolver, local: null);

        var act = () => sut.ReadAsync(ServerId, 0, 200);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task WriteAsync_routes_through_the_matching_hosts_resolved_target()
    {
        var matchingTarget = FakeHostTarget(hasServer: true);
        var connections = new FakeHostConnectionSource([
            new HostConnection("prod-1", matchingTarget),
        ]);
        var local = Substitute.For<ILogStream>();
        var resolver = Substitute.For<IServerExecutionTargetResolver>();
        resolver.ResolveAsync(ServerId, "prod-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(matchingTarget));

        var sut = new HostAwareLogStream(connections, resolver, local);

        // SshDockerLogStream.WriteAsync always throws WritesDisabledException — this asserts the resolve path
        // reached it (not the local stream), not that the write itself succeeds.
        var act = () => sut.WriteAsync(ServerId, "some input");

        await act.Should().ThrowAsync<WritesDisabledException>();
        await local.DidNotReceive().WriteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FollowAsync_routes_through_the_matching_hosts_resolved_target()
    {
        var matchingTarget = FakeHostTarget(hasServer: true, logsOutput: "2024-01-01T00:00:00.000000000Z followed line\n");
        var connections = new FakeHostConnectionSource([
            new HostConnection("prod-1", matchingTarget),
        ]);
        var local = Substitute.For<ILogStream>();
        var resolver = Substitute.For<IServerExecutionTargetResolver>();
        resolver.ResolveAsync(ServerId, "prod-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(matchingTarget));

        var sut = new HostAwareLogStream(connections, resolver, local);

        var lines = new List<ConsoleLine>();
        await foreach (var line in sut.FollowAsync(ServerId, new ConsoleTailOptions(200)))
        {
            lines.Add(line);
        }

        lines.Should().ContainSingle().Which.Text.Should().Be("followed line");
    }

    [Fact]
    public void SupportsInput_is_always_false()
    {
        var sut = new HostAwareLogStream(
            Substitute.For<IHostConnectionSource>(), Substitute.For<IServerExecutionTargetResolver>(), local: null);

        sut.SupportsInput.Should().BeFalse();
    }

    [Fact]
    public void Constructor_rejects_a_null_connection_source()
    {
        var act = () => new HostAwareLogStream(
            null!, Substitute.For<IServerExecutionTargetResolver>(), Substitute.For<ILogStream>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_rejects_a_null_resolver()
    {
        var act = () => new HostAwareLogStream(
            Substitute.For<IHostConnectionSource>(), null!, Substitute.For<ILogStream>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_accepts_a_null_local_log_stream()
    {
        var act = () => new HostAwareLogStream(
            Substitute.For<IHostConnectionSource>(), Substitute.For<IServerExecutionTargetResolver>(), local: null);

        act.Should().NotThrow();
    }
}
