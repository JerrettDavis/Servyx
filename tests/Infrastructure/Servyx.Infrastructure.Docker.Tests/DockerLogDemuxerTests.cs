using FluentAssertions;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;

namespace Servyx.Infrastructure.Docker.Tests;

public class DockerLogDemuxerTests
{
    /// <summary>Builds a single Docker log frame: an 8-byte header followed by the given payload text.</summary>
    private static byte[] Frame(byte streamType, string payload)
    {
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var header = new byte[8];
        header[0] = streamType;
        // header[1..3] are zero padding.
        header[4] = (byte)(payloadBytes.Length >> 24);
        header[5] = (byte)(payloadBytes.Length >> 16);
        header[6] = (byte)(payloadBytes.Length >> 8);
        header[7] = (byte)payloadBytes.Length;

        var frame = new byte[header.Length + payloadBytes.Length];
        header.CopyTo(frame, 0);
        payloadBytes.CopyTo(frame, header.Length);
        return frame;
    }

    private const byte StdOut = 1;
    private const byte StdErr = 2;

    [Fact]
    public void Feed_decodes_a_single_stdout_line()
    {
        var demuxer = new DockerLogDemuxer();

        var lines = demuxer.Feed(Frame(StdOut, "2024-01-01T00:00:00.000000000Z hello world\n"));

        lines.Should().ContainSingle();
        lines[0].Stream.Should().Be(OutputStream.StdOut);
        lines[0].Text.Should().Be("hello world");
        lines[0].Timestamp.Should().Be(DateTimeOffset.Parse("2024-01-01T00:00:00.000000000Z"));
    }

    [Fact]
    public void Feed_attributes_stderr_frames_to_stderr()
    {
        var demuxer = new DockerLogDemuxer();

        var lines = demuxer.Feed(Frame(StdErr, "2024-01-01T00:00:00.000000000Z an error occurred\n"));

        lines.Should().ContainSingle();
        lines[0].Stream.Should().Be(OutputStream.StdErr);
        lines[0].Text.Should().Be("an error occurred");
    }

    [Fact]
    public void Feed_correctly_interleaves_stdout_and_stderr_frames()
    {
        var demuxer = new DockerLogDemuxer();

        var chunk = Frame(StdOut, "2024-01-01T00:00:00.000000000Z out-1\n")
            .Concat(Frame(StdErr, "2024-01-01T00:00:00.100000000Z err-1\n"))
            .Concat(Frame(StdOut, "2024-01-01T00:00:00.200000000Z out-2\n"))
            .ToArray();

        var lines = demuxer.Feed(chunk);

        lines.Select(l => (l.Stream, l.Text)).Should().Equal(
            (OutputStream.StdOut, "out-1"),
            (OutputStream.StdErr, "err-1"),
            (OutputStream.StdOut, "out-2"));
    }

    [Fact]
    public void Feed_handles_a_frame_header_split_across_buffer_boundaries()
    {
        var demuxer = new DockerLogDemuxer();
        var frame = Frame(StdOut, "2024-01-01T00:00:00.000000000Z split-header\n");

        // Split in the middle of the 8-byte header itself.
        var firstChunk = frame[..3];
        var secondChunk = frame[3..];

        var firstResult = demuxer.Feed(firstChunk);
        firstResult.Should().BeEmpty("the header hasn't fully arrived yet, so nothing can be decoded");

        var secondResult = demuxer.Feed(secondChunk);
        secondResult.Should().ContainSingle();
        secondResult[0].Text.Should().Be("split-header");
        secondResult[0].Stream.Should().Be(OutputStream.StdOut);
    }

    [Fact]
    public void Feed_handles_a_frame_payload_split_across_buffer_boundaries()
    {
        var demuxer = new DockerLogDemuxer();
        var frame = Frame(StdOut, "2024-01-01T00:00:00.000000000Z split-payload-line\n");

        // Split partway through the payload (well past the 8-byte header).
        var splitPoint = 8 + 10;
        var firstChunk = frame[..splitPoint];
        var secondChunk = frame[splitPoint..];

        var firstResult = demuxer.Feed(firstChunk);
        firstResult.Should().BeEmpty("the line's terminating newline hasn't arrived yet");

        var secondResult = demuxer.Feed(secondChunk);
        secondResult.Should().ContainSingle();
        secondResult[0].Text.Should().Be("split-payload-line");
    }

    [Fact]
    public void Feed_handles_a_frame_split_exactly_at_the_header_payload_boundary()
    {
        var demuxer = new DockerLogDemuxer();
        var frame = Frame(StdOut, "2024-01-01T00:00:00.000000000Z boundary\n");

        var firstChunk = frame[..8];
        var secondChunk = frame[8..];

        demuxer.Feed(firstChunk).Should().BeEmpty();
        var result = demuxer.Feed(secondChunk);

        result.Should().ContainSingle();
        result[0].Text.Should().Be("boundary");
    }

