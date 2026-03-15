using System.ComponentModel;
using GalaxyNG.Engine.Models;
using GalaxyNG.Engine.Services;
using GalaxyNG.Server.Services;
using ModelContextProtocol.Server;

namespace GalaxyNG.Server.Mcp;

[McpServerToolType]
public sealed class GameTools(GameService svc)
{
    [McpServerTool, Description("Get general information about a game: galaxy size, current turn, list of races.")]
    public async Task<string> GetGameInfo(
        [Description("The game ID")] string gameId,
        CancellationToken ct = default)
    {
        var game = await svc.GetGameAsync(gameId, ct);
        if (game is null)
        {
            return $"Game '{gameId}' not found.";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Game: {game.Name} (ID: {game.Id})");
        sb.AppendLine($"Turn: {game.Turn}");
        sb.AppendLine($"Galaxy size: {game.GalaxySize} ly");
        sb.AppendLine($"Planets: {game.Planets.Count}");
        sb.AppendLine("Races:");
        foreach (var p in game.Players.Values)
        {
            string status = p.IsEliminated ? " [ELIMINATED]" : p.IsBot ? " [BOT]" : "";
            sb.AppendLine($"  {p.Name}{status}  Drive:{p.Tech.Drive:F2} Wpn:{p.Tech.Weapons:F2} Shd:{p.Tech.Shields:F2} Cargo:{p.Tech.Cargo:F2}");
        }
        return sb.ToString();
    }

    [McpServerTool, Description(
        "Get current turn state for a race: turn number, game finished flag, winner, and whether this race already submitted final orders.")]
    public async Task<string> GetTurnState(
        [Description("Game ID")] string gameId,
        [Description("Your race name")] string raceName,
        [Description("Your password")] string password,
        CancellationToken ct = default)
    {
        var game = await svc.GetGameAsync(gameId, ct);
        if (game is null)
        {
            return """{"error":"game_not_found"}""";
        }

        var player = game.GetPlayer(raceName);
        if (player is null || player.Password != password)
        {
            return """{"error":"auth_failed"}""";
        }

        var payload = new
        {
            turn = game.Turn,
            isFinished = game.IsFinished,
            winnerName = game.WinnerName,
            mySubmitted = player.Submitted,
        };
        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    [McpServerTool, Description(
        "Get the full turn report for your race. Contains: your planets, groups, fleets, alien intel, battles, bombings, and an ASCII map.")]
    public async Task<string> GetTurnReport(
        [Description("Game ID")] string gameId,
        [Description("Your race name")] string raceName,
        [Description("Your password")] string password,
        CancellationToken ct = default)
    {
        var report = await svc.GetReportAsync(gameId, raceName, password, ct);
        return report ?? "Authentication failed or game not found.";
    }

    [McpServerTool, Description(
        "Get private diplomacy context for your race: currently opened private channels and per-contact messaging signals " +
        "(last sender, unanswered streak, own/their message counts).")]
    public async Task<string> GetDiplomacyContext(
        [Description("Game ID")] string gameId,
        [Description("Your race name")] string raceName,
        [Description("Your password")] string password,
        [Description("How many latest pair messages to analyze")] int lookback = 30,
        CancellationToken ct = default)
    {
        var game = await svc.GetGameAsync(gameId, ct);
        if (game is null)
        {
            return """{"error":"game_not_found"}""";
        }

        var me = game.GetPlayer(raceName);
        if (me is null || me.Password != password)
        {
            return """{"error":"auth_failed"}""";
        }

        var contacts = new List<object>();
        foreach (var other in game.Players.Values
                     .Where(p => !p.IsEliminated && !p.Id.Equals(me.Id, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var pairId = BuildPairKey(me.Id, other.Id);
            var channelOpen = game.IdentifiedContactPairs.Contains(pairId);

            var pairMessages = game.DiplomacyMessages
                .Where(m => IsPrivatePairMessage(m, me.Id, other.Id))
                .OrderByDescending(m => m.SentAt)
                .Take(Math.Max(5, lookback))
                .OrderBy(m => m.SentAt)
                .ToList();

            var myMessages = pairMessages.Count(m => m.SenderId.Equals(me.Id, StringComparison.OrdinalIgnoreCase));
            var theirMessages = pairMessages.Count - myMessages;
            var last = pairMessages.LastOrDefault();
            var unansweredMineStreak = 0;
            for (int i = pairMessages.Count - 1; i >= 0; i--)
            {
                if (!pairMessages[i].SenderId.Equals(me.Id, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                unansweredMineStreak++;
            }

            var allianceUntil = me.AllianceUntilTurn.GetValueOrDefault(other.Id, 0);
            var isAlly = me.Allies.Contains(other.Id);
            var isAtWar = me.AtWar.Contains(other.Id);

            contacts.Add(new
            {
                race = other.Name,
                channelOpen,
                messageCount = pairMessages.Count,
                myMessages,
                theirMessages,
                unansweredMineStreak,
                lastSender = last?.SenderName,
                lastTurn = last?.Turn,
                diplomaticStatus = isAtWar ? "WAR" : isAlly ? "ALLY" : "NEUTRAL",
                allianceUntilTurn = isAlly ? allianceUntil : (int?)null,
            });
        }

        var payload = new
        {
            turn = game.Turn,
            race = me.Name,
            contacts
        };
        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    [McpServerTool, Description(
        "Report bot activity status for UI/observer panels (reading-report, thinking, validating, submitted, etc.).")]
    public async Task<string> ReportBotStatus(
        [Description("Game ID")] string gameId,
        [Description("Race name")] string raceName,
        [Description("Status code")] string status,
        [Description("Optional short detail text")] string? detail = null,
        [Description("Optional reasoning/thinking text")] string? thinking = null,
        CancellationToken ct = default)
    {
        await svc.BroadcastBotStatusAsync(gameId, raceName, status, detail, thinking, ct);
        return "OK";
    }

    [McpServerTool, Description(
        "Validate orders without submitting them. Returns a list of errors if any.")]
    public async Task<string> ValidateOrders(
        [Description("Game ID")] string gameId,
        [Description("Your race name")] string raceName,
        [Description("Your password")] string password,
        [Description("Orders text in GalaxyNG format")] string orders,
        CancellationToken ct = default)
    {
        var game = await svc.GetGameAsync(gameId, ct);
        if (game is null)
        {
            return "Game not found.";
        }

        var player = game.GetPlayer(raceName);
        if (player is null || player.Password != password)
        {
            return "Auth failed.";
        }

        var (parsed, errors) = new OrderParser().Parse(orders);
        var validator = new OrderValidator(game, player);
        var results = validator.ValidateAll(parsed);

        var allErrors = errors.Concat(
            results.Where(r => !r.Ok).Select(r => r.Error ?? "Unknown error")).ToList();

        return allErrors.Count == 0
            ? $"OK — {parsed.Count} order(s) parsed successfully."
            : "Errors:\n" + string.Join("\n", allErrors.Select(e => $"  - {e}"));
    }

    [McpServerTool, Description(
        "Submit orders for the current turn. Set final=true when you are done to signal readiness.")]
    public async Task<string> SubmitOrders(
        [Description("Game ID")] string gameId,
        [Description("Your race name")] string raceName,
        [Description("Your password")] string password,
        [Description("Orders text in GalaxyNG format")] string orders,
        [Description("Set to true when these are your final orders for the turn")] bool final = false,
        CancellationToken ct = default)
    {
        var (ok, error) = await svc.SubmitOrdersAsync(gameId, raceName, password, orders, final, ct);
        return ok ? $"Orders accepted.{(final ? " Marked as FINAL." : "")}"
                  : $"Error: {error}";
    }

    [McpServerTool, Description(
        "Get a forecast: simulate the turn with your orders and return a predicted report (other players' actions not included).")]
    public async Task<string> GetForecast(
        [Description("Game ID")] string gameId,
        [Description("Your race name")] string raceName,
        [Description("Your password")] string password,
        [Description("Orders to forecast")] string orders,
        CancellationToken ct = default)
    {
        var forecast = await svc.GetForecastAsync(gameId, raceName, password, orders, ct);
        return forecast ?? "Auth failed or game not found.";
    }

    [McpServerTool, Description(
        "Calculate the distance in light-years between two planets.")]
    public async Task<string> CalculateDistance(
        [Description("Game ID")] string gameId,
        [Description("First planet name")] string planet1,
        [Description("Second planet name")] string planet2,
        CancellationToken ct = default)
    {
        var game = await svc.GetGameAsync(gameId, ct);
        if (game is null)
        {
            return "Game not found.";
        }

        var p1 = game.GetPlanet(planet1);
        var p2 = game.GetPlanet(planet2);
        if (p1 is null)
        {
            return $"Planet '{planet1}' not found.";
        }

        if (p2 is null)
        {
            return $"Planet '{planet2}' not found.";
        }

        return $"Distance from {p1.Name} to {p2.Name}: {p1.DistanceTo(p2):F2} ly";
    }

    [McpServerTool, Description(
        "Calculate ship stats: mass, speed, cargo capacity, and defense. " +
        "All values at tech level 1.0 unless techLevels provided (format: 'drive,weapons,shields,cargo').")]
    public string CalculateShipStats(
        [Description("Drive mass")] double drive,
        [Description("Number of attack guns")] int attacks,
        [Description("Weapons mass")] double weapons,
        [Description("Shields mass")] double shields,
        [Description("Cargo mass")] double cargo,
        [Description("Tech levels as 'drive,weapons,shields,cargo' e.g. '1.5,1.2,1.0,2.0'")] string techLevels = "1,1,1,1")
    {
        var tech = ParseTech(techLevels);
        var st = new ShipType { Name = "Test", Drive = drive, Attacks = attacks, Weapons = weapons, Shields = shields, Cargo = cargo };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Ship stats:");
        sb.AppendLine($"  Mass:            {st.Mass:F2}");
        sb.AppendLine($"  Speed (no cargo):{st.SpeedEmpty(tech.Drive):F2} ly/turn");
        sb.AppendLine($"  Cargo capacity:  {st.BaseCargoCapacity * tech.Cargo:F2} units");
        sb.AppendLine($"  Attack strength: {st.AttackStrength(tech.Weapons):F2}");
        sb.AppendLine($"  Defense:         {st.DefenseStrength(tech.Shields, tech.Cargo, 0):F2}");
        return sb.ToString();
    }

    private static TechLevels ParseTech(string s)
    {
        var parts = s.Split(',');
        double Get(int i) => parts.Length > i && double.TryParse(parts[i], out double v) ? v : 1.0;
        return new TechLevels(Get(0), Get(1), Get(2), Get(3));
    }

    private static bool IsPrivatePairMessage(DiplomacyMessage message, string playerAId, string playerBId)
    {
        if (message.RecipientIds.Count == 0)
        {
            return false;
        }

        var participants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { message.SenderId };
        foreach (var recipientId in message.RecipientIds)
        {
            participants.Add(recipientId);
        }

        return participants.Count == 2 && participants.Contains(playerAId) && participants.Contains(playerBId);
    }

    private static string BuildPairKey(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? $"{a}:{b}" : $"{b}:{a}";
}
