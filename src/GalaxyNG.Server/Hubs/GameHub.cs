using Microsoft.AspNetCore.SignalR;

namespace GalaxyNG.Server.Hubs;

public sealed class GameHub : Hub
{
    public async Task JoinGameGroup(string gameId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
        await Clients.Caller.SendAsync("Joined", gameId);
    }

    public async Task LeaveGameGroup(string gameId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, gameId);

    /// <summary>Subscribe to all server log entries broadcast in real time.</summary>
    public async Task JoinLogsGroup()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "server-logs");
        await Clients.Caller.SendAsync("LogsJoined");
    }

    public async Task LeaveLogsGroup() =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "server-logs");
}
