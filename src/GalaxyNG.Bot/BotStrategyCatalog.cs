using System.Globalization;
using System.Text;

namespace GalaxyNG.Bot;

public sealed record BotStrategy(
    string Id,
    string Name,
    string Prompt,
    string CommanderCues);

public static class BotStrategyCatalog
{
    private static readonly Lazy<IReadOnlyList<BotStrategy>> _all = new(LoadStrategies);

    public static IReadOnlyList<BotStrategy> All => _all.Value;

    public static BotStrategy PickForBot(string gameId, string raceName, string? preferredId = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            var exact = All.FirstOrDefault(s => s.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        if (All.Count == 0)
        {
            return Fallback();
        }

        var seedText = $"{gameId}:{raceName}";
        var seed = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(seedText));
        var idx = seed % All.Count;
        return All[idx];
    }

    public static string BuildStrategyUserHint(BotStrategy strategy)
        => $"Strategy profile ({strategy.Name}):\n{strategy.Prompt}";

    private static IReadOnlyList<BotStrategy> LoadStrategies()
    {
        try
        {
            var baseDir = Path.Combine(AppContext.BaseDirectory, "Prompts", "Strategies");
            if (!Directory.Exists(baseDir))
            {
                return [Fallback()];
            }

            var items = new List<BotStrategy>();
            foreach (var file in Directory.GetFiles(baseDir, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var text = File.ReadAllText(file, Encoding.UTF8).Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var id = Path.GetFileNameWithoutExtension(file);
                var name = ExtractTitle(text) ?? ToTitleCase(id);
                var commanderCues = ExtractSection(text, "Commander profile cues") ??
                                    "Act consistently with this strategy's tempo and diplomatic style.";

                items.Add(new BotStrategy(id, name, text, commanderCues));
            }

            return items.Count > 0 ? items : [Fallback()];
        }
        catch
        {
            return [Fallback()];
        }
    }

    private static BotStrategy Fallback() => new(
        Id: "balanced-frontier",
        Name: "Balanced Frontier Control",
        Prompt: "Balanced approach: secure nearby planets, keep colonization active, and build military pressure by mid-game.",
        CommanderCues: "Pragmatic, calm, and consistent.");

    private static string? ExtractTitle(string markdown)
    {
        using var sr = new StringReader(markdown);
        while (sr.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                return trimmed[2..].Trim();
            }

            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                break;
            }
        }

        return null;
    }

    private static string? ExtractSection(string markdown, string heading)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var capture = false;
        var captured = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                var title = line.TrimStart('#', ' ').Trim();
                if (capture && !title.Equals(heading, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                capture = title.Equals(heading, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!capture)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                captured.Add(line.TrimStart('-', ' '));
            }
        }

        return captured.Count == 0 ? null : string.Join(" ", captured);
    }

    private static string ToTitleCase(string id)
    {
        var text = id.Replace('-', ' ').Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text);
    }
}
