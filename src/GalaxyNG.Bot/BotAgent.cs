using System.Text.RegularExpressions;
using GalaxyNG.Engine.Services;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace GalaxyNG.Bot;

/// <summary>
/// LLM bot that plays GalaxyNG by interacting with the server REST API.
/// On each turn: gets report → asks LLM for orders → validates → submits.
/// </summary>
public sealed class BotAgent(
    BotConfig                config,
    LlmClient                llm,
    IHttpClientFactory       httpFactory,
    ILogger<BotAgent>        logger)
{
    private readonly CommanderPersona _commander = CommanderPersona.ForRace(config.RaceName);

    public async Task RunTurnAsync(int turn, CancellationToken ct = default)
    {
        logger.LogInformation("━━━ [{Race}] Начинаю ход {Turn} ━━━", config.RaceName, turn);

        // 1. Get turn report
        await PostBotStatusAsync("reading-report", $"ход {turn}", ct);
        logger.LogInformation("📋 [{Race}] Читаю отчёт за ход {Turn}…", config.RaceName, turn);
        string report = await GetReportAsync(ct);
        if (string.IsNullOrWhiteSpace(report))
        {
            logger.LogWarning("❌ [{Race}] Не удалось получить отчёт за ход {Turn}", config.RaceName, turn);
            await PostBotStatusAsync("idle", "нет отчёта", ct);
            return;
        }
        int reportLines = report.Split('\n').Length;
        logger.LogInformation("📋 [{Race}] Отчёт получен ({Lines} строк)", config.RaceName, reportLines);

        if (turn <= 3)
        {
            var openingOrders = await BuildOpeningOrdersAsync(turn, report, ct);
            if (!string.IsNullOrWhiteSpace(openingOrders))
            {
                logger.LogInformation("🧭 [{Race}] Использую детерминированный opening на ходу {Turn}", config.RaceName, turn);
                await PostBotStatusAsync("validating", $"ход {turn}, opening-script", ct);

                if (!ValidateOrderSyntax(openingOrders, out var openingError))
                {
                    logger.LogWarning("⚠️ [{Race}] Opening приказы не прошли локальную проверку: {Err}", config.RaceName, openingError);
                }
                else
                {
                    await SubmitOrdersAsync(openingOrders, final: true, ct);
                    logger.LogInformation("✅ [{Race}] Opening-приказы для хода {Turn} отправлены", config.RaceName, turn);
                    await PostBotStatusAsync("submitted", $"ход {turn} (opening)", ct);
                    return;
                }
            }
        }

        // 2. Ask LLM for orders (with retry on validation failure)
        string orders = "";
        bool haveValidOrders = false;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await PostBotStatusAsync("thinking", $"ход {turn}, попытка {attempt}/3", ct);
            logger.LogInformation("🧠 [{Race}] Думаю над ходом {Turn} (попытка {Attempt}/3)…", config.RaceName, turn, attempt);

            // Heartbeat: log every 20s while waiting for LLM
            using var thinkingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = Task.Run(async () =>
            {
                try
                {
                    while (!thinkingCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(20_000, thinkingCts.Token);
                        logger.LogInformation("⏳ [{Race}] Всё ещё жду ответа от LLM (ход {Turn})…", config.RaceName, turn);
                    }
                }
                catch (OperationCanceledException) { }
            });

            var messages = new List<ChatMessage>
            {
                ChatMessage.System(StrategyPrompt.System),
                ChatMessage.System(BuildCommanderInstruction()),
                ChatMessage.User($"## Turn Report\n\n{report}\n\nProvide your orders for this turn."),
            };

            if (attempt > 1 && !string.IsNullOrEmpty(orders))
            {
                messages.Add(ChatMessage.Assistant(orders));
                messages.Add(ChatMessage.User("Those orders had validation errors. Please fix and rewrite ALL orders."));
            }

            string raw = await llm.CompleteAsync(messages, ct);
            await thinkingCts.CancelAsync();
            await heartbeat;

            // Broadcast the full LLM response (reasoning + orders) to the UI
            await PostBotStatusAsync("thinking", $"ход {turn}, попытка {attempt}/3 — ответ получен", raw, ct);

            orders = NormalizeOrders(ExtractOrders(raw));
            int orderLines = orders.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            logger.LogInformation("📝 [{Race}] LLM составил {Lines} приказов (из {Total} знаков ответа)",
                config.RaceName, orderLines, raw.Length);

            if (!ValidateOrderSyntax(orders, out var syntaxError))
            {
                logger.LogWarning("⚠️ [{Race}] Невалидный синтаксис приказов (попытка {Attempt}): {Error}",
                    config.RaceName, attempt, syntaxError);
                continue;
            }

            // 3. Validate
            await PostBotStatusAsync("validating", $"ход {turn}, попытка {attempt}", ct);
            logger.LogInformation("🔍 [{Race}] Проверяю приказы у сервера…", config.RaceName);
            var validation = await ValidateOrdersAsync(orders, ct);
            if (validation.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("✅ [{Race}] Приказы прошли проверку (попытка {Attempt})", config.RaceName, attempt);
                haveValidOrders = true;
                break;
            }

            logger.LogWarning("⚠️ [{Race}] Ошибка проверки (попытка {Attempt}): {Error}", config.RaceName, attempt, validation);
            if (attempt == 3)
                logger.LogError("❌ [{Race}] Отправляю лучший вариант после 3 попыток", config.RaceName);
        }

        if (!haveValidOrders)
        {
            // Keep the turn moving even when model output is malformed.
            orders = "o AUTOUNLOAD";
            logger.LogWarning("⚠️ [{Race}] Использую безопасный fallback-приказ: {Order}", config.RaceName, orders);
        }

        // 4. Submit
        await PostBotStatusAsync("submitting", $"ход {turn}", ct);
        logger.LogInformation("📤 [{Race}] Отправляю приказы для хода {Turn}…", config.RaceName, turn);
        await SubmitOrdersAsync(orders, final: true, ct);
        logger.LogInformation("✅ [{Race}] Приказы для хода {Turn} отправлены!", config.RaceName, turn);
        await PostBotStatusAsync("submitted", $"ход {turn}", ct);
    }

    /// <summary>Loop: poll server for new turns and play automatically.</summary>
    public async Task RunLoopAsync(CancellationToken ct = default)
    {
        int lastTurn = -1;
        logger.LogInformation("🚀 [{Race}] Бот подключился к игре {Game}", config.RaceName, config.GameId);
        await PostBotStatusAsync("idle", "ожидание первого хода", ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                int turn = await GetCurrentTurnAsync(ct);
                if (turn != lastTurn)
                {
                    logger.LogInformation("🔔 [{Race}] Новый ход: {Turn}", config.RaceName, turn);
                    lastTurn = turn;
                    await RunTurnAsync(turn, ct);
                }
                else
                {
                    await PostBotStatusAsync("waiting", $"ожидаю хода {turn + 1}", ct);
                    logger.LogDebug("[{Race}] Жду следующего хода (сейчас ход {Turn})", config.RaceName, turn);
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ [{Race}] Ошибка в игровом цикле — повтор через 60с", config.RaceName);
                await PostBotStatusAsync("error", ex.Message, ct);
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
        }
    }

    // ---- HTTP helpers ----

    private HttpClient Http => httpFactory.CreateClient("server");

    private async Task<string> GetReportAsync(CancellationToken ct)
    {
        var url = $"{config.ServerUrl}/api/games/{config.GameId}/report/{Uri.EscapeDataString(config.RaceName)}?password={Uri.EscapeDataString(config.Password)}";
        var response = await Http.GetAsync(url, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : "";
    }

    private async Task<string> ValidateOrdersAsync(string orders, CancellationToken ct)
    {
        var url = $"{config.ServerUrl}/api/games/{config.GameId}/forecast/{Uri.EscapeDataString(config.RaceName)}?password={Uri.EscapeDataString(config.Password)}&orders={Uri.EscapeDataString(orders)}";
        try
        {
            var response = await Http.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? "OK" : "Error";
        }
        catch { return "Error"; }
    }

    private async Task SubmitOrdersAsync(string orders, bool final, CancellationToken ct)
    {
        var url = $"{config.ServerUrl}/api/games/{config.GameId}/orders";
        var body = new { raceName = config.RaceName, password = config.Password, orders, final };
        using var response = await Http.PostAsJsonAsync(url, body, ct);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Orders submit failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {text}");
        }
    }

    private async Task<int> GetCurrentTurnAsync(CancellationToken ct)
    {
        var url  = $"{config.ServerUrl}/api/games/{config.GameId}";
        var json = await Http.GetFromJsonAsync<JsonElement>(url, ct);
        return json.GetProperty("turn").GetInt32();
    }

    private async Task PostBotStatusAsync(string status, string? detail, CancellationToken ct)
        => await PostBotStatusAsync(status, detail, null, ct);

    private async Task PostBotStatusAsync(string status, string? detail, string? thinking, CancellationToken ct)
    {
        try
        {
            var url  = $"{config.ServerUrl}/api/games/{config.GameId}/bot-status";
            var body = new { raceName = config.RaceName, status, detail, thinking };
            await Http.PostAsJsonAsync(url, body, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not post bot status: {Msg}", ex.Message);
        }
    }

    private static string ExtractOrders(string llmResponse)
    {
        // Parse the structured response format:
        // REASONING: ...
        // ORDERS:
        // <orders>
        var lines  = llmResponse.ReplaceLineEndings("\n").Split('\n');
        bool inOrders = false;
        var  result   = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("ORDERS:", StringComparison.OrdinalIgnoreCase))
            {
                inOrders = true;
                continue;
            }
            if (inOrders)
                result.Add(line);
        }

        return result.Count > 0
            ? string.Join("\n", result).Trim()
            : llmResponse.Trim();  // fallback: use raw response
    }

    private static string NormalizeOrders(string rawOrders)
    {
        var text = rawOrders.ReplaceLineEndings("\n").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "";

        // Recover compact outputs where commands were glued together, e.g. "...transportp P1..."
        text = Regex.Replace(text, @"(?<=[0-9A-Za-z])(?=[cyn=qnprvmdtesilugbxhjawfo]\s)", "\n");

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(';')[0].Trim()) // comments are optional; strip to avoid parser ambiguity
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(NormalizeCompactDesignOrder);

        return string.Join("\n", lines).Trim();
    }

    private static string NormalizeCompactDesignOrder(string line)
    {
        // Convert compact form like "d Scout11000" => "d Scout 1 1 0 0 0"
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[0].Equals("d", StringComparison.OrdinalIgnoreCase))
            return line;

        var m = Regex.Match(parts[1], @"^(?<name>[A-Za-z][A-Za-z0-9_-]*?)(?<stats>\d{5,})$");
        if (!m.Success)
            return line;

        var stats = m.Groups["stats"].Value;
        var name  = m.Groups["name"].Value;
        var drive   = stats[0].ToString();
        var attacks = stats[1].ToString();
        var weapons = stats[2].ToString();
        var shields = stats[3].ToString();
        var cargo   = stats[4..]; // cargo can be multi-digit

        return $"d {name} {drive} {attacks} {weapons} {shields} {cargo}";
    }

    private static bool ValidateOrderSyntax(string orders, out string error)
    {
        var parser = new OrderParser();
        var (parsed, parseErrors) = parser.Parse(orders);
        if (parsed.Count == 0)
        {
            error = parseErrors.Count > 0
                ? string.Join("; ", parseErrors.Take(3))
                : "no parsed orders";
            return false;
        }

        if (parseErrors.Count >= parsed.Count)
        {
            error = string.Join("; ", parseErrors.Take(3));
            return false;
        }

        error = "";
        return true;
    }

    private async Task<string> BuildOpeningOrdersAsync(int turn, string report, CancellationToken ct)
    {
        var myPlanets = ParseMyPlanets(report);
        if (myPlanets.Count == 0)
            return "";

        var home = myPlanets[0];
        var groups = ParseGroupNumbers(report);
        var target = await FindNearestTargetPlanetAsync(home.Name, ct);

        var lines = new List<string> { "o AUTOUNLOAD" };
        var hasHaulerDesign = ReportMentionsShipType(report, "Hauler");
        var hasScoutDesign = ReportMentionsShipType(report, "Scout");

        if (!hasScoutDesign)
            lines.Add("d Scout 1 1 0 0 0");
        if (!hasHaulerDesign)
            lines.Add("d Hauler 1 1 0 0 2");

        lines.Add($"p {home.Name} Hauler");

        if (turn == 1)
        {
            lines.Add("@ ALL");
            lines.Add(BuildOpeningGreeting());
            lines.Add("@");
        }

        if (turn == 1 && groups.Count > 0 && !string.IsNullOrWhiteSpace(target))
            lines.Add($"s {groups[0]} {target}");

        if (turn >= 3 && groups.Count > 1 && !string.IsNullOrWhiteSpace(target))
            lines.Add($"s {groups[1]} {target}");

        return string.Join('\n', lines);
    }

    private async Task<string?> FindNearestTargetPlanetAsync(string homePlanet, CancellationToken ct)
    {
        try
        {
            var url = $"{config.ServerUrl}/api/games/{config.GameId}/spectate";
            var spectate = await Http.GetFromJsonAsync<JsonElement>(url, ct);
            var planets = spectate.GetProperty("planets").EnumerateArray()
                .Select(p => new PlanetSnapshot(
                    Name: p.GetProperty("name").GetString() ?? "",
                    X: p.GetProperty("x").GetDouble(),
                    Y: p.GetProperty("y").GetDouble(),
                    OwnerId: p.TryGetProperty("ownerId", out var owner) && owner.ValueKind != JsonValueKind.Null
                        ? owner.GetString()
                        : null))
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .ToList();

            var home = planets.FirstOrDefault(p => p.Name.Equals(homePlanet, StringComparison.OrdinalIgnoreCase));
            if (home is null)
                return planets.FirstOrDefault()?.Name;

            var preferred = planets
                .Where(p => !p.Name.Equals(home.Name, StringComparison.OrdinalIgnoreCase) && p.OwnerId is null)
                .OrderBy(p => Distance(home, p))
                .FirstOrDefault();
            if (preferred is not null)
                return preferred.Name;

            return planets
                .Where(p => !p.Name.Equals(home.Name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => Distance(home, p))
                .Select(p => p.Name)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not find opening nearest target planet: {Msg}", ex.Message);
            return null;
        }
    }

    private static double Distance(PlanetSnapshot a, PlanetSnapshot b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool ReportMentionsShipType(string report, string shipType)
    {
        var pattern = $@"^\s*{Regex.Escape(shipType)}\s+";
        return Regex.IsMatch(report, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
    }

    private static List<(string Name, double X, double Y)> ParseMyPlanets(string report)
    {
        var lines = report.ReplaceLineEndings("\n").Split('\n');
        var result = new List<(string, double, double)>();
        var inSection = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("= YOUR PLANETS", StringComparison.OrdinalIgnoreCase))
            {
                inSection = true;
                continue;
            }

            if (inSection && line.StartsWith("="))
                break;

            if (!inSection || string.IsNullOrWhiteSpace(line) || line.StartsWith("Name", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = Regex.Split(line.Trim(), @"\s+");
            if (parts.Length < 3)
                continue;
            if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var x))
                continue;
            if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var y))
                continue;
            result.Add((parts[0], x, y));
        }

        return result;
    }

    private static List<int> ParseGroupNumbers(string report)
    {
        var lines = report.ReplaceLineEndings("\n").Split('\n');
        var result = new List<int>();
        var inSection = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("= YOUR GROUPS", StringComparison.OrdinalIgnoreCase))
            {
                inSection = true;
                continue;
            }

            if (inSection && line.StartsWith("="))
                break;

            if (!inSection || string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = Regex.Split(line.Trim(), @"\s+");
            if (parts.Length == 0)
                continue;
            if (int.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
                result.Add(num);
        }

        return result;
    }

    private sealed record PlanetSnapshot(string Name, double X, double Y, string? OwnerId);

    private string BuildCommanderInstruction()
    {
        return $"You are Supreme Commander {_commander.Name} of race {config.RaceName}. " +
               $"Leadership profile: {_commander.Profile}. " +
               $"Diplomatic style: {_commander.DiplomacyTone}. " +
               "If you send diplomacy messages, keep this voice consistent and distinct.";
    }

    private string BuildOpeningGreeting()
    {
        return $"{_commander.GreetingPrefix} Я {_commander.Name}, главнокомандующий расы {config.RaceName}. " +
               $"{_commander.GreetingCore}";
    }

    private sealed record CommanderPersona(
        string Name,
        string Profile,
        string DiplomacyTone,
        string GreetingPrefix,
        string GreetingCore)
    {
        private static readonly string[] Names =
        [
            "Архонт Велаар",
            "Маршал Ивен Косс",
            "Стратег Риан Тейл",
            "Прим-командор Сайла Нор",
            "Адмирал Корвин Дейр",
            "Коммодор Лекса Вар",
            "Доминус Харек Соль",
            "Наварх Мирен Каэль",
        ];

        private static readonly (string profile, string diplomacy, string prefix, string core)[] Archetypes =
        [
            ("холодный аналитик, опирается на математику и контроль рисков", "коротко, формально, через расчёт", "Канал подтверждён.", "Предпочитаем чёткие договорённости и соблюдение границ."),
            ("агрессивный экспансионист, любит инициативу и давление темпа", "резко и напористо, без долгих вступлений", "Эфир открыт.", "Мы растём быстро и ждём от соседей ясной позиции."),
            ("дипломат-прагматик, строит коалиции ради выгоды", "дружелюбно и предметно, с акцентом на взаимную выгоду", "Связь установлена.", "Открыты к обмену данными и временным союзам."),
            ("инженер-реформист, ценит технологическое превосходство", "спокойно, технично, с упором на развитие", "Линия чистая.", "Наша ставка на технологию и устойчивую логистику."),
            ("осторожный защитник, приоритет безопасность ядра империи", "вежливо, но с заметной дистанцией", "Сигнал принят.", "Соблюдаем порядок и внимательно следим за военной активностью."),
        ];

        public static CommanderPersona ForRace(string raceName)
        {
            var hash = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(raceName));
            var name = Names[hash % Names.Length];
            var arc = Archetypes[hash % Archetypes.Length];
            return new CommanderPersona(name, arc.profile, arc.diplomacy, arc.prefix, arc.core);
        }
    }
}
