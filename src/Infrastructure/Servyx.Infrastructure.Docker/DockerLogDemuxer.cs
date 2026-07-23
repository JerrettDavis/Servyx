using System.Text;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker;

/// <summary>A single decoded, attributed log line produced by <see cref="DockerLogDemuxer"/>.</summary>
/// <param name="Stream">Which stream (stdout/stderr) the line came from.</param>
/// <param name="Timestamp">The line's timestamp, as reported by the Docker log driver.</param>
/// <param name="Text">The line's text, with the framing header and timestamp prefix removed.</param>
public sealed record DockerLogLine(OutputStream Stream, DateTimeOffset Timestamp, string Text);

/// <summary>
/// Incrementally de-multiplexes a Docker container log stream that was requested without a TTY. Such a
/// stream interleaves stdout and stderr behind repeated 8-byte frame headers:
/// <c>[streamType:1][000:3][length:4 big-endian]</c> followed by exactly <c>length</c> bytes of payload,
/// per the <c>attach</c>/<c>logs</c> Docker Engine API documentation. This class also assembles frame
/// payloads into newline-delimited lines (a single frame is not guaranteed to align with a line
/// boundary) and splits Docker's <c>timestamps=true</c> RFC3339Nano prefix off each line.
/// </summary>
/// <remarks>
/// <see cref="Feed"/> is fully incremental: a frame header or payload split across two separate calls
/// (e.g. across two socket reads) is handled correctly, because all in-progress state is carried in
/// instance fields between calls rather than assumed to arrive in one contiguous buffer.
/// </remarks>
public sealed class DockerLogDemuxer
{
    private enum FrameState
    {
        Header,
        Payload,
    }

    private const byte StdOutStreamType = 1;
    private const byte StdErrStreamType = 2;

    private readonly bool _demultiplex;

    private FrameState _frameState = FrameState.Header;
    private readonly byte[] _headerBuffer = new byte[8];
    private int _headerBytesFilled;
    private OutputStream _currentFrameStream = OutputStream.StdOut;
    private int _payloadBytesRemaining;

    private readonly MemoryStream _stdoutBuffer = new();
    private readonly MemoryStream _stderrBuffer = new();

    /// <summary>
    /// Creates a demultiplexer.
    /// </summary>
    /// <param name="demultiplex">
    /// Whether the input stream carries Docker's 8-byte frame headers at all. Pass <see langword="false"/>
    /// for a container created with a TTY (<c>Config.Tty == true</c>): such containers' log streams are
    /// <em>not</em> framed — stdout and stderr are combined into one plain, unframed byte stream by the
    /// PTY itself — so attempting to parse frame headers out of it would misinterpret the first bytes of
    /// real log text as a header and misparse or hang. In that mode every line is attributed to
    /// <see cref="OutputStream.StdOut"/>, since a TTY-combined stream cannot distinguish the two.
    /// </param>
    public DockerLogDemuxer(bool demultiplex = true)
    {
        _demultiplex = demultiplex;
    }

