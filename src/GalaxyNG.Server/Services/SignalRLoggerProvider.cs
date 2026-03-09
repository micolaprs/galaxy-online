namespace GalaxyNG.Server.Services;

/// <summary>
/// Plugs into the ASP.NET logging pipeline and forwards game-relevant log entries
/// to <see cref="LogBroadcastService"/> so they can be pushed to connected
/// browser clients in real time.
/// Infrastructure noise (HTTP request logs, SignalR transport, Kestrel, etc.)
/// is filtered out — only game service and bot logs reach the console.
/// </summary>
public sealed class SignalRLoggerProvider(LogBroadcastService broadcaster) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        new SignalRLogger(categoryName, broadcaster);

    public void Dispose() { }
}

file sealed class SignalRLogger(string category, LogBroadcastService broadcaster) : ILogger
{
    // Only forward log entries from game-relevant namespaces.
    // Everything else (ASP.NET HTTP pipeline, SignalR transport, Kestrel, etc.)
    // is suppressed to keep the in-game console readable.
    private static readonly string[] AllowedPrefixes =
    [
        "GalaxyNG.",          // all our own services, controllers, hubs
      "Bot[",               // bot remote log entries forwarded via /api/logs/ingest
    ];

    private static string ShortCategory(string cat)
    {
        var dot = cat.LastIndexOf('.');
        return dot >= 0 ? cat[(dot + 1)..] : cat;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel < LogLevel.Information)
        {
            return false;
        }

        foreach (var prefix in AllowedPrefixes)
        {
            if (category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        // Always forward warnings/errors regardless of category
        return logLevel >= LogLevel.Warning;
    }

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var msg = formatter(state, exception);
        if (exception is not null)
        {
            msg += $"\n{exception.GetType().Name}: {exception.Message}";
        }

        broadcaster.Broadcast(logLevel.ToString(), ShortCategory(category), msg);
    }
}
