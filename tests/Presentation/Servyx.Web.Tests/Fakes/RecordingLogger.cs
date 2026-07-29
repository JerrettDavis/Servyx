using Microsoft.Extensions.Logging;

namespace Servyx.Web.Tests.Fakes;

/// <summary>An <see cref="ILogger"/> that keeps every entry written to it, for asserting on log output.</summary>
public sealed class RecordingLogger : ILogger
{
    /// <summary>One recorded log entry.</summary>
    /// <param name="Level">The severity it was written at.</param>
    /// <param name="EventId">The event id it carried.</param>
    /// <param name="Message">The fully formatted message.</param>
    public sealed record Entry(LogLevel Level, EventId EventId, string Message);

    private readonly List<Entry> _entries = [];

    /// <summary>Everything written, in order.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

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
        _entries.Add(new Entry(logLevel, eventId, formatter(state, exception)));
    }
}
