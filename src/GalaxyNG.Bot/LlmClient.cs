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

        logger.LogDebug("Calling LLM model={Model} messages={Count}", config.Model, messages.Count);

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
