using NSubstitute;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Tests.Provisioning;

/// <summary>
/// A substituted SSH host: an NSubstitute <see cref="ITransport"/> that hands out an
/// <see cref="IExecutionTarget"/> backed by an in-memory filesystem and a recorded command log.
/// </summary>
/// <remarks>
/// <para>
/// This is the SSH counterpart of the Docker tests' substituted <c>IDockerClient</c>, and the reason none of
/// these tests need an SSH server. <see cref="IExecutionTarget"/> is the seam the rest of this project
/// already substitutes at (see <c>CompositeExecutionTargetTests</c>), and it is exactly the surface
/// <c>SshProcessProvisioner</c> uses, so nothing is stubbed out that the production code would otherwise
/// exercise.
/// </para>
/// <para>
/// The filesystem model mirrors <see cref="SftpFileChannel"/>'s convention that a
/// <see cref="TargetPath"/>'s value is the absolute remote path minus its leading slash, so paths recorded
/// here are directly comparable to the absolute paths the provisioner is configured with.
/// </para>
/// </remarks>
internal sealed class SshHostDouble
{
    internal SshHostDouble()
    {
        Session = Substitute.For<IExecutionTarget>();

        Session
            .ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = (CommandSpec)call[0]!;
                Commands.Add(command);
                Order.Add($"exec:{command.Executable}");

                // Model just enough of mkdir for ListDirectoryAsync to distinguish "directory absent" from
                // "directory empty" — the two answers ReconcileAsync deliberately treats differently.
                if (command.Executable == "mkdir" && command.Arguments.Count > 0)
                {
                    Directories.Add(command.Arguments[^1].TrimEnd('/'));
                }

                return Task.FromResult(ExecHandler(command));
            });

        Session
            .ExistsAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Files.ContainsKey(Absolute(call)) || Directories.Contains(Absolute(call))));

        Session
            .OpenReadAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var path = Absolute(call);
                return Files.TryGetValue(path, out var bytes)
                    ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
                    : Task.FromException<Stream>(new FileNotFoundException($"No such file '{path}'.", path));
            });

        Session
            .WriteFileAsync(Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var path = Absolute(call);
                using var buffer = new MemoryStream();
                ((Stream)call[1]!).CopyTo(buffer);
                Files[path] = buffer.ToArray();
                Order.Add($"write:{path}");
                return Task.FromResult(new FileWriteReceipt(null, "sha", DateTimeOffset.UnixEpoch));
            });

        Session
            .DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var path = Absolute(call);
                Deleted.Add(path);
                Files.Remove(path);
                return Task.CompletedTask;
            });

        Session
            .ListDirectoryAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var directory = Absolute(call).TrimEnd('/');
                var prefix = directory + "/";

                IReadOnlyList<FileEntry> entries = Files
                    .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && !f.Key[prefix.Length..].Contains('/', StringComparison.Ordinal))
                    .Select(f => new FileEntry(f.Key[prefix.Length..], false, f.Value.LongLength, null))
                    .OrderBy(e => e.Name, StringComparer.Ordinal)
                    .ToList();

                return entries.Count == 0 && !Directories.Contains(directory)
                    ? Task.FromException<IReadOnlyList<FileEntry>>(new DirectoryNotFoundException($"No such directory '{directory}'."))
                    : Task.FromResult(entries);
            });

        Transport = Substitute.For<ITransport>();
        Transport.TransportId.Returns("ssh");
        Transport
            .ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Connected.Add((TargetDescriptor)call[0]!);
                return Task.FromResult(Session);
            });
    }

    /// <summary>The substituted transport handed to the provisioner.</summary>
    internal ITransport Transport { get; }

    /// <summary>The substituted session every <c>ConnectAsync</c> returns.</summary>
    internal IExecutionTarget Session { get; }

    /// <summary>How each command answers. Defaults to success; replace it to exercise a failing step.</summary>
    internal Func<CommandSpec, CommandResult> ExecHandler { get; set; } =
        _ => new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);

    /// <summary>Every command the provisioner executed, in order, as argv arrays.</summary>
    internal List<CommandSpec> Commands { get; } = [];

    /// <summary>Exec and write operations interleaved, in order, so orderings can be asserted directly.</summary>
    internal List<string> Order { get; } = [];

    /// <summary>Every descriptor the provisioner connected with.</summary>
    internal List<TargetDescriptor> Connected { get; } = [];

    /// <summary>The in-memory host filesystem, keyed by absolute path.</summary>
    internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Directories known to exist, so an absent directory is distinguishable from an empty one.</summary>
    internal HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

    /// <summary>Every path deleted, in order.</summary>
    internal List<string> Deleted { get; } = [];

    /// <summary>Seeds a file onto the host, e.g. a pre-existing marker for a refresh or reconcile test.</summary>
    internal void PutFile(string absolutePath, byte[] content)
    {
        Files[absolutePath] = content;

        var lastSlash = absolutePath.LastIndexOf('/');
        if (lastSlash > 0)
        {
            Directories.Add(absolutePath[..lastSlash]);
        }
    }

    /// <summary>Forgets everything recorded so far, so a test can assert on a single phase in isolation.</summary>
    internal void ClearRecordings()
    {
        Commands.Clear();
        Order.Clear();
        Connected.Clear();
        Deleted.Clear();
        Transport.ClearReceivedCalls();
        Session.ClearReceivedCalls();
    }

    private static string Absolute(NSubstitute.Core.CallInfo call) => "/" + ((TargetPath)call[0]!).Value;
}
