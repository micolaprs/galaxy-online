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
    private CommanderProfile? _commander;
    private readonly List<DiplomaticMemoryEntry> _myDiplomaticHistory = [];
    private bool _retired;

    public async Task RunTurnAsync(int turn, CancellationToken ct = default)
    {
        await EnsureCommanderProfileAsync(ct);
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
        await RefreshMyDiplomaticHistoryAsync(ct);

        var checkpoint = await EvaluateCheckpointDecisionAsync(turn, report, ct);
        if (checkpoint.ShouldSurrender)
        {
            var surrenderOrders = BuildSurrenderOrders(checkpoint);
            await PostBotStatusAsync("submitting", $"ход {turn}, решение: капитуляция", ct);
            logger.LogInformation("🏳️ [{Race}] Принял решение о капитуляции на ходу {Turn}", config.RaceName, turn);
            await SubmitOrdersAsync(surrenderOrders, final: true, ct);
            await PostBotStatusAsync("submitted", $"ход {turn} (капитуляция)", ct);
            _retired = true;
            return;
        }

        if (turn <= 3)
        {
            var openingOrders = await BuildOpeningOrdersAsync(turn, report, ct);
            if (!string.IsNullOrWhiteSpace(openingOrders))
            {
                if (!string.IsNullOrWhiteSpace(checkpoint.ContinueMessage))
                    openingOrders = AppendGlobalMessage(openingOrders, checkpoint.ContinueMessage);
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
            if (!string.IsNullOrWhiteSpace(checkpoint.ContinueMessage))
                orders = AppendGlobalMessage(orders, checkpoint.ContinueMessage);
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
            if (_retired)
            {
                await PostBotStatusAsync("idle", "раса капитулировала", ct);
                break;
            }
            try
            {
                var state = await GetCurrentTurnAsync(ct);
                if (state.IsFinished)
                {
                    var detail = !string.IsNullOrWhiteSpace(state.WinnerName)
                        ? $"игра завершена, победитель: {state.WinnerName}"
                        : "игра завершена";
                    await PostBotStatusAsync("idle", detail, ct);
                    logger.LogInformation("🏁 [{Race}] Игра завершена. {Detail}", config.RaceName, detail);
                    break;
                }
                if (state.Turn != lastTurn)
                {
                    logger.LogInformation("🔔 [{Race}] Новый ход: {Turn}", config.RaceName, state.Turn);
                    lastTurn = state.Turn;
                    await RunTurnAsync(state.Turn, ct);
                }
                else
                {
                    await PostBotStatusAsync("waiting", $"ожидаю хода {state.Turn + 1}", ct);
                    logger.LogDebug("[{Race}] Жду следующего хода (сейчас ход {Turn})", config.RaceName, state.Turn);
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

    private async Task<GameStateSnapshot> GetCurrentTurnAsync(CancellationToken ct)
    {
        var url  = $"{config.ServerUrl}/api/games/{config.GameId}";
        var json = await Http.GetFromJsonAsync<JsonElement>(url, ct);
        return new GameStateSnapshot(
            Turn: json.GetProperty("turn").GetInt32(),
            IsFinished: json.TryGetProperty("isFinished", out var finished) && finished.GetBoolean(),
            WinnerName: json.TryGetProperty("winnerName", out var winner) && winner.ValueKind == JsonValueKind.String
                ? winner.GetString()
                : null);
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
            if (line.Trim().StartsWith("ORDERS:", StringComparison.OrdinalIgnoreCase))
            {
                inOrders = true;
                continue;
            }
            if (inOrders)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("```"))
                    continue;
                result.Add(line);
            }
        }

        return result.Count > 0 ? string.Join("\n", result).Trim() : "";
    }

    private static string NormalizeOrders(string rawOrders)
    {
        var text = rawOrders.ReplaceLineEndings("\n").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = new List<string>();
        var rawLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        bool inMessage = false;

        foreach (var raw in rawLines)
        {
            var line = raw.Split(';')[0].Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("@"))
            {
                normalized.Add(line);
                inMessage = !inMessage;
                continue;
            }

            if (inMessage)
            {
                normalized.Add(line);
                continue;
            }

            // Recover glued commands safely for non-message lines, e.g. "u2p P1 Haulerp P2 MAT"
            var expanded = Regex.Replace(
                line,
                @"(?<=[A-Za-z0-9])(?=[cyn=qnprvmdtesilugbxhjawfo@](?:\d|\s))",
                "\n");

            foreach (var chunk in expanded.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var cmdLine = NormalizeCompactDesignOrder(NormalizeCompactGroupCommand(chunk.Trim()));
                if (IsLikelyOrderLine(cmdLine))
                    normalized.Add(cmdLine);
            }
        }

        return string.Join("\n", normalized).Trim();
    }

    private static bool IsLikelyOrderLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        var c = char.ToLowerInvariant(line.TrimStart()[0]);
        return "cyn=qnprvmdtesilugbxhjawfo@".Contains(c);
    }

    private static string NormalizeCompactGroupCommand(string line)
    {
        // Convert compact group commands like "l2 COL" => "l 2 COL"
        var m = Regex.Match(line, @"^(?<cmd>[silugbxh])(?<num>\d+)(?<tail>\s+.*)?$", RegexOptions.IgnoreCase);
        if (!m.Success)
            return line;
        var cmd = m.Groups["cmd"].Value;
        var num = m.Groups["num"].Value;
        var tail = m.Groups["tail"].Success ? m.Groups["tail"].Value : "";
        return $"{cmd} {num}{tail}";
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

        if (parseErrors.Count > 0)
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
            lines.Add(await GenerateBroadcastMessageAsync(
                "Это первое официальное приветствие вашей расы в новой партии. Коротко представь главнокомандующего и дипломатический стиль.",
                turn,
                ct));
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

    private async Task<CheckpointDecision> EvaluateCheckpointDecisionAsync(int turn, string report, CancellationToken ct)
    {
        if (turn < 30 || turn % 5 != 0)
            return CheckpointDecision.None;

        try
        {
            var spectateUrl = $"{config.ServerUrl}/api/games/{config.GameId}/spectate";
            var spectate = await Http.GetFromJsonAsync<JsonElement>(spectateUrl, ct);
            var players = spectate.GetProperty("players").EnumerateArray()
                .Select(p => new PlayerCheckpointStat(
                    Id: p.GetProperty("id").GetString() ?? "",
                    Name: p.GetProperty("name").GetString() ?? "",
                    PlanetCount: p.GetProperty("planetCount").GetInt32(),
                    IsEliminated: p.GetProperty("isEliminated").GetBoolean(),
                    TechPower:
                        p.GetProperty("tech").GetProperty("drive").GetDouble()
                      + p.GetProperty("tech").GetProperty("weapons").GetDouble()
                      + p.GetProperty("tech").GetProperty("shields").GetDouble()
                      + p.GetProperty("tech").GetProperty("cargo").GetDouble()))
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .ToList();

            var me = players.FirstOrDefault(p => p.Name.Equals(config.RaceName, StringComparison.OrdinalIgnoreCase));
            if (me is null)
                return CheckpointDecision.None;

            var active = players.Where(p => !p.IsEliminated).ToList();
            if (active.Count <= 1)
                return CheckpointDecision.None;

            double Score(PlayerCheckpointStat p) => p.PlanetCount * 100 + p.TechPower * 25;

            var leader = active.OrderByDescending(Score).First();
            var myScore = Score(me);
            var leaderScore = Math.Max(1.0, Score(leader));
            var scoreRatio = myScore / leaderScore;

            var allies = ParseAlliesFromReport(report);
            var allyCandidate = active
                .Where(p => allies.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Name != me.Name)
                .OrderByDescending(Score)
                .FirstOrDefault();

            var shouldSurrender = me.PlanetCount <= 1 && scoreRatio < 0.45 && turn >= 30;
            if (shouldSurrender)
            {
                var msg = await GenerateBroadcastMessageAsync(
                    allyCandidate is null
                        ? $"Ход {turn}. Вы проигрываете и должны объявить капитуляцию от имени расы {config.RaceName}."
                        : $"Ход {turn}. Вы проигрываете и сдаётесь в пользу союзника {allyCandidate.Name}.",
                    turn,
                    ct);
                return new CheckpointDecision(true, allyCandidate?.Name, msg);
            }

            var continueMsg = await GenerateBroadcastMessageAsync(
                $"Ход {turn}. Вы решили продолжать игру. Сообщи в общий чат о текущем положении и намерении сражаться дальше.",
                turn,
                ct);
            return new CheckpointDecision(false, null, continueMsg);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Checkpoint decision failed: {Msg}", ex.Message);
            return CheckpointDecision.None;
        }
    }

    private static List<string> ParseAlliesFromReport(string report)
    {
        var allies = new List<string>();
        var lines = report.ReplaceLineEndings("\n").Split('\n');
        var inStatus = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("= STATUS OF PLAYERS", StringComparison.OrdinalIgnoreCase))
            {
                inStatus = true;
                continue;
            }

            if (inStatus && line.StartsWith("="))
                break;
            if (!inStatus || line.Length == 0 || line.StartsWith("Race") || line.StartsWith("-"))
                continue;

            var m = Regex.Match(line, @"^(?<race>[^\[]+)\[ALLY\]");
            if (m.Success)
                allies.Add(m.Groups["race"].Value.Trim());
        }

        return allies;
    }

    private string BuildSurrenderOrders(CheckpointDecision checkpoint)
    {
        var lines = new List<string>
        {
            "o AUTOUNLOAD",
            "@ ALL",
            checkpoint.ContinueMessage,
            "@",
        };
        RememberDiplomaticMessage(checkpoint.ContinueMessage, isSurrender: true);

        if (!string.IsNullOrWhiteSpace(checkpoint.AllyName))
            lines.Add($"a {checkpoint.AllyName}");
        lines.Add($"q {checkpoint.AllyName ?? "RETIRE"}");
        return string.Join('\n', lines);
    }

    private static string AppendGlobalMessage(string orders, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return orders;

        var text = orders.Trim();
        var payload = $"@ ALL\n{message}\n@";
        if (text.Contains(payload, StringComparison.Ordinal))
            return text;
        return string.IsNullOrWhiteSpace(text) ? payload : $"{text}\n{payload}";
    }

    private async Task EnsureCommanderProfileAsync(CancellationToken ct)
    {
        if (_commander is not null)
            return;

        try
        {
            var response = await llm.CompleteAsync(
            [
                ChatMessage.System("""
                    You generate a compact character profile for a space strategy commander.
                    Return STRICT JSON with keys:
                    commanderName, shortBackstory, coreTraits, diplomaticTone, signaturePhrase.
                    Keep values short (1 sentence each; commanderName 2-4 words).
                    """),
                ChatMessage.User($"Race: {config.RaceName}. Create a distinct commander persona in Russian."),
            ], ct);

            var jsonText = TryExtractJsonObject(response);
            if (!string.IsNullOrWhiteSpace(jsonText))
            {
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;
                _commander = new CommanderProfile(
                    CommanderName: ReadJson(root, "commanderName", $"Командор {config.RaceName}"),
                    ShortBackstory: ReadJson(root, "shortBackstory", "Ветеран пограничных кампаний и стратег колониальных операций."),
                    CoreTraits: ReadJson(root, "coreTraits", "прагматичный, дисциплинированный, хладнокровный"),
                    DiplomaticTone: ReadJson(root, "diplomaticTone", "коротко, уважительно и по делу"),
                    SignaturePhrase: ReadJson(root, "signaturePhrase", "Канал подтверждён.")
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not generate commander profile from LLM: {Msg}", ex.Message);
        }

        _commander ??= CommanderProfile.Fallback(config.RaceName);
    }

    private async Task RefreshMyDiplomaticHistoryAsync(CancellationToken ct)
    {
        try
        {
            var url = $"{config.ServerUrl}/api/games/{config.GameId}/spectate";
            var spectate = await Http.GetFromJsonAsync<JsonElement>(url, ct);
            if (!spectate.TryGetProperty("diplomacy", out var diplomacy))
                return;

            var entries = new List<DiplomaticMemoryEntry>();
            if (diplomacy.TryGetProperty("globalMessages", out var globals))
            {
                foreach (var m in globals.EnumerateArray())
                {
                    var sender = m.TryGetProperty("senderName", out var sn) ? sn.GetString() ?? "" : "";
                    if (!sender.Equals(config.RaceName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var text = m.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    var turn = m.TryGetProperty("turn", out var trn) ? trn.GetInt32() : 0;
                    if (!string.IsNullOrWhiteSpace(text))
                        entries.Add(new DiplomaticMemoryEntry(turn, text, false));
                }
            }

            _myDiplomaticHistory.Clear();
            _myDiplomaticHistory.AddRange(entries.OrderBy(e => e.Turn).TakeLast(20));
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not refresh diplomatic history: {Msg}", ex.Message);
        }
    }

    private async Task<string> GenerateBroadcastMessageAsync(string intent, int turn, CancellationToken ct)
    {
        await EnsureCommanderProfileAsync(ct);
        var profile = _commander!;
        var history = _myDiplomaticHistory.Count == 0
            ? "(нет прошлых сообщений)"
            : string.Join("\n", _myDiplomaticHistory.TakeLast(8).Select(m => $"- Ход {m.Turn}: {m.Text}"));

        var response = await llm.CompleteAsync(
        [
            ChatMessage.System($"""
                Ты пишешь дипломатические сообщения ТОЛЬКО от имени главнокомандующего.
                Персонаж:
                - Имя: {profile.CommanderName}
                - История: {profile.ShortBackstory}
                - Характер: {profile.CoreTraits}
                - Тон: {profile.DiplomaticTone}
                - Фирменная фраза: {profile.SignaturePhrase}
                Пиши коротко (1 предложение, максимум 140 символов), без markdown, без списка приказов и без символа ';'.
                """),
            ChatMessage.User($"""
                Ход: {turn}
                История прошлых сообщений этой расы:
                {history}

                Задача:
                {intent}
                """),
        ], ct);

        var message = response.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrWhiteSpace(message))
            message = $"{profile.SignaturePhrase} Раса {config.RaceName} продолжает текущий курс.";
        message = message.Replace(';', ',');
        if (message.Length > 160)
        {
            message = message[..160];
            var cut = message.LastIndexOf(' ');
            if (cut > 80) message = message[..cut];
            message = message.TrimEnd(',', '.', ' ') + ".";
        }

        RememberDiplomaticMessage(message, isSurrender: false, turn);
        return message;
    }

    private void RememberDiplomaticMessage(string text, bool isSurrender, int turn = 0)
    {
        var effectiveTurn = turn > 0 ? turn : (_myDiplomaticHistory.LastOrDefault()?.Turn ?? 0);
        _myDiplomaticHistory.Add(new DiplomaticMemoryEntry(effectiveTurn, text, isSurrender));
        if (_myDiplomaticHistory.Count > 30)
            _myDiplomaticHistory.RemoveRange(0, _myDiplomaticHistory.Count - 30);
    }

    private static string? TryExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return text[start..(end + 1)];
    }

    private static string ReadJson(JsonElement root, string key, string fallback)
    {
        return root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String
            ? (prop.GetString() ?? fallback)
            : fallback;
    }

    private sealed record PlanetSnapshot(string Name, double X, double Y, string? OwnerId);
    private sealed record PlayerCheckpointStat(string Id, string Name, int PlanetCount, bool IsEliminated, double TechPower);
    private sealed record GameStateSnapshot(int Turn, bool IsFinished, string? WinnerName);
    private sealed record CheckpointDecision(bool ShouldSurrender, string? AllyName, string ContinueMessage)
    {
        public static CheckpointDecision None => new(false, null, "");
    }

    private string BuildCommanderInstruction()
    {
        var profile = _commander ?? CommanderProfile.Fallback(config.RaceName);
        var history = _myDiplomaticHistory.Count == 0
            ? "none"
            : string.Join(" | ", _myDiplomaticHistory.TakeLast(5).Select(m => $"T{m.Turn}:{m.Text}"));

        return $"You are Supreme Commander {profile.CommanderName} of race {config.RaceName}. " +
               $"Backstory: {profile.ShortBackstory}. " +
               $"Leadership traits: {profile.CoreTraits}. " +
               $"Diplomatic style: {profile.DiplomaticTone}. " +
               $"Recent diplomacy messages sent by your race: {history}. " +
               "If you send diplomacy messages, keep this voice consistent and distinct.";
    }

    private sealed record CommanderProfile(
        string CommanderName,
        string ShortBackstory,
        string CoreTraits,
        string DiplomaticTone,
        string SignaturePhrase)
    {
        public static CommanderProfile Fallback(string raceName)
        {
            var idx = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(raceName)) % 6;
            return idx switch
            {
                0 => new CommanderProfile("Архонт Велаар", "Ветеран приграничных конфликтов и мастер оборонительных кампаний.", "осторожный, расчётливый, дисциплинированный", "сдержанный и прагматичный", "Канал подтверждён."),
                1 => new CommanderProfile("Маршал Кайрос", "Бывший командир экспедиционных флотов дальнего рубежа.", "агрессивный, решительный, прямой", "жёсткий и напористый", "Эфир открыт."),
                2 => new CommanderProfile("Коммодор Лекса Вар", "Инженер-стратег, построившая технократическую военную доктрину.", "техничный, спокойный, системный", "деловой и технический", "Линия чистая."),
                3 => new CommanderProfile("Наварх Мирен Каэль", "Дипломат войны, умеющий превращать перемирия в выгоду.", "гибкий, дипломатичный, прагматичный", "вежливый и предметный", "Связь установлена."),
                4 => new CommanderProfile("Адмирал Корвин Дейр", "Командовал флотом в затяжных войнах на истощение.", "стойкий, холодный, упорный", "короткий и сухой", "Сигнал принят."),
                _ => new CommanderProfile("Доминус Харек Соль", "Выходец из колониальных ополчений, сторонник быстрых ударов.", "инициативный, резкий, амбициозный", "энергичный и уверенный", "Передача устойчива."),
            };
        }
    }

    private sealed record DiplomaticMemoryEntry(int Turn, string Text, bool IsSurrender);
}
