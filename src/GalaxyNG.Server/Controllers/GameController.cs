using GalaxyNG.Engine.Services;
using GalaxyNG.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace GalaxyNG.Server.Controllers;

[ApiController]
[Route("api/games")]
public sealed class GameController(GameService svc) : ControllerBase
{
    // POST /api/games — create a new game
    [HttpPost]
    public async Task<IActionResult> CreateGame([FromBody] CreateGameRequest req, CancellationToken ct)
    {
        if (req.Players is not { Count: >= 1 })
            return BadRequest("At least one player required.");

        var opts = req.GalaxySize.HasValue ? new GalaxyGeneratorOptions
        {
            GalaxySize   = req.GalaxySize.Value,
            PlayerCount  = req.Players.Count,
            MinDist      = req.MinDist ?? GalaxyGenerator.DefaultOptions(req.Players.Count).MinDist,
            StuffPlanets = req.StuffPlanets ?? 5,
        } : null;

        var players = req.Players.Select(p => (p.Name, p.Password, p.IsBot)).ToList();
        var game = await svc.CreateGameAsync(req.Name, players, opts, req.AutoRun, ct);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Created($"{baseUrl}/api/games/{game.Id}", new
        {
            gameId   = game.Id,
            joinLink = $"{baseUrl}/?game={game.Id}",
            players  = game.Players.Values.Select(p => new { p.Id, p.Name, p.Password }),
            turn     = game.Turn,
        });
    }

    // GET /api/games — list all games
    [HttpGet]
    public async Task<IActionResult> ListGames(CancellationToken ct)
    {
        var games = await svc.ListGamesAsync(ct);
        return Ok(games.Select(g => new
        {
            g.Id, g.Name, g.Turn,
            playerCount = g.Players.Count,
            g.LastTurnRunAt,
        }));
    }

    // GET /api/games/{id} — game info
    [HttpGet("{id}")]
    public async Task<IActionResult> GetGame(string id, CancellationToken ct)
    {
        var game = await svc.GetGameAsync(id, ct);
        return game is null ? NotFound() : Ok(new
        {
            game.Id, game.Name, game.Turn, game.GalaxySize,
            players = game.Players.Values.Select(p => new
            {
                p.Name, p.Tech, p.IsBot, p.IsEliminated, p.Submitted,
            }),
            planetCount = game.Planets.Count,
        });
    }

    // POST /api/games/{id}/orders — submit orders
    [HttpPost("{id}/orders")]
    public async Task<IActionResult> SubmitOrders(string id, [FromBody] SubmitOrdersRequest req, CancellationToken ct)
    {
        var (ok, error) = await svc.SubmitOrdersAsync(
            id, req.RaceName, req.Password, req.Orders, req.Final, ct);

        return ok ? Ok(new { submitted = true })
                  : BadRequest(new { error });
    }

    // GET /api/games/{id}/report/{race}?password=xxx — turn report
    [HttpGet("{id}/report/{race}")]
    public async Task<IActionResult> GetReport(string id, string race,
        [FromQuery] string password, CancellationToken ct)
    {
        var report = await svc.GetReportAsync(id, race, password, ct);
        return report is null ? NotFound() : Content(report, "text/plain");
    }

    // GET /api/games/{id}/forecast/{race}?password=xxx — forecast
    [HttpGet("{id}/forecast/{race}")]
    public async Task<IActionResult> GetForecast(string id, string race,
        [FromQuery] string password, [FromQuery] string orders = "", CancellationToken ct = default)
    {
        var forecast = await svc.GetForecastAsync(id, race, password, orders, ct);
        return forecast is null ? NotFound() : Content(forecast, "text/plain");
    }

    // GET /api/games/{id}/spectate — public game state (no auth required)
    [HttpGet("{id}/spectate")]
    public async Task<IActionResult> GetSpectate(string id, CancellationToken ct)
    {
        var game = await svc.GetGameAsync(id, ct);
        if (game is null) return NotFound();
        return Ok(new
        {
            game.Id, game.Name, game.Turn, game.GalaxySize,
            game.LastTurnRunAt, game.AutoRunOnAllSubmitted,
            players = game.Players.Values.Select(p => new
            {
                p.Id, p.Name, p.IsBot, p.Submitted, p.IsEliminated,
                p.Tech,
                planetCount = game.PlanetsOwnedBy(p.Id).Count(),
            }),
            planets = game.Planets.Values.Select(p => new
            {
                p.Name, p.X, p.Y, p.Size, p.OwnerId, p.Population,
            }),
            battles  = game.Battles.Select(b => new
            {
                b.PlanetName, b.Winner, b.Participants,
            }),
            bombings = game.Bombings.Select(b => new
            {
                b.PlanetName, b.AttackerRace, b.PreviousOwner,
            }),
        });
    }

    // POST /api/games/{id}/run-turn — manually trigger turn
    [HttpPost("{id}/run-turn")]
    public async Task<IActionResult> RunTurn(string id, CancellationToken ct)
    {
        var (ok, error) = await svc.RunTurnAsync(id, ct);
        return ok ? Ok(new { message = "Turn completed." })
                  : BadRequest(new { error });
    }
}

// ---- DTOs ----

public sealed record PlayerInput(string Name, string Password, bool IsBot = false);

public sealed record CreateGameRequest(
    string            Name,
    List<PlayerInput> Players,
    double? GalaxySize   = null,
    double? MinDist      = null,
    int?    StuffPlanets = null,
    bool    AutoRun      = false
);

public sealed record SubmitOrdersRequest(
    string RaceName,
    string Password,
    string Orders,
    bool   Final = false
);
