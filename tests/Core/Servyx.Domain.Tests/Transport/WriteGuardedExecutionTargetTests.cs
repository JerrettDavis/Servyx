using NSubstitute;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Transport;

/// <summary>
/// The write guard's whole value is that it refuses <em>before</em> anything reaches the inner target, so
/// almost every assertion here is paired with a <c>DidNotReceive</c> on the inner substitute: an exception
/// thrown after the bytes were already on their way is not a guard, it is a log line.
/// </summary>
public class WriteGuardedExecutionTargetTests
{
    private static TargetPath Path(string relative) => new SandboxedPathResolver("/palworld").Resolve(relative);

    private static (WriteGuardedExecutionTarget Guard, IExecutionTarget Inner) Guarded(WriteMode mode)
    {
        var inner = Substitute.For<IExecutionTarget>();
        return (new WriteGuardedExecutionTarget(inner, mode, "palworld-server"), inner);
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public async Task WriteFileAsync_is_refused_before_the_inner_target_is_touched(WriteMode mode)
    {
        var (guard, inner) = Guarded(mode);
        using var content = new MemoryStream([1, 2, 3]);

        var act = async () => await guard.WriteFileAsync(Path("PalWorldSettings.ini"), content, new FileWriteOptions(null));

        await act.Should().ThrowAsync<WritesDisabledException>();
        await inner.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());

        // Not one byte was read out of the caller's stream either — the refusal happens before the content is
        // so much as looked at.
        content.Position.Should().Be(0);
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public async Task DeleteAsync_is_refused_before_the_inner_target_is_touched(WriteMode mode)
    {
        var (guard, inner) = Guarded(mode);

        var act = async () => await guard.DeleteAsync(Path("world.sav"));

        await act.Should().ThrowAsync<WritesDisabledException>();
        await inner.DidNotReceive().DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_refusal_is_synchronous_so_a_caller_that_never_awaits_still_cannot_write()
    {
        var (guard, _) = Guarded(WriteMode.ReadOnly);

        // Deliberately not awaited: the exception must be raised by the call itself, not parked in a Task that
        // a fire-and-forget caller would drop on the floor.
        Action act = () => _ = guard.DeleteAsync(Path("world.sav"));

        act.Should().Throw<WritesDisabledException>();
    }

    [Fact]
    public async Task WriteFileAsync_delegates_when_writes_are_enabled()
    {
        var (guard, inner) = Guarded(WriteMode.Enabled);
        var receipt = new FileWriteReceipt("pre", "post", DateTimeOffset.UnixEpoch);
        inner.WriteFileAsync(Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(receipt));

        using var content = new MemoryStream([1, 2, 3]);
        var options = new FileWriteOptions("pre");
        var path = Path("PalWorldSettings.ini");

        var result = await guard.WriteFileAsync(path, content, options);

        result.Should().BeSameAs(receipt);
        await inner.Received(1).WriteFileAsync(path, content, options, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_delegates_when_writes_are_enabled()
    {
        var (guard, inner) = Guarded(WriteMode.Enabled);
        var path = Path("servyx-backups/old.tar.gz");

        await guard.DeleteAsync(path);

        await inner.Received(1).DeleteAsync(path, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    [InlineData(WriteMode.Enabled)]
    public async Task Every_read_member_delegates_in_every_mode(WriteMode mode)
    {
        var (guard, inner) = Guarded(mode);
        inner.ExistsAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        inner.StatAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new FileStat(true, false, 3, null, null)));
        inner.ListDirectoryAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<FileEntry>>([new FileEntry("a", false, 1, null)]));
        inner.OpenReadAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(new MemoryStream()));

        var path = Path("Pal/Saved");

        (await guard.ExistsAsync(path)).Should().BeTrue();
        (await guard.StatAsync(path)).Exists.Should().BeTrue();
        (await guard.ListDirectoryAsync(path)).Should().HaveCount(1);
        await using var stream = await guard.OpenReadAsync(path);
        stream.Should().NotBeNull();

        await inner.Received(1).ExistsAsync(path, Arg.Any<CancellationToken>());
        await inner.Received(1).StatAsync(path, Arg.Any<CancellationToken>());
        await inner.Received(1).ListDirectoryAsync(path, Arg.Any<CancellationToken>());
        await inner.Received(1).OpenReadAsync(path, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_is_not_gated_by_write_mode_because_intent_is_declared_not_inferred()
    {
        // docker exec can mutate, but Servyx classifies control operations by the readOnly flag their
        // definition declares, not by verb. Gating the raw channel here would block M2's read-only control
        // probes on every ReadOnly server, which is precisely the case they exist for.
        var (guard, inner) = Guarded(WriteMode.ReadOnly);
        var result = new CommandResult(0, "players: 3", string.Empty, TimeSpan.Zero);
        inner.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(result));

        var spec = new CommandSpec("rcon", ["ShowPlayers"]);

        (await guard.ExecuteAsync(spec)).Should().BeSameAs(result);
        await inner.Received(1).ExecuteAsync(spec, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disposal_delegates_so_the_transports_session_is_actually_released()
    {
        var (guard, inner) = Guarded(WriteMode.Enabled);

        await guard.DisposeAsync();

        await inner.Received(1).DisposeAsync();
    }

    [Fact]
    public void The_refusal_message_names_the_target_and_the_mode_that_refused()
    {
        var (guard, _) = Guarded(WriteMode.PreviewOnly);

        Action act = () => _ = guard.DeleteAsync(Path("world.sav"));

        act.Should().Throw<WritesDisabledException>()
            .Which.Message.Should().Contain("palworld-server").And.Contain("PreviewOnly");
    }

    [Fact]
    public void Only_Enabled_permits_writes()
    {
        var inner = Substitute.For<IExecutionTarget>();

        new WriteGuardedExecutionTarget(inner, WriteMode.ReadOnly).WritesPermitted.Should().BeFalse();
        new WriteGuardedExecutionTarget(inner, WriteMode.PreviewOnly).WritesPermitted.Should().BeFalse();
        new WriteGuardedExecutionTarget(inner, WriteMode.Enabled).WritesPermitted.Should().BeTrue();
    }

    [Fact]
    public void A_guard_over_nothing_is_rejected_at_construction()
    {
        var act = () => new WriteGuardedExecutionTarget(null!, WriteMode.Enabled);

        act.Should().Throw<ArgumentNullException>();
    }
}
