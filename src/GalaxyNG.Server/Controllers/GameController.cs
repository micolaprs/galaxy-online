using GalaxyNG.Engine.Models;
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

        // maxTurns is the primary input; galaxy size is derived from it.
        int maxTurns = req.MaxTurns is > 0 ? req.MaxTurns.Value : 60;

        GalaxyGeneratorOptions? opts = req.GalaxySize.HasValue
            ? new GalaxyGeneratorOptions
            {
                GalaxySize   = req.GalaxySize.Value,
                PlayerCount  = req.Players.Count,
                MinDist      = req.MinDist ?? GalaxyGenerator.DefaultOptions(req.Players.Count, maxTurns).MinDist,
                StuffPlanets = req.StuffPlanets ?? 5,
            }
            : GalaxyGenerator.DefaultOptions(req.Players.Count, maxTurns);

        var players = req.Players.Select(p => (p.Name, p.Password, p.IsBot)).ToList();
        var game = await svc.CreateGameAsync(req.Name, players, opts, req.AutoRun, maxTurns, ct);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Created($"{baseUrl}/api/games/{game.Id}", new
        {
            gameId   = game.Id,
            joinLink = $"{baseUrl}/?game={game.Id}",
            players  = game.Players.Values.Select(p => new { p.Id, p.Name, p.Password }),
            turn     = game.Turn,
            maxTurns = game.MaxTurns,
        });
    }

    // DELETE /api/games — wipe all saved games (dev/admin use)
    [HttpDelete]
    public async Task<IActionResult> DeleteAllGames(CancellationToken ct)
    {
        await svc.DeleteAllGamesAsync(ct);
        return Ok(new { message = "All games deleted." });
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
            g.MaxTurns,
            g.IsFinished,
            g.WinnerName,
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
            game.MaxTurns, game.IsFinished, game.WinnerPlayerId, game.WinnerName, game.FinishReason,
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

    // POST /api/games/{id}/validate-orders — validate orders and return structured errors
    [HttpPost("{id}/validate-orders")]
    public async Task<IActionResult> ValidateOrders(string id, [FromBody] ValidateOrdersRequest req, CancellationToken ct)
    {
        var game = await svc.GetGameAsync(id, ct);
        if (game is null) return NotFound();

        var player = game.Players.Values.FirstOrDefault(p =>
            string.Equals(p.Name, req.RaceName, StringComparison.OrdinalIgnoreCase));
        if (player is null) return NotFound(new { error = $"Player '{req.RaceName}' not found." });
        if (player.Password != req.Password) return Unauthorized(new { error = "Wrong password." });

        var (parsed, parseErrors) = new GalaxyNG.Engine.Services.OrderParser().Parse(req.Orders ?? "");
        var validator = new GalaxyNG.Engine.Services.OrderValidator(game, player);
        var results = validator.ValidateAll(parsed);
        var allErrors = parseErrors
            .Concat(results.Where(r => !r.Ok).Select(r => r.Error ?? "Unknown error"))
            .ToList();

        return Ok(new
        {
            valid  = allErrors.Count == 0,
            errors = allErrors,
        });
    }

    // GET /api/games/{id}/spectate — public game state (no auth required)
    [HttpGet("{id}/spectate")]
    public async Task<IActionResult> GetSpectate(string id, CancellationToken ct)
    {
        var game = await svc.GetGameAsync(id, ct);
        if (game is null) return NotFound();

        var fleetRoutes = game.Players.Values
            .SelectMany(p => p.Groups
                .Where(g =>
                    (g.InHyperspace
                     && !string.IsNullOrWhiteSpace(g.Origin)
                     && !string.IsNullOrWhiteSpace(g.Destination)
                     && !string.Equals(g.Origin, g.Destination, StringComparison.OrdinalIgnoreCase))
                    ||
                    (!g.InHyperspace
                     && !string.IsNullOrWhiteSpace(g.LastRouteOrigin)
                     && !string.IsNullOrWhiteSpace(g.LastRouteDestination)
                     && !string.Equals(g.LastRouteOrigin, g.LastRouteDestination, StringComparison.OrdinalIgnoreCase)
                     && game.Turn - g.LastRouteTurn <= 4))
                .Select(g =>
                {
                    var origin = g.InHyperspace ? g.Origin! : g.LastRouteOrigin!;
                    var destination = g.InHyperspace ? g.Destination! : g.LastRouteDestination!;

                    var totalDistance = 0.0;
                    if (game.Planets.TryGetValue(origin, out var op) && game.Planets.TryGetValue(destination, out var dp))
                        totalDistance = op.DistanceTo(dp);

                    var remainingDistance = g.InHyperspace ? Math.Max(0, g.Distance) : 0;
                    var progress = totalDistance > 0
                        ? Math.Clamp(1 - (remainingDistance / totalDistance), 0, 1)
                        : 1;

                    var speed = p.ShipTypes.TryGetValue(g.ShipTypeName, out var st)
                        ? st.SpeedLoaded(g.Tech.Drive, g.Tech.Cargo, g.CargoLoad)
                        : g.LastRouteSpeed;

                    return new
                    {
                        speed,
                        progress,
                        ownerId = p.Id,
                        fleetName = string.IsNullOrWhiteSpace(g.FleetName) ? $"Group {g.Number}" : g.FleetName!,
                        origin,
                        destination,
                        ships = g.Ships,
                        active = g.InHyperspace,
                    };
                }))
            .GroupBy(r => new { r.ownerId, r.fleetName, r.origin, r.destination, r.active })
            .Select(g => new
            {
                g.Key.ownerId,
                g.Key.fleetName,
                g.Key.origin,
                g.Key.destination,
                ships = g.Sum(x => x.ships),
                active = g.Key.active,
                speed = g.Sum(x => x.speed * Math.Max(1, x.ships)) / Math.Max(1, g.Sum(x => Math.Max(1, x.ships))),
                progress = g.Sum(x => x.progress * Math.Max(1, x.ships)) / Math.Max(1, g.Sum(x => Math.Max(1, x.ships))),
            })
            .ToList();

        var playerInfos = game.Players.Values
            .Select(p => new
            {
                p.Id, p.Name, p.IsBot, p.Submitted, p.IsEliminated,
                p.Tech,
                planetCount = game.PlanetsOwnedBy(p.Id).Count(),
            })
            .ToList();

        var globalMessages = game.DiplomacyMessages
            .Where(m => m.RecipientIds.Count == 0)
            .OrderByDescending(m => m.SentAt)
            .Take(80)
            .OrderBy(m => m.SentAt)
            .Select(m => new
            {
                m.Id,
                m.Turn,
                m.SentAt,
                senderId = m.SenderId,
                senderName = m.SenderName,
                text = UiTextPolicy.Clean(m.Text, 240),
            })
            .ToList();

        var visibility = BuildVisibilityMap(game);
        var privateChats = BuildPrivateChats(game, visibility);

        return Ok(new
        {
            game.Id, game.Name, game.Turn, game.GalaxySize,
            game.LastTurnRunAt, game.AutoRunOnAllSubmitted,
            game.MaxTurns, game.IsFinished, game.WinnerPlayerId, game.WinnerName, game.FinishReason,
            players = playerInfos,
            planets = game.Planets.Values.Select(p => new
            {
                p.Name, p.X, p.Y, p.Size, p.OwnerId, p.Population,
                hasShips = game.Players.Values
                    .Any(pl => pl.Groups.Any(g => g.At == p.Name && !g.InHyperspace)),
            }),
            battles  = game.Battles.Select(b => new
            {
                b.PlanetName, b.Winner, b.Participants,
            }),
            bombings = game.Bombings.Select(b => new
            {
                b.PlanetName, b.AttackerRace, b.PreviousOwner,
            }),
            fleetRoutes,
            diplomacy = new
            {
                globalMessages,
                privateChats,
            },
        });
    }

    // GET /api/games/{id}/spectate/planet/{name} — detailed public planet info
    [HttpGet("{id}/spectate/planet/{name}")]
    public async Task<IActionResult> GetPlanetDetail(string id, string name, CancellationToken ct)
    {
        var game = await svc.GetGameAsync(id, ct);
        if (game is null) return NotFound();
        var planet = game.GetPlanet(name);
        if (planet is null) return NotFound();

        var owner = planet.OwnerId is not null
            ? game.Players.GetValueOrDefault(planet.OwnerId)
            : null;

        var groups = game.Players.Values
            .SelectMany(p => p.Groups
                .Where(g => g.At == planet.Name && !g.InHyperspace)
                .Select(g => new { g.Ships, g.ShipTypeName, ownerName = p.Name, ownerId = p.Id }))
            .ToList();

        return Ok(new
        {
            planet.Name, planet.X, planet.Y, planet.Size, planet.Resources,
            planet.Population, planet.Industry, planet.OwnerId,
            ownerName    = owner?.Name,
            planet.IsHome,
            production   = planet.Production,
            producing    = planet.Producing.ToString(),
            planet.ShipTypeName,
            stockpiles   = new
            {
                capital   = planet.Stockpiles.Capital,
                materials = planet.Stockpiles.Materials,
                colonists = planet.Stockpiles.Colonists,
            },
            groups,
        });
    }

    // GET /api/games/{id}/history — turn history list
    [HttpGet("{id}/history")]
    public async Task<IActionResult> GetHistory(string id, CancellationToken ct)
    {
        var game = await svc.GetGameAsync(id, ct);
        if (game is null) return NotFound();
        return Ok(game.TurnHistory
            .OrderByDescending(h => h.Turn)
            .Select(h => new
            {
                h.Turn,
                h.RunAt,
                players      = h.PlayerOrders.Keys.ToList(),
                battleCount  = h.Battles.Count,
                bombingCount = h.Bombings.Count,
                battles      = h.Battles,
                bombings     = h.Bombings,
            }));
    }

    // GET /api/games/{id}/history/{turn}/player/{race} — player orders for a specific turn
    [HttpGet("{id}/history/{turn}/player/{race}")]
    public async Task<IActionResult> GetTurnPlayerOrders(
        string id, int turn, string race, CancellationToken ct)
    {
        var game = await svc.GetGameAsync(id, ct);
        if (game is null) return NotFound();
        var hist = game.TurnHistory.FirstOrDefault(h => h.Turn == turn);
        if (hist is null) return NotFound();

        hist.PlayerOrders.TryGetValue(race, out var orders);
        hist.PlayerReasoning.TryGetValue(race, out var reasoning);
        var safeOrders    = UiTextPolicy.Clean(orders, 2200);
        var safeReasoning = UiTextPolicy.Clean(reasoning, 2200);
        return Ok(new
        {
            turn, race,
            orders    = safeOrders,
            reasoning = safeReasoning,
            battles   = hist.Battles,
            bombings  = hist.Bombings,
        });
    }

    // POST /api/games/{id}/history/{turn}/player/{race}/summary — LLM turn summary
    [HttpPost("{id}/history/{turn}/player/{race}/summary")]
    public async Task<IActionResult> GetTurnSummary(
        string id, int turn, string race, CancellationToken ct)
    {
        var summary = await svc.GenerateTurnSummaryAsync(id, race, turn, ct);
        return summary is null
            ? Problem("LLM not available or turn not found.")
            : Ok(new { summary });
    }

    // GET /api/games/{id}/ai/summaries — list saved galaxy summaries
    [HttpGet("{id}/ai/summaries")]
    public async Task<IActionResult> GetAiSummaries(string id, CancellationToken ct)
    {
        var summaries = await svc.GetAiSummariesAsync(id, ct);
        return Ok(summaries
            .OrderByDescending(s => s.Turn)
            .Select(s => new
            {
                s.Turn,
                Summary = UiTextPolicy.Clean(s.Summary, 900),
                s.GeneratedAt,
            }));
    }

    // POST /api/games/{id}/ai/summary — generate + save galaxy summary
    [HttpPost("{id}/ai/summary")]
    public async Task<IActionResult> GenerateAiSummary(string id, CancellationToken ct)
    {
        var summary = await svc.GenerateGalaxySummaryAsync(id, ct);
        return summary is null
            ? Problem("LLM not available or game not found.")
            : Ok(new { summary });
    }

    // POST /api/games/{id}/run-turn — manually trigger turn
    [HttpPost("{id}/run-turn")]
    public async Task<IActionResult> RunTurn(string id, CancellationToken ct)
    {
        var (ok, error) = await svc.RunTurnAsync(id, ct);
        return ok ? Ok(new { message = "Turn completed." })
                  : BadRequest(new { error });
    }

    // POST /api/games/{id}/bot-status — bot reports its current activity
    [HttpPost("{id}/bot-status")]
    public async Task<IActionResult> PostBotStatus(
        string id, [FromBody] BotStatusRequest req, CancellationToken ct)
    {
        await svc.BroadcastBotStatusAsync(id, req.RaceName, req.Status, req.Detail, req.Thinking, ct);
        return Ok();
    }

    private static Dictionary<string, HashSet<string>> BuildVisibilityMap(Game game)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var sensorRange = Math.Max(24, game.GalaxySize * 0.6);
        foreach (var player in game.Players.Values)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var anchors = new List<Planet>();
            foreach (var planet in game.PlanetsOwnedBy(player.Id))
            {
                names.Add(planet.Name);
                anchors.Add(planet);
            }

            foreach (var group in player.Groups.Where(g => !g.InHyperspace))
            {
                names.Add(group.At);
                if (game.Planets.TryGetValue(group.At, out var atPlanet))
                    anchors.Add(atPlanet);
            }

            foreach (var anchor in anchors)
            {
                foreach (var candidate in game.Planets.Values)
                {
                    if (anchor.DistanceTo(candidate) <= sensorRange)
                        names.Add(candidate.Name);
                }
            }

            map[player.Id] = names;
        }

        // Allies share map visibility while alliance is active.
        foreach (var player in game.Players.Values)
        {
            if (!map.TryGetValue(player.Id, out var mine))
                continue;

            foreach (var allyId in player.Allies)
            {
                if (player.AllianceUntilTurn.TryGetValue(allyId, out var until) && game.Turn > until)
                    continue;
                if (!map.TryGetValue(allyId, out var allyVision))
                    continue;
                mine.UnionWith(allyVision);
            }
        }

        return map;
    }

    private static List<object> BuildPrivateChats(
        Game game,
        Dictionary<string, HashSet<string>> visibilityByPlayer)
    {
        var players = game.Players.Values.OrderBy(p => p.Name).ToList();
        var chats = new List<object>();

        for (int i = 0; i < players.Count; i++)
        {
            for (int j = i + 1; j < players.Count; j++)
            {
                var p1 = players[i];
                var p2 = players[j];
                if (!game.IdentifiedContactPairs.Contains(BuildPairChannelId(p1.Id, p2.Id)))
                    continue;

                var overlap = visibilityByPlayer[p1.Id]
                    .Intersect(visibilityByPlayer[p2.Id], StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name)
                    .ToList();

                var messages = game.DiplomacyMessages
                    .Where(m => IsPrivatePairMessage(m, p1.Id, p2.Id))
                    .OrderByDescending(m => m.SentAt)
                    .Take(60)
                    .OrderBy(m => m.SentAt)
                    .Select(m => new
                    {
                        m.Id,
                        m.Turn,
                        m.SentAt,
                        senderId = m.SenderId,
                        senderName = m.SenderName,
                        text = UiTextPolicy.Clean(m.Text, 240),
                    })
                    .ToList();

                chats.Add(new
                {
                    channelId = BuildPairChannelId(p1.Id, p2.Id),
                    playerAId = p1.Id,
                    playerAName = p1.Name,
                    playerBId = p2.Id,
                    playerBName = p2.Name,
                    overlapPlanets = overlap.Take(4).ToList(),
                    messages,
                });
            }
        }

        return chats;
    }

    private static string BuildPairChannelId(string playerAId, string playerBId)
    {
        return string.CompareOrdinal(playerAId, playerBId) <= 0
            ? $"{playerAId}:{playerBId}"
            : $"{playerBId}:{playerAId}";
    }

    private static bool IsPrivatePairMessage(DiplomacyMessage message, string playerAId, string playerBId)
    {
        if (message.RecipientIds.Count == 0)
            return false;

        var participants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { message.SenderId };
        foreach (var recipientId in message.RecipientIds)
            participants.Add(recipientId);

        return participants.Count == 2 && participants.Contains(playerAId) && participants.Contains(playerBId);
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
    bool    AutoRun      = false,
    int?    MaxTurns     = null
);

public sealed record SubmitOrdersRequest(
    string RaceName,
    string Password,
    string Orders,
    bool   Final = false
);

public sealed record BotStatusRequest(
    string  RaceName,
    string  Status,
    string? Detail   = null,
    string? Thinking = null
);

public sealed record ValidateOrdersRequest(
    string  RaceName,
    string  Password,
    string? Orders = null
);
