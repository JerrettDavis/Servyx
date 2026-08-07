using System.Text;
using NSubstitute;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Provisioning;

namespace Servyx.Infrastructure.Docker.Tests.Provisioning;

/// <summary>
/// Covers <see cref="DeployedFileSeeder"/>, which materializes a deployment's declared files into a
/// container's storage before its workload is started for the first time.
/// </summary>
/// <remarks>
/// Two properties carry the security weight of this type and are asserted directly rather than inferred:
/// that seeding is refused on a server whose write mode is not <see cref="WriteMode.Enabled"/> (because it
/// goes through <see cref="WriteGuardedExecutionTarget"/> like every other write, not around it), and that
/// nothing derived from a secret-sourced file's content is ever renderable as text. Both are asserted
/// against the guard itself rather than a stand-in, so a future edit that routed seeding around the guard
/// would fail here.
/// </remarks>
public class DeployedFileSeederTests
{
    private const string SecretContent = "a-real-rcon-password-9f3b2c";
    private const string RootPath = "/data";

    private static TargetPath Path(string relative) => new SandboxedPathResolver(RootPath).Resolve(relative);

    private static SeededFile SecretFile(bool createOnly = true, string mode = "0600") =>
        new(Path("config/credential"), Encoding.UTF8.GetBytes(SecretContent), mode, createOnly, isSensitive: true);

