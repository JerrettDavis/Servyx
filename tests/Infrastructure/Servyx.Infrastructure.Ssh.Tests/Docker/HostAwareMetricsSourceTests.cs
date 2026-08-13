using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Unit tests for <see cref="HostAwareMetricsSource"/> — the seam that routes each server's metrics polling
/// to the specific host it actually lives on, instead of the single process-wide metrics source the
/// composition root used to leave fixed to either local Docker or the one statically-declared ssh+docker
/// host. <see cref="IHostConnectionSource"/>, <see cref="IServerExecutionTargetResolver"/>,
/// <see cref="IExecutionTarget"/> and the local <see cref="IMetricsSource"/> here are all plain NSubstitute
/// doubles — no real SSH connection or Docker daemon anywhere in this file.
/// </summary>
public class HostAwareMetricsSourceTests
{
    private const string ServerId = "palworld-1";

    private sealed class FakeHostConnectionSource(IReadOnlyList<HostConnection> connections) : IHostConnectionSource
    {
        public Task<IReadOnlyList<HostConnection>> GetConnectionsAsync(CancellationToken ct = default) =>
            Task.FromResult(connections);
    }

    /// <summary>Builds an <see cref="IExecutionTarget"/> answering <c>docker container inspect</c> and <c>docker stats</c> only.</summary>
    private static IExecutionTarget FakeHostTarget(bool hasServer, string statsOutput = "")
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

                if (spec.Arguments.Contains("stats"))
                {
                    return Task.FromResult(new CommandResult(0, statsOutput, string.Empty, TimeSpan.Zero));
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

    private static async Task<List<ResourceSample>> CollectAsync(
        IMetricsSource source, string serverId, int count, CancellationToken ct = default)
    {
        var samples = new List<ResourceSample>();
        await foreach (var sample in source.StreamAsync(serverId, ct))
        {
            samples.Add(sample);
            if (samples.Count >= count)
            {
                break;
            }
        }

        return samples;
    }

    [Fact]
    public async Task Zero_hosts_resolves_through_the_local_metrics_source_without_probing_anything()
    {
        var connections = new FakeHostConnectionSource([]);
        var local = Substitute.For<IMetricsSource>();
        local.StreamAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(SingleSample(new ResourceSample(DateTimeOffset.UnixEpoch, 1.0, 100, 0, 0)));
        var resolver = Substitute.For<IServerExecutionTargetResolver>();

        var sut = new HostAwareMetricsSource(connections, resolver, local);

        var samples = await CollectAsync(sut, ServerId, 1);

        samples.Should().ContainSingle().Which.MemoryBytes.Should().Be(100);
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_server_matched_by_a_registered_host_resolves_through_that_hosts_execution_target()
    {
        var matchingTarget = FakeHostTarget(
            hasServer: true,
            statsOutput: """{"CPUPerc":"12.50%","MemUsage":"256MiB / 1GiB"}""");
        var connections = new FakeHostConnectionSource([
            new HostConnection("prod-1", matchingTarget),
        ]);
        var local = Substitute.For<IMetricsSource>();
        var resolver = Substitute.For<IServerExecutionTargetResolver>();
        resolver.ResolveAsync(ServerId, "prod-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(matchingTarget));

        var sut = new HostAwareMetricsSource(connections, resolver, local);

        var samples = await CollectAsync(sut, ServerId, 1);

        samples.Should().ContainSingle().Which.CpuPercent.Should().BeApproximately(12.5, 0.01);
        await resolver.Received(1).ResolveAsync(ServerId, "prod-1", Arg.Any<CancellationToken>());
        local.DidNotReceive().StreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_server_matched_by_no_registered_host_falls_back_to_local()
    {
        var connections = new FakeHostConnectionSource([
            new HostConnection("prod-1", FakeHostTarget(hasServer: false)),
        ]);
        var local = Substitute.For<IMetricsSource>();
        local.StreamAsync(ServerId, Arg.Any<CancellationToken>())
            .Returns(SingleSample(new ResourceSample(DateTimeOffset.UnixEpoch, 2.0, 200, 0, 0)));
        var resolver = Substitute.For<IServerExecutionTargetResolver>();

        var sut = new HostAwareMetricsSource(connections, resolver, local);

        var samples = await CollectAsync(sut, ServerId, 1);

        samples.Should().ContainSingle().Which.MemoryBytes.Should().Be(200);
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A host that throws while being probed (unreachable, mid-registration, etc.) must not stop the search
    /// for a good one — mirrors <c>CompositeServerDiscovery</c>'s own per-host partial-failure handling.
    /// </summary>
    [Fact]
    public async Task A_host_that_throws_while_being_probed_is_treated_as_not_found_there()
    {
        var matchingTarget = FakeHostTarget(
            hasServer: true,
            statsOutput: """{"CPUPerc":"5.00%","MemUsage":"128MiB / 1GiB"}""");
        var connections = new FakeHostConnectionSource([
            new HostConnection("unreachable-host", UnreachableHostTarget()),
            new HostConnection("good-host", matchingTarget),
        ]);
        var local = Substitute.For<IMetricsSource>();
        var resolver = Substitute.For<IServerExecutionTargetResolver>();
        resolver.ResolveAsync(ServerId, "good-host", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(matchingTarget));

        var sut = new HostAwareMetricsSource(connections, resolver, local);

        var samples = await CollectAsync(sut, ServerId, 1);

        samples.Should().ContainSingle().Which.CpuPercent.Should().BeApproximately(5.0, 0.01);
    }

    [Fact]
    public async Task No_match_and_no_local_fallback_throws_rather_than_degrading_to_an_empty_stream()
    {
        var connections = new FakeHostConnectionSource([]);
        var resolver = Substitute.For<IServerExecutionTargetResolver>();

        var sut = new HostAwareMetricsSource(connections, resolver, local: null);

        var act = () => CollectAsync(sut, ServerId, 1);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_rejects_a_null_connection_source()
    {
        var act = () => new HostAwareMetricsSource(
            null!, Substitute.For<IServerExecutionTargetResolver>(), Substitute.For<IMetricsSource>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_rejects_a_null_resolver()
    {
        var act = () => new HostAwareMetricsSource(
            Substitute.For<IHostConnectionSource>(), null!, Substitute.For<IMetricsSource>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_accepts_a_null_local_metrics_source()
    {
        var act = () => new HostAwareMetricsSource(
            Substitute.For<IHostConnectionSource>(), Substitute.For<IServerExecutionTargetResolver>(), local: null);

        act.Should().NotThrow();
    }

    private static async IAsyncEnumerable<ResourceSample> SingleSample(ResourceSample sample)
    {
        yield return sample;
        await Task.CompletedTask;
    }
}
