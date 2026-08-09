using Microsoft.Extensions.Logging;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// An <see cref="ILoggerFactory"/> whose loggers all write into one shared list, for asserting on what a
/// bootstrap-phase composition actually told the operator.
/// </summary>
/// <remarks>
/// Bootstrap-phase loggers are created before <c>builder.Build()</c> exists, so they never go through the
/// container's logging pipeline — a test that wants to see what they said has to supply the factory itself,
/// exactly as a stdio-transport MCP host must. See <c>AddServyxCore</c>'s <c>bootstrapLoggerFactory</c>
/// parameter.
/// </remarks>
public sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly List<RecordingLogger.Entry> _entries = [];

    /// <summary>Everything written through every logger this factory created, in order.</summary>
    public IReadOnlyList<RecordingLogger.Entry> Entries => _entries;

    /// <summary>Whether this factory was disposed. The composition must never dispose one it was handed.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new SharedLogger(_entries);

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        // Not exercised by this composition; no bootstrap-phase code path registers a provider.
    }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;

    private sealed class SharedLogger(List<RecordingLogger.Entry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            entries.Add(new RecordingLogger.Entry(logLevel, eventId, formatter(state, exception)));
        }
    }
}
