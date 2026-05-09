using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TodoApi.Tests.Sync.TestHelpers;

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentBag<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

    public void Dispose() { }

    public sealed record LogEntry(
        LogLevel Level,
        string Category,
        string Message,
        Exception? Exception
    );

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentBag<LogEntry> _entries;

        public CapturingLogger(string category, ConcurrentBag<LogEntry> entries)
        {
            _category = category;
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            _entries.Add(new LogEntry(logLevel, _category, formatter(state, exception), exception));
        }
    }
}
