using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GalaxyNG.Server.Services;

public sealed class CodexLlmProvider(IConfiguration config, ILogger<CodexLlmProvider> logger) : ILlmProvider
{
    private readonly string _baseUrl = (config["Llm:BaseUrl"] ?? throw new InvalidOperationException("Llm:BaseUrl is required")).TrimEnd('/');
    private readonly string _model = config["Llm:Model"] ?? throw new InvalidOperationException("Llm:Model is required");
    private readonly string _apiKey = config["Llm:ApiKey"] ?? "";
    private readonly string _accountId = config["Llm:AccountId"] ?? "";
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
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        req.Headers.Add("OpenAI-Beta", "responses=experimental");
        req.Headers.Add("originator", "pi");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(_accountId))
        {
            req.Headers.Add("chatgpt-account-id", _accountId);
        }

        using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Codex returned {Status}: {Body}", response.StatusCode, error);
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
            {
                continue;
            }

            var data = line[6..].Trim();
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
            {
                continue;
            }

            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("type", out var typeNode))
            {
                continue;
            }

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
                {
                    text.Append(chunk);
                }
            }
        }

        var result = text.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}

