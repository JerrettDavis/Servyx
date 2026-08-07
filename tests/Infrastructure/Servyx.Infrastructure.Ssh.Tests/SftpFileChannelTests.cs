using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Tests;

/// <summary>
/// <see cref="SftpFileChannel.ListDirectoryAsync"/> must translate a missing remote directory into
/// <see cref="DirectoryNotFoundException"/> — the same "not found" contract
/// <see cref="Servyx.Infrastructure.Docker.DockerExecutionTarget.ListDirectoryAsync"/> already honors, and
/// the same contract this type's own <c>StatAsync</c>/<c>OpenReadAsync</c> already honor for their own
/// not-found cases. Before this fix, only <c>ListDirectoryAsync</c> let SSH.NET's
/// <see cref="SftpPathNotFoundException"/> propagate uncaught, which — for any caller distinguishing "path
/// does not exist" (a genuine, empty result) from "the read otherwise failed" — silently inverted a missing
/// directory over SSH into a reported failure instead of an honest, empty listing.
/// </summary>
public class SftpFileChannelTests
{
    private static SftpFileChannel Sut(ISftpClient client) =>
        new(client, ownsClient: false, NullLogger.Instance);

    /// <summary>
    /// Builds the <see cref="TargetPath"/> for <paramref name="relative"/> and, alongside it, the exact
    /// remote path string <c>SftpFileChannel.ToRemotePath</c> derives from it (<c>"/" + TargetPath.Value</c>
    /// — a bare SFTP channel has no notion of a container-style root prefix, unlike
    /// <see cref="Servyx.Infrastructure.Docker.DockerExecutionTarget"/>), so a test can set up the substitute
    /// against the string the production code will actually call with.
    /// </summary>
    private static (TargetPath Path, string RemotePath) Resolve(string relative)
    {
        var path = new SandboxedPathResolver("/sandbox-root").Resolve(relative);
        return (path, "/" + path.Value);
    }

    [Fact]
    public async Task ListDirectoryAsync_OnAMissingDirectory_ThrowsDirectoryNotFoundException()
    {
        var (path, remotePath) = Resolve("does-not-exist");

        var client = Substitute.For<ISftpClient>();
        client.ListDirectoryAsync(remotePath, Arg.Any<CancellationToken>())
            .Returns(ThrowingEnumerable(new SftpPathNotFoundException("No such directory")));

        var sut = Sut(client);

        var act = async () => await sut.ListDirectoryAsync(path);

        await act.Should().ThrowAsync<DirectoryNotFoundException>(
            "a missing directory is a genuine 'nothing here', not an unhandled SFTP-specific fault leaking " +
            "past this transport's IExecutionTarget contract");
    }

    /// <summary>
    /// Guards against the fix above over-catching: wrapping the enumeration in try/catch must not change
    /// what a successful listing returns. Uses directory-only entries — <see cref="SftpFileAttributes"/> has
    /// no public constructor and is sealed (SSH.NET builds it internally from a real SFTP response), so a
    /// file entry's <c>Attributes.Size</c> read is out of reach for a hand-built fake; <c>ListDirectoryAsync</c>
    /// never dereferences <c>Attributes</c> for a directory entry, which is exactly what this test needs to
    /// stay true to exercise the pass-through and the "." / ".." filter without it.
    /// </summary>
    [Fact]
    public async Task ListDirectoryAsync_OnAnExistingDirectory_ReturnsItsEntries()
    {
        var (path, remotePath) = Resolve("world");

        // Built into a local first, deliberately: configuring nested substitutes (FakeDirectory calls
        // Substitute.For<ISftpFile>() and .Returns() on each) inline inside this Returns() call would
        // overwrite NSubstitute's "last call" tracking before the outer Returns() runs — see NSubstitute's
        // own guidance against `mySub.SomeMethod().Returns(ConfigOtherSub())`.
        var payload = Entries(
            FakeDirectory("."),
            FakeDirectory(".."),
            FakeDirectory("Players"),
            FakeDirectory("AnotherWorld"));

        var client = Substitute.For<ISftpClient>();
        client.ListDirectoryAsync(remotePath, Arg.Any<CancellationToken>()).Returns(payload);

        var sut = Sut(client);

        var entries = await sut.ListDirectoryAsync(path);

        entries.Select(e => e.Name).Should().BeEquivalentTo(
            ["Players", "AnotherWorld"], "'.' and '..' must be filtered out, everything else passed through");
        entries.Should().OnlyContain(e => e.IsDirectory);
    }

    private static ISftpFile FakeDirectory(string name)
    {
        var file = Substitute.For<ISftpFile>();
        file.Name.Returns(name);
        file.IsDirectory.Returns(true);
        file.LastWriteTimeUtc.Returns(DateTime.UnixEpoch);
        return file;
    }

    private static async IAsyncEnumerable<ISftpFile> Entries(params ISftpFile[] files)
    {
        foreach (var file in files)
        {
            yield return file;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Builds an <see cref="IAsyncEnumerable{T}"/> that throws <paramref name="exception"/> on its first
    /// <c>MoveNextAsync</c> — how NSubstitute simulates SSH.NET's <c>ListDirectoryAsync</c> failing before it
    /// ever yields an entry for a path that does not exist.
    /// </summary>
    private static async IAsyncEnumerable<ISftpFile> ThrowingEnumerable(Exception exception)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162 // Unreachable code: required so the compiler recognizes this as an iterator method.
        yield break;
#pragma warning restore CS0162
    }
}
