namespace GalaxyNG.Server.Services;

/// <summary>
/// Plugs into the ASP.NET logging pipeline and forwards every log entry
/// to <see cref="LogBroadcastService"/> so it can be pushed to connected
/// browser clients in real time.
/// </summary>
public sealed class SignalRLoggerProvider(LogBroadcastService broadcaster) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        new SignalRLogger(categoryName, broadcaster);

    public void Dispose() { }
}

file sealed class SignalRLogger(string category, LogBroadcastService broadcaster) : ILogger
{
    // Strip long namespace prefix to keep messages concise in the console
    private static string ShortCategory(string cat)
    {
        var dot = cat.LastIndexOf('.');
        return dot >= 0 ? cat[(dot + 1)..] : cat;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var msg = formatter(state, exception);
        if (exception is not null)
            msg += $"\n{exception.GetType().Name}: {exception.Message}";

        broadcaster.Broadcast(logLevel.ToString(), ShortCategory(category), msg);
    }
}
