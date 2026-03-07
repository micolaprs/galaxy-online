using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace GalaxyNG.Bot;

/// <summary>Minimal OpenAI-compatible chat completion client.</summary>
public sealed class LlmClient(HttpClient http, LlmConfig config, ILogger<LlmClient> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy      = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition    = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<string> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default)
    {
        int inputChars = messages.Sum(m => m.Content.Length);
        logger.LogInformation(
            "⏳ LLM request → provider={Provider} api={Api} model={Model} messages={Count} inputChars={Chars} maxTokens={Max}",
            config.Provider, config.Api, config.Model, messages.Count, inputChars, config.MaxTokens);

        var sw = Stopwatch.StartNew();
        var text = await CompleteByApiAsync(messages, ct);
        sw.Stop();
        logger.LogInformation(
            "✅ LLM response ← {Ms}ms, {Chars} chars",
            sw.ElapsedMilliseconds, text.Length);

        return text;
    }

    private async Task<string> CompleteByApiAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct)
    {
        if (IsOpenAiCodexProvider(config.Provider) && NormalizeApi(config.Api) == "responses")
            return await CompleteWithCodexResponsesApiAsync(messages, ct);

        return NormalizeApi(config.Api) switch
        {
            "responses" => await CompleteWithResponsesApiAsync(messages, ct),
            _ => await CompleteWithChatCompletionsAsync(messages, ct),
        };
    }

    private async Task<string> CompleteWithChatCompletionsAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct)
    {
        var request = new
        {
            model       = config.Model,
            messages    = messages.Select(m => new { role = m.Role, content = m.Content }),
            temperature = config.Temperature,
            max_tokens  = config.MaxTokens,
        };

        using var response = await http.PostAsJsonAsync("chat/completions", request, JsonOpts, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOpts, ct);
        if (result?.Choices is not { Count: > 0 })
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"LLM returned no choices. Body: {raw}");
        }

        return result.Choices[0].Message.Content;
    }

    private async Task<string> CompleteWithResponsesApiAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct)
    {
        var request = new
        {
            model = config.Model,
            input = messages.Select(m => new
            {
                role = m.Role,
                content = m.Content,
            }),
            temperature = config.Temperature,
            max_output_tokens = config.MaxTokens,
        };

        using var response = await http.PostAsJsonAsync("responses", request, JsonOpts, ct);
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        if (TryExtractResponseText(doc.RootElement, out var text) && !string.IsNullOrWhiteSpace(text))
            return text;

        throw new InvalidOperationException($"LLM responses API returned no text. Body: {raw}");
    }

    private async Task<string> CompleteWithCodexResponsesApiAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct)
    {
        var request = new
        {
            model = config.Model,
            store = false,
            stream = true,
            instructions = messages.FirstOrDefault(m => m.Role == "system")?.Content,
            input = messages
                .Where(m => m.Role != "system")
                .Select(BuildCodexInputItem),
            text = new { verbosity = "medium" },
            include = new[] { "reasoning.encrypted_content" },
            tool_choice = "auto",
            parallel_tool_calls = true,
        };
        var bodyJson = JsonSerializer.Serialize(request, JsonOpts);

        using var req = new HttpRequestMessage(HttpMethod.Post, "codex/responses")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(bodyJson)),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Codex responses API error {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {errorBody}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var sseText = new StringBuilder();
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
                    sseText.Append(chunk);
                }
            }
            else if (!sawDelta &&
                eventType == "response.output_text.done" &&
                doc.RootElement.TryGetProperty("text", out var doneNode))
            {
                var chunk = doneNode.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(chunk))
                    sseText.Append(chunk);
            }
        }

        var result = sseText.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(result))
            return result;

        throw new InvalidOperationException("LLM codex responses API returned no text.");
    }

    private static object BuildCodexInputItem(ChatMessage m)
    {
        if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                type = "message",
                role = "assistant",
                status = "completed",
                content = new[]
                {
                    new
                    {
                        type = "output_text",
                        text = m.Content,
                        annotations = Array.Empty<object>(),
                    },
                },
            };
        }

        return new
        {
            role = "user",
            content = new[]
            {
                new
                {
                    type = "input_text",
                    text = m.Content,
                },
            },
        };
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
                    var v = textNode.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                        chunks.Add(v);
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

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content)   => new("user", content);
    public static ChatMessage Assistant(string content) => new("assistant", content);
}

// ---- Response DTOs ----
file sealed record ChatCompletionResponse(
    [property: JsonPropertyName("choices")] List<Choice> Choices
);
file sealed record Choice(
    [property: JsonPropertyName("message")] MessageContent Message
);
file sealed record MessageContent(
    [property: JsonPropertyName("content")] string Content
);
