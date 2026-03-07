using GalaxyNG.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace GalaxyNG.Server.Controllers;

/// <summary>
/// Receives log entries from external processes (bots) and rebroadcasts
/// them to all SignalR clients subscribed to the "server-logs" group.
/// </summary>
[ApiController]
[Route("api/logs")]
public sealed class LogsController(LogBroadcastService broadcaster) : ControllerBase
{
    // POST /api/logs/ingest — bot or external process forwards a log entry
    [HttpPost("ingest")]
    public IActionResult Ingest([FromBody] IngestLogRequest req)
    {
        broadcaster.Broadcast(req.Level, req.Category, req.Message);
        return Ok();
    }
}

public sealed record IngestLogRequest(
    string Level,
    string Category,
    string Message
);
