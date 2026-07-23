using Microsoft.Extensions.Logging;

namespace Servyx.Infrastructure.Ssh.Tests.Integration;

/// <summary>A minimal <see cref="ILogger"/> that records formatted messages by level, for assertions like "no warning was logged".</summary>
internal sealed class TestLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
