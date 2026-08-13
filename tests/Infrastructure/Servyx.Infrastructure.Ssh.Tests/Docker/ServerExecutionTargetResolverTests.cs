using NSubstitute;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Unit tests for <see cref="ServerExecutionTargetResolver"/> — the per-server routing seam later
/// increments (console, metrics, RCON, backups) resolve a server's actual <see cref="IExecutionTarget"/>
/// through. <see cref="IHostConnectionSource"/> and every <see cref="ITransport"/>/<see cref="IExecutionTarget"/>
/// here are plain NSubstitute doubles — no real SSH connection or Docker daemon anywhere in this file.
/// </summary>
public class ServerExecutionTargetResolverTests
{
    private sealed class FakeHostConnectionSource(IReadOnlyList<HostConnection> connections) : IHostConnectionSource
    {
        public Task<IReadOnlyList<HostConnection>> GetConnectionsAsync(CancellationToken ct = default) =>
            Task.FromResult(connections);
    }

    /// <summary>
    /// The local-fallback case: a server with a <see langword="null"/> host key (discovered on the local
    /// Docker daemon) must resolve through the local <see cref="ITransport"/> this instance was constructed
    /// with, connected against a <see cref="TargetDescriptor"/> scoped to that server's own id — never
    /// against <see cref="IHostConnectionSource"/>, which is never even asked.
    /// </summary>
    [Fact]
    public async Task Null_host_key_resolves_through_the_local_transport_scoped_to_the_server_id()
    {
        var localTarget = Substitute.For<IExecutionTarget>();
        var localTransport = Substitute.For<ITransport>();
        localTransport.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(localTarget));
        var connections = Substitute.For<IHostConnectionSource>();

        var sut = new ServerExecutionTargetResolver(connections, localTransport);

        var result = await sut.ResolveAsync("container-123", hostKey: null);

        result.Should().BeSameAs(localTarget);
        await localTransport.Received(1).ConnectAsync(
            Arg.Is<TargetDescriptor>(d =>
                d != null && d.TransportId == "docker" && d.Options["containerId"] == "container-123"),
            Arg.Any<CancellationToken>());
        await connections.DidNotReceive().GetConnectionsAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The remote-host case: a server with a non-null host key must resolve to that specific registered/
    /// configured host's own <see cref="IExecutionTarget"/> — the exact <see cref="HostConnection"/> whose
    /// <see cref="HostConnection.HostKey"/> matches, never a different one in the same set and never the
    /// local transport.
    /// </summary>
    [Fact]
    public async Task Non_null_host_key_resolves_the_matching_registered_hosts_execution_target()
    {
        var otherTarget = Substitute.For<IExecutionTarget>();
        var matchingTarget = Substitute.For<IExecutionTarget>();
        var connections = new FakeHostConnectionSource([
            new HostConnection("prod-1", otherTarget),
            new HostConnection("prod-2", matchingTarget),
        ]);
        var localTransport = Substitute.For<ITransport>();

        var sut = new ServerExecutionTargetResolver(connections, localTransport);

        var result = await sut.ResolveAsync("any-server", hostKey: "prod-2");

        result.Should().BeSameAs(matchingTarget);
        await localTransport.DidNotReceive().ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A host key naming nothing currently connectable must throw rather than silently falling back to the
    /// local transport or a different registered host — either would run the server's reads/writes against
    /// the wrong machine.
    /// </summary>
    [Fact]
    public async Task Unknown_host_key_throws_rather_than_falling_back_to_local_or_a_different_host()
    {
        var connections = new FakeHostConnectionSource([
            new HostConnection("prod-1", Substitute.For<IExecutionTarget>()),
        ]);
        var localTransport = Substitute.For<ITransport>();
        var sut = new ServerExecutionTargetResolver(connections, localTransport);

        var act = () => sut.ResolveAsync("any-server", hostKey: "does-not-exist");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*does-not-exist*");
        await localTransport.DidNotReceive().ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A null host key with no local transport registered at all (this process only ever called
    /// <c>AddServyxSshDocker</c>, never <c>AddServyxDocker</c>) must throw a clear exception rather than
    /// return null or crash on a null-reference.
    /// </summary>
    [Fact]
    public async Task Null_host_key_with_no_local_transport_registered_throws()
    {
        var connections = Substitute.For<IHostConnectionSource>();
        var sut = new ServerExecutionTargetResolver(connections, local: null);

        var act = () => sut.ResolveAsync("any-server", hostKey: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_rejects_a_null_connection_source()
    {
        var act = () => new ServerExecutionTargetResolver(null!, Substitute.For<ITransport>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveAsync_rejects_a_blank_server_id(string? serverId)
    {
        var sut = new ServerExecutionTargetResolver(
            Substitute.For<IHostConnectionSource>(), Substitute.For<ITransport>());

        var act = () => sut.ResolveAsync(serverId!, hostKey: null);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
