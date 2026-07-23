using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;

namespace Servyx.Infrastructure.Docker.Tests;

public class DockerLogStreamTests
{
    /// <summary>Captures every log call made against it, for asserting on log content in tests.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, formatter(state, exception)));
    }

    /// <summary>
    /// A stream that yields the given bytes over successive reads and then throws <see cref="IOException"/>
    /// on the read immediately following, simulating a connection dropping mid-stream.
    /// </summary>
    private sealed class DroppingStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= data.Length)
            {
                throw new IOException("Simulated connection drop.");
            }

            var toCopy = Math.Min(buffer.Length, data.Length - _position);
            data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return ValueTask.FromResult(toCopy);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Frame(byte streamType, string payload)
    {
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var header = new byte[8];
        header[0] = streamType;
        header[4] = (byte)(payloadBytes.Length >> 24);
        header[5] = (byte)(payloadBytes.Length >> 16);
        header[6] = (byte)(payloadBytes.Length >> 8);
        header[7] = (byte)payloadBytes.Length;

        var frame = new byte[header.Length + payloadBytes.Length];
        header.CopyTo(frame, 0);
        payloadBytes.CopyTo(frame, header.Length);
        return frame;
    }

    private static MemoryStream BuildLogStream(params string[] lines)
    {
        using var buffer = new MemoryStream();
        foreach (var line in lines)
        {
            var bytes = Frame(1, $"2024-01-01T00:00:00.000000000Z {line}\n");
            buffer.Write(bytes, 0, bytes.Length);
        }

        return new MemoryStream(buffer.ToArray());
    }

    private static (IDockerClient Client, IContainerOperations Containers) CreateClientSubstitute(bool tty = false)
    {
        var containers = Substitute.For<IContainerOperations>();
        var client = Substitute.For<IDockerClient>();
        client.Containers.Returns(containers);
        containers.InspectContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ContainerInspectResponse { Config = new Config { Tty = tty } }));
        return (client, containers);
    }

    [Fact]
    public void SupportsInput_is_false()
    {
        var (client, _) = CreateClientSubstitute();
        var logStream = new DockerLogStream(client);

        logStream.SupportsInput.Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_throws_WritesDisabledException()
    {
        var (client, _) = CreateClientSubstitute();
        var logStream = new DockerLogStream(client);

        var act = async () => await logStream.WriteAsync("any-container", "some command");

        await act.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task FollowAsync_assigns_monotonically_increasing_offsets_within_one_call()
    {
        var (client, containers) = CreateClientSubstitute();
#pragma warning disable CS0618
        containers.GetContainerLogsAsync(Arg.Any<string>(), Arg.Any<ContainerLogsParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(BuildLogStream("first", "second", "third")));
#pragma warning restore CS0618

        var logStream = new DockerLogStream(client);
        var lines = new List<ConsoleLine>();
        await foreach (var line in logStream.FollowAsync("container-a", new ConsoleTailOptions(100)))
        {
            lines.Add(line);
        }

        lines.Select(l => l.Offset).Should().Equal(0, 1, 2);
        lines.Select(l => l.Text).Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task FollowAsync_offsets_continue_monotonically_across_a_simulated_reconnect()
    {
        var (client, containers) = CreateClientSubstitute();
#pragma warning disable CS0618
        containers.GetContainerLogsAsync(Arg.Any<string>(), Arg.Any<ContainerLogsParameters>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult<Stream>(BuildLogStream("first", "second")),
                _ => Task.FromResult<Stream>(BuildLogStream("third", "fourth")));
#pragma warning restore CS0618

        var logStream = new DockerLogStream(client);

        var firstConnectionLines = new List<ConsoleLine>();
        await foreach (var line in logStream.FollowAsync("container-a", new ConsoleTailOptions(100)))
        {
            firstConnectionLines.Add(line);
        }

        // Simulate a dropped connection followed by a reconnect: a second FollowAsync call for the
        // same server must not restart offset numbering at zero, so a client can resume without
        // mistaking a repeated offset for a duplicate line.
        var secondConnectionLines = new List<ConsoleLine>();
        await foreach (var line in logStream.FollowAsync("container-a", new ConsoleTailOptions(100)))
        {
            secondConnectionLines.Add(line);
        }

        firstConnectionLines.Select(l => l.Offset).Should().Equal(0, 1);
        secondConnectionLines.Select(l => l.Offset).Should().Equal(2, 3);
        secondConnectionLines.Select(l => l.Text).Should().Equal("third", "fourth");
    }

    [Fact]
    public async Task FollowAsync_tracks_offsets_independently_per_server()
    {
        var (client, containers) = CreateClientSubstitute();
#pragma warning disable CS0618
        containers.GetContainerLogsAsync("container-a", Arg.Any<ContainerLogsParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(BuildLogStream("a1", "a2")));
        containers.GetContainerLogsAsync("container-b", Arg.Any<ContainerLogsParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(BuildLogStream("b1")));
#pragma warning restore CS0618

        var logStream = new DockerLogStream(client);

        var linesA = new List<ConsoleLine>();
        await foreach (var line in logStream.FollowAsync("container-a", new ConsoleTailOptions(100)))
        {
            linesA.Add(line);
        }

        var linesB = new List<ConsoleLine>();
        await foreach (var line in logStream.FollowAsync("container-b", new ConsoleTailOptions(100)))
        {
            linesB.Add(line);
        }

        linesA.Select(l => l.Offset).Should().Equal(0, 1);
        linesB.Select(l => l.Offset).Should().Equal(0);
    }

    [Fact]
    public async Task ReadAsync_returns_only_lines_from_the_requested_offset_and_count()
    {
        var (client, containers) = CreateClientSubstitute();
#pragma warning disable CS0618
        containers.GetContainerLogsAsync(Arg.Any<string>(), Arg.Any<ContainerLogsParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(BuildLogStream("one", "two", "three", "four")));
#pragma warning restore CS0618

        var logStream = new DockerLogStream(client);

        var results = await logStream.ReadAsync("container-a", fromOffset: 1, count: 2);

        results.Select(l => l.Text).Should().Equal("two", "three");
    }

    [Fact]
    public async Task FollowAsync_preserves_stdout_stderr_attribution_on_ConsoleLine()
    {
        var (client, containers) = CreateClientSubstitute();
        var mixedFrames = Frame(1, "2024-01-01T00:00:00.000000000Z out-line\n")
            .Concat(Frame(2, "2024-01-01T00:00:00.000000000Z err-line\n"))
            .ToArray();
#pragma warning disable CS0618
        containers.GetContainerLogsAsync(Arg.Any<string>(), Arg.Any<ContainerLogsParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(new MemoryStream(mixedFrames)));
#pragma warning restore CS0618

        var logStream = new DockerLogStream(client);
        var lines = new List<ConsoleLine>();
        await foreach (var line in logStream.FollowAsync("container-a", new ConsoleTailOptions(100)))
        {
            lines.Add(line);
        }

        lines.Select(l => (l.Stream, l.Text)).Should().Equal(
            (OutputStream.StdOut, "out-line"),
            (OutputStream.StdErr, "err-line"));
    }

    [Fact]
    public async Task FollowAsync_bypasses_the_demultiplexer_for_a_tty_container()
    {
        // A TTY container's log stream is NOT framed: no 8-byte headers, just plain combined text.
        // Feeding that through the demultiplexer unmodified would misparse the first bytes of real log
        // text as a frame header, so DockerLogStream must detect Config.Tty and bypass it.
        var (client, containers) = CreateClientSubstitute(tty: true);
        var plainText = System.Text.Encoding.UTF8.GetBytes(
            "2024-01-01T00:00:00.000000000Z tty-line-one\n2024-01-01T00:00:01.000000000Z tty-line-two\n");
#pragma warning disable CS0618
        containers.GetContainerLogsAsync(Arg.Any<string>(), Arg.Any<ContainerLogsParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(new MemoryStream(plainText)));
#pragma warning restore CS0618

        var logStream = new DockerLogStream(client);
        var lines = new List<ConsoleLine>();
        await foreach (var line in logStream.FollowAsync("container-a", new ConsoleTailOptions(100)))
        {
            lines.Add(line);
        }

        lines.Select(l => l.Text).Should().Equal("tty-line-one", "tty-line-two");
        lines.Should().OnlyContain(l => l.Stream == OutputStream.StdOut, "a TTY-combined stream cannot distinguish stdout from stderr");
    }

    [Fact]
    public async Task FollowAsync_demultiplexes_a_non_tty_container_explicitly()
    {
        var (client, containers) = CreateClientSubstitute(tty: false);
#pragma warning disable CS0618
        containers.GetContainerLogsAsync(Arg.Any<string>(), Arg.Any<ContainerLogsParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(BuildLogStream("framed-line")));
#pragma warning restore CS0618

        var logStream = new DockerLogStream(client);
        var lines = new List<ConsoleLine>();
        await foreach (var line in logStream.FollowAsync("container-a", new ConsoleTailOptions(100)))
        {
            lines.Add(line);
        }

        lines.Select(l => l.Text).Should().Equal("framed-line");
    }

    [Fact]
    public async Task FollowAsync_ends_cleanly_and_logs_the_cause_when_the_connection_drops()
    {
        var (client, containers) = CreateClientSubstitute();
        var framed = Frame(1, "2024-01-01T00:00:00.000000000Z before-drop\n");
#pragma warning disable CS0618
        containers.GetContainerLogsAsync(Arg.Any<string>(), Arg.Any<ContainerLogsParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(new DroppingStream(framed)));
#pragma warning restore CS0618

        var logger = new CapturingLogger<DockerLogStream>();
        var logStream = new DockerLogStream(client, logger);

        var lines = new List<ConsoleLine>();
        var act = async () =>
        {
            await foreach (var line in logStream.FollowAsync("container-a", new ConsoleTailOptions(100)))
            {
                lines.Add(line);
            }
        };

        // The stream drop must not surface as an unhandled exception...
        await act.Should().NotThrowAsync();
        lines.Select(l => l.Text).Should().Equal("before-drop");

        // ...but the cause must still be observable, distinguishing a genuine failure from a clean end.
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Exception is IOException);
    }
}
