using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using NSubstitute;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker.Tests;

/// <summary>
/// The Docker write path, which exists only when the target is constructed with
/// <see cref="WriteMode.Enabled"/>. The M1 guarantee — default construction refuses — is asserted in
/// <see cref="DockerExecutionTargetTests"/> and deliberately not restated here beyond one companion test
/// that pins the two constructions side by side.
/// </summary>
public class DockerExecutionTargetWriteTests
{
    private const string ContainerRef = "palworld-server";
    private const string Root = "/palworld";

    /// <summary>Records everything the write path sends to the daemon, so each assertion can name one call.</summary>
    private sealed class DaemonRecorder
    {
        public DaemonRecorder(WriteMode writeMode)
        {
            Client = Substitute.For<IDockerClient>();
            Containers = Substitute.For<IContainerOperations>();
            Exec = Substitute.For<IExecOperations>();
            Client.Containers.Returns(Containers);
            Client.Exec.Returns(Exec);

            Containers
                .WhenForAnyArgs(c => c.ExtractArchiveToContainerAsync(default!, default!, default!, default))
                .Do(ci =>
                {
                    ExtractedTo.Add(ci.ArgAt<ContainerPathStatParameters>(1).Path);
                    using var copy = new MemoryStream();
                    ci.ArgAt<Stream>(2).CopyTo(copy);
                    ExtractedArchives.Add(copy.ToArray());
                });

            Exec.ExecCreateContainerAsync(Arg.Any<string>(), Arg.Any<ContainerExecCreateParameters>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    Commands.Add([.. ci.ArgAt<ContainerExecCreateParameters>(1).Cmd]);
                    return Task.FromResult(new ContainerExecCreateResponse { ID = $"exec-{Commands.Count}" });
                });

            Exec.StartAndAttachContainerExecAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new MultiplexedStream(new MemoryStream(), multiplexed: false)));

            Exec.InspectContainerExecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = NextExitCode() }));

            Target = new DockerExecutionTarget(Client, ContainerRef, Root, ownsClient: false, writeMode: writeMode);
        }

        public IDockerClient Client { get; }

        public IContainerOperations Containers { get; }

        public IExecOperations Exec { get; }

        public DockerExecutionTarget Target { get; }

        public List<string> ExtractedTo { get; } = [];

        public List<byte[]> ExtractedArchives { get; } = [];

        public List<string[]> Commands { get; } = [];

        /// <summary>Exit codes handed out in order, one per exec; anything past the end is 0.</summary>
        public List<long> ExitCodes { get; } = [];

        private int _inspected;

        private long NextExitCode() =>
            _inspected < ExitCodes.Count ? ExitCodes[_inspected++] : 0;

        /// <summary>Makes the given root-relative path exist with this content.</summary>
        public DaemonRecorder WithFile(string relative, string content, uint mode = 0x1A4)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var leaf = relative[(relative.LastIndexOf('/') + 1)..];

            Containers
                .GetArchiveFromContainerAsync(Arg.Any<string>(), Arg.Is<GetArchiveFromContainerParameters>(p => p != null && p.Path == Root + "/" + relative), true, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new GetArchiveFromContainerResponse
                {
                    Stat = new ContainerPathStatResponse { Name = leaf, Size = bytes.LongLength, Mode = mode },
                    Stream = null!,
                }));

            Containers
                .GetArchiveFromContainerAsync(Arg.Any<string>(), Arg.Is<GetArchiveFromContainerParameters>(p => p != null && p.Path == Root + "/" + relative), false, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new GetArchiveFromContainerResponse { Stream = new MemoryStream(BuildTar(leaf, bytes)) }));

            return this;
        }

        /// <summary>Makes the given root-relative path absent, on both the stat and the archive read.</summary>
        public DaemonRecorder WithMissing(string relative)
        {
            Containers
                .GetArchiveFromContainerAsync(Arg.Any<string>(), Arg.Is<GetArchiveFromContainerParameters>(p => p != null && p.Path == Root + "/" + relative), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<GetArchiveFromContainerResponse>(new DockerApiException(HttpStatusCode.NotFound, "not found")));

            return this;
        }
    }

    private static byte[] BuildTar(string name, byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
        }

        return buffer.ToArray();
    }

    private static IReadOnlyList<TarEntry> ReadTar(byte[] archive)
    {
        using var raw = new MemoryStream(archive);
        using var reader = new TarReader(raw, leaveOpen: true);

        var entries = new List<TarEntry>();
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: true)) is not null)
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static TargetPath Path(string relative) => new SandboxedPathResolver(Root).Resolve(relative);

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text), writable: false);

    [Fact]
    public async Task Default_construction_still_refuses_while_an_enabled_one_writes()
    {
        // The M1 negative guarantee and the M4 capability, side by side against the same daemon: the only
        // difference is the write mode the target was constructed with.
        var readOnly = new DaemonRecorder(WriteMode.ReadOnly).WithMissing("new.ini");
        var enabled = new DaemonRecorder(WriteMode.Enabled).WithMissing("new.ini");

        var refuse = async () => await readOnly.Target.WriteFileAsync(Path("new.ini"), Content("x"), new FileWriteOptions(null));
        await refuse.Should().ThrowAsync<WritesDisabledException>();
        readOnly.ExtractedArchives.Should().BeEmpty("a refusal must happen before any I/O");

        await enabled.Target.WriteFileAsync(Path("new.ini"), Content("x"), new FileWriteOptions(null));
        enabled.ExtractedArchives.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_write_places_a_temp_sibling_and_then_moves_it_over_the_target()
    {
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithMissing("Pal/Saved/Config/new.ini");

        await daemon.Target.WriteFileAsync(Path("Pal/Saved/Config/new.ini"), Content("port=8211"), new FileWriteOptions(null));

        // Placed into the target's own directory — anywhere else and the move below stops being atomic the
        // moment it crosses a filesystem boundary.
        daemon.ExtractedTo.Should().ContainSingle().Which.Should().Be("/palworld/Pal/Saved/Config");

        var entry = ReadTar(daemon.ExtractedArchives.Single()).Single();
        entry.Name.Should().StartWith("new.ini.servyx-tmp-");
        entry.Name.Should().NotBe("new.ini", "the target itself is never written in place");

        var move = daemon.Commands.Should().ContainSingle().Which;
        move[0].Should().Be("mv");
        move.Should().Contain(a => a.StartsWith("/palworld/Pal/Saved/Config/new.ini.servyx-tmp-", StringComparison.Ordinal));
        move[^1].Should().Be("/palworld/Pal/Saved/Config/new.ini");
    }

    [Fact]
    public async Task The_receipt_carries_the_pre_image_hash_of_what_was_overwritten()
    {
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithFile("PalWorldSettings.ini", "[settings]\nold=1\n");

        var receipt = await daemon.Target.WriteFileAsync(
            Path("PalWorldSettings.ini"), Content("[settings]\nnew=1\n"), new FileWriteOptions(null));

        receipt.PreImageSha256.Should().Be(Sha256("[settings]\nold=1\n"));
        receipt.PostImageSha256.Should().Be(Sha256("[settings]\nnew=1\n"));
    }

    [Fact]
    public async Task Creating_a_file_that_did_not_exist_yields_a_null_pre_image()
    {
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithMissing("fresh.ini");

        var receipt = await daemon.Target.WriteFileAsync(Path("fresh.ini"), Content("hello"), new FileWriteOptions(null));

        receipt.PreImageSha256.Should().BeNull();
        receipt.PostImageSha256.Should().Be(Sha256("hello"));
    }

    [Fact]
    public async Task A_matching_expected_pre_image_hash_lets_the_write_through()
    {
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithFile("PalWorldSettings.ini", "old");

        await daemon.Target.WriteFileAsync(
            Path("PalWorldSettings.ini"), Content("new"), new FileWriteOptions(Sha256("old")));

        daemon.ExtractedArchives.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_drifted_pre_image_hash_is_refused_before_any_temp_file_appears()
    {
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithFile("PalWorldSettings.ini", "changed-underneath-us");

        var act = async () => await daemon.Target.WriteFileAsync(
            Path("PalWorldSettings.ini"), Content("new"), new FileWriteOptions(Sha256("what-we-last-saw")));

        var drift = (await act.Should().ThrowAsync<TargetDriftException>()).Which;
        drift.ExpectedHash.Should().Be(Sha256("what-we-last-saw"));
        drift.ActualHash.Should().Be(Sha256("changed-underneath-us"));

        daemon.ExtractedArchives.Should().BeEmpty("no temp file may be created for a write that is refused");
        daemon.Commands.Should().BeEmpty("and nothing may be executed in the container either");
    }

    [Fact]
    public async Task Expecting_content_where_there_is_none_is_drift_not_a_create()
    {
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithMissing("PalWorldSettings.ini");

        var act = async () => await daemon.Target.WriteFileAsync(
            Path("PalWorldSettings.ini"), Content("new"), new FileWriteOptions(Sha256("we-thought-this-was-there")));

        await act.Should().ThrowAsync<TargetDriftException>();
        daemon.ExtractedArchives.Should().BeEmpty();
    }

    [Fact]
    public async Task An_existing_files_mode_is_preserved_across_the_write()
    {
        // 0600: a secrets file nobody but the owner may read. A write that quietly widened it to 0644 would be
        // a disclosure, and one nothing else in the system would notice.
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithFile("secrets.env", "TOKEN=old", mode: 0x180);

        await daemon.Target.WriteFileAsync(Path("secrets.env"), Content("TOKEN=new"), new FileWriteOptions(null));

        ReadTar(daemon.ExtractedArchives.Single()).Single().Mode
            .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task A_new_file_is_created_readable_but_not_world_writable()
    {
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithMissing("fresh.ini");

        await daemon.Target.WriteFileAsync(Path("fresh.ini"), Content("hello"), new FileWriteOptions(null));

        ReadTar(daemon.ExtractedArchives.Single()).Single().Mode.Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    // -- DirectPlacement: the shape a write into a created-but-not-started container has to take ---------

    [Fact]
    public async Task A_direct_placement_write_reaches_the_target_without_executing_anything_in_the_container()
    {
        // The whole point. `docker exec` starts a process inside a *running* container, so the mv that
        // finalizes an atomic write cannot happen before the container's first start. The archive endpoint
        // can, so a direct placement is one PUT /containers/{id}/archive and nothing else.
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithMissing("config/credential");

        await daemon.Target.WriteFileAsync(
            Path("config/credential"),
            Content("a-secret"),
            new FileWriteOptions(null) { Strategy = FileWriteStrategy.DirectPlacement });

        daemon.ExtractedTo.Should().ContainSingle().Which.Should().Be("/palworld/config");
        ReadTar(daemon.ExtractedArchives.Single()).Single().Name
            .Should().Be("credential", "a direct placement lands on the target's own name, with no temp sibling");
        daemon.Commands.Should().BeEmpty("an exec would need a running container, which is exactly what there isn't");
    }

    [Fact]
    public async Task A_direct_placement_write_carries_the_declared_mode_in_the_archive_itself()
    {
        // 0600 arrives with the file rather than after it: there is no window in which a credential sits
        // at a wider mode, and no chmod exec that a not-yet-started container could not have run anyway.
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithMissing("config/credential");

        await daemon.Target.WriteFileAsync(
            Path("config/credential"),
            Content("a-secret"),
            new FileWriteOptions(null) { Strategy = FileWriteStrategy.DirectPlacement, Mode = 0x180 });

        ReadTar(daemon.ExtractedArchives.Single()).Single().Mode
            .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        daemon.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task An_explicit_mode_wins_over_the_mode_the_file_already_had()
    {
        // Preserving an existing mode is right for a config write and wrong for a file that is being
        // re-seeded deliberately: the caller stated what the permissions must be.
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithFile("config/credential", "old", mode: 0x1A4);

        await daemon.Target.WriteFileAsync(
            Path("config/credential"),
            Content("new"),
            new FileWriteOptions(null) { Strategy = FileWriteStrategy.DirectPlacement, Mode = 0x100 });

        ReadTar(daemon.ExtractedArchives.Single()).Single().Mode.Should().Be(UnixFileMode.UserRead);
    }

    [Fact]
    public async Task A_direct_placement_write_still_reports_and_checks_the_pre_image()
    {
        // Every read the write path makes is an archive read, so drift detection and the receipt work the
        // same on a container that has never started as on one that is running.
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithFile("config/credential", "old");

        var receipt = await daemon.Target.WriteFileAsync(
            Path("config/credential"),
            Content("new"),
            new FileWriteOptions(Sha256("old")) { Strategy = FileWriteStrategy.DirectPlacement });

        receipt.PreImageSha256.Should().Be(Sha256("old"));
        receipt.PostImageSha256.Should().Be(Sha256("new"));

        var other = new DaemonRecorder(WriteMode.Enabled).WithFile("config/credential", "old");
        var drifted = async () => await other.Target.WriteFileAsync(
            Path("config/credential"),
            Content("newer"),
            new FileWriteOptions(Sha256("something-else")) { Strategy = FileWriteStrategy.DirectPlacement });

        await drifted.Should().ThrowAsync<TargetDriftException>();
        other.ExtractedArchives.Should().BeEmpty("the refused write placed nothing");
    }

    [Fact]
    public async Task A_direct_placement_write_is_refused_on_a_read_only_target_like_any_other()
    {
        var daemon = new DaemonRecorder(WriteMode.ReadOnly).WithMissing("config/credential");

        var act = async () => await daemon.Target.WriteFileAsync(
            Path("config/credential"),
            Content("a-secret"),
            new FileWriteOptions(null) { Strategy = FileWriteStrategy.DirectPlacement, Mode = 0x180 });

        await act.Should().ThrowAsync<WritesDisabledException>();
        daemon.ExtractedArchives.Should().BeEmpty();
        daemon.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task The_default_strategy_is_still_the_atomic_one()
    {
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithMissing("config/credential");

        await daemon.Target.WriteFileAsync(Path("config/credential"), Content("x"), new FileWriteOptions(null));

        ReadTar(daemon.ExtractedArchives.Single()).Single().Name.Should().StartWith("credential.servyx-tmp-");
        daemon.Commands.Should().ContainSingle().Which[0].Should().Be("mv");
    }

    [Fact]
    public void A_mode_outside_the_low_nine_permission_bits_is_refused_when_the_options_are_built()
    {
        var setUserId = () => new FileWriteOptions(null) { Mode = 0x800 };
        var negative = () => new FileWriteOptions(null) { Mode = -1 };

        setUserId.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_failed_move_removes_the_temp_sibling_and_reports_the_target_unchanged()
    {
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithFile("PalWorldSettings.ini", "old");
        daemon.ExitCodes.Add(1); // the mv fails; the cleanup rm that follows succeeds.

        var act = async () => await daemon.Target.WriteFileAsync(
            Path("PalWorldSettings.ini"), Content("new"), new FileWriteOptions(null));

        (await act.Should().ThrowAsync<IOException>()).Which.Message.Should().Contain("unchanged");

        daemon.Commands.Should().HaveCount(2);
        daemon.Commands[0][0].Should().Be("mv");
        daemon.Commands[1][0].Should().Be("rm");
        daemon.Commands[1][^1].Should().StartWith("/palworld/PalWorldSettings.ini.servyx-tmp-");
    }

    [Fact]
    public async Task DeleteAsync_removes_the_file_when_writes_are_enabled()
    {
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithFile("servyx-backups/old.tar.gz", "archive");

        await daemon.Target.DeleteAsync(Path("servyx-backups/old.tar.gz"));

        var command = daemon.Commands.Should().ContainSingle().Which;
        command[0].Should().Be("rm");
        command[^1].Should().Be("/palworld/servyx-backups/old.tar.gz");
    }

    [Fact]
    public async Task DeleteAsync_reports_a_missing_file_rather_than_silently_succeeding()
    {
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithMissing("servyx-backups/gone.tar.gz");

        var act = async () => await daemon.Target.DeleteAsync(Path("servyx-backups/gone.tar.gz"));

        await act.Should().ThrowAsync<FileNotFoundException>();
        daemon.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_refuses_a_directory_because_this_seam_deletes_files_only()
    {
        var daemon = new DaemonRecorder(WriteMode.Enabled);
        daemon.Containers
            .GetArchiveFromContainerAsync(Arg.Any<string>(), Arg.Any<GetArchiveFromContainerParameters>(), true, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetArchiveFromContainerResponse
            {
                // Go's os.ModeDir, the top bit of os.FileMode.
                Stat = new ContainerPathStatResponse { Name = "Saved", Mode = 0x8000_0000u },
                Stream = null!,
            }));

        var act = async () => await daemon.Target.DeleteAsync(Path("Pal/Saved"));

        await act.Should().ThrowAsync<IOException>();
        daemon.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_delete_is_reported_rather_than_swallowed()
    {
        var daemon = new DaemonRecorder(WriteMode.Enabled).WithFile("servyx-backups/locked.tar.gz", "archive");
        daemon.ExitCodes.Add(1);

        var act = async () => await daemon.Target.DeleteAsync(Path("servyx-backups/locked.tar.gz"));

        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task The_general_exec_channel_runs_regardless_of_write_mode()
    {
        // DockerExecutionTarget.ExecuteAsync carries no internal write-mode check of its own — gating a
        // caller-declared CommandSpec by intent is WriteGuardedExecutionTarget's job, not this class's (see
        // the class-level remarks). WriteFileAsync/DeleteAsync are the two members this class itself
        // refuses; ExecuteAsync is not one of them, on any write mode.
        var daemon = new DaemonRecorder(WriteMode.ReadOnly);

        var result = await daemon.Target.ExecuteAsync(new CommandSpec("echo", ["hi"]));

        result.ExitCode.Should().Be(0);
        daemon.Commands.Should().ContainSingle().Which.Should().Equal("echo", "hi");
    }

    [Fact]
    public void The_constructed_write_mode_is_reported_honestly()
    {
        new DaemonRecorder(WriteMode.ReadOnly).Target.WriteMode.Should().Be(WriteMode.ReadOnly);
        new DaemonRecorder(WriteMode.PreviewOnly).Target.WriteMode.Should().Be(WriteMode.PreviewOnly);
        new DaemonRecorder(WriteMode.Enabled).Target.WriteMode.Should().Be(WriteMode.Enabled);
    }

    [Fact]
    public async Task PreviewOnly_refuses_exactly_as_ReadOnly_does()
    {
        var daemon = new DaemonRecorder(WriteMode.PreviewOnly).WithFile("PalWorldSettings.ini", "old");

        var write = async () => await daemon.Target.WriteFileAsync(Path("PalWorldSettings.ini"), Content("new"), new FileWriteOptions(null));
        var delete = async () => await daemon.Target.DeleteAsync(Path("PalWorldSettings.ini"));

        await write.Should().ThrowAsync<WritesDisabledException>();
        await delete.Should().ThrowAsync<WritesDisabledException>();
        daemon.ExtractedArchives.Should().BeEmpty();
        daemon.Commands.Should().BeEmpty();
    }
}