    [Fact]
    public void Feed_handles_two_frames_whose_payloads_combine_into_one_line()
    {
        // A single log line does not have to arrive within a single frame.
        var demuxer = new DockerLogDemuxer();
        var chunk = Frame(StdOut, "2024-01-01T00:00:00.000000000Z partial-")
            .Concat(Frame(StdOut, "line-completed\n"))
            .ToArray();

        var lines = demuxer.Feed(chunk);

        lines.Should().ContainSingle();
        lines[0].Text.Should().Be("partial-line-completed");
    }

    [Fact]
    public void Feed_yields_nothing_until_a_complete_line_terminator_arrives()
    {
        var demuxer = new DockerLogDemuxer();

        var result = demuxer.Feed(Frame(StdOut, "2024-01-01T00:00:00.000000000Z no newline yet"));

        result.Should().BeEmpty();
    }

    [Fact]
    public void Feed_falls_back_to_utc_now_when_no_valid_timestamp_prefix_is_present()
    {
        var demuxer = new DockerLogDemuxer();
        var before = DateTimeOffset.UtcNow;

        var result = demuxer.Feed(Frame(StdOut, "no timestamp prefix here\n"));

        var after = DateTimeOffset.UtcNow;
        result.Should().ContainSingle();
        result[0].Text.Should().Be("no timestamp prefix here");
        result[0].Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Feed_strips_trailing_carriage_return()
    {
        var demuxer = new DockerLogDemuxer();

        var result = demuxer.Feed(Frame(StdOut, "2024-01-01T00:00:00.000000000Z crlf-line\r\n"));

        result.Should().ContainSingle();
        result[0].Text.Should().Be("crlf-line");
    }

    [Fact]
    public void Feed_handles_multiple_lines_in_a_single_frame()
    {
        var demuxer = new DockerLogDemuxer();

        var result = demuxer.Feed(Frame(
            StdOut,
            "2024-01-01T00:00:00.000000000Z line-one\n2024-01-01T00:00:01.000000000Z line-two\n"));

        result.Select(l => l.Text).Should().Equal("line-one", "line-two");
    }

    [Fact]
    public void Feed_processes_byte_by_byte_without_losing_or_misattributing_data()
    {
        var demuxer = new DockerLogDemuxer();
        var chunk = Frame(StdOut, "2024-01-01T00:00:00.000000000Z byte-by-byte\n")
            .Concat(Frame(StdErr, "2024-01-01T00:00:00.000000000Z also-byte-by-byte\n"))
            .ToArray();

        var results = new List<DockerLogLine>();
        foreach (var b in chunk)
        {
            results.AddRange(demuxer.Feed([b]));
        }

        results.Select(l => (l.Stream, l.Text)).Should().Equal(
            (OutputStream.StdOut, "byte-by-byte"),
            (OutputStream.StdErr, "also-byte-by-byte"));
    }

    [Fact]
    public void Feed_in_passthrough_mode_decodes_plain_unframed_text_as_stdout()
    {
        var demuxer = new DockerLogDemuxer(demultiplex: false);
        var plainBytes = System.Text.Encoding.UTF8.GetBytes("2024-01-01T00:00:00.000000000Z tty-line\n");

        var result = demuxer.Feed(plainBytes);

        result.Should().ContainSingle();
        result[0].Stream.Should().Be(OutputStream.StdOut);
        result[0].Text.Should().Be("tty-line");
    }

    [Fact]
    public void Feed_in_passthrough_mode_does_not_misinterpret_text_that_looks_like_a_frame_header()
    {
        // A byte sequence that would, in demultiplexing mode, be parsed as a stream-type byte plus a
        // huge big-endian length (and therefore hang waiting for a payload that never arrives) must be
        // treated as ordinary text in passthrough mode.
        var demuxer = new DockerLogDemuxer(demultiplex: false);
        var trickyBytes = new byte[] { 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF }
            .Concat(System.Text.Encoding.UTF8.GetBytes("normal log text\n"))
            .ToArray();

        var result = demuxer.Feed(trickyBytes);

        result.Should().ContainSingle();
        result[0].Stream.Should().Be(OutputStream.StdOut);
        result[0].Text.Should().Contain("normal log text");
    }

    [Fact]
    public void Feed_in_passthrough_mode_handles_multiple_lines_and_split_buffers()
    {
        var demuxer = new DockerLogDemuxer(demultiplex: false);
        var fullText = System.Text.Encoding.UTF8.GetBytes(
            "2024-01-01T00:00:00.000000000Z line-one\n2024-01-01T00:00:01.000000000Z line-two\n");

        var firstHalf = fullText[..20];
        var secondHalf = fullText[20..];

        var firstResult = demuxer.Feed(firstHalf);
        var secondResult = demuxer.Feed(secondHalf);

        firstResult.Concat(secondResult).Select(l => l.Text).Should().Equal("line-one", "line-two");
    }
}
