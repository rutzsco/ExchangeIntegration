using Microsoft.Extensions.Logging;

namespace ExchangeIntegration.Tests.TestSupport;

public sealed record LogEntry(
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, string?> Properties,
    Exception? Exception);

/// <summary>Captures structured log output so tests can assert on it, including what must never appear in it.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = new Dictionary<string, string?>();
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
            {
                properties[pair.Key] = pair.Value?.ToString();
            }
        }

        _entries.Add(new LogEntry(logLevel, formatter(state, exception), properties, exception));
    }

    /// <summary>Everything that was logged flattened into one string, for "must not contain" assertions.</summary>
    public string Everything() => string.Join(
        '\n',
        _entries.Select(entry =>
            $"{entry.Message} {string.Join(' ', entry.Properties.Select(pair => $"{pair.Key}={pair.Value}"))} {entry.Exception}"));
}
