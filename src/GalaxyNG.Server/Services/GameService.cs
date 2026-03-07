using GalaxyNG.Engine.Models;
using GalaxyNG.Engine.Services;
using GalaxyNG.Server.Data;
using GalaxyNG.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace GalaxyNG.Server.Services;

public sealed class GameService(
    GameStore            store,
    GalaxyGenerator      generator,
    TurnProcessor        processor,
    ReportGenerator      reporter,
    LlmService           llm,
    IHubContext<GameHub> hub,
    ILogger<GameService> logger)
{
    // In-memory cache of active games
    private readonly Dictionary<string, Game> _cache = [];
    private readonly SemaphoreSlim            _lock  = new(1, 1);

    // ---- Admin ----

    public async Task DeleteAllGamesAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _cache.Clear();
            await store.DeleteAllAsync(ct);
            logger.LogInformation("All games deleted.");
        }
        finally { _lock.Release(); }
    }

    // ---- Game lifecycle ----

    public async Task<Game> CreateGameAsync(
        string gameName,
        IReadOnlyList<(string name, string password, bool isBot)> players,
        GalaxyGeneratorOptions? opts  = null,
        bool autoRun = false,
        CancellationToken ct = default)
    {
        string gameId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var playerSpecs = players.Select((p, i) =>
            (id: $"P{i + 1}", p.name, p.password, p.isBot)).ToList();

        var game = generator.Generate(gameId, gameName, playerSpecs, opts);
        game.AutoRunOnAllSubmitted = autoRun;
        game.HostPlayerId          = playerSpecs[0].id;

        await _lock.WaitAsync(ct);
        try
        {
            _cache[gameId] = game;
            await store.SaveAsync(game, ct);
        }
        finally { _lock.Release(); }

        logger.LogInformation("Created game {Id} '{Name}' with {Count} players", gameId, gameName, players.Count);
        return game;
    }

    public async Task<Game?> GetGameAsync(string gameId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(gameId, out var cached)) return cached;
            var loaded = await store.LoadAsync(gameId, ct);
            if (loaded is not null) _cache[gameId] = loaded;
            return loaded;
        }
        finally { _lock.Release(); }
    }

    public async Task<IEnumerable<Game>> ListGamesAsync(CancellationToken ct = default)
    {
        var games = new List<Game>();
        foreach (var id in store.ListGameIds())
        {
            var g = await GetGameAsync(id, ct);
            if (g is not null) games.Add(g);
        }
        return games;
    }

    // ---- Player join ----

    public async Task<(bool ok, string? error, Player? player)> JoinGameAsync(
        string gameId, string playerName, string password, CancellationToken ct = default)
    {
        var game = await GetGameAsync(gameId, ct);
        if (game is null) return (false, "Game not found.", null);

        var existing = game.GetPlayer(playerName);
        if (existing is null) return (false, "Race not found. The game creator must add you.", null);
        if (existing.Password != password) return (false, "Invalid password.", null);

        return (true, null, existing);
    }

    // ---- Orders ----

    public async Task<(bool ok, string? error)> SubmitOrdersAsync(
        string gameId, string raceName, string password,
        string orderText, bool isFinal, CancellationToken ct = default)
    {
        var game = await GetGameAsync(gameId, ct);
        if (game is null) return (false, "Game not found.");

        var player = game.GetPlayer(raceName);
        if (player is null || player.Password != password) return (false, "Auth failed.");

        var parser   = new OrderParser();
        var (orders, errors) = parser.Parse(orderText);
        if (errors.Count > 0 && errors.Count == orders.Count)
            return (false, string.Join("; ", errors));

        player.Orders        = orders;
        player.PendingOrders = orderText;
        player.Submitted     = isFinal;

        await SaveGameAsync(game, ct);

        if (isFinal)
        {
            var waiting = game.Players.Values
                .Where(p => !p.IsEliminated && !p.Submitted)
                .Select(p => p.Name).ToList();

            if (waiting.Count == 0)
                logger.LogInformation("✅ [{Race}] подал приказы. Все игроки готовы — запускаем ход {Turn}",
                    raceName, game.Turn);
            else
                logger.LogInformation("✅ [{Race}] подал приказы за ход {Turn}. Ждём: [{Waiting}]",
                    raceName, game.Turn, string.Join(", ", waiting));

            await hub.Clients.Group(gameId)
                .SendAsync("PlayerSubmitted", new { raceName }, ct);
        }

        if (game.AutoRunOnAllSubmitted && game.AllPlayersSubmitted())
            _ = Task.Run(() => RunTurnAsync(gameId, CancellationToken.None), CancellationToken.None);

        return (true, null);
    }

    // ---- Turn running ----

    public async Task<(bool ok, string? error)> RunTurnAsync(
        string gameId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var game = _cache.GetValueOrDefault(gameId) ?? await store.LoadAsync(gameId, ct);
            if (game is null) return (false, "Game not found.");

            // Capture orders + reasoning BEFORE the turn processor clears them
            var histEntry = new TurnHistoryEntry
            {
                Turn = game.Turn,
                RunAt = DateTime.UtcNow,
                PlayerOrders = game.Players.Values
                    .Where(p => !string.IsNullOrEmpty(p.PendingOrders))
                    .ToDictionary(p => p.Name, p => p.PendingOrders!),
                PlayerReasoning = new Dictionary<string, string>(game.CurrentTurnReasoning),
            };
            game.CurrentTurnReasoning.Clear();

            processor.RunTurn(game);
            game.LastTurnRunAt = DateTime.UtcNow;

            // Capture results AFTER the turn
            histEntry.Battles  = game.Battles
                .Select(b => $"Битва при {b.PlanetName}: {string.Join(" vs ", b.Participants)} → {b.Winner} побеждает")
                .ToList();
            histEntry.Bombings = game.Bombings
                .Select(b => $"{b.AttackerRace} бомбардировал {b.PlanetName}" +
                             (b.PreviousOwner != null ? $" (был {b.PreviousOwner})" : ""))
                .ToList();
            game.TurnHistory.Add(histEntry);

            await store.SaveAsync(game, ct);
            logger.LogInformation("Ran turn {Turn} for game {Id}", game.Turn, game.Id);

            await hub.Clients.Group(gameId)
                .SendAsync("TurnComplete", new { turn = game.Turn }, ct);

            return (true, null);
        }
        finally { _lock.Release(); }
    }

    // ---- Reports ----

    public async Task<string?> GetReportAsync(
        string gameId, string raceName, string password, CancellationToken ct = default)
    {
        var game = await GetGameAsync(gameId, ct);
        if (game is null) return null;
        var player = game.GetPlayer(raceName);
        if (player is null || player.Password != password) return null;
        return reporter.GenerateTurnReport(game, player);
    }

    public async Task<string?> GetForecastAsync(
        string gameId, string raceName, string password,
        string orderText, CancellationToken ct = default)
    {
        // Run a clone with only this player's orders to produce a forecast
        var game = await GetGameAsync(gameId, ct);
        if (game is null) return null;
        var player = game.GetPlayer(raceName);
        if (player is null || player.Password != password) return null;

        // Deep clone game for simulation
        var clone       = CloneGame(game);
        var clonePlayer = clone.GetPlayer(player.Id)!;

        var (orders, _) = new OrderParser().Parse(orderText);
        clonePlayer.Orders = orders;

        processor.RunTurn(clone);
        return reporter.GenerateTurnReport(clone, clone.GetPlayer(player.Id)!);
    }

    // ---- Bot status ----

    public async Task BroadcastBotStatusAsync(
        string gameId, string raceName, string status, string? detail, string? thinking = null,
        CancellationToken ct = default)
    {
        logger.LogInformation("Bot {Race} status: {Status}{Detail}",
            raceName, status, detail is not null ? $" — {detail}" : "");

        // Persist reasoning so it survives beyond the SignalR event lifetime
        if (thinking is not null)
        {
            var game = await GetGameAsync(gameId, ct);
            if (game is not null)
                game.CurrentTurnReasoning[raceName] = thinking;
        }

        await hub.Clients.Group(gameId).SendAsync("BotStatusUpdate", new
        {
            raceName,
            status,
            detail,
            thinking,
            time = DateTimeOffset.UtcNow.ToString("HH:mm:ss"),
        }, ct);
    }

    // ---- AI summaries ----

    public async Task<string?> GenerateGalaxySummaryAsync(string gameId, CancellationToken ct = default)
    {
        var game = await GetGameAsync(gameId, ct);
        if (game is null) return null;

        var summary = await llm.GenerateGalaxySummaryAsync(game, ct);
        if (summary is null) return null;

        // Remove existing summary for this turn, then add new one
        game.AiSummaries.RemoveAll(s => s.Turn == game.Turn);
        game.AiSummaries.Add(new AiSummaryEntry { Turn = game.Turn, Summary = summary });
        await SaveGameAsync(game, ct);
        return summary;
    }

    public async Task<string?> GenerateTurnSummaryAsync(
        string gameId, string raceName, int turn, CancellationToken ct = default)
    {
        var game = await GetGameAsync(gameId, ct);
        if (game is null) return null;
        return await llm.GenerateTurnSummaryAsync(game, raceName, turn, ct);
    }

    public async Task<List<AiSummaryEntry>> GetAiSummariesAsync(
        string gameId, CancellationToken ct = default)
    {
        var game = await GetGameAsync(gameId, ct);
        return game?.AiSummaries ?? [];
    }

    // ---- helpers ----

    private async Task SaveGameAsync(Game game, CancellationToken ct)
    {
        _cache[game.Id] = game;
        await store.SaveAsync(game, ct);
    }

    private static Game CloneGame(Game original)
    {
        // Use JSON round-trip for a simple deep clone
        var json = System.Text.Json.JsonSerializer.Serialize(original);
        return System.Text.Json.JsonSerializer.Deserialize<Game>(json)!;
    }
}