    /// <summary>
    /// Feeds a chunk of raw bytes into the demultiplexer, returning every line that became complete as a
    /// result (zero or more; a chunk that only completes a partial frame or a partial line yields nothing
    /// until enough data arrives in a subsequent call).
    /// </summary>
    public IReadOnlyList<DockerLogLine> Feed(ReadOnlySpan<byte> chunk)
    {
        if (!_demultiplex)
        {
            var passthroughLines = new List<DockerLogLine>();
            _stdoutBuffer.Write(chunk);
            DrainCompletedLines(_stdoutBuffer, OutputStream.StdOut, passthroughLines);
            return passthroughLines;
        }

        var lines = new List<DockerLogLine>();
        var offset = 0;
        while (offset < chunk.Length)
        {
            if (_frameState == FrameState.Header)
            {
                var need = _headerBuffer.Length - _headerBytesFilled;
                var take = Math.Min(need, chunk.Length - offset);
                chunk.Slice(offset, take).CopyTo(_headerBuffer.AsSpan(_headerBytesFilled));
                _headerBytesFilled += take;
                offset += take;

                if (_headerBytesFilled < _headerBuffer.Length)
                {
                    break; // Header itself split across chunks; wait for the rest.
                }

                _currentFrameStream = _headerBuffer[0] == StdErrStreamType ? OutputStream.StdErr : OutputStream.StdOut;
                _payloadBytesRemaining =
                    (_headerBuffer[4] << 24) | (_headerBuffer[5] << 16) | (_headerBuffer[6] << 8) | _headerBuffer[7];
                _headerBytesFilled = 0;
                _frameState = _payloadBytesRemaining == 0 ? FrameState.Header : FrameState.Payload;
            }
            else
            {
                var target = _currentFrameStream == OutputStream.StdErr ? _stderrBuffer : _stdoutBuffer;
                var take = Math.Min(_payloadBytesRemaining, chunk.Length - offset);
                target.Write(chunk.Slice(offset, take));
                offset += take;
                _payloadBytesRemaining -= take;

                if (_payloadBytesRemaining == 0)
                {
                    _frameState = FrameState.Header;

                    // Drain as soon as this frame completes, rather than batching all draining until
                    // the whole chunk has been processed, so that stdout/stderr lines are yielded in
                    // true arrival order instead of being grouped by stream.
                    DrainCompletedLines(target, _currentFrameStream, lines);
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// Scans a per-stream byte buffer for complete (newline-terminated) lines, decodes and yields them,
    /// and compacts the buffer down to just the trailing partial line (if any). Scanning at the byte
    /// level (rather than decoding first) is safe here because <c>'\n'</c> (0x0A) can never appear as a
    /// continuation byte of a multi-byte UTF-8 sequence.
    /// </summary>
    private static void DrainCompletedLines(MemoryStream buffer, OutputStream stream, List<DockerLogLine> output)
    {
        var bytes = buffer.GetBuffer();
        var length = (int)buffer.Length;

        var segmentStart = 0;
        var lastLineEnd = -1;

        for (var i = 0; i < length; i++)
        {
            if (bytes[i] != (byte)'\n')
            {
                continue;
            }

            var lineLength = i - segmentStart;
            if (lineLength > 0 && bytes[segmentStart + lineLength - 1] == (byte)'\r')
            {
                lineLength--;
            }

            var text = Encoding.UTF8.GetString(bytes, segmentStart, lineLength);
            output.Add(SplitTimestamp(stream, text));

            segmentStart = i + 1;
            lastLineEnd = i;
        }

        if (lastLineEnd < 0)
        {
            return; // No complete line yet.
        }

        var remainderLength = length - segmentStart;
        if (remainderLength == 0)
        {
            buffer.SetLength(0);
            return;
        }

        var remainder = new byte[remainderLength];
        Array.Copy(bytes, segmentStart, remainder, 0, remainderLength);
        buffer.SetLength(0);
        buffer.Write(remainder, 0, remainderLength);
    }

    /// <summary>
    /// Splits Docker's <c>timestamps=true</c> RFC3339Nano prefix (e.g.
    /// <c>2024-01-01T00:00:00.123456789Z </c>) off the front of a decoded line. Falls back to the
    /// current time and the line unmodified if no valid timestamp prefix is present.
    /// </summary>
    private static DockerLogLine SplitTimestamp(OutputStream stream, string line)
    {
        var spaceIndex = line.IndexOf(' ');
        if (spaceIndex > 0)
        {
            var candidate = line[..spaceIndex];
            if (DateTimeOffset.TryParse(
                    candidate,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var timestamp))
            {
                return new DockerLogLine(stream, timestamp, line[(spaceIndex + 1)..]);
            }
        }

        return new DockerLogLine(stream, DateTimeOffset.UtcNow, line);
    }
}
