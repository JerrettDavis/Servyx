using System.Security.Cryptography;
using System.Text;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process.Tests;

/// <summary>
/// Tests for <see cref="LocalExecutionTarget"/>: argv handling, the atomic-write contract, and the sandbox.
/// </summary>
public class LocalExecutionTargetTests
{
    /// <summary>
    /// The exact strings an injection attempt would use — one POSIX, one Windows. They appear throughout these
    /// tests as ordinary data and must remain one inert argv element everywhere they go.
    /// </summary>
    private const string PosixPayload = "; rm -rf /";

    private const string WindowsPayload = "& del /f";

    private static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);

    // ---------------------------------------------------------------------------------------------------
    // Argv construction
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_command_becomes_an_argument_list_and_never_a_command_line()
    {
        // ProcessStartInfo has two mutually exclusive ways to pass arguments: Arguments, which is a single
        // string the runtime hands to the OS verbatim, and ArgumentList, which the runtime escapes element by
        // element. Servyx uses only the second. Asserting Arguments is empty is asserting that no code path
        // built a line for a hostile element to escape out of.
        var spec = new CommandSpec("steamcmd", ["+app_update", PosixPayload, WindowsPayload]);

        var startInfo = LocalExecutionTarget.BuildStartInfo(spec, Path.GetTempPath());

        startInfo.Arguments.Should().BeEmpty();
        startInfo.ArgumentList.Should().Equal("+app_update", PosixPayload, WindowsPayload);
        startInfo.FileName.Should().Be("steamcmd");
        startInfo.UseShellExecute.Should().BeFalse("a shell is the one thing that could reinterpret an argument");
    }

    [Fact]
    public void Environment_overrides_are_applied_to_the_child_and_nothing_else()
    {
        var spec = new CommandSpec(
            "steamcmd",
            [],
            WorkingDirectory: null,
            EnvironmentOverrides: new Dictionary<string, string>(StringComparer.Ordinal) { ["SERVER_NAME"] = PosixPayload });

        var startInfo = LocalExecutionTarget.BuildStartInfo(spec, Path.GetTempPath());

        startInfo.Environment["SERVER_NAME"].Should().Be(PosixPayload);
        Environment.GetEnvironmentVariable("SERVER_NAME").Should().BeNull("the override must not leak into this process");
    }

    [SkippableFact]
    public async Task A_hostile_argument_arrives_at_the_program_as_one_literal_argument()
    {
        // The end-to-end version of the test above: the payloads are actually handed to a real program, which
        // prints back exactly what it received. If anything anywhere had joined them into a command line for a
        // shell to re-split, the output would differ.
        var unavailable = TestScripts.UnavailableReason;
        Skip.If(unavailable is not null, unavailable ?? string.Empty);

        using var temp = new TempDirectory();
        await using var target = new LocalExecutionTarget(temp.Root);

        var result = await target.ExecuteAsync(TestScripts.EchoArguments(temp.Root, PosixPayload, WindowsPayload, "plain"));

        result.ExitCode.Should().Be(0);
        result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Should().Equal(PosixPayload, WindowsPayload, "plain");
    }

    [SkippableFact]
    public async Task A_hostile_argument_deletes_nothing_because_it_is_never_interpreted()
    {
        // The consequence, stated as an outcome rather than as a string comparison: run the payloads for real
        // and check the files that a successfully-injected "rm -rf" or "del /f" would have removed.
        var unavailable = TestScripts.UnavailableReason;
        Skip.If(unavailable is not null, unavailable ?? string.Empty);

        using var temp = new TempDirectory();
        temp.WriteFile("sentinel.txt", "must survive");
        temp.WriteFile("saves/world.sav", "must also survive");

        await using var target = new LocalExecutionTarget(temp.Root);
        await target.ExecuteAsync(TestScripts.EchoArguments(temp.Root, PosixPayload, WindowsPayload));

        File.Exists(temp.At("sentinel.txt")).Should().BeTrue();
        File.Exists(temp.At("saves", "world.sav")).Should().BeTrue();
    }

    [SkippableFact]
    public async Task ExecuteAsync_reports_the_exit_code_and_both_streams()
    {
        var unavailable = TestScripts.UnavailableReason;
        Skip.If(unavailable is not null, unavailable ?? string.Empty);

        using var temp = new TempDirectory();
        await using var target = new LocalExecutionTarget(temp.Root);

        var spec = TestScripts.Build(
            temp.Root,
            unixScript: "printf 'to-stdout\\n'; printf 'to-stderr\\n' >&2; exit 7",
            windowsScript: "Write-Output 'to-stdout'; [Console]::Error.WriteLine('to-stderr'); exit 7");

        var result = await target.ExecuteAsync(spec);

        result.ExitCode.Should().Be(7);
        result.Succeeded.Should().BeFalse();
        result.StandardOutput.Should().Contain("to-stdout");
        result.StandardError.Should().Contain("to-stderr");
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [SkippableFact]
    public async Task ExecuteStreamingAsync_yields_stdout_and_stderr_as_chunks()
    {
        var unavailable = TestScripts.UnavailableReason;
        Skip.If(unavailable is not null, unavailable ?? string.Empty);

        using var temp = new TempDirectory();
        await using var target = new LocalExecutionTarget(temp.Root);

        var spec = TestScripts.Build(
            temp.Root,
            unixScript: "printf 'one\\ntwo\\n'; printf 'oops\\n' >&2",
            windowsScript: "Write-Output 'one'; Write-Output 'two'; [Console]::Error.WriteLine('oops')");

        var chunks = new List<OutputChunk>();
        await foreach (var chunk in target.ExecuteStreamingAsync(spec))
        {
            chunks.Add(chunk);
        }

        chunks.Where(c => c.Stream == OutputStream.StdOut).Select(c => c.Text).Should().Equal("one", "two");
        chunks.Where(c => c.Stream == OutputStream.StdErr).Select(c => c.Text).Should().Equal("oops");
        chunks.Should().OnlyContain(c => c.Timestamp > DateTimeOffset.UnixEpoch);
    }

    // ---------------------------------------------------------------------------------------------------
    // Atomic writes and the drift contract
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WriteFileAsync_returns_a_null_pre_image_hash_for_a_file_that_did_not_exist()
    {
        using var temp = new TempDirectory();
        await using var target = new LocalExecutionTarget(temp.Root);

        var receipt = await target.WriteFileAsync(target.Resolve("server.cfg"), Content("port=8211"), new FileWriteOptions(null));

        receipt.PreImageSha256.Should().BeNull();
        receipt.PostImageSha256.Should().Be(Sha256Hex("port=8211"));
        File.ReadAllText(temp.At("server.cfg")).Should().Be("port=8211");
    }

    [Fact]
    public async Task WriteFileAsync_reports_the_hash_of_the_content_it_replaced()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("server.cfg", "port=8211");
        await using var target = new LocalExecutionTarget(temp.Root);

        var receipt = await target.WriteFileAsync(
            target.Resolve("server.cfg"),
            Content("port=27015"),
            new FileWriteOptions(Sha256Hex("port=8211")));

        receipt.PreImageSha256.Should().Be(Sha256Hex("port=8211"));
        receipt.PostImageSha256.Should().Be(Sha256Hex("port=27015"));
        File.ReadAllText(temp.At("server.cfg")).Should().Be("port=27015");
    }

    [Fact]
    public async Task WriteFileAsync_leaves_no_temporary_file_behind_after_a_successful_write()
    {
        // The temp file is a sibling of the target on purpose — a temp path in another directory would make
        // the rename non-atomic the moment it crossed a filesystem boundary — so it must be gone afterwards.
        using var temp = new TempDirectory();
        await using var target = new LocalExecutionTarget(temp.Root);

        await target.WriteFileAsync(target.Resolve("server.cfg"), Content("port=8211"), new FileWriteOptions(null));

        Directory.EnumerateFiles(temp.Root)
            .Should().NotContain(p => p.Contains(LocalExecutionTarget.TemporaryFileInfix, StringComparison.Ordinal));
        Directory.EnumerateFiles(temp.Root).Should().ContainSingle();
    }

    [Fact]
    public async Task WriteFileAsync_refuses_a_drifted_write_before_performing_any_io()
    {
        // The contract IExecutionTarget states: "the write is refused and TargetDriftException is thrown
        // before any I/O occurs". Asserted three ways — the exception, the untouched content, and the absence
        // of any temp file, which is the observable proof that nothing was written and then rolled back.
        using var temp = new TempDirectory();
        temp.WriteFile("server.cfg", "port=8211");
        var before = temp.Snapshot();

        await using var target = new LocalExecutionTarget(temp.Root);

        var act = () => target.WriteFileAsync(
            target.Resolve("server.cfg"),
            Content("port=27015"),
            new FileWriteOptions(Sha256Hex("something the caller never saw")));

        var drift = (await act.Should().ThrowAsync<TargetDriftException>()).Which;
        drift.ExpectedHash.Should().Be(Sha256Hex("something the caller never saw"));
        drift.ActualHash.Should().Be(Sha256Hex("port=8211"));
        drift.Path.Should().NotBeNull();

        File.ReadAllText(temp.At("server.cfg")).Should().Be("port=8211");
        temp.Snapshot().Should().Equal(before, "a refused write must create nothing, not even a temp file");
    }

    [Fact]
    public async Task WriteFileAsync_refuses_a_write_that_expected_an_existing_file_when_none_exists()
    {
        using var temp = new TempDirectory();
        await using var target = new LocalExecutionTarget(temp.Root);

        var act = () => target.WriteFileAsync(
            target.Resolve("server.cfg"),
            Content("port=27015"),
            new FileWriteOptions(Sha256Hex("port=8211")));

        (await act.Should().ThrowAsync<TargetDriftException>()).Which.ActualHash.Should().BeNull();
        File.Exists(temp.At("server.cfg")).Should().BeFalse();
    }

    [Fact]
    public async Task A_write_can_be_read_back_through_the_same_session()
    {
        using var temp = new TempDirectory();
        await using var target = new LocalExecutionTarget(temp.Root);
        var path = target.Resolve("nested/deeper/server.cfg");
        Directory.CreateDirectory(temp.At("nested", "deeper"));

        await target.WriteFileAsync(path, Content("port=8211"), new FileWriteOptions(null));

        (await target.ExistsAsync(path)).Should().BeTrue();

        await using var stream = await target.OpenReadAsync(path);
        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).Should().Be("port=8211");
    }

    // ---------------------------------------------------------------------------------------------------
    // The sandbox
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_traversal_out_of_the_root_is_rejected_when_the_path_is_constructed()
    {
        // Rejected at TargetPath construction, not at the call site that would have used it — which is the
        // whole reason TargetPath's constructor is internal to Servyx.Domain and SandboxedPathResolver is its
        // only factory. No second sandboxing mechanism is invented here.
        using var temp = new TempDirectory();
        await using var target = new LocalExecutionTarget(temp.Root);

        var act = () => target.Resolve("../escaped.txt");

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("nested/../../escaped.txt")]
    [InlineData("nested/./../../escaped.txt")]
    public async Task Every_shape_of_traversal_is_rejected(string attempt)
    {
        using var temp = new TempDirectory();
        await using var target = new LocalExecutionTarget(temp.Root);

        var act = () => target.Resolve(attempt);

        act.Should().Throw<PathEscapesSandboxException>().Which.AttemptedPath.Should().Be(attempt);
    }

    [Fact]
    public async Task An_absolute_path_outside_the_root_is_rejected()
    {
        using var outside = new TempDirectory("outside");
        using var temp = new TempDirectory();
        await using var target = new LocalExecutionTarget(temp.Root);

        var act = () => target.Resolve(Path.Combine(outside.Root, "secrets.txt"));

        act.Should().Throw<PathEscapesSandboxException>();
    }

    [Fact]
    public async Task A_default_target_path_is_refused_rather_than_treated_as_the_root()
    {
        // default(TargetPath) is always constructible because it is a struct; TargetPath's own remarks say so
        // and say it must not be treated as a validated path. This is that rule, enforced.
        using var temp = new TempDirectory();
        await using var target = new LocalExecutionTarget(temp.Root);

        var act = () => target.ExistsAsync(default);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [SkippableFact]
    public async Task A_symlink_whose_target_leaves_the_root_is_refused_at_the_moment_of_io()
    {
        // SandboxedPathResolver is explicitly lexical and cannot see through the filesystem; its own remarks
        // require infrastructure to canonicalize and re-verify. A local target is the one transport that can
        // actually do that, so this asserts it does. Creating a symlink needs elevation or Developer Mode on
        // Windows, so the test skips rather than fails where that is unavailable.
        using var outside = new TempDirectory("outside");
        using var temp = new TempDirectory();
        var secret = Path.Combine(outside.Root, "secrets.txt");
        File.WriteAllText(secret, "not yours");

        try
        {
            File.CreateSymbolicLink(temp.At("link.txt"), secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip.If(true, $"This machine does not permit creating symbolic links: {ex.Message}");
        }

        await using var target = new LocalExecutionTarget(temp.Root);

        // The path is lexically inside the root, so it resolves; the refusal happens when the I/O canonicalizes it.
        var path = target.Resolve("link.txt");
        var act = () => target.OpenReadAsync(path);

        (await act.Should().ThrowAsync<PathEscapesSandboxException>()).WithMessage("*link*");
    }

    // ---------------------------------------------------------------------------------------------------
    // The remaining IExecutionTarget surface
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StatAsync_describes_a_file_a_directory_and_an_absent_path()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("server.cfg", "port=8211");
        Directory.CreateDirectory(temp.At("saves"));

        await using var target = new LocalExecutionTarget(temp.Root);

        var file = await target.StatAsync(target.Resolve("server.cfg"));
        file.Exists.Should().BeTrue();
        file.IsDirectory.Should().BeFalse();
        file.SizeBytes.Should().Be(9);
        file.ModifiedAt.Should().NotBeNull();

        var directory = await target.StatAsync(target.Resolve("saves"));
        directory.Exists.Should().BeTrue();
        directory.IsDirectory.Should().BeTrue();
        directory.SizeBytes.Should().BeNull();

        var absent = await target.StatAsync(target.Resolve("nope.cfg"));
        absent.Exists.Should().BeFalse();
        absent.SizeBytes.Should().BeNull();
    }

    [Fact]
    public async Task StatAsync_reports_a_posix_mode_only_where_the_platform_has_one()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("server.cfg", "port=8211");
        await using var target = new LocalExecutionTarget(temp.Root);

        var stat = await target.StatAsync(target.Resolve("server.cfg"));

        if (OperatingSystem.IsWindows())
        {
            // FileStat.PermitsWriteBy documents null-on-Windows as the expected shape: NTFS ACLs are a
            // different model, and inventing mode bits would be a fabricated fact.
            stat.Mode.Should().BeNull();
        }
        else
        {
            stat.Mode.Should().NotBeNull();
            stat.Mode!.Value.Should().BeInRange(0, 0x1FF);
        }
    }

    [Fact]
    public async Task ListDirectoryAsync_lists_immediate_children_only_and_sorts_them()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("b.txt", "bb");
        temp.WriteFile("a.txt", "a");
        temp.WriteFile("saves/deep.sav", "ignored at this level");

        await using var target = new LocalExecutionTarget(temp.Root);

        var entries = await target.ListDirectoryAsync(target.Resolve(""));

        entries.Select(e => e.Name).Should().Equal("a.txt", "b.txt", "saves");
        entries.Single(e => e.Name == "saves").IsDirectory.Should().BeTrue();
        entries.Single(e => e.Name == "b.txt").SizeBytes.Should().Be(2);
        entries.Single(e => e.Name == "saves").SizeBytes.Should().BeNull();
    }

    [Fact]
    public async Task ListDirectoryAsync_distinguishes_an_absent_directory_from_an_empty_one()
    {
        // The distinction ReconcileAsync depends on: an absent marker root and an empty one mean the same
        // thing to a sweep, but only because the sweep decides that — the transport must not conflate them.
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.At("empty"));

        await using var target = new LocalExecutionTarget(temp.Root);

        (await target.ListDirectoryAsync(target.Resolve("empty"))).Should().BeEmpty();

        var act = () => target.ListDirectoryAsync(target.Resolve("absent"));
        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task OpenReadAsync_throws_for_a_file_that_is_not_there()
    {
        using var temp = new TempDirectory();
        await using var target = new LocalExecutionTarget(temp.Root);

        var act = () => target.OpenReadAsync(target.Resolve("nope.cfg"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_removes_a_file_and_reports_an_absent_one_rather_than_succeeding_silently()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("server.cfg", "port=8211");

        await using var target = new LocalExecutionTarget(temp.Root);
        var path = target.Resolve("server.cfg");

        await target.DeleteAsync(path);
        File.Exists(temp.At("server.cfg")).Should().BeFalse();

        var act = () => target.DeleteAsync(path);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task A_disposed_session_refuses_further_work()
    {
        using var temp = new TempDirectory();
        var target = new LocalExecutionTarget(temp.Root);
        var path = target.Resolve("server.cfg");

        await target.DisposeAsync();

        var act = () => target.ExistsAsync(path);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }
}
