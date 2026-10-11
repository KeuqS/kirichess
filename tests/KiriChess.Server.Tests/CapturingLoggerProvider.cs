using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KiriChess.Server.Tests;

/// <summary>
/// Captures formatted log messages instead of writing them anywhere, so a test can assert that an
/// audit line (9d.3) was actually produced rather than trusting the source read. Registered per test
/// via <c>ConfigureTestServices(services => services.AddLogging(b => b.AddProvider(provider)))</c> -
/// see LeaderboardCacheTests for the same ConfigureTestServices pattern used for a live dependency
/// instead of a capture sink.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

    public void Dispose() { }

    private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception));
    }
}
