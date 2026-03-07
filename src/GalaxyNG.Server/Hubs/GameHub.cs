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
}
