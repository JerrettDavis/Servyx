using System.Formats.Tar;
using System.Net;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using NSubstitute;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;

namespace Servyx.Infrastructure.Docker.Tests;

public class DockerExecutionTargetTests
{
    private static (DockerExecutionTarget Target, IContainerOperations Containers) CreateTargetWithContainers(string rootPath = "/palworld")
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);
        return (new DockerExecutionTarget(client, "palworld-server", rootPath), containers);
    }

    private static DockerExecutionTarget CreateTarget(out IDockerClient client)
    {
        client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);
        return new DockerExecutionTarget(client, "palworld-server", "/palworld");
    }

    private static TargetPath SomePath() => new SandboxedPathResolver("/palworld").Resolve("some/file.txt");

    /// <summary>Builds an in-memory tar archive, in the same shape Docker's archive API returns.</summary>
    private static byte[] BuildTar(params (TarEntryType Type, string Name, byte[]? Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (type, name, content) in entries)
            {
                var entry = new PaxTarEntry(type, name);
                if (content is not null)
                {
                    entry.DataStream = new MemoryStream(content);
                }

                writer.WriteEntry(entry);
            }
        }

        return buffer.ToArray();
    }

    private static void SetupArchive(IContainerOperations containers, byte[] tarBytes) =>
        containers
            .GetArchiveFromContainerAsync(Arg.Any<string>(), Arg.Any<GetArchiveFromContainerParameters>(), false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetArchiveFromContainerResponse { Stream = new MemoryStream(tarBytes) }));

    private static void SetupNotFound(IContainerOperations containers, bool statOnly = false) =>
        containers
            .GetArchiveFromContainerAsync(Arg.Any<string>(), Arg.Any<GetArchiveFromContainerParameters>(), statOnly, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GetArchiveFromContainerResponse>(new DockerApiException(HttpStatusCode.NotFound, "not found")));

    private static void SetupStat(IContainerOperations containers, ContainerPathStatResponse stat) =>
        containers
            .GetArchiveFromContainerAsync(Arg.Any<string>(), Arg.Any<GetArchiveFromContainerParameters>(), true, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetArchiveFromContainerResponse { Stat = stat, Stream = null! }));

    [Fact]
    public async Task WriteFileAsync_throws_WritesDisabledException_unconditionally()
    {
        var target = CreateTarget(out _);

        var act = async () => await target.WriteFileAsync(SomePath(), new MemoryStream(), new FileWriteOptions(null));

        await act.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task DeleteAsync_throws_WritesDisabledException_unconditionally()
    {
        var target = CreateTarget(out _);

        var act = async () => await target.DeleteAsync(SomePath());

        await act.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task ExecuteAsync_throws_NotSupportedException()
    {
        var target = CreateTarget(out _);
        var spec = new CommandSpec("echo", ["hello"]);

        var act = async () => await target.ExecuteAsync(spec);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public void ExecuteStreamingAsync_throws_NotSupportedException()
    {
        var target = CreateTarget(out _);
        var spec = new CommandSpec("echo", ["hello"]);

        var act = () => target.ExecuteStreamingAsync(spec);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task DisposeAsync_disposes_owned_client()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);
        var target = new DockerExecutionTarget(client, "palworld-server", "/palworld", ownsClient: true);

        await target.DisposeAsync();

        client.Received(1).Dispose();
    }

    [Fact]
    public async Task DisposeAsync_does_not_dispose_client_it_does_not_own()
    {
        var client = Substitute.For<IDockerClient>();
        var containers = Substitute.For<IContainerOperations>();
        client.Containers.Returns(containers);
        var target = new DockerExecutionTarget(client, "palworld-server", "/palworld", ownsClient: false);

        await target.DisposeAsync();

        client.DidNotReceive().Dispose();
    }

    // --- OpenReadAsync ---------------------------------------------------

    [Fact]
    public async Task OpenReadAsync_returns_the_content_of_the_requested_file()
    {
        var (target, containers) = CreateTargetWithContainers();
        var content = "hello world"u8.ToArray();
        SetupArchive(containers, BuildTar((TarEntryType.RegularFile, "file.txt", content)));

        await using var stream = await target.OpenReadAsync(new SandboxedPathResolver("/palworld").Resolve("file.txt"));
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).Should().Be("hello world");
    }

    [Fact]
    public async Task OpenReadAsync_throws_when_the_requested_path_is_a_directory()
    {
        // Regression test: requesting a directory must not silently return some arbitrary descendant
        // file's bytes. The archive for a directory request contains the directory's own root entry
        // ("bar/"), its descendant(s), and (here) a sibling whose name is a prefix-collision risk
        // ("bar2.txt") — exact leaf-name matching must pick out "bar" itself, see it is a directory, and
        // throw, rather than matching the wrong entry or just returning the first regular file found.
        var (target, containers) = CreateTargetWithContainers();
        SetupArchive(
            containers,
            BuildTar(
                (TarEntryType.Directory, "bar/", null),
                (TarEntryType.RegularFile, "bar/nested.txt", "nested content"u8.ToArray()),
                (TarEntryType.RegularFile, "bar2.txt", "decoy content"u8.ToArray())));

        var act = async () => await target.OpenReadAsync(new SandboxedPathResolver("/palworld").Resolve("bar"));

        await act.Should().ThrowAsync<IOException>().WithMessage("*directory*");
    }

    [Fact]
    public async Task OpenReadAsync_throws_FileNotFoundException_when_the_path_does_not_exist()
    {
        var (target, containers) = CreateTargetWithContainers();
        SetupNotFound(containers);

        var act = async () => await target.OpenReadAsync(new SandboxedPathResolver("/palworld").Resolve("missing.txt"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task OpenReadAsync_matches_the_exact_leaf_even_when_a_sibling_name_is_a_prefix()
    {
        // "bar" and "bar2.txt" share a textual prefix; exact-equality matching (not StartsWith) must
        // still pick "bar2.txt" correctly when that is what's requested.
        var (target, containers) = CreateTargetWithContainers();
        SetupArchive(
            containers,
            BuildTar(
                (TarEntryType.RegularFile, "bar2.txt", "bar2 content"u8.ToArray())));

        await using var stream = await target.OpenReadAsync(new SandboxedPathResolver("/palworld").Resolve("bar2.txt"));
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).Should().Be("bar2 content");
    }

    // --- ListDirectoryAsync ----------------------------------------------

    [Fact]
    public async Task ListDirectoryAsync_lists_only_immediate_children_not_deeper_descendants()
    {
        var (target, containers) = CreateTargetWithContainers();
        SetupArchive(
            containers,
            BuildTar(
                (TarEntryType.Directory, "dir/", null),
                (TarEntryType.RegularFile, "dir/top-level.txt", "x"u8.ToArray()),
                (TarEntryType.Directory, "dir/subdir/", null),
                (TarEntryType.RegularFile, "dir/subdir/nested.txt", "y"u8.ToArray()),
                (TarEntryType.RegularFile, "dir/subdir/deeper/very-nested.txt", "z"u8.ToArray())));

        var entries = await target.ListDirectoryAsync(new SandboxedPathResolver("/palworld").Resolve("dir"));

        entries.Select(e => e.Name).Should().BeEquivalentTo(["top-level.txt", "subdir"]);
        entries.Single(e => e.Name == "subdir").IsDirectory.Should().BeTrue();
        entries.Single(e => e.Name == "top-level.txt").IsDirectory.Should().BeFalse();
    }

    [Fact]
    public async Task ListDirectoryAsync_folds_a_subtree_reachable_only_through_descendants_into_its_owning_directory()
    {
        // The archive never explicitly lists "subdir/" as its own entry — only a file deep beneath it —
        // yet "subdir" must still surface as exactly one directory entry, not be missed or duplicated.
        var (target, containers) = CreateTargetWithContainers();
        SetupArchive(
            containers,
            BuildTar(
                (TarEntryType.Directory, "dir/", null),
                (TarEntryType.RegularFile, "dir/subdir/a.txt", "a"u8.ToArray()),
                (TarEntryType.RegularFile, "dir/subdir/b.txt", "b"u8.ToArray())));

        var entries = await target.ListDirectoryAsync(new SandboxedPathResolver("/palworld").Resolve("dir"));

        entries.Should().ContainSingle();
        entries[0].Name.Should().Be("subdir");
        entries[0].IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task ListDirectoryAsync_distinguishes_siblings_whose_names_share_a_prefix()
    {
        var (target, containers) = CreateTargetWithContainers();
        SetupArchive(
            containers,
            BuildTar(
                (TarEntryType.Directory, "dir/", null),
                (TarEntryType.RegularFile, "dir/bar", "bar content"u8.ToArray()),
                (TarEntryType.RegularFile, "dir/bar2", "bar2 content"u8.ToArray())));

        var entries = await target.ListDirectoryAsync(new SandboxedPathResolver("/palworld").Resolve("dir"));

        entries.Select(e => e.Name).Should().BeEquivalentTo(["bar", "bar2"]);
    }

    [Fact]
    public async Task ListDirectoryAsync_throws_DirectoryNotFoundException_when_the_path_does_not_exist()
    {
        var (target, containers) = CreateTargetWithContainers();
        SetupNotFound(containers);

        var act = async () => await target.ListDirectoryAsync(new SandboxedPathResolver("/palworld").Resolve("missing-dir"));

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    // --- ExistsAsync / StatAsync ------------------------------------------

    [Fact]
    public async Task ExistsAsync_returns_true_when_the_path_is_present()
    {
        var (target, containers) = CreateTargetWithContainers();
        SetupStat(containers, new ContainerPathStatResponse { Name = "file.txt", Size = 42, Mode = 0, Mtime = DateTime.UtcNow });

        var exists = await target.ExistsAsync(new SandboxedPathResolver("/palworld").Resolve("file.txt"));

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_returns_false_when_the_path_is_absent()
    {
        var (target, containers) = CreateTargetWithContainers();
        SetupNotFound(containers, statOnly: true);

        var exists = await target.ExistsAsync(new SandboxedPathResolver("/palworld").Resolve("missing.txt"));

        exists.Should().BeFalse();
    }

    [Fact]
    public async Task StatAsync_reports_file_metadata()
    {
        var (target, containers) = CreateTargetWithContainers();
        var mtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        SetupStat(containers, new ContainerPathStatResponse { Name = "file.txt", Size = 1234, Mode = 0, Mtime = mtime });

        var stat = await target.StatAsync(new SandboxedPathResolver("/palworld").Resolve("file.txt"));

        stat.Exists.Should().BeTrue();
        stat.IsDirectory.Should().BeFalse();
        stat.SizeBytes.Should().Be(1234);
        stat.ModifiedAt.Should().Be(mtime);
    }

    [Fact]
    public async Task StatAsync_reports_directory_via_the_mode_bit()
    {
        var (target, containers) = CreateTargetWithContainers();
        // os.ModeDir is the top bit (1<<31) of Go's os.FileMode.
        SetupStat(containers, new ContainerPathStatResponse { Name = "dir", Size = 0, Mode = 0x8000_0000, Mtime = DateTime.UtcNow });

        var stat = await target.StatAsync(new SandboxedPathResolver("/palworld").Resolve("dir"));

        stat.Exists.Should().BeTrue();
        stat.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task StatAsync_reports_non_existence_for_an_absent_path()
    {
        var (target, containers) = CreateTargetWithContainers();
        SetupNotFound(containers, statOnly: true);

        var stat = await target.StatAsync(new SandboxedPathResolver("/palworld").Resolve("missing.txt"));

        stat.Exists.Should().BeFalse();
    }
}
