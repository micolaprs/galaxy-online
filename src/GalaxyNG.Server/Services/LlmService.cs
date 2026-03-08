using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GalaxyNG.Engine.Models;

namespace GalaxyNG.Server.Services;

/// <summary>Calls an OpenAI-compatible LLM API to produce game summaries.</summary>
public sealed class LlmService
{
    private readonly ILogger<LlmService> _logger;
    private readonly string _provider;
    private readonly string _api;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly string _accountId;
    private readonly double _temp;
    private readonly int _maxTokens;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public LlmService(IConfiguration config, ILogger<LlmService> logger)
    {
        _logger = logger;
        _provider = config["Llm:Provider"] ?? "openai/codex";
        var isCodex = IsOpenAiCodexProvider(_provider);
        _temp = double.TryParse(
            config["Llm:Temperature"],
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var t) ? t : 0.7;
        _maxTokens = int.TryParse(config["Llm:MaxTokens"], out var m) ? m : 1024;
        _api = NormalizeApi(config["Llm:Api"] ?? (isCodex ? "responses" : "chat-completions"));
        _baseUrl = (config["Llm:BaseUrl"] ?? (isCodex ? "https://chatgpt.com/backend-api" : "http://localhost:1234/v1")).TrimEnd('/');
        _model = config["Llm:Model"] ?? (isCodex ? "gpt-5.3-codex" : "qwen/qwen3.5-9b");
        _apiKey = config["Llm:ApiKey"] ?? (isCodex ? "" : "lm-studio");
        _accountId = config["Llm:AccountId"] ?? "";
    }

    // ---- Public API ----

    public async Task<string?> GenerateGalaxySummaryAsync(Game game, CancellationToken ct = default)
    {
        var prompt = BuildGalaxySummaryPrompt(game);
        var raw = await CallLlmAsync(
            "Ты аналитик космической стратегической игры GalaxyNG. Ответ только на русском языке, без markdown (без **, списков, заголовков), без разделов размышлений и без служебных тегов.",
            prompt, _maxTokens, ct);
        return UiTextPolicy.Clean(raw, 900);
    }

    public async Task<string?> GenerateTurnSummaryAsync(
        Game game, string raceName, int turn, CancellationToken ct = default)
    {
        var hist = game.TurnHistory.FirstOrDefault(h => h.Turn == turn);
        if (hist is null) return null;

        hist.PlayerOrders.TryGetValue(raceName, out var orders);
        var prompt = BuildTurnSummaryPrompt(raceName, turn, orders ?? "", hist.Battles, hist.Bombings);
        var raw = await CallLlmAsync(
            "Ты аналитик GalaxyNG. Пиши кратко на русском языке (не более 3-4 предложений), без markdown (без **, списков, заголовков), без reasoning/thinking и без служебных блоков.",
            prompt, 512, ct);
        return UiTextPolicy.Clean(raw, 700);
    }

    // ---- Prompt builders ----

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
            sb.AppendLine("Мирный ход — сражений не было.");

        foreach (var b in game.Battles)
            sb.AppendLine($"- Битва при {b.PlanetName}: {string.Join(" vs ", b.Participants)} → победитель: {b.Winner}");

