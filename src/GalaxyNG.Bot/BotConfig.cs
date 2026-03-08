namespace GalaxyNG.Bot;

public sealed record BotConfig
{
    public required string GameId    { get; init; }
    public required string RaceName  { get; init; }
    public required string Password  { get; init; }
    public required string ServerUrl { get; init; }   // e.g. http://localhost:5000
    public int LlmTimeoutSeconds     { get; init; } = 90;

    public LlmConfig Llm { get; init; } = new();
}

public sealed record LlmConfig
{
    public string Provider     { get; init; } = "lmstudio";
    public string Api          { get; init; } = "chat-completions";
    public string BaseUrl      { get; init; } = "http://localhost:1234/v1";
    public string Model        { get; init; } = "qwen/qwen3.5-9b";
    public double Temperature  { get; init; } = 0.7;
    public int    MaxTokens    { get; init; } = 4096;
    public string ApiKey       { get; init; } = "lm-studio"; // LM Studio ignores this
    public string AccountId    { get; init; } = "";
    public string AuthFilesDir { get; init; } = "";
}
