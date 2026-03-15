namespace GalaxyNG.Server.Services;

/// <summary>
/// Appends timestamped log lines to ~/.galaxyng/games/{gameId}/game.log.
/// Thread-safe via per-file locks.
/// </summary>
public sealed class GameFileLogWriter
{
    private readonly string _basePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".galaxyng", "games");

    // Per-game locks to avoid interleaved writes when multiple tasks log simultaneously
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _locks = new();

    public void Log(string gameId, string level, string source, string message)
    {
        var dir = Path.Combine(_basePath, gameId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "game.log");
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [{level,-11}] [{source}] {message}{Environment.NewLine}";
        var fileLock = _locks.GetOrAdd(gameId, _ => new object());
        lock (fileLock)
        {
            File.AppendAllText(path, line);
        }
    }
}
