using System.Net.Http.Json;
using System.Text.Json;
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

        // 2. Ask LLM for orders (with retry on validation failure)
        string orders = "";
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

            orders = ExtractOrders(raw);
            int orderLines = orders.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            logger.LogInformation("📝 [{Race}] LLM составил {Lines} приказов (из {Total} знаков ответа)",
                config.RaceName, orderLines, raw.Length);

            // 3. Validate
            await PostBotStatusAsync("validating", $"ход {turn}, попытка {attempt}", ct);
            logger.LogInformation("🔍 [{Race}] Проверяю приказы у сервера…", config.RaceName);
            var validation = await ValidateOrdersAsync(orders, ct);
            if (validation.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("✅ [{Race}] Приказы прошли проверку (попытка {Attempt})", config.RaceName, attempt);
                break;
            }

            logger.LogWarning("⚠️ [{Race}] Ошибка проверки (попытка {Attempt}): {Error}", config.RaceName, attempt, validation);
            if (attempt == 3)
                logger.LogError("❌ [{Race}] Отправляю лучший вариант после 3 попыток", config.RaceName);
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
        await Http.PostAsJsonAsync(url, body, ct);
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
}
