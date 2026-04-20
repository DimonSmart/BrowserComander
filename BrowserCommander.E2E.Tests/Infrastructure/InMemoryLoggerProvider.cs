using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

namespace BrowserCommander.E2E.Tests.Infrastructure;

internal sealed class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    public string GetSnapshot()
    {
        var builder = new StringBuilder();
        foreach (var entry in _entries)
        {
            builder.AppendLine(entry);
        }

        return builder.ToString();
    }

    private sealed class InMemoryLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly ConcurrentQueue<string> _entries;

        public InMemoryLogger(string categoryName, ConcurrentQueue<string> entries)
        {
            _categoryName = categoryName;
            _entries = entries;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var exceptionText = exception is null ? string.Empty : Environment.NewLine + exception;
            _entries.Enqueue(
                $"{DateTimeOffset.UtcNow:O} [{logLevel}] {_categoryName}: {message}{exceptionText}");
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
