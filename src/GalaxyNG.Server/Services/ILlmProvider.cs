namespace GalaxyNG.Server.Services;

public interface ILlmProvider
{
    Task<string?> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        double temperature,
        CancellationToken ct = default);
}

