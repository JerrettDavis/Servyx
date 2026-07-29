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

    private static async IAsyncEnumerable<OutputChunk> Chunks(params OutputChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }

        await Task.CompletedTask;
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

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    [InlineData(WriteMode.Enabled)]
    public async Task ExecuteAsync_runs_a_declared_read_only_command_in_every_mode(WriteMode mode)
    {
        // This is the positive counterpart of what used to be asserted as "exec is not gated at all". The
        // guarantee that mattered was never that the channel is ungated — it was that a read-only control
        // probe reaches live state on a ReadOnly server, which is precisely the case M2 exists for. That
        // guarantee is now carried by the spec's declared intent rather than by the channel being open.
        var (guard, inner) = Guarded(mode);
        var result = new CommandResult(0, "players: 3", string.Empty, TimeSpan.Zero);
        inner.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(result));

        var spec = new CommandSpec("rcon", ["ShowPlayers"], Intent: CommandIntent.ReadOnly);

        (await guard.ExecuteAsync(spec)).Should().BeSameAs(result);
        await inner.Received(1).ExecuteAsync(spec, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    [InlineData(WriteMode.Enabled)]
    public async Task ExecuteStreamingAsync_runs_a_declared_read_only_command_in_every_mode(WriteMode mode)
    {
        var (guard, inner) = Guarded(mode);
        var chunk = new OutputChunk(OutputStream.StdOut, "attached", DateTimeOffset.UnixEpoch);
        inner.ExecuteStreamingAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>()).Returns(Chunks(chunk));

        var spec = new CommandSpec("tail", ["-f", "server.log"], Intent: CommandIntent.ReadOnly);

        var chunks = new List<OutputChunk>();
        await foreach (var item in guard.ExecuteStreamingAsync(spec))
        {
            chunks.Add(item);
        }

        chunks.Should().ContainSingle().Which.Text.Should().Be("attached");
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public async Task ExecuteAsync_refuses_a_mutating_command_before_the_inner_target_is_touched(WriteMode mode)
    {
        var (guard, inner) = Guarded(mode);

        var act = async () => await guard.ExecuteAsync(
            new CommandSpec("tar", ["--create", "--file", "/backups/world.tar.gz"], Intent: CommandIntent.Mutating));

        await act.Should().ThrowAsync<WritesDisabledException>();
        await inner.DidNotReceive().ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public void ExecuteStreamingAsync_refuses_a_mutating_command_at_the_call_not_on_first_enumeration(WriteMode mode)
    {
        var (guard, inner) = Guarded(mode);

        // Deliberately not enumerated: an async-iterator refusal that only fires on MoveNextAsync would let a
        // caller that builds the sequence and drops it start the process anyway.
        Action act = () => _ = guard.ExecuteStreamingAsync(new CommandSpec("steamcmd", ["+app_update", "2394010"]));

        act.Should().Throw<WritesDisabledException>();
        inner.DidNotReceive().ExecuteStreamingAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public void A_command_that_declares_no_intent_is_refused_because_the_default_is_Mutating(WriteMode mode)
    {
        // The load-bearing property of this whole seam: an adapter that never thought about intent is
        // refused on a read-only server rather than silently permitted. A future adapter that forgets is
        // caught here, by a failing operation, not by review.
        var (guard, inner) = Guarded(mode);

        new CommandSpec("steamcmd", ["+app_update", "2394010"]).Intent.Should().Be(CommandIntent.Mutating);

        Action act = () => _ = guard.ExecuteAsync(new CommandSpec("steamcmd", ["+app_update", "2394010"]));

        act.Should().Throw<WritesDisabledException>();
        inner.DidNotReceive().ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Mutating_is_the_zero_value_so_every_uninitialised_intent_is_the_safe_one()
    {
        // Not a tautology worth skipping: if a later edit reorders CommandIntent, every default(CommandIntent)
        // — including CommandSpec's own parameter default, a deserialised zero, and any struct field that was
        // never assigned — silently flips to "permitted". This is the assertion that catches that.
        default(CommandIntent).Should().Be(CommandIntent.Mutating);
        ((int)CommandIntent.Mutating).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_runs_a_mutating_command_when_writes_are_enabled()
    {
        var (guard, inner) = Guarded(WriteMode.Enabled);
        var result = new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);
        inner.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(result));

        var spec = new CommandSpec("tar", ["--create", "--file", "/backups/world.tar.gz"]);

        (await guard.ExecuteAsync(spec)).Should().BeSameAs(result);
        await inner.Received(1).ExecuteAsync(spec, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void The_command_refusal_message_names_the_executable_the_target_and_the_mode()
    {
        var (guard, _) = Guarded(WriteMode.PreviewOnly);

        Action act = () => _ = guard.ExecuteAsync(new CommandSpec("tar", ["--create"]));

        act.Should().Throw<WritesDisabledException>()
            .Which.Message.Should().Contain("tar").And.Contain("palworld-server").And.Contain("PreviewOnly");
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
