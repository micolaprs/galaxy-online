using System.Text.Json;
using GalaxyNG.Engine.Models;

namespace GalaxyNG.Server.Data;

/// <summary>Simple JSON file store — one file per game, versioned by turn.</summary>
public sealed class GameStore(ILogger<GameStore> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _basePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".galaxyng", "games");

    public async Task SaveAsync(Game game, CancellationToken ct = default)
    {
        Directory.CreateDirectory(GameDir(game.Id));
        var path = GameFile(game.Id);
        var json = JsonSerializer.Serialize(game, JsonOpts);
        await File.WriteAllTextAsync(path, json, ct);
        logger.LogDebug("Saved game {Id} turn {Turn}", game.Id, game.Turn);
    }

    public async Task<Game?> LoadAsync(string gameId, CancellationToken ct = default)
    {
        var path = GameFile(gameId);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<Game>(json, JsonOpts);
    }

    public IEnumerable<string> ListGameIds()
    {
        if (!Directory.Exists(_basePath)) return [];
        return Directory.GetDirectories(_basePath).Select(Path.GetFileName).OfType<string>();
    }

    public Task DeleteAllAsync(CancellationToken ct = default)
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
        logger.LogInformation("Deleted all saved games from {Path}", _basePath);
        return Task.CompletedTask;
    }

    private string GameDir(string id)  => Path.Combine(_basePath, id);
    private string GameFile(string id) => Path.Combine(GameDir(id), "game.json");
}
