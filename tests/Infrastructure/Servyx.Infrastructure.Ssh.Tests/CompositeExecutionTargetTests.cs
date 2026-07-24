using NSubstitute;
using Servyx.Domain.Connectors;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh;

namespace Servyx.Infrastructure.Ssh.Tests;

/// <summary>
/// SSH exec and SFTP are independent capabilities that compose (<c>docs/connectors.md</c>, "SSH and SFTP
/// are independent"). These tests assert <see cref="CompositeExecutionTarget"/> reports exactly the union
/// of its constituents' channels, and routes each operation strictly to the target that owns it.
/// </summary>
public class CompositeExecutionTargetTests
{
    private static readonly TargetPath SomePath = new SandboxedPathResolver(Path.GetTempPath()).Resolve("file.txt");

    [Fact]
    public void Constructor_throws_when_both_targets_are_null()
    {
        var act = () => new CompositeExecutionTarget(null, null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Both_targets_present_reports_the_union_of_channels()
    {
        var exec = Substitute.For<IExecutionTarget>();
        var file = Substitute.For<IExecutionTarget>();

        var composite = new CompositeExecutionTarget(exec, file);

        composite.AvailableChannels.Should().Be(
            ConnectorChannel.Exec | ConnectorChannel.Stdin |
            ConnectorChannel.FileRead | ConnectorChannel.FileWrite | ConnectorChannel.DirectoryList);
    }

    [Fact]
    public void Exec_only_composite_reports_no_file_write_bit()
    {
        var exec = Substitute.For<IExecutionTarget>();

        var composite = new CompositeExecutionTarget(exec, fileTarget: null);

        composite.AvailableChannels.Should().HaveFlag(ConnectorChannel.Exec);
        composite.AvailableChannels.Should().NotHaveFlag(ConnectorChannel.FileWrite);
        composite.AvailableChannels.Should().NotHaveFlag(ConnectorChannel.FileRead);
        composite.AvailableChannels.Should().NotHaveFlag(ConnectorChannel.DirectoryList);
    }

    [Fact]
    public void Sftp_only_composite_reports_no_exec_bit()
    {
        var file = Substitute.For<IExecutionTarget>();

        var composite = new CompositeExecutionTarget(execTarget: null, file);

        composite.AvailableChannels.Should().HaveFlag(ConnectorChannel.FileRead);
        composite.AvailableChannels.Should().HaveFlag(ConnectorChannel.FileWrite);
        composite.AvailableChannels.Should().NotHaveFlag(ConnectorChannel.Exec);
        composite.AvailableChannels.Should().NotHaveFlag(ConnectorChannel.Stdin);
    }

    [Fact]
    public async Task ExecuteAsync_routes_to_the_exec_target_only()
    {
        var exec = Substitute.For<IExecutionTarget>();
        var file = Substitute.For<IExecutionTarget>();
        var expected = new CommandResult(0, "ok", "", TimeSpan.FromMilliseconds(1));
        var spec = new CommandSpec("echo", ["hi"]);
        exec.ExecuteAsync(spec, Arg.Any<CancellationToken>()).Returns(Task.FromResult(expected));

        var composite = new CompositeExecutionTarget(exec, file);
        var result = await composite.ExecuteAsync(spec);

        result.Should().Be(expected);
        await exec.Received(1).ExecuteAsync(spec, Arg.Any<CancellationToken>());
        await file.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_on_an_sftp_only_composite_throws_without_touching_the_file_target()
    {
        var file = Substitute.For<IExecutionTarget>();
        var composite = new CompositeExecutionTarget(execTarget: null, file);

        var act = () => composite.ExecuteAsync(new CommandSpec("echo", ["hi"]));

        await act.Should().ThrowAsync<NotSupportedException>();
        await file.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task WriteFileAsync_routes_to_the_file_target_only()
    {
        var exec = Substitute.For<IExecutionTarget>();
        var file = Substitute.For<IExecutionTarget>();
        var receipt = new FileWriteReceipt(null, "abc", DateTimeOffset.UtcNow);
        var options = new FileWriteOptions(null);
        using var content = new MemoryStream();
        file.WriteFileAsync(SomePath, content, options, Arg.Any<CancellationToken>()).Returns(Task.FromResult(receipt));

        var composite = new CompositeExecutionTarget(exec, file);
        var result = await composite.WriteFileAsync(SomePath, content, options);

        result.Should().Be(receipt);
        await file.Received(1).WriteFileAsync(SomePath, content, options, Arg.Any<CancellationToken>());
        await exec.DidNotReceiveWithAnyArgs().WriteFileAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task WriteFileAsync_on_an_exec_only_composite_throws_without_touching_the_exec_target()
    {
        var exec = Substitute.For<IExecutionTarget>();
        var composite = new CompositeExecutionTarget(exec, fileTarget: null);
        using var content = new MemoryStream();

        var act = () => composite.WriteFileAsync(SomePath, content, new FileWriteOptions(null));

        await act.Should().ThrowAsync<NotSupportedException>();
        await exec.DidNotReceiveWithAnyArgs().WriteFileAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task DisposeAsync_disposes_both_distinct_targets_exactly_once()
    {
        var exec = Substitute.For<IExecutionTarget>();
        var file = Substitute.For<IExecutionTarget>();
        var composite = new CompositeExecutionTarget(exec, file);

        await composite.DisposeAsync();

        await exec.Received(1).DisposeAsync();
        await file.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_disposes_a_shared_target_only_once()
    {
        var shared = Substitute.For<IExecutionTarget>();
        var composite = new CompositeExecutionTarget(shared, shared);

        await composite.DisposeAsync();

        await shared.Received(1).DisposeAsync();
    }
}
