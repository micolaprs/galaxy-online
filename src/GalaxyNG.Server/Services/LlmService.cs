using System.Text;
using GalaxyNG.Engine.Models;

namespace GalaxyNG.Server.Services;

/// <summary>Builds prompts and delegates completion to a configured LLM provider.</summary>
public sealed class LlmService(IConfiguration config, ILlmProvider provider)
{
    private readonly double _temp = double.TryParse(
        config["Llm:Temperature"],
        System.Globalization.NumberStyles.Any,
        System.Globalization.CultureInfo.InvariantCulture,
        out var t) ? t : 0.7;
    private readonly int _maxTokens = int.TryParse(config["Llm:MaxTokens"], out var m) ? m : 1024;

    public async Task<string?> GenerateGalaxySummaryAsync(Game game, CancellationToken ct = default)
    {
        var prompt = BuildGalaxySummaryPrompt(game);
        var raw = await provider.CompleteAsync(
            "Ты аналитик космической стратегической игры GalaxyNG. Ответ только на русском языке, без markdown (без **, списков, заголовков), без разделов размышлений и без служебных тегов.",
            prompt, _maxTokens, _temp, ct);
        return UiTextPolicy.Clean(raw, 900);
    }

    public async Task<string?> GenerateTurnSummaryAsync(
        Game game, string raceName, int turn, CancellationToken ct = default)
    {
        var hist = game.TurnHistory.FirstOrDefault(h => h.Turn == turn);
        if (hist is null)
        {
            return null;
        }

        hist.PlayerOrders.TryGetValue(raceName, out var orders);
        var prompt = BuildTurnSummaryPrompt(raceName, turn, orders ?? "", hist.Battles, hist.Bombings);
        var raw = await provider.CompleteAsync(
            "Ты аналитик GalaxyNG. Пиши кратко на русском языке (не более 3-4 предложений), без markdown (без **, списков, заголовков), без reasoning/thinking и без служебных блоков.",
            prompt, 512, _temp, ct);
        return UiTextPolicy.Clean(raw, 700);
    }

    private static string BuildGalaxySummaryPrompt(Game game)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Игра: {game.Name} (ID: {game.Id})");
        sb.AppendLine($"Текущий ход: {game.Turn}");
        sb.AppendLine();
        sb.AppendLine("=== ИГРОКИ ===");

        foreach (var p in game.Players.Values)
        {
            if (p.IsEliminated) { sb.AppendLine($"- {p.Name}: ВЫБЫЛ"); continue; }
            var planets = game.PlanetsOwnedBy(p.Id).ToList();
            var totalPop = planets.Sum(pl => pl.Population);
            var totalInd = planets.Sum(pl => pl.Industry);
            var shipCount = p.Groups.Sum(g => g.Ships);
            sb.AppendLine(
                $"- {p.Name}: {planets.Count} планет, " +
                $"нас.={totalPop:F0}, инд.={totalInd:F0}, корабли={shipCount}, " +
                $"техн.(Д{p.Tech.Drive:F1}/О{p.Tech.Weapons:F1}/З{p.Tech.Shields:F1}/Г{p.Tech.Cargo:F1})");
        }

        sb.AppendLine();
        sb.AppendLine("=== СОБЫТИЯ ПОСЛЕДНЕГО ХОДА ===");

        if (game.Battles.Count == 0 && game.Bombings.Count == 0)
        {
            sb.AppendLine("Мирный ход — сражений не было.");
        }

        foreach (var b in game.Battles)
        {
            sb.AppendLine($"- Битва при {b.PlanetName}: {string.Join(" vs ", b.Participants)} → победитель: {b.Winner}");
        }

        foreach (var b in game.Bombings)
        {
            sb.AppendLine($"- {b.AttackerRace} бомбардировал {b.PlanetName}" +
                          (b.PreviousOwner != null ? $" (был {b.PreviousOwner})" : ""));
        }

        sb.AppendLine();
        sb.AppendLine(
            "Напиши краткую аналитическую сводку о текущем состоянии игры: " +
            "кто лидирует, какова стратегическая ситуация, что важного произошло. " +
            "4-6 предложений.");

        return sb.ToString();
    }

    private static string BuildTurnSummaryPrompt(
        string raceName, int turn, string orders,
        List<string> battles, List<string> bombings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Игрок: {raceName}, Ход: {turn}");
        sb.AppendLine();
        sb.AppendLine("=== ПРИКАЗЫ ИГРОКА ===");
        sb.AppendLine(string.IsNullOrWhiteSpace(orders) ? "(нет приказов)" : orders);

        if (battles.Count + bombings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== СОБЫТИЯ ХОДА ===");
            foreach (var b in battles)
            {
                sb.AppendLine($"- {b}");
            }

            foreach (var b in bombings)
            {
                sb.AppendLine($"- {b}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(
            $"Кратко опиши стратегию {raceName} на этом ходу: " +
            "что они делали, куда двигались, что строили. 2-3 предложения.");

        return sb.ToString();
    }
}
