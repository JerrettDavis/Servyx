using NSubstitute;

using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Azure.Tests.Provisioning;

/// <summary>
/// A substituted SSH host, reduced to exactly what <c>SshProcessProvisioner</c> touches while installing:
/// connect, run a command, write a file.
/// </summary>
/// <remarks>
/// A deliberately smaller sibling of the equivalent double in <c>Servyx.Infrastructure.Ssh.Tests</c> (which is
/// internal to that assembly and so not reachable from here), and a direct copy of the one in
/// <c>Servyx.Infrastructure.DigitalOcean.Tests</c> — which is itself a small piece of evidence for the
/// "mechanical repeat" claim, since the shape H side of the hand-off genuinely does not change between clouds.
/// This one models no filesystem, because these tests are not about what the SSH adapter installs — they are
/// about whether the descriptor the Azure adapter produced survives the trip into it.
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
                Commands.Add((CommandSpec)call[0]!);
                return Task.FromResult(new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));
            });

        Session
            .WriteFileAsync(Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Written.Add("/" + ((TargetPath)call[0]!).Value);
                return Task.FromResult(new FileWriteReceipt(null, "sha", DateTimeOffset.UnixEpoch));
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

    /// <summary>The substituted transport handed to the SSH provisioner.</summary>
    internal ITransport Transport { get; }

    /// <summary>The substituted session every <c>ConnectAsync</c> returns.</summary>
    internal IExecutionTarget Session { get; }

    /// <summary>Every descriptor the SSH provisioner connected with, in order.</summary>
    internal List<TargetDescriptor> Connected { get; } = [];

    /// <summary>Every command the SSH provisioner ran, as argv arrays.</summary>
    internal List<CommandSpec> Commands { get; } = [];

    /// <summary>Every absolute path the SSH provisioner wrote.</summary>
    internal List<string> Written { get; } = [];
}
