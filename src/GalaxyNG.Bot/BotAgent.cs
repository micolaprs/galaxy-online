using System.Text.RegularExpressions;
using GalaxyNG.Engine.Services;
using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GalaxyNG.Bot;

/// <summary>
/// LLM bot that plays GalaxyNG through MCP tools exposed by the game server.
/// </summary>
public sealed class BotAgent(
    BotConfig                config,
    LlmClient                llm,
    ILoggerFactory           loggerFactory,
    ILogger<BotAgent>        logger)
{
    private readonly TimeSpan _llmTimeout = TimeSpan.FromSeconds(Math.Clamp(config.LlmTimeoutSeconds, 30, 300));
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly BotStrategy _strategy = BotStrategyCatalog.PickForBot(config.GameId, config.RaceName, config.StrategyId);
    private string SystemPrompt => StrategyPrompt.BuildSystemPrompt(_strategy);
    private CommanderProfile? _commander;
    private readonly List<DiplomaticMemoryEntry> _myDiplomaticHistory = [];
    private bool _sentInitialGreeting;
    private bool _retired;
    private string _lastValidationErrors = "";
    private List<StatusRow> _lastStatusRows = [];
    private TurnToolContext? _turnToolContext;
    private McpClient? _mcpClient;
    private readonly SemaphoreSlim _mcpInitLock = new(1, 1);

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
        _lastStatusRows = ParseStatusRowsFromReport(report);
        _turnToolContext = BuildTurnToolContext(turn, report);

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
                ChatMessage.System(SystemPrompt),
                ChatMessage.System(BuildCommanderInstruction()),
                ChatMessage.User(BotStrategyCatalog.BuildStrategyUserHint(_strategy)),
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
        logger.LogInformation("🧩 [{Race}] Стратегия бота: {StrategyId} — {StrategyName}",
            config.RaceName, _strategy.Id, _strategy.Name);
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

    // ---- MCP helpers ----

    private async Task<McpClient> GetMcpClientAsync(CancellationToken ct)
    {
        if (_mcpClient is not null)
            return _mcpClient;

        await _mcpInitLock.WaitAsync(ct);
        try
        {
            if (_mcpClient is not null)
                return _mcpClient;

            var endpoint = new Uri($"{config.ServerUrl.TrimEnd('/')}/mcp");
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.AutoDetect,
                Name = $"galaxyng-bot-{config.RaceName}",
            }, _loggerFactory);

            _mcpClient = await McpClient.CreateAsync(
                clientTransport: transport,
                clientOptions: null,
                loggerFactory: _loggerFactory,
                cancellationToken: ct);

            return _mcpClient;
        }
        finally
        {
            _mcpInitLock.Release();
        }
    }

    private async Task<string> CallMcpToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken ct)
    {
        var client = await GetMcpClientAsync(ct);
        var result = await client.CallToolAsync(toolName, args, cancellationToken: ct);
        var text = string.Concat(result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text ?? ""));
        var payload = text.Trim();

        if (result.IsError.GetValueOrDefault(false))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(payload) ? $"MCP tool error: {toolName}" : payload);

        return payload;
    }

    private async Task<string> GetReportAsync(CancellationToken ct)
    {
        try
        {
            var text = await CallMcpToolAsync("get_turn_report", new Dictionary<string, object?>
            {
                ["gameId"] = config.GameId,
                ["raceName"] = config.RaceName,
                ["password"] = config.Password,
            }, ct);

            return text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ? "" : text;
        }
        catch (Exception ex)
        {
            logger.LogDebug("Get turn report via MCP failed: {Msg}", ex.Message);
            _mcpClient = null;
            return "";
        }
    }

    private async Task<string> ValidateOrdersAsync(string orders, CancellationToken ct)
    {
        try
        {
            var text = await CallMcpToolAsync("validate_orders", new Dictionary<string, object?>
            {
                ["gameId"] = config.GameId,
                ["raceName"] = config.RaceName,
                ["password"] = config.Password,
                ["orders"] = orders,
            }, ct);

            return text.StartsWith("OK", StringComparison.OrdinalIgnoreCase) ? "OK" : text;
        }
        catch (Exception ex)
        {
            logger.LogDebug("Validate orders via MCP failed: {Msg}", ex.Message);
            _mcpClient = null;
            return "Error: could not reach MCP server";
        }
    }

    private async Task SubmitOrdersAsync(string orders, bool final, CancellationToken ct)
    {
        var text = await CallMcpToolAsync("submit_orders", new Dictionary<string, object?>
        {
            ["gameId"] = config.GameId,
            ["raceName"] = config.RaceName,
            ["password"] = config.Password,
            ["orders"] = orders,
            ["final"] = final,
        }, ct);

        if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Orders submit failed via MCP: {text}");
    }

    private async Task<GameStateSnapshot> GetCurrentTurnAsync(CancellationToken ct)
    {
        var text = await CallMcpToolAsync("get_turn_state", new Dictionary<string, object?>
        {
            ["gameId"] = config.GameId,
            ["raceName"] = config.RaceName,
            ["password"] = config.Password,
        }, ct);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var errNode))
            throw new InvalidOperationException($"MCP get_turn_state failed: {errNode.GetString()}");

        return new GameStateSnapshot(
            Turn: root.GetProperty("turn").GetInt32(),
            IsFinished: root.TryGetProperty("isFinished", out var finished) && finished.GetBoolean(),
            MySubmitted: root.TryGetProperty("mySubmitted", out var submitted) && submitted.GetBoolean(),
            WinnerName: root.TryGetProperty("winnerName", out var winner) && winner.ValueKind == JsonValueKind.String
                ? winner.GetString()
                : null);
    }

    private async Task PostBotStatusAsync(string status, string? detail, CancellationToken ct)
        => await PostBotStatusAsync(status, detail, null, ct);

    private async Task PostBotStatusAsync(string status, string? detail, string? thinking, CancellationToken ct)
    {
        try
        {
            await CallMcpToolAsync("report_bot_status", new Dictionary<string, object?>
            {
                ["gameId"] = config.GameId,
                ["raceName"] = config.RaceName,
                ["status"] = status,
                ["detail"] = detail ?? "",
                ["thinking"] = thinking ?? "",
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not post bot status via MCP: {Msg}", ex.Message);
            _mcpClient = null;
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
        var target = FindNearestTargetPlanetFromReport(home.Name, report);

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

    private static string? FindNearestTargetPlanetFromReport(string homePlanet, string report)
    {
        try
        {
            var planets = ParseScoutedPlanetSnapshots(report);
            if (planets.Count == 0)
                return null;

            var home = planets.FirstOrDefault(p => p.Name.Equals(homePlanet, StringComparison.OrdinalIgnoreCase));
            if (home is null)
                return planets.FirstOrDefault()?.Name;

            var preferred = planets
                .Where(p => !p.Name.Equals(home.Name, StringComparison.OrdinalIgnoreCase) && p.IsUninhabited)
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
        catch
        {
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

    private static List<PlanetSnapshot> ParseScoutedPlanetSnapshots(string report)
    {
        var snapshots = new Dictionary<string, PlanetSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in ParseMyPlanets(report))
            snapshots[p.Name] = new PlanetSnapshot(p.Name, p.X, p.Y, OwnerKnown: true, IsUninhabited: false);

        var lines = report.ReplaceLineEndings("\n").Split('\n');
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("= UNINHABITED PLANETS", StringComparison.OrdinalIgnoreCase))
            {
                inSection = true;
                continue;
            }

            if (inSection && line.StartsWith("="))
                break;
            if (!inSection || string.IsNullOrWhiteSpace(line))
                continue;

            var m = Regex.Match(line, @"^(?<name>\S+)\s+X:(?<x>-?\d+(?:\.\d+)?)\s+Y:(?<y>-?\d+(?:\.\d+)?)\b",
                RegexOptions.IgnoreCase);
            if (!m.Success)
                continue;

            if (!double.TryParse(m.Groups["x"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(m.Groups["y"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var y))
                continue;

            var name = m.Groups["name"].Value;
            snapshots[name] = new PlanetSnapshot(name, x, y, OwnerKnown: true, IsUninhabited: true);
        }

        return snapshots.Values.ToList();
    }

    private static List<StatusRow> ParseStatusRowsFromReport(string report)
    {
        var rows = new List<StatusRow>();
        var lines = report.ReplaceLineEndings("\n").Split('\n');
        var inStatus = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("= STATUS OF PLAYERS", StringComparison.OrdinalIgnoreCase))
            {
                inStatus = true;
                continue;
            }

            if (inStatus && line.StartsWith("="))
                break;
            if (!inStatus || string.IsNullOrWhiteSpace(line))
                continue;
            if (line.StartsWith("Race", StringComparison.OrdinalIgnoreCase) || line.StartsWith("-", StringComparison.Ordinal))
                continue;

            var m = Regex.Match(line.Trim(),
                @"^(?<race>.+?)\s+(?<drive>-?\d+(?:\.\d+)?)\s+(?<wpn>-?\d+(?:\.\d+)?)\s+(?<shd>-?\d+(?:\.\d+)?)\s+(?<cargo>-?\d+(?:\.\d+)?)\s+(?<pops>-?\d+(?:\.\d+)?)\s+(?<ind>-?\d+(?:\.\d+)?)\s+(?<plnts>-?\d+)\s*$");
            if (!m.Success)
                continue;

            var race = Regex.Replace(m.Groups["race"].Value, @"\s*\[[^\]]+\]\s*", " ").Trim();
            if (string.IsNullOrWhiteSpace(race))
                continue;

            if (!double.TryParse(m.Groups["drive"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var drive) ||
                !double.TryParse(m.Groups["wpn"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var wpn) ||
                !double.TryParse(m.Groups["shd"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var shd) ||
                !double.TryParse(m.Groups["cargo"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var cargo) ||
                !int.TryParse(m.Groups["plnts"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var plnts))
                continue;

            rows.Add(new StatusRow(
                Name: race,
                PlanetCount: Math.Max(0, plnts),
                IsEliminated: plnts <= 0,
                TechPower: drive + wpn + shd + cargo));
        }

        return rows;
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
            var players = ParseStatusRowsFromReport(report)
                .Select(p => new PlayerCheckpointStat(
                    Name: p.Name,
                    PlanetCount: p.PlanetCount,
                    IsEliminated: p.IsEliminated,
                    TechPower: p.TechPower))
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
                .Where(c => !allies.Contains(c, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(c => ScoreDiplomaticTarget(context.Signals.GetValueOrDefault(c), turn))
                .FirstOrDefault()
                ?? context.PrivateContacts[0];

            var signal = context.Signals.GetValueOrDefault(target);
            if (signal is not null && signal.UnansweredMineStreak >= 3 && signal.LastTurn is int lastTurn && turn - lastTurn < 8)
                return result;

            var intent = signal is not null && signal.UnansweredMineStreak > 0
                ? $"Личное сообщение для расы {target}: собеседник молчит уже {signal.UnansweredMineStreak} сообщений подряд. " +
                  "Учитывай напряжение от игнора: сначала сдержанно, затем жестче, без повтора прежних формулировок."
                : $"Личное сообщение для расы {target}: дипломатическая игра, блеф или предложение сделки/союза.";

            var privateMsg = await GenerateBroadcastMessageAsync(
                intent,
                turn,
                ct);
            result = AppendPrivateMessage(result, target, privateMsg);

            if (!allies.Contains(target, StringComparer.OrdinalIgnoreCase) &&
                turn % 6 == 0 &&
                (signal is null || signal.UnansweredMineStreak < 3))
                result = $"{result}\na {target} {turn + 8}";
        }

        return result;
    }

    private Task<DiplomacyContext?> GetDiplomacyContextAsync(CancellationToken ct)
    {
        return GetDiplomacyContextInternalAsync(ct);
    }

    private async Task<DiplomacyContext?> GetDiplomacyContextInternalAsync(CancellationToken ct)
    {
        try
        {
            var json = await CallMcpToolAsync("get_diplomacy_context", new
            Dictionary<string, object?>
            {
                ["gameId"] = config.GameId,
                ["raceName"] = config.RaceName,
                ["password"] = config.Password,
                ["lookback"] = 40
            }, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("contacts", out var contactsNode) ||
                contactsNode.ValueKind != JsonValueKind.Array)
                return BuildFallbackDiplomacyContext();

            var contacts = new List<string>();
            var signals = new Dictionary<string, PrivateDiplomacySignal>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in contactsNode.EnumerateArray())
            {
                var race = node.TryGetProperty("race", out var raceProp) && raceProp.ValueKind == JsonValueKind.String
                    ? raceProp.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(race))
                    continue;

                var channelOpen = node.TryGetProperty("channelOpen", out var openProp) && openProp.ValueKind == JsonValueKind.True;
                var myMessages = node.TryGetProperty("myMessages", out var myMsgProp) && myMsgProp.TryGetInt32(out var myMsgVal) ? myMsgVal : 0;
                var theirMessages = node.TryGetProperty("theirMessages", out var theirMsgProp) && theirMsgProp.TryGetInt32(out var theirMsgVal) ? theirMsgVal : 0;
                var unansweredMineStreak = node.TryGetProperty("unansweredMineStreak", out var streakProp) && streakProp.TryGetInt32(out var streakVal) ? streakVal : 0;
                var lastSender = node.TryGetProperty("lastSender", out var senderProp) && senderProp.ValueKind == JsonValueKind.String
                    ? senderProp.GetString()
                    : null;
                int? lastTurn = node.TryGetProperty("lastTurn", out var turnProp) && turnProp.TryGetInt32(out var turnVal)
                    ? turnVal
                    : null;

                contacts.Add(race);
                signals[race] = new PrivateDiplomacySignal(
                    Race: race,
                    ChannelOpen: channelOpen,
                    MyMessages: myMessages,
                    TheirMessages: theirMessages,
                    UnansweredMineStreak: unansweredMineStreak,
                    LastSender: lastSender,
                    LastTurn: lastTurn);
            }

            var openedContacts = contacts
                .Where(c => signals.TryGetValue(c, out var s) && s.ChannelOpen)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (openedContacts.Count == 0)
                return BuildFallbackDiplomacyContext();

            return new DiplomacyContext(openedContacts, signals);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not read diplomacy context from MCP: {Msg}", ex.Message);
            return BuildFallbackDiplomacyContext();
        }
    }

    private DiplomacyContext? BuildFallbackDiplomacyContext()
    {
        var contacts = _lastStatusRows
            .Where(r => !r.IsEliminated && !r.Name.Equals(config.RaceName, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return contacts.Count == 0
            ? null
            : new DiplomacyContext(contacts, new Dictionary<string, PrivateDiplomacySignal>(StringComparer.OrdinalIgnoreCase));
    }

    private static int ScoreDiplomaticTarget(PrivateDiplomacySignal? signal, int turn)
    {
        if (signal is null)
            return 5;

        var score = 0;
        if (signal.ChannelOpen) score += 20;
        score += Math.Min(12, signal.TheirMessages * 2);
        score -= Math.Min(15, signal.MyMessages);
        score -= signal.UnansweredMineStreak * 12;
        if (signal.LastSender is not null && signal.LastSender.Equals(signal.Race, StringComparison.OrdinalIgnoreCase))
            score += 10;
        if (signal.LastTurn is int t)
        {
            var age = Math.Max(0, turn - t);
            score += Math.Min(10, age);
        }
        return score;
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
                ChatMessage.User($"""
                    Race: {config.RaceName}.
                    Strategy: {_strategy.Name} ({_strategy.Id})
                    Strategy cues:
                    {_strategy.CommanderCues}

                    Create a distinct commander persona in Russian aligned with this strategic doctrine.
                    """),
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

        _commander ??= CommanderProfile.Fallback(config.RaceName, _strategy.Id);
    }

    private Task RefreshMyDiplomaticHistoryAsync(CancellationToken ct)
    {
        _ = ct;
        return Task.CompletedTask;
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
                - Стратегия: {_strategy.Name} ({_strategy.Id})
                - Стратегические акценты: {_strategy.CommanderCues}
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

    private TurnToolContext BuildTurnToolContext(int turn, string report)
    {
        var designedShips = ExtractDesignedShipNames(report)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var knownPlanets = ParsePlanetNamesFromReport(report);
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

    private static List<string> ParsePlanetNamesFromReport(string report)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        foreach (var raw in report.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("= "))
            {
                section = line[2..].Trim().ToUpperInvariant();
                continue;
            }
            if (line.StartsWith("#") || line.StartsWith("Name", StringComparison.OrdinalIgnoreCase))
                continue;

            if (section.Contains("YOUR PLANETS", StringComparison.Ordinal))
            {
                var parts = Regex.Split(line, @"\s+");
                if (parts.Length < 3)
                    continue;
                if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                    continue;
                if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                    continue;
                result.Add(parts[0]);
                continue;
            }

            if (section.Contains("ALIEN PLANETS", StringComparison.Ordinal) ||
                section.Contains("UNINHABITED PLANETS", StringComparison.Ordinal))
            {
                var parts = Regex.Split(line, @"\s+");
                if (parts.Length > 0)
                    result.Add(parts[0]);
            }
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
            - Known planets from current report (strict for `p`, advisory for `s`/`r`): {planets}
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
                if (!allowedCargo.Contains(parts[2]))
                    problems.Add($"unknown route cargo: {parts[2]} (try: {SuggestToken(parts[2], ctx.AllowedCargoTypes)})");
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
                keep = int.TryParse(parts[1], out var g) && (allowedGroups.Count == 0 || allowedGroups.Contains(g));
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
                parts[2] = CanonicalizeToken(parts[2], ctx.AllowedCargoTypes);
                keep = ctx.AllowedCargoTypes.Contains(parts[2], StringComparer.OrdinalIgnoreCase);
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
                // Keep movement destination untouched; server-side validator decides validity.
            }
            else if (cmd == "l" && parts.Count >= 3)
            {
                fixes += CanonicalizeTokenInPlace(parts, 2, new List<string> { "CAP", "COL", "MAT" });
            }
            else if (cmd == "r" && parts.Count >= 4)
            {
                fixes += CanonicalizeTokenInPlace(parts, 2, ctx.AllowedCargoTypes);
                // Keep route endpoints untouched for the same reason.
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

    private sealed record PlanetSnapshot(string Name, double X, double Y, bool OwnerKnown, bool IsUninhabited);
    private sealed record PlayerCheckpointStat(string Name, int PlanetCount, bool IsEliminated, double TechPower);
    private sealed record StatusRow(string Name, int PlanetCount, bool IsEliminated, double TechPower);
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
        var profile = _commander ?? CommanderProfile.Fallback(config.RaceName, _strategy.Id);
        var history = _myDiplomaticHistory.Count == 0
            ? "none"
            : string.Join(" | ", _myDiplomaticHistory.TakeLast(5).Select(m => $"T{m.Turn}:{m.Text}"));

        return $"You are Supreme Commander {profile.CommanderName} of race {config.RaceName}. " +
               $"Strategic doctrine: {_strategy.Name} ({_strategy.Id}). " +
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
        public static CommanderProfile Fallback(string raceName, string strategyId)
        {
            var idx = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode($"{raceName}:{strategyId}")) % 6;
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
    private sealed record DiplomacyContext(
        List<string> PrivateContacts,
        Dictionary<string, PrivateDiplomacySignal> Signals);
    private sealed record PrivateDiplomacySignal(
        string Race,
        bool ChannelOpen,
        int MyMessages,
        int TheirMessages,
        int UnansweredMineStreak,
        string? LastSender,
        int? LastTurn);
}
