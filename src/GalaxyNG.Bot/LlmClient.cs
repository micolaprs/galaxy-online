using System.Diagnostics;
using System.Net.Http.Json;
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
        var request = new
        {
            model       = config.Model,
            messages    = messages.Select(m => new { role = m.Role, content = m.Content }),
            temperature = config.Temperature,
            max_tokens  = config.MaxTokens,
        };

        int inputChars = messages.Sum(m => m.Content.Length);
        logger.LogInformation(
            "⏳ LLM request → model={Model} messages={Count} inputChars={Chars} maxTokens={Max}",
            config.Model, messages.Count, inputChars, config.MaxTokens);

        var sw = Stopwatch.StartNew();
        using var response = await http.PostAsJsonAsync("chat/completions", request, JsonOpts, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOpts, ct);
        sw.Stop();

        if (result?.Choices is not { Count: > 0 })
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"LLM returned no choices. Body: {raw}");
        }

        var text = result.Choices[0].Message.Content;
        logger.LogInformation(
            "✅ LLM response ← {Ms}ms, {Chars} chars",
            sw.ElapsedMilliseconds, text.Length);

        return text;
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
