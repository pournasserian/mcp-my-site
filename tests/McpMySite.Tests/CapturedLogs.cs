using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace McpMySite.Tests;

/// <summary>A logger provider that keeps every record, for asserting on what was logged and what was not.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<Entry> _entries = new();

    public sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State)
    {
        public object? this[string key] => State.FirstOrDefault(p => p.Key == key).Value;
    }

    public IReadOnlyList<Entry> Entries => _entries.ToList();

    public IReadOnlyList<Entry> In(string category) => Entries.Where(e => e.Category == category).ToList();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    /// <summary>A logger for one category, for code under test that takes an <see cref="ILogger{T}"/>.</summary>
    public ILogger<T> For<T>() => new Logger<T>(new Factory(this));

    public void Dispose()
    {
    }

    private sealed class Logger(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            owner._entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), exception, properties));
        }
    }

    private sealed class Factory(CapturedLogs owner) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => owner.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }
}
