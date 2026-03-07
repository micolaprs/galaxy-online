using GalaxyNG.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace GalaxyNG.Server.Services;

/// <summary>
/// Singleton that forwards structured log entries to all SignalR clients
/// subscribed to the "server-logs" group. Must be initialized with the
/// hub context after the app is built (see Program.cs).
/// </summary>
public sealed class LogBroadcastService
{
    private IHubContext<GameHub>? _hub;

    public void Initialize(IHubContext<GameHub> hub) => _hub = hub;

    public void Broadcast(string level, string category, string message)
    {
        if (_hub is null) return;
        _ = _hub.Clients.Group("server-logs").SendAsync("LogEntry", new
        {
            level,
            category,
            message,
            time = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff"),
        });
    }
}
