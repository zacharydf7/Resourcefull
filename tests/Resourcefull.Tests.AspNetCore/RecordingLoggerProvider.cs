using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Resourcefull.Tests.AspNetCore;

/// <summary>Records every message logged at Information or above, with its category.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(string Category, string Message)> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Messages);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(string, string)> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) messages.Enqueue((category, formatter(state, exception)));
        }
    }
}
