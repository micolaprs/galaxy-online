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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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

    public async Task<string> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        Func<string, string, Task<string>> toolExecutor,
        CancellationToken ct = default)
    {
        int inputChars = messages.Sum(m => m.Content.Length);
        logger.LogInformation(
            "⏳ LLM request → provider={Provider} api={Api} model={Model} messages={Count} inputChars={Chars} maxTokens={Max} tools={Tools}",
            config.Provider, config.Api, config.Model, messages.Count, inputChars, config.MaxTokens, tools.Count);

        var sw = Stopwatch.StartNew();
        string text;
        if (IsOpenAiCodexProvider(config.Provider) && NormalizeApi(config.Api) == "responses")
        {
            text = await CompleteWithCodexAgenticAsync(messages, tools, toolExecutor, ct);
        }
        else
        {
            text = await CompleteWithChatCompletionsAgenticAsync(messages, tools, toolExecutor, ct);
        }

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
        {
            return await CompleteWithCodexResponsesApiAsync(messages, ct);
        }

        return NormalizeApi(config.Api) switch
        {
            "responses" => await CompleteWithResponsesApiAsync(messages, ct),
            _ => await CompleteWithChatCompletionsAsync(messages, ct),
        };
    }

    private async Task<string> CompleteWithChatCompletionsAgenticAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        Func<string, string, Task<string>> toolExecutor,
        CancellationToken ct)
    {
        var toolDefs = tools.Select(t => new
        {
            type = "function",
            function = new { name = t.Name, description = t.Description, parameters = t.Parameters },
        }).ToArray();

        var workingMessages = messages
            .Select(m => (object)new { role = m.Role, content = m.Content })
            .ToList();

        for (int iteration = 0; iteration < 6; iteration++)
        {
            var request = new
            {
                model = config.Model,
                messages = workingMessages,
                temperature = config.Temperature,
                max_tokens = config.MaxTokens,
                enable_thinking = false,
                tools = toolDefs,
                tool_choice = "auto",
            };

            using var response = await http.PostAsJsonAsync("chat/completions", request, JsonOpts, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponseWithTools>(JsonOpts, ct);
            if (result?.Choices is not { Count: > 0 })
            {
                var raw = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"LLM returned no choices. Body: {raw}");
            }

            var choice = result.Choices[0];
            var toolCalls = choice.Message.ToolCalls;

            if (toolCalls is { Count: > 0 })
            {
                workingMessages.Add(new
                {
                    role = "assistant",
                    content = choice.Message.Content,
                    tool_calls = toolCalls.Select(tc => new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new { name = tc.Function.Name, arguments = tc.Function.Arguments },
                    }).ToArray(),
                });

                foreach (var tc in toolCalls)
                {
                    var toolResult = await toolExecutor(tc.Function.Name, tc.Function.Arguments);
                    workingMessages.Add(new { role = "tool", tool_call_id = tc.Id, content = toolResult });
                }
                // Some model templates (e.g. Qwen3 in LM Studio) require the last message to be
                // a user turn. Add a minimal continuation prompt so the template doesn't error.
                workingMessages.Add(new { role = "user", content = "(tool results provided above; continue)" });
                continue;
            }

            if (!string.IsNullOrWhiteSpace(choice.Message.Content))
            {
                return choice.Message.Content!;
            }

            throw new InvalidOperationException("LLM chat completions agentic returned no text or tool calls.");
        }

        throw new InvalidOperationException("LLM chat completions agentic exceeded max iterations (6).");
    }

    private async Task<string> CompleteWithChatCompletionsAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct)
    {
        var request = new
        {
            model = config.Model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            temperature = config.Temperature,
            max_tokens = config.MaxTokens,
            enable_thinking = false,   // suppress <think> blocks (Qwen3/LM Studio)
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
        {
            return text;
        }

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
                    sseText.Append(chunk);
                }
            }
            else if (!sawDelta &&
                eventType == "response.output_text.done" &&
                doc.RootElement.TryGetProperty("text", out var doneNode))
            {
                var chunk = doneNode.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(chunk))
                {
                    sseText.Append(chunk);
                }
            }
        }

        var result = sseText.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        throw new InvalidOperationException("LLM codex responses API returned no text.");
    }

    private async Task<string> CompleteWithCodexAgenticAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        Func<string, string, Task<string>> toolExecutor,
        CancellationToken ct)
    {
        var inputItems = new List<object>(
            messages.Where(m => m.Role != "system").Select(BuildCodexInputItem));

        var toolDefs = tools.Select(t => new
        {
            type = "function",
            name = t.Name,
            description = t.Description,
            parameters = t.Parameters,
        }).ToArray();

        var systemInstruction = messages.FirstOrDefault(m => m.Role == "system")?.Content;

        for (int iteration = 0; iteration < 6; iteration++)
        {
            var request = new
            {
                model = config.Model,
                store = false,
                stream = true,
                instructions = systemInstruction,
                input = inputItems,
                text = new { verbosity = "medium" },
                include = new[] { "reasoning.encrypted_content" },
                tool_choice = "auto",
                parallel_tool_calls = true,
                tools = toolDefs,
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
                    $"Codex agentic API error {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {errorBody}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var sseText = new StringBuilder();
            var sawDelta = false;
            var functionCalls = new List<(string Id, string CallId, string Name, string Arguments)>();

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
                        sseText.Append(chunk);
                    }
                }
                else if (!sawDelta &&
                    eventType == "response.output_text.done" &&
                    doc.RootElement.TryGetProperty("text", out var doneNode))
                {
                    var chunk = doneNode.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(chunk))
                    {
                        sseText.Append(chunk);
                    }
                }
                else if (eventType == "response.output_item.done" &&
                    doc.RootElement.TryGetProperty("item", out var itemNode) &&
                    itemNode.TryGetProperty("type", out var itemTypeNode) &&
                    itemTypeNode.GetString() == "function_call")
                {
                    var id = itemNode.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? "" : "";
                    var callId = itemNode.TryGetProperty("call_id", out var callIdNode) ? callIdNode.GetString() ?? "" : "";
                    var name = itemNode.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
                    var args = itemNode.TryGetProperty("arguments", out var argsNode) ? argsNode.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        functionCalls.Add((id, callId, name, args));
                    }
                }
            }

            if (functionCalls.Count > 0)
            {
                foreach (var (id, callId, name, argsJson) in functionCalls)
                {
                    inputItems.Add(new { type = "function_call", id, call_id = callId, name, arguments = argsJson });
                    var toolResult = await toolExecutor(name, argsJson);
                    inputItems.Add(new { type = "function_call_output", call_id = callId, output = toolResult });
                }
                continue;
            }

            var result = sseText.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }

            throw new InvalidOperationException("LLM codex agentic responses API returned no text.");
        }

        throw new InvalidOperationException("LLM codex agentic responses API exceeded max iterations (6).");
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
            if (!string.IsNullOrWhiteSpace(text))
            {
                return true;
            }
        }

        if (!root.TryGetProperty("output", out var outputArr) || outputArr.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var chunks = new List<string>();
        foreach (var outputItem in outputArr.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("content", out var contentArr) || contentArr.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in contentArr.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var textNode))
                {
                    var v = textNode.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        chunks.Add(v);
                    }
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

public sealed record ToolDefinition(string Name, string Description, object Parameters);
public sealed record ToolCallRequest(string Name, string CallId, string ArgumentsJson);

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
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

// ---- DTOs with tool-call support ----
file sealed record ChatCompletionResponseWithTools(
    [property: JsonPropertyName("choices")] List<ChoiceWithTools> Choices
);
file sealed record ChoiceWithTools(
    [property: JsonPropertyName("message")] MessageWithTools Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason
);
file sealed record MessageWithTools(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")] List<ToolCall>? ToolCalls
);
file sealed record ToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("function")] ToolCallFunction Function
);
file sealed record ToolCallFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments
);
