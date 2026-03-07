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
    public async Task RunTurnAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Bot {Race} starting turn cycle for game {Game}", config.RaceName, config.GameId);

        // 1. Get turn report
        await PostBotStatusAsync("reading-report", null, ct);
        logger.LogInformation("📋 Fetching turn report from server…");
        string report = await GetReportAsync(ct);
        if (string.IsNullOrWhiteSpace(report))
        {
            logger.LogWarning("Could not retrieve turn report.");
            await PostBotStatusAsync("idle", "no report", ct);
            return;
        }
        logger.LogInformation("📋 Got turn report ({Lines} lines)", report.Split('\n').Length);

        // 2. Ask LLM for orders (with retry on validation failure)
        string orders = "";
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await PostBotStatusAsync("thinking", $"LLM call attempt {attempt}", ct);
            logger.LogInformation("Bot {Race} calling LLM (attempt {Attempt})", config.RaceName, attempt);

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
            orders = ExtractOrders(raw);
            int orderLines = orders.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            logger.LogInformation("📝 Extracted {Lines} order lines", orderLines);

            // 3. Validate
            await PostBotStatusAsync("validating", $"attempt {attempt}", ct);
            logger.LogInformation("🔍 Validating orders with server…");
            var validation = await ValidateOrdersAsync(orders, ct);
            if (validation.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("✅ Orders validated on attempt {Attempt}", attempt);
                break;
            }

            logger.LogWarning("⚠️ Validation failed (attempt {Attempt}): {Error}", attempt, validation);
            if (attempt == 3) logger.LogError("❌ Giving up after 3 attempts — submitting best effort.");
        }

        // 4. Submit
        await PostBotStatusAsync("submitting", null, ct);
        await SubmitOrdersAsync(orders, final: true, ct);
        logger.LogInformation("Bot {Race} submitted orders for game {Game}", config.RaceName, config.GameId);
        await PostBotStatusAsync("submitted", null, ct);
    }

    /// <summary>Loop: poll server for new turns and play automatically.</summary>
    public async Task RunLoopAsync(CancellationToken ct = default)
    {
        int lastTurn = -1;
        logger.LogInformation("Bot {Race} entering game loop", config.RaceName);
        await PostBotStatusAsync("idle", "waiting for first turn", ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                int turn = await GetCurrentTurnAsync(ct);
                if (turn != lastTurn)
                {
                    lastTurn = turn;
                    await RunTurnAsync(ct);
                }
                else
                {
                    await PostBotStatusAsync("waiting", $"turn {turn}", ct);
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Bot loop error — retrying in 60s");
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
    {
        try
        {
            var url  = $"{config.ServerUrl}/api/games/{config.GameId}/bot-status";
            var body = new { raceName = config.RaceName, status, detail };
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
