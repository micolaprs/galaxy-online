using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace GalaxyNG.Bot.Services;

/// <summary>
/// Forwards every log line from the bot process to the server's
/// POST /api/logs/ingest endpoint so they appear in the web console.
/// Fire-and-forget — never blocks the caller.
/// </summary>
public sealed class RemoteLoggerProvider(
    string serverUrl,
    string raceName) : ILoggerProvider
{
    // One shared HttpClient for all loggers in this provider
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public ILogger CreateLogger(string categoryName) =>
        new RemoteLogger(serverUrl, raceName, categoryName, Http);

    public void Dispose() { }
}

file sealed class RemoteLogger(
    string serverUrl,
    string raceName,
    string category,
    HttpClient http) : ILogger
{
    // Show only the short class name to keep messages readable
    private static string Short(string c)
    {
        var dot = c.LastIndexOf('.');
        return dot >= 0 ? c[(dot + 1)..] : c;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    // Forward Debug and above (skip Trace — too noisy)
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var msg = formatter(state, exception);
        if (exception is not null)
            msg += $"\n{exception.GetType().Name}: {exception.Message}";

        var entry = new
        {
            level    = logLevel.ToString(),
            category = $"Bot[{raceName}]/{Short(category)}",
            message  = msg,
        };

        // POST fire-and-forget — ignore any errors to not disturb the bot
        _ = http.PostAsJsonAsync($"{serverUrl}/api/logs/ingest", entry)
                .ContinueWith(_ => { /* swallow */ }, TaskContinuationOptions.None);
    }
}
