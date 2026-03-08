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
    private readonly TimeSpan _llmTimeout = TimeSpan.FromSeconds(Math.Clamp(config.LlmTimeoutSeconds, 30, 300));
    private CommanderProfile? _commander;
    private readonly List<DiplomaticMemoryEntry> _myDiplomaticHistory = [];
    private bool _sentInitialGreeting;
    private bool _retired;
    private string _lastValidationErrors = "";
    private TurnToolContext? _turnToolContext;

    private static readonly IReadOnlyList<ToolDefinition> TurnTools =
    [
        new ToolDefinition(
            Name: "get_turn_context",
            Description: "Get canonical, current-turn tokens for orders: planet names, ship names, allowed production and cargo values.",
            Parameters: new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }),
        new ToolDefinition(
            Name: "design_ship",
            Description: "Return canonical design command for a requested template role (scout, hauler, fighter).",
            Parameters: new
            {
                type = "object",
                properties = new
                {
                    role = new { type = "string", description = "scout | hauler | fighter" }
                },
                required = new[] { "role" }
            }),
        new ToolDefinition(
            Name: "validate_orders",
            Description: "Validate your proposed GalaxyNG orders. Returns 'OK' or a list of errors. Call this before finalizing.",
            Parameters: new
            {
                type = "object",
                properties = new
                {
                    orders = new { type = "string", description = "Orders to validate, one per line" }
                },
                required = new[] { "orders" }
            })
    ];

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
            await PostBotStatusAsync("submitting", $"ход {turn}, fallback без отчёта", ct);
            await SubmitOrdersAsync("o AUTOUNLOAD", final: true, ct);
            await PostBotStatusAsync("submitted", $"ход {turn} (fallback)", ct);
            return;
        }
        int reportLines = report.Split('\n').Length;
        logger.LogInformation("📋 [{Race}] Отчёт получен ({Lines} строк)", config.RaceName, reportLines);
        await RefreshMyDiplomaticHistoryAsync(ct);
        _turnToolContext = await BuildTurnToolContextAsync(turn, report, ct);

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
                ChatMessage.User(BuildDynamicTokenHint(_turnToolContext)),
                ChatMessage.User(BuildJsonOutputContractHint()),
                ChatMessage.User($"## Turn Report\n\n{report}\n\nProvide your orders for this turn."),
            };

            if (attempt > 1 && !string.IsNullOrEmpty(orders))
            {
                messages.Add(ChatMessage.Assistant(orders));
                var shipNames = ExtractDesignedShipNames(report);
                var shipHint = shipNames.Count > 0
                    ? $"\nREMINDER: Your designed ship names are: {string.Join(", ", shipNames)}. Use these EXACT names in `p` commands — no suffixes or variants."
                    : "";
                messages.Add(ChatMessage.User(_lastValidationErrors is { Length: > 0 }
                    ? $"Those orders had validation errors. Fix them and rewrite ALL orders.\n\n{_lastValidationErrors}{shipHint}"
                    : $"Those orders had validation errors. Fix them and rewrite ALL orders.{shipHint}"));
            }

            string raw = await CompleteLlmWithTimeoutAsync(messages, $"turn-{turn}-attempt-{attempt}", ct, TurnTools, TurnToolExecutor);
            await thinkingCts.CancelAsync();
            await heartbeat;

            // Broadcast the full LLM response (reasoning + orders) to the UI
            await PostBotStatusAsync("thinking", $"ход {turn}, попытка {attempt}/3 — ответ получен", raw, ct);

            var extracted = ExtractOrdersFromStructuredOrText(raw, out var usedJson);
            orders = NormalizeOrders(extracted);
            if (!string.IsNullOrWhiteSpace(checkpoint.ContinueMessage))
                orders = AppendGlobalMessage(orders, checkpoint.ContinueMessage);
            orders = await AppendDynamicDiplomacyAsync(orders, turn, report, ct);
            orders = CanonicalizeOrdersWithTurnContext(orders, _turnToolContext, out var canonicalFixes);
            orders = EnforceCommandWhitelist(orders, _turnToolContext, out var rewrittenByPreflight, out var droppedByPreflight);
            if (rewrittenByPreflight > 0 || droppedByPreflight > 0)
            {
                logger.LogInformation("🧱 [{Race}] Preflight: rewritten={Rewritten}, dropped={Dropped}",
                    config.RaceName, rewrittenByPreflight, droppedByPreflight);
            }
            if (!ValidateOrdersAgainstTurnContext(orders, _turnToolContext, out var semanticError))
            {
                _lastValidationErrors = semanticError;
                logger.LogWarning("⚠️ [{Race}] Локальная проверка контекста не пройдена (попытка {Attempt}): {Error}",
                    config.RaceName, attempt, semanticError);
                continue;
            }
            if (canonicalFixes > 0)
            {
                logger.LogInformation("🛠️ [{Race}] Автоканонизация исправила {Count} токен(ов) в приказах",
                    config.RaceName, canonicalFixes);
            }
            int orderLines = orders.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            logger.LogInformation("📝 [{Race}] LLM составил {Lines} приказов (из {Total} знаков ответа, json={Json})",
                config.RaceName, orderLines, raw.Length, usedJson);

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
                _lastValidationErrors = "";
                logger.LogInformation("✅ [{Race}] Приказы прошли проверку (попытка {Attempt})", config.RaceName, attempt);
                haveValidOrders = true;
                break;
            }

            _lastValidationErrors = validation;
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
                else if (!state.MySubmitted)
                {
                    logger.LogWarning("↻ [{Race}] Ход {Turn} не отмечен как submitted, повторяю отправку.",
                        config.RaceName, state.Turn);
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
        var url  = $"{config.ServerUrl}/api/games/{config.GameId}/validate-orders";
        var body = new { raceName = config.RaceName, password = config.Password, orders };
        try
        {
            using var response = await Http.PostAsJsonAsync(url, body, ct);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (json.TryGetProperty("valid", out var validProp) && validProp.GetBoolean())
                return "OK";

            // Collect error list from response
            var errorList = new List<string>();
            if (json.TryGetProperty("errors", out var errArr) && errArr.ValueKind == JsonValueKind.Array)
                foreach (var e in errArr.EnumerateArray())
                    if (e.GetString() is { } s) errorList.Add(s);

            return errorList.Count > 0
                ? "Errors:\n" + string.Join("\n", errorList.Select(e => $"  - {e}"))
                : "Error: validation failed";
        }
        catch (Exception ex)
        {
            logger.LogDebug("Validate-orders call failed: {Msg}", ex.Message);
            return "Error: could not reach server";
        }
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
        var mySubmitted = false;
        if (json.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in players.EnumerateArray())
            {
                var name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.Equals(config.RaceName, StringComparison.OrdinalIgnoreCase))
                    continue;
                mySubmitted = p.TryGetProperty("submitted", out var s) && s.ValueKind == JsonValueKind.True;
                break;
            }
        }

        return new GameStateSnapshot(
            Turn: json.GetProperty("turn").GetInt32(),
            IsFinished: json.TryGetProperty("isFinished", out var finished) && finished.GetBoolean(),
            MySubmitted: mySubmitted,
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

    private static string ExtractOrdersFromStructuredOrText(string llmResponse, out bool usedJson)
    {
        usedJson = false;
        var json = TryExtractJsonObject(llmResponse);
        if (string.IsNullOrWhiteSpace(json))
            return "";

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array)
                return "";

            var lines = new List<string>();
            foreach (var cmd in commands.EnumerateArray())
            {
                if (cmd.ValueKind == JsonValueKind.String)
                {
                    var line = cmd.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(line))
                        lines.Add(line);
                    continue;
                }

                if (cmd.ValueKind != JsonValueKind.Object)
                    continue;
                if (!cmd.TryGetProperty("cmd", out var codeNode) || codeNode.ValueKind != JsonValueKind.String)
                    continue;

                var code = codeNode.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                var args = new List<string>();
                if (cmd.TryGetProperty("args", out var argsNode) && argsNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in argsNode.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.String)
                        {
                            var s = a.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                                args.Add(s.Trim());
                        }
                        else if (a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var n))
                        {
                            args.Add(n.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                }

                var full = args.Count == 0 ? code : $"{code} {string.Join(' ', args)}";
                lines.Add(full);
            }

            usedJson = true;
            return string.Join('\n', lines).Trim();
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeOrders(string rawOrders)
    {
        var text = rawOrders.ReplaceLineEndings("\n").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = new List<string>();
        bool inMessage = false;

        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Inside @ blocks: pass lines through as-is (no stripping, no filtering)
            if (inMessage)
            {
                if (raw.Trim().StartsWith("@"))
                {
                    normalized.Add(raw.Trim());
                    inMessage = false;
                }
                else
                {
                    normalized.Add(raw);
                }
                continue;
            }

            var line = raw.Split(';')[0].Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("@"))
            {
                normalized.Add(line);
                inMessage = true;
                continue;
            }

            var cmdLine = NormalizeCompactDesignOrder(NormalizeCompactGroupCommand(line));
            if (IsLikelyOrderLine(cmdLine))
                normalized.Add(cmdLine);
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
        var hasHaulerDesign  = ReportMentionsShipType(report, "Hauler");
        var hasScoutDesign   = ReportMentionsShipType(report, "Scout");
        var hasFighterDesign = ReportMentionsShipType(report, "Fighter");

        if (!hasScoutDesign)
            lines.Add("d Scout 1 0 0 0 0");
        if (!hasHaulerDesign)
            lines.Add("d Hauler 1 0 0 0 2");
        if (!hasFighterDesign)
            lines.Add("d Fighter 2 2 2 1 0");

        // Turn 0: start with Haulers to expand quickly
        // Turn 1+: switch to Fighters to build military
        lines.Add(turn == 0 ? $"p {home.Name} Hauler" : $"p {home.Name} Fighter");

        if (turn == 0 && !_sentInitialGreeting)
        {
            lines.Add("@ ALL");
            lines.Add(await GenerateBroadcastMessageAsync(
                "Это первое официальное приветствие вашей расы в новой партии. Коротко представь главнокомандующего и дипломатический стиль.",
                turn,
                ct));
            lines.Add("@");
            _sentInitialGreeting = true;
        }

        // Turn 0: send any starting group toward nearest planet
        if (turn == 0 && groups.Count > 0 && !string.IsNullOrWhiteSpace(target))
            lines.Add($"s {groups[0]} {target}");

        // Turn 1+: load colonists and send Haulers to expand
        if (turn >= 1 && groups.Count > 0 && !string.IsNullOrWhiteSpace(target))
        {
            lines.Add($"l {groups[0]} COL");
            lines.Add($"s {groups[0]} {target}");
            // Set a cargo route so Haulers cycle automatically
            lines.Add($"r {home.Name} COL {target}");
        }

        // Turn 2+: send second group (likely a Fighter) forward
        if (turn >= 2 && groups.Count > 1 && !string.IsNullOrWhiteSpace(target))
            lines.Add($"s {groups[1]} {target}");

        return await AppendDynamicDiplomacyAsync(string.Join('\n', lines), turn, report, ct);
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

    private static List<string> ExtractDesignedShipNames(string report)
    {
        // Parses the YOUR SHIP DESIGNS section: lines like "  Fighter   2  2  2  1  0"
        var names = new List<string>();
        bool inSection = false;
        foreach (var line in report.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Contains("SHIP DESIGN", StringComparison.OrdinalIgnoreCase))
            {
                inSection = true;
                continue;
            }
            if (inSection)
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("YOUR ") || line.TrimStart().StartsWith("==="))
                {
                    if (names.Count > 0) break;
                    continue;
                }
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out _))
                    names.Add(parts[0]);
            }
        }
        return names;
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

            var m = Regex.Match(line, @"^(?<race>[^\[]+)\[ALLY");
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

    private static string AppendPrivateMessage(string orders, string targetRace, string message)
    {
        if (string.IsNullOrWhiteSpace(targetRace) || string.IsNullOrWhiteSpace(message))
            return orders;

        var text = orders.Trim();
        var payload = $"@ {targetRace}\n{message}\n@";
        if (text.Contains(payload, StringComparison.Ordinal))
            return text;
        return string.IsNullOrWhiteSpace(text) ? payload : $"{text}\n{payload}";
    }

    private async Task<string> AppendDynamicDiplomacyAsync(string orders, int turn, string report, CancellationToken ct)
    {
        var result = orders;
        var context = await GetDiplomacyContextAsync(ct);
        if (context is null)
            return result;

        if (turn > 0 && turn % 4 == 0)
        {
            var global = await GenerateBroadcastMessageAsync(
                "Свободное дипломатическое сообщение в общий чат: эмоция, угроза, ирония, самоутверждение или хвалебная речь.",
                turn,
                ct);
            result = AppendGlobalMessage(result, global);
        }

        if (turn >= 2 && turn % 3 == 0 && context.PrivateContacts.Count > 0)
        {
            var allies = ParseAlliesFromReport(report);
            var target = context.PrivateContacts
                .FirstOrDefault(c => !allies.Contains(c, StringComparer.OrdinalIgnoreCase))
                ?? context.PrivateContacts[0];

            var privateMsg = await GenerateBroadcastMessageAsync(
                $"Личное сообщение для расы {target}: дипломатическая игра, блеф или предложение сделки/союза.",
                turn,
                ct);
            result = AppendPrivateMessage(result, target, privateMsg);

            if (!allies.Contains(target, StringComparer.OrdinalIgnoreCase) && turn % 6 == 0)
                result = $"{result}\na {target} {turn + 8}";
        }

        return result;
    }

    private async Task<DiplomacyContext?> GetDiplomacyContextAsync(CancellationToken ct)
    {
        try
        {
            var url = $"{config.ServerUrl}/api/games/{config.GameId}/spectate";
            var spectate = await Http.GetFromJsonAsync<JsonElement>(url, ct);
            var players = spectate.GetProperty("players").EnumerateArray().ToList();
            var me = players.FirstOrDefault(p =>
                string.Equals(p.GetProperty("name").GetString(), config.RaceName, StringComparison.OrdinalIgnoreCase));
            if (me.ValueKind == JsonValueKind.Undefined)
                return null;

            var myId = me.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(myId))
                return null;

            var contacts = new List<string>();
            if (spectate.TryGetProperty("diplomacy", out var diplomacy)
                && diplomacy.TryGetProperty("privateChats", out var privateChats))
            {
                foreach (var chat in privateChats.EnumerateArray())
                {
                    var aId = chat.GetProperty("playerAId").GetString() ?? "";
                    var bId = chat.GetProperty("playerBId").GetString() ?? "";
                    var aName = chat.GetProperty("playerAName").GetString() ?? "";
                    var bName = chat.GetProperty("playerBName").GetString() ?? "";
                    if (string.Equals(aId, myId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(bName))
                        contacts.Add(bName);
                    else if (string.Equals(bId, myId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(aName))
                        contacts.Add(aName);
                }
            }

            return new DiplomacyContext([.. contacts.Distinct(StringComparer.OrdinalIgnoreCase)]);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not load diplomacy context: {Msg}", ex.Message);
            return null;
        }
    }

    private async Task EnsureCommanderProfileAsync(CancellationToken ct)
    {
        if (_commander is not null)
            return;

        try
        {
            var response = await CompleteLlmWithTimeoutAsync(
            [
                ChatMessage.System("""
                    You generate a compact character profile for a space strategy commander.
                    Return STRICT JSON with keys:
                    commanderName, shortBackstory, coreTraits, diplomaticTone, signaturePhrase.
                    Keep values short (1 sentence each; commanderName 2-4 words).
                    """),
                ChatMessage.User($"Race: {config.RaceName}. Create a distinct commander persona in Russian."),
            ], "commander-profile", ct);

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
                    if (turn == 0)
                        _sentInitialGreeting = true;
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

        var response = await CompleteLlmWithTimeoutAsync(
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
        ], $"diplomacy-turn-{turn}", ct);

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

    private async Task<string> TurnToolExecutor(string name, string argsJson)
    {
        if (name.Equals("validate_orders", StringComparison.OrdinalIgnoreCase))
        {
            var args = JsonDocument.Parse(argsJson).RootElement;
            var orders = args.TryGetProperty("orders", out var o) ? o.GetString() ?? "" : "";
            var result = await ValidateOrdersAsync(orders, CancellationToken.None);
            logger.LogInformation("🔧 [{Race}] Tool call: validate_orders → {Result}", config.RaceName, result[..Math.Min(80, result.Length)]);
            return result;
        }

        if (name.Equals("get_turn_context", StringComparison.OrdinalIgnoreCase))
        {
            var ctx = _turnToolContext;
            if (ctx is null)
                return """{"error":"turn context is not initialized"}""";

            var payload = JsonSerializer.Serialize(new
            {
                turn = ctx.Turn,
                race = config.RaceName,
                allowedProductionTypes = ctx.AllowedProductionTypes,
                allowedCargoTypes = ctx.AllowedCargoTypes,
                designedShipNames = ctx.DesignedShipNames,
                knownPlanetNames = ctx.KnownPlanetNames,
                idleGroupNumbers = ctx.GroupNumbers,
            });
            logger.LogInformation("🔧 [{Race}] Tool call: get_turn_context → planets={Planets} ships={Ships}",
                config.RaceName, ctx.KnownPlanetNames.Count, ctx.DesignedShipNames.Count);
            return payload;
        }

        if (name.Equals("design_ship", StringComparison.OrdinalIgnoreCase))
        {
            var args = JsonDocument.Parse(argsJson).RootElement;
            var role = args.TryGetProperty("role", out var r) ? (r.GetString() ?? "") : "";
            var command = role.Trim().ToLowerInvariant() switch
            {
                "scout" => "d Scout 1 0 0 0 0",
                "hauler" => "d Hauler 1 0 0 0 2",
                "fighter" => "d Fighter 2 2 2 1 0",
                _ => "d Scout 1 0 0 0 0",
            };
            logger.LogInformation("🔧 [{Race}] Tool call: design_ship({Role}) → {Command}", config.RaceName, role, command);
            return JsonSerializer.Serialize(new { role, command });
        }

        logger.LogWarning("⚠️ [{Race}] Unknown tool requested: {Tool}", config.RaceName, name);
        return $"Error: unknown tool '{name}'.";
    }

    private async Task<TurnToolContext> BuildTurnToolContextAsync(int turn, string report, CancellationToken ct)
    {
        var designedShips = ExtractDesignedShipNames(report)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var knownPlanets = await GetKnownPlanetNamesFromSpectateAsync(ct);
        if (knownPlanets.Count == 0)
            knownPlanets = ParsePlanetNamesFromReport(report);
        var groupNumbers = ParseGroupNumbers(report);

        var allowedProduction = new List<string> { "CAP", "MAT", "DRIVE", "WEAPONS", "SHIELDS", "CARGO" };
        foreach (var ship in designedShips)
            if (!allowedProduction.Contains(ship, StringComparer.OrdinalIgnoreCase))
                allowedProduction.Add(ship);

        return new TurnToolContext(
            Turn: turn,
            KnownPlanetNames: knownPlanets,
            DesignedShipNames: designedShips,
            AllowedProductionTypes: allowedProduction,
            AllowedCargoTypes: new List<string> { "CAP", "COL", "MAT", "EMPTY" },
            GroupNumbers: groupNumbers);
    }

    private async Task<List<string>> GetKnownPlanetNamesFromSpectateAsync(CancellationToken ct)
    {
        try
        {
            var url = $"{config.ServerUrl}/api/games/{config.GameId}/spectate";
            var spectate = await Http.GetFromJsonAsync<JsonElement>(url, ct);
            if (!spectate.TryGetProperty("planets", out var planets) || planets.ValueKind != JsonValueKind.Array)
                return [];

            return planets.EnumerateArray()
                .Select(p => p.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not load planets from spectate for tool context: {Msg}", ex.Message);
            return [];
        }
    }

    private static List<string> ParsePlanetNamesFromReport(string report)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in report.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line.StartsWith("=") || line.StartsWith("#") || line.StartsWith("Name", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = Regex.Split(line, @"\s+");
            if (parts.Length < 3)
                continue;
            if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                continue;
            if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                continue;

            result.Add(parts[0]);
        }

        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string BuildDynamicTokenHint(TurnToolContext? ctx)
    {
        if (ctx is null)
            return "If uncertain, call get_turn_context and validate_orders tools before finalizing orders.";

        var planets = ctx.KnownPlanetNames.Count == 0
            ? "(none)"
            : string.Join(", ", ctx.KnownPlanetNames);
        var ships = ctx.DesignedShipNames.Count == 0
            ? "(none)"
            : string.Join(", ", ctx.DesignedShipNames);
        var groups = ctx.GroupNumbers.Count == 0
            ? "(none)"
            : string.Join(", ", ctx.GroupNumbers);
        var prod = string.Join(", ", ctx.AllowedProductionTypes);
        var cargo = string.Join(", ", ctx.AllowedCargoTypes);

        return $"""
            STRICT TOKEN CONSTRAINTS (current turn):
            - Allowed planets for `s`/`r`/`p`: {planets}
            - Allowed production values for `p`: {prod}
            - Exact ship names from your designs: {ships}
            - Allowed cargo values: {cargo}
            - Available group numbers from report: {groups}
            If uncertain or validation fails, call `get_turn_context` and then `validate_orders`.
            """;
    }

    private static string BuildJsonOutputContractHint()
    {
        return """
            OUTPUT CONTRACT (STRICT, JSON ONLY):
            Return exactly one JSON object with this shape:
            {
              "reasoning": "short reasoning",
              "commands": [
                { "cmd": "o", "args": ["AUTOUNLOAD"] },
                { "cmd": "p", "args": ["P11", "Fighter"] },
                { "cmd": "s", "args": [1, "P12"] }
              ]
            }
            Rules:
            - No markdown, no code fences, no prose outside JSON.
            - `commands` is required and must contain all orders for the turn.
            - Each command must be either object {"cmd","args"} or a single string line.
            """;
    }

    private static bool ValidateOrdersAgainstTurnContext(string orders, TurnToolContext? ctx, out string error)
    {
        if (string.IsNullOrWhiteSpace(orders))
        {
            error = "JSON commands were empty or unparsable.";
            return false;
        }
        if (ctx is null)
        {
            error = "";
            return true;
        }

        var allowedGroups = new HashSet<int>(ctx.GroupNumbers);
        var allowedPlanets = new HashSet<string>(ctx.KnownPlanetNames, StringComparer.OrdinalIgnoreCase);
        var allowedProd = new HashSet<string>(ctx.AllowedProductionTypes, StringComparer.OrdinalIgnoreCase);
        var allowedCargo = new HashSet<string>(ctx.AllowedCargoTypes, StringComparer.OrdinalIgnoreCase);
        var localCargo = new HashSet<string>(new[] { "CAP", "COL", "MAT" }, StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        foreach (var line in orders.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;
            var cmd = parts[0].ToLowerInvariant();

            if (cmd == "p" && parts.Length >= 3)
            {
                if (!allowedPlanets.Contains(parts[1]))
                    problems.Add($"unknown planet in p: {parts[1]} (try: {SuggestToken(parts[1], ctx.KnownPlanetNames)})");
                if (!allowedProd.Contains(parts[2]))
                    problems.Add($"unknown production type in p: {parts[2]} (try: {SuggestToken(parts[2], ctx.AllowedProductionTypes)})");
            }
            else if (cmd == "s" && parts.Length >= 3)
            {
                if (!int.TryParse(parts[1], out var g) || (allowedGroups.Count > 0 && !allowedGroups.Contains(g)))
                    problems.Add($"unknown group in s: {parts[1]}");
                if (!allowedPlanets.Contains(parts[2]))
                    problems.Add($"unknown planet in s: {parts[2]} (try: {SuggestToken(parts[2], ctx.KnownPlanetNames)})");
            }
            else if (cmd == "l" && parts.Length >= 3)
            {
                if (!int.TryParse(parts[1], out var g) || (allowedGroups.Count > 0 && !allowedGroups.Contains(g)))
                    problems.Add($"unknown group in l: {parts[1]}");
                if (!localCargo.Contains(parts[2]))
                    problems.Add($"unknown cargo in l: {parts[2]} (try: {SuggestToken(parts[2], localCargo.ToList())})");
            }
            else if (cmd == "r" && parts.Length >= 4)
            {
                if (!allowedPlanets.Contains(parts[1]))
                    problems.Add($"unknown route planet: {parts[1]} (try: {SuggestToken(parts[1], ctx.KnownPlanetNames)})");
                if (!allowedCargo.Contains(parts[2]))
                    problems.Add($"unknown route cargo: {parts[2]} (try: {SuggestToken(parts[2], ctx.AllowedCargoTypes)})");
                if (!allowedPlanets.Contains(parts[3]))
                    problems.Add($"unknown route destination: {parts[3]} (try: {SuggestToken(parts[3], ctx.KnownPlanetNames)})");
            }
        }

        if (problems.Count == 0)
        {
            error = "";
            return true;
        }

        error = "Local semantic validation failed:\n" + string.Join("\n", problems.Distinct().Take(6).Select(p => $"  - {p}"));
        return false;
    }

    private static string EnforceCommandWhitelist(string orders, TurnToolContext? ctx, out int rewritten, out int dropped)
    {
        rewritten = 0;
        dropped = 0;
        if (ctx is null || string.IsNullOrWhiteSpace(orders))
            return orders;

        var allowedGroups = new HashSet<int>(ctx.GroupNumbers);
        var outLines = new List<string>();

        foreach (var rawLine in orders.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("@"))
            {
                outLines.Add(rawLine);
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count == 0)
                continue;
            var cmd = parts[0].ToLowerInvariant();
            var original = string.Join(' ', parts);

            bool keep = true;
            if (cmd == "p" && parts.Count >= 3)
            {
                parts[1] = CanonicalizeToken(parts[1], ctx.KnownPlanetNames);
                parts[2] = CanonicalizeToken(parts[2], ctx.AllowedProductionTypes);
                keep = ctx.KnownPlanetNames.Contains(parts[1], StringComparer.OrdinalIgnoreCase)
                    && ctx.AllowedProductionTypes.Contains(parts[2], StringComparer.OrdinalIgnoreCase);
            }
            else if (cmd == "s" && parts.Count >= 3)
            {
                parts[2] = CanonicalizeToken(parts[2], ctx.KnownPlanetNames);
                keep = int.TryParse(parts[1], out var g) && (allowedGroups.Count == 0 || allowedGroups.Contains(g))
                    && ctx.KnownPlanetNames.Contains(parts[2], StringComparer.OrdinalIgnoreCase);
            }
            else if (cmd == "l" && parts.Count >= 3)
            {
                var allowedLocal = new List<string> { "CAP", "COL", "MAT" };
                parts[2] = CanonicalizeToken(parts[2], allowedLocal);
                keep = int.TryParse(parts[1], out var g) && (allowedGroups.Count == 0 || allowedGroups.Contains(g))
                    && allowedLocal.Contains(parts[2], StringComparer.OrdinalIgnoreCase);
            }
            else if (cmd == "r" && parts.Count >= 4)
            {
                parts[1] = CanonicalizeToken(parts[1], ctx.KnownPlanetNames);
                parts[2] = CanonicalizeToken(parts[2], ctx.AllowedCargoTypes);
                parts[3] = CanonicalizeToken(parts[3], ctx.KnownPlanetNames);
                keep = ctx.KnownPlanetNames.Contains(parts[1], StringComparer.OrdinalIgnoreCase)
                    && ctx.AllowedCargoTypes.Contains(parts[2], StringComparer.OrdinalIgnoreCase)
                    && ctx.KnownPlanetNames.Contains(parts[3], StringComparer.OrdinalIgnoreCase);
            }

            if (!keep)
            {
                dropped++;
                continue;
            }

            var rewrittenLine = string.Join(' ', parts);
            if (!rewrittenLine.Equals(original, StringComparison.Ordinal))
                rewritten++;
            outLines.Add(rewrittenLine);
        }

        return string.Join('\n', outLines).Trim();
    }

    private static string SuggestToken(string token, IReadOnlyList<string> allowed)
    {
        if (allowed.Count == 0) return "(none)";
        var canonical = CanonicalizeToken(token, allowed);
        if (allowed.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            return canonical;
        return allowed[0];
    }

    private static string CanonicalizeOrdersWithTurnContext(string orders, TurnToolContext? ctx, out int fixes)
    {
        fixes = 0;
        if (ctx is null || string.IsNullOrWhiteSpace(orders))
            return orders;

        var lines = new List<string>();
        bool inMessage = false;
        foreach (var rawLine in orders.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (inMessage)
            {
                lines.Add(rawLine);
                if (line.StartsWith("@"))
                    inMessage = false;
                continue;
            }
            if (line.StartsWith("@"))
            {
                lines.Add(rawLine);
                inMessage = true;
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count == 0)
                continue;

            var cmd = parts[0].ToLowerInvariant();
            if (cmd == "p" && parts.Count >= 3)
            {
                fixes += CanonicalizeTokenInPlace(parts, 1, ctx.KnownPlanetNames);
                fixes += CanonicalizeTokenInPlace(parts, 2, ctx.AllowedProductionTypes);
            }
            else if (cmd == "s" && parts.Count >= 3)
            {
                fixes += CanonicalizeTokenInPlace(parts, 2, ctx.KnownPlanetNames);
            }
            else if (cmd == "l" && parts.Count >= 3)
            {
                fixes += CanonicalizeTokenInPlace(parts, 2, new List<string> { "CAP", "COL", "MAT" });
            }
            else if (cmd == "r" && parts.Count >= 4)
            {
                fixes += CanonicalizeTokenInPlace(parts, 1, ctx.KnownPlanetNames);
                fixes += CanonicalizeTokenInPlace(parts, 2, ctx.AllowedCargoTypes);
                fixes += CanonicalizeTokenInPlace(parts, 3, ctx.KnownPlanetNames);
            }

            lines.Add(string.Join(' ', parts));
        }

        return string.Join('\n', lines).Trim();
    }

    private static int CanonicalizeTokenInPlace(List<string> tokens, int index, IReadOnlyList<string> allowed)
    {
        if (index >= tokens.Count || allowed.Count == 0)
            return 0;
        var original = tokens[index];
        var canonical = CanonicalizeToken(original, allowed);
        if (canonical.Equals(original, StringComparison.Ordinal))
            return 0;
        tokens[index] = canonical;
        return 1;
    }

    private static string CanonicalizeToken(string token, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(token))
            return token;

        // Exact match first.
        var exact = allowed.FirstOrDefault(a => a.Equals(token, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exact))
            return exact;

        var normalized = NormalizeToken(token);
        if (string.IsNullOrWhiteSpace(normalized))
            return token;

        var exactNormalized = allowed.FirstOrDefault(a => NormalizeToken(a).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exactNormalized))
            return exactNormalized;

        // Strip common suffix noise (e.g. FighterP, FighterS1, P11s2).
        var stripped = Regex.Replace(normalized, @"(S\d+|L\d+|P)$", "", RegexOptions.IgnoreCase);
        if (!string.IsNullOrWhiteSpace(stripped) && !stripped.Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            var strippedMatch = allowed.FirstOrDefault(a =>
                NormalizeToken(a).Equals(stripped, StringComparison.OrdinalIgnoreCase)
                || stripped.StartsWith(NormalizeToken(a), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(strippedMatch))
                return strippedMatch;
        }

        // Prefix repair: FIGHTERP -> Fighter, WEAPONSP -> WEAPONS, COLS3 -> COL.
        var prefixMatches = allowed
            .Where(a =>
            {
                var an = NormalizeToken(a);
                return normalized.StartsWith(an, StringComparison.OrdinalIgnoreCase)
                       || an.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(a => Math.Abs(NormalizeToken(a).Length - normalized.Length))
            .ThenBy(a => a.Length)
            .ToList();
        if (prefixMatches.Count > 0)
            return prefixMatches[0];

        // Fallback: allow planets like P11s2 to map to P11 by alnum-prefix.
        var alphaNumPrefix = Regex.Match(normalized, @"^[A-Z]+\d+", RegexOptions.IgnoreCase).Value;
        if (!string.IsNullOrWhiteSpace(alphaNumPrefix))
        {
            var pref = allowed.FirstOrDefault(a => NormalizeToken(a).Equals(alphaNumPrefix, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(pref))
                return pref;
        }

        return token;
    }

    private static string NormalizeToken(string token)
        => Regex.Replace(token.Trim().ToUpperInvariant(), @"[^A-Z0-9_-]", "");

    private async Task<string> CompleteLlmWithTimeoutAsync(
        IReadOnlyList<ChatMessage> messages,
        string context,
        CancellationToken ct,
        IReadOnlyList<ToolDefinition>? tools = null,
        Func<string, string, Task<string>>? toolExecutor = null)
    {
        using var llmTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        llmTimeoutCts.CancelAfter(_llmTimeout);
        try
        {
            if (tools is not null && toolExecutor is not null)
                return await llm.CompleteAsync(messages, tools, toolExecutor, llmTimeoutCts.Token);
            return await llm.CompleteAsync(messages, llmTimeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"LLM timeout in {context} after {_llmTimeout.TotalSeconds:F0}s");
        }
    }

    private sealed record PlanetSnapshot(string Name, double X, double Y, string? OwnerId);
    private sealed record PlayerCheckpointStat(string Id, string Name, int PlanetCount, bool IsEliminated, double TechPower);
    private sealed record GameStateSnapshot(int Turn, bool IsFinished, bool MySubmitted, string? WinnerName);
    private sealed record TurnToolContext(
        int Turn,
        List<string> KnownPlanetNames,
        List<string> DesignedShipNames,
        List<string> AllowedProductionTypes,
        List<string> AllowedCargoTypes,
        List<int> GroupNumbers);
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
    private sealed record DiplomacyContext(List<string> PrivateContacts);
}