    /// <summary>
    /// A guarded target over a substituted inner session — the same shape a Servyx-registered transport
    /// hands out, so what is exercised here is the real decorator and not a test double of it.
    /// </summary>
    private static (WriteGuardedExecutionTarget Guard, IExecutionTarget Inner) Guarded(
        WriteMode mode, bool alreadyExists = false, bool writeFails = false)
    {
        var inner = Substitute.For<IExecutionTarget>();
        inner.ExistsAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(alreadyExists));
        inner.WriteFileAsync(Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>())
            .Returns(writeFails
                ? Task.FromException<FileWriteReceipt>(new IOException("the daemon refused the archive"))
                : Task.FromResult(new FileWriteReceipt(null, "post", DateTimeOffset.UnixEpoch)));
        inner.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero)));

        return (new WriteGuardedExecutionTarget(inner, mode, "seeded-container"), inner);
    }

    // -- The write guard -------------------------------------------------------------------------------

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public async Task SeedAsync_is_refused_on_a_non_writable_server_before_any_byte_reaches_the_target(WriteMode mode)
    {
        // The load-bearing assertion of this whole feature: seeding is a write, it goes through the same
        // guarded member every other write goes through, and a read-only control tier therefore refuses it.
        var (guard, inner) = Guarded(mode);

        var act = async () => await DeployedFileSeeder.SeedAsync(guard, [SecretFile()], RootPath);

        await act.Should().ThrowAsync<WritesDisabledException>();
        await inner.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public async Task A_refusal_never_echoes_the_secret_it_was_about_to_write(WriteMode mode)
    {
        var (guard, _) = Guarded(mode);

        var act = async () => await DeployedFileSeeder.SeedAsync(guard, [SecretFile()], RootPath);

        var thrown = (await act.Should().ThrowAsync<WritesDisabledException>()).Which;
        thrown.ToString().Should().NotContain(SecretContent);
    }

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public async Task The_mode_step_cannot_become_a_way_around_the_gate_the_write_went_through(WriteMode mode)
    {
        // The declared mode now rides inside the guarded write rather than following it as a separate
        // chmod, so a refusal takes the mode with it and no command is issued in the container at all.
        // Asserted separately because a seeded credential left at the transport's default mode would be
        // readable by every process in the container.
        var (guard, inner) = Guarded(mode);

        var act = async () => await DeployedFileSeeder.SeedAsync(guard, [SecretFile()], RootPath);

        await act.Should().ThrowAsync<WritesDisabledException>();
        await inner.DidNotReceive().ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    // -- createOnly ------------------------------------------------------------------------------------

    [Fact]
    public async Task SeedAsync_does_not_overwrite_an_existing_file_when_createOnly_is_set()
    {
        var (guard, inner) = Guarded(WriteMode.Enabled, alreadyExists: true);

        var outcomes = await DeployedFileSeeder.SeedAsync(guard, [SecretFile()], RootPath);

        outcomes.Should().ContainSingle().Which.Action.Should().Be(SeededFileAction.SkippedBecauseItAlreadyExists);
        await inner.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
        await inner.DidNotReceive().ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedAsync_writes_when_the_file_is_absent()
    {
        var (guard, inner) = Guarded(WriteMode.Enabled, alreadyExists: false);

        var outcomes = await DeployedFileSeeder.SeedAsync(guard, [SecretFile()], RootPath);

        outcomes.Should().ContainSingle().Which.Action.Should().Be(SeededFileAction.Written);
        await inner.Received(1).WriteFileAsync(
            Path("config/credential"), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedAsync_overwrites_an_existing_file_when_createOnly_is_explicitly_false()
    {
        var (guard, inner) = Guarded(WriteMode.Enabled, alreadyExists: true);

        var outcomes = await DeployedFileSeeder.SeedAsync(guard, [SecretFile(createOnly: false)], RootPath);

        outcomes.Should().ContainSingle().Which.Action.Should().Be(SeededFileAction.Written);
        await inner.Received(1).WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedAsync_writes_exactly_the_declared_bytes()
    {
        var (guard, inner) = Guarded(WriteMode.Enabled);
        byte[]? written = null;
        inner.WriteFileAsync(Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                using var buffer = new MemoryStream();
                call.Arg<Stream>()!.CopyTo(buffer);
                written = buffer.ToArray();
                return Task.FromResult(new FileWriteReceipt(null, "post", DateTimeOffset.UnixEpoch));
            });

        await DeployedFileSeeder.SeedAsync(guard, [SecretFile()], RootPath);

        Encoding.UTF8.GetString(written!).Should().Be(SecretContent);
    }

    // -- Mode ------------------------------------------------------------------------------------------

    [Fact]
    public async Task SeedAsync_carries_the_declared_mode_inside_the_write_rather_than_in_a_later_command()
    {
        // The mode is part of the one write, not a chmod after it. A follow-up command is a second
        // operation that can fail on its own — and, against the created-but-not-started container this
        // whole feature targets, cannot run at all, because there is no process in it to run one.
        var (guard, inner) = Guarded(WriteMode.Enabled);
        FileWriteOptions? options = null;
        inner.WriteFileAsync(
                Arg.Any<TargetPath>(),
                Arg.Any<Stream>(),
                Arg.Do<FileWriteOptions>(o => options = o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new FileWriteReceipt(null, "post", DateTimeOffset.UnixEpoch)));

        await DeployedFileSeeder.SeedAsync(guard, [SecretFile(mode: "0400")], RootPath);

        options.Should().NotBeNull();
        options!.Mode.Should().Be(0x100, "0400 octal is the user-read bit and nothing else");
        await inner.DidNotReceive().ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedAsync_declares_direct_placement_because_the_container_is_not_running_yet()
    {
        // A stage-and-rename write finalizes with a process inside the container. There isn't one between
        // create and start, so the strategy is stated rather than discovered — see FileWriteStrategy.
        var (guard, inner) = Guarded(WriteMode.Enabled);
        FileWriteOptions? options = null;
        inner.WriteFileAsync(
                Arg.Any<TargetPath>(),
                Arg.Any<Stream>(),
                Arg.Do<FileWriteOptions>(o => options = o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new FileWriteReceipt(null, "post", DateTimeOffset.UnixEpoch)));

        await DeployedFileSeeder.SeedAsync(guard, [SecretFile()], RootPath);

        options.Should().NotBeNull();
        options!.Strategy.Should().Be(FileWriteStrategy.DirectPlacement);
        options.ExpectedPreImageHash.Should().BeNull();
    }

    [Theory]
    [InlineData("0600", 0x180)]
    [InlineData("600", 0x180)]
    [InlineData("0400", 0x100)]
    [InlineData("0644", 0x1A4)]
    [InlineData("0777", 0x1FF)]
    public void A_declared_mode_is_read_as_octal(string mode, int expected) =>
        new SeededFile(Path("f"), Encoding.UTF8.GetBytes("x"), mode).PosixMode.Should().Be(expected);

    [Theory]
    [InlineData("0900")]  // not octal
    [InlineData("rw-")]   // symbolic, not numeric
    [InlineData("4755")]  // set-user-id, which this seam deliberately cannot express
    [InlineData("00600")] // too many digits
    public void A_mode_that_is_not_plain_octal_permission_bits_is_refused_at_construction(string mode)
    {
        var act = () => new SeededFile(Path("f"), Encoding.UTF8.GetBytes("x"), mode);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task A_failed_write_fails_the_seed_without_naming_the_secret()
    {
        var (guard, _) = Guarded(WriteMode.Enabled, writeFails: true);

        var act = async () => await DeployedFileSeeder.SeedAsync(guard, [SecretFile()], RootPath);

        var thrown = (await act.Should().ThrowAsync<IOException>()).Which;
        thrown.Message.Should().Contain("config/credential").And.Contain(SeededFile.Mask);
        thrown.ToString().Should().NotContain(SecretContent);
    }

    // -- Masking ---------------------------------------------------------------------------------------

    [Fact]
    public void A_sensitive_files_own_text_rendering_masks_its_content()
    {
        // ToString rather than a separately-named helper on purpose: interpolation is how a value reaches a
        // log by accident, so the default rendering has to be the safe one.
        var rendered = $"{SecretFile()}";

        rendered.Should().NotContain(SecretContent);
        rendered.Should().Contain(SeededFile.Mask);
        rendered.Should().Contain("config/credential").And.Contain("0600");
    }

    [Fact]
    public void A_non_sensitive_files_text_rendering_still_omits_its_content()
    {
        // "Not sensitive" is the caller's claim; a leak that depends on that claim being right is not a
        // control, so content is omitted from the rendering either way.
        var file = new SeededFile(Path("marker.txt"), Encoding.UTF8.GetBytes("managed by servyx"), isSensitive: false);

        file.ToString().Should().NotContain("managed by servyx");
        file.ToString().Should().Contain("17 bytes");
    }

    // -- Argument checks -------------------------------------------------------------------------------

    [Fact]
    public async Task SeedAsync_with_no_files_touches_the_target_not_at_all()
    {
        var (guard, inner) = Guarded(WriteMode.ReadOnly);

        (await DeployedFileSeeder.SeedAsync(guard, [], RootPath)).Should().BeEmpty();

        inner.ReceivedCalls().Should().BeEmpty();
    }
}