        foreach (var b in game.Bombings)
            sb.AppendLine($"- {b.AttackerRace} бомбардировал {b.PlanetName}" +
                          (b.PreviousOwner != null ? $" (был {b.PreviousOwner})" : ""));

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
            foreach (var b in battles) sb.AppendLine($"- {b}");
            foreach (var b in bombings) sb.AppendLine($"- {b}");
        }

        sb.AppendLine();
        sb.AppendLine(
            $"Кратко опиши стратегию {raceName} на этом ходу: " +
            "что они делали, куда двигались, что строили. 2-3 предложения.");

        return sb.ToString();
    }

    // ---- HTTP call ----

    private async Task<string?> CallLlmAsync(
        string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        try
        {
            if (IsOpenAiCodexProvider(_provider) && _api == "responses")
                return await CallCodexResponsesAsync(systemPrompt, userPrompt, ct);

            if (_api == "responses")
                return await CallResponsesAsync(systemPrompt, userPrompt, maxTokens, ct);

            return await CallChatCompletionsAsync(systemPrompt, userPrompt, maxTokens, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM call failed (provider={Provider}, api={Api}, model={Model})", _provider, _api, _model);
            return null;
        }
    }

    private async Task<string?> CallChatCompletionsAsync(
        string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        var payload = new
        {
            model = _model,
            temperature = _temp,
            max_tokens = maxTokens,
            enable_thinking = false,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
        AddAuthHeaders(req);
        req.Content = JsonContent.Create(payload);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("LLM returned {Status} for chat/completions", resp.StatusCode);
            return null;
        }

        var result = await resp.Content.ReadFromJsonAsync<LlmResponse>(cancellationToken: ct);
        return result?.Choices?[0].Message?.Content?.Trim();
    }

    private async Task<string?> CallResponsesAsync(
        string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        var payload = new
        {
            model = _model,
            input = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            temperature = _temp,
            max_output_tokens = maxTokens,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/responses");
        AddAuthHeaders(req);
        req.Content = JsonContent.Create(payload);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("LLM returned {Status} for responses", resp.StatusCode);
            return null;
        }

        var raw = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        if (TryExtractResponseText(doc.RootElement, out var text) && !string.IsNullOrWhiteSpace(text))
            return text.Trim();

        return null;
    }

    private async Task<string?> CallCodexResponsesAsync(
        string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var payload = new
        {
            model = _model,
            store = false,
            stream = true,
            instructions = systemPrompt,
            input = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = userPrompt },
                    },
                },
            },
            text = new { verbosity = "medium" },
            include = new[] { "reasoning.encrypted_content" },
            tool_choice = "none",
            parallel_tool_calls = false,
        };
        var bodyJson = JsonSerializer.Serialize(payload);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/codex/responses")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(bodyJson)),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        AddAuthHeaders(req);
        req.Headers.Add("OpenAI-Beta", "responses=experimental");
        req.Headers.Add("originator", "pi");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(_accountId))
            req.Headers.Add("chatgpt-account-id", _accountId);

        using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Codex responses returned {Status}: {Body}", response.StatusCode, error);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        var sawDelta = false;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line[6..].Trim();
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
                continue;

            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("type", out var typeNode))
                continue;

            var eventType = typeNode.GetString() ?? "";
            if (eventType == "response.output_text.delta" &&
                doc.RootElement.TryGetProperty("delta", out var deltaNode))
            {
                var chunk = deltaNode.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(chunk))
                {
                    sawDelta = true;
                    text.Append(chunk);
                }
            }
            else if (!sawDelta &&
                     eventType == "response.output_text.done" &&
                     doc.RootElement.TryGetProperty("text", out var doneNode))
            {
                var chunk = doneNode.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(chunk))
                    text.Append(chunk);
            }
        }

        var result = text.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private void AddAuthHeaders(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    private static bool TryExtractResponseText(JsonElement root, out string text)
    {
        text = "";
        if (root.TryGetProperty("output_text", out var outputText))
        {
            text = outputText.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(text)) return true;
        }

        if (!root.TryGetProperty("output", out var outputArr) || outputArr.ValueKind != JsonValueKind.Array)
            return false;

        var chunks = new List<string>();
        foreach (var outputItem in outputArr.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("content", out var contentArr) || contentArr.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var contentItem in contentArr.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var textNode))
                {
                    var value = textNode.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        chunks.Add(value);
                }
            }
        }

        text = string.Join("\n", chunks);
        return !string.IsNullOrWhiteSpace(text);
    }

    private static string NormalizeApi(string api)
    {
        return api.Trim().ToLowerInvariant() switch
        {
            "responses" or "openai-responses" => "responses",
            "chat-completions" or "chat_completions" or "openai-chat-completions" => "chat-completions",
            _ => "chat-completions",
        };
    }

    private static bool IsOpenAiCodexProvider(string provider)
    {
        var p = provider.Trim().ToLowerInvariant();
        return p is "openai/codex" or "openai-codex";
    }
}

file sealed class LlmResponse { public LlmChoice[]? Choices { get; set; } }
file sealed class LlmChoice { public LlmMessage? Message { get; set; } }
file sealed class LlmMessage { public string? Content { get; set; } }
