using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GalaxyNG.Server.Services;

public sealed class LmStudioLlmProvider(IConfiguration config, ILogger<LmStudioLlmProvider> logger) : ILlmProvider
{
    private readonly string _baseUrl = (config["Llm:BaseUrl"] ?? throw new InvalidOperationException("Llm:BaseUrl is required")).TrimEnd('/');
    private readonly string _model = config["Llm:Model"] ?? throw new InvalidOperationException("Llm:Model is required");
    private readonly string _apiKey = config["Llm:ApiKey"] ?? "";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public async Task<string?> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        double temperature,
        CancellationToken ct = default)
    {
        var payload = new
        {
            model = _model,
            temperature,
            max_tokens = maxTokens,
            enable_thinking = false,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        req.Content = JsonContent.Create(payload);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("LM Studio provider returned {Status}", resp.StatusCode);
            return null;
        }

        var result = await resp.Content.ReadFromJsonAsync<LlmResponse>(cancellationToken: ct);
        return result?.Choices?[0].Message?.Content?.Trim();
    }
}

file sealed class LlmResponse { public LlmChoice[]? Choices { get; set; } }
file sealed class LlmChoice { public LlmMessage? Message { get; set; } }
file sealed class LlmMessage { public string? Content { get; set; } }

