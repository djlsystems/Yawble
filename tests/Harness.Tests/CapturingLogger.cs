using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>A logger that keeps every line it is given, for a test that asserts on what was logged.</summary>
public sealed class CapturingLogger : ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception), exception));
}
