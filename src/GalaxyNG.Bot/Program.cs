using GalaxyNG.Bot;
using GalaxyNG.Bot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging((ctx, logging) =>
    {
        // Forward every bot log line to the server's web console (fire-and-forget)
        var serverUrl = ctx.Configuration["Bot:ServerUrl"] ?? "http://localhost:5055";
        var raceName = ctx.Configuration["Bot:RaceName"] ?? "Bot";
        logging.AddProvider(new RemoteLoggerProvider(serverUrl, raceName));
    })
    .ConfigureServices((ctx, services) =>
    {
        var cfg = ctx.Configuration;

        var llmConfig = BuildLlmConfig(cfg);

        var botConfig = new BotConfig
        {
            GameId = cfg["Bot:GameId"] ?? throw new InvalidOperationException("Bot:GameId required"),
            RaceName = cfg["Bot:RaceName"] ?? throw new InvalidOperationException("Bot:RaceName required"),
            Password = cfg["Bot:Password"] ?? throw new InvalidOperationException("Bot:Password required"),
            ServerUrl = cfg["Bot:ServerUrl"] ?? "http://localhost:5000",
            StrategyId = cfg["Bot:StrategyId"],
            LlmTimeoutSeconds = int.TryParse(cfg["Bot:LlmTimeoutSeconds"], out int lts)
                ? Math.Clamp(lts, 30, 600) : 180,
            TurnStartDelaySeconds = int.TryParse(cfg["Bot:TurnStartDelaySeconds"], out int tsd)
                ? Math.Max(0, tsd) : 0,
            PollIntervalSeconds = int.TryParse(cfg["Bot:PollIntervalSeconds"], out int pi)
                ? Math.Clamp(pi, 3, 120) : 8,
            Llm = llmConfig,
        };

        services.AddSingleton(botConfig);
        services.AddSingleton(botConfig.Llm);

        services.AddHttpClient("llm", (sp, client) =>
        {
            var c = sp.GetRequiredService<LlmConfig>();
            var bc = sp.GetRequiredService<BotConfig>();
            client.BaseAddress = new Uri(c.BaseUrl.TrimEnd('/') + "/");
            // Must be longer than LlmTimeoutSeconds so the CancellationToken fires first,
            // giving a clean timeout message instead of a raw socket error.
            client.Timeout = TimeSpan.FromSeconds(bc.LlmTimeoutSeconds + 120);
            if (!string.IsNullOrWhiteSpace(c.ApiKey))
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {c.ApiKey}");
            }

            if (IsOpenAiCodexProvider(c.Provider))
            {
                if (!string.IsNullOrWhiteSpace(c.AccountId))
                {
                    client.DefaultRequestHeaders.Add("chatgpt-account-id", c.AccountId);
                }

                client.DefaultRequestHeaders.Add("OpenAI-Beta", "responses=experimental");
                client.DefaultRequestHeaders.Add("originator", "pi");
                client.DefaultRequestHeaders.Add("accept", "text/event-stream");
            }
        });

        services.AddSingleton<LlmClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient("llm");
            var cfg = sp.GetRequiredService<LlmConfig>();
            var log = sp.GetRequiredService<ILogger<LlmClient>>();
            return new LlmClient(http, cfg, log);
        });
        services.AddSingleton<BotAgent>();
        services.AddHostedService<BotHostedService>();
    })
    .Build();

await host.RunAsync();

static LlmConfig BuildLlmConfig(IConfiguration cfg)
{
    var provider = cfg["Bot:Llm:Provider"] ?? "lmstudio";
    var isOpenAiCodex = IsOpenAiCodexProvider(provider);
    var authFilesDir = cfg["Bot:Llm:AuthFilesDir"] ?? Environment.GetEnvironmentVariable("GALAXYNG_OPENAI_CODEX_AUTH_DIR") ?? "";

    var defaultBaseUrl = isOpenAiCodex
        ? "https://chatgpt.com/backend-api"
        : "http://localhost:1234/v1";
    var defaultModel = isOpenAiCodex
        ? "gpt-5.3-codex"
        : "qwen/qwen3.5-9b";
    var defaultApi = isOpenAiCodex
        ? "responses"
        : "chat-completions";

    var llm = new LlmConfig
    {
        Provider = provider,
        Api = cfg["Bot:Llm:Api"] ?? defaultApi,
        BaseUrl = cfg["Bot:Llm:BaseUrl"] ?? defaultBaseUrl,
        Model = cfg["Bot:Llm:Model"] ?? defaultModel,
        Temperature = double.TryParse(cfg["Bot:Llm:Temperature"], out double t) ? t : 0.7,
        MaxTokens = int.TryParse(cfg["Bot:Llm:MaxTokens"], out int m) ? m : 4096,
        ApiKey = cfg["Bot:Llm:ApiKey"] ?? (isOpenAiCodex ? "" : "lm-studio"),
        AccountId = cfg["Bot:Llm:AccountId"] ?? "",
        AuthFilesDir = authFilesDir,
    };

    if (isOpenAiCodex && !string.IsNullOrWhiteSpace(llm.AuthFilesDir))
    {
        if (TryReadOpenAiCodexCredentials(llm.AuthFilesDir, out var token, out var accountId))
        {
            llm = llm with
            {
                ApiKey = string.IsNullOrWhiteSpace(llm.ApiKey) ? token : llm.ApiKey,
                AccountId = string.IsNullOrWhiteSpace(llm.AccountId) ? accountId : llm.AccountId,
            };
        }
    }

    var serializeDefault = provider.Trim().ToLowerInvariant() == "lmstudio";
    var serialized = cfg["Bot:Llm:Serialized"] is string sv
        ? sv.Equals("true", StringComparison.OrdinalIgnoreCase)
        : serializeDefault;
    llm = llm with { Serialized = serialized };

    return llm;
}

static bool TryReadOpenAiCodexCredentials(string authFilesDir, out string token, out string accountId)
{
    token = "";
    accountId = "";
    try
    {
        var authPath = Path.Combine(ExpandHome(authFilesDir), "auth.json");
        if (!File.Exists(authPath))
        {
            return false;
        }

        var auth = JsonDocument.Parse(File.ReadAllText(authPath));
        if (auth.RootElement.TryGetProperty("tokens", out var tokensNode) &&
            tokensNode.TryGetProperty("access_token", out var accessTokenNode))
        {
            var value = accessTokenNode.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                token = value;
                if (tokensNode.TryGetProperty("account_id", out var accountIdNode))
                {
                    accountId = accountIdNode.GetString() ?? "";
                }

                if (string.IsNullOrWhiteSpace(accountId))
                {
                    accountId = TryExtractAccountIdFromJwt(token);
                }

                return true;
            }
        }

        if (auth.RootElement.TryGetProperty("OPENAI_API_KEY", out var apiKeyNode))
        {
            var apiKey = apiKeyNode.GetString();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                token = apiKey;
                return true;
            }
        }

        return false;
    }
    catch
    {
        return false;
    }
}

static string TryExtractAccountIdFromJwt(string jwt)
{
    try
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return "";
        }

        var payload = Base64UrlDecode(parts[1]);
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var authNode) &&
            authNode.TryGetProperty("chatgpt_account_id", out var idNode))
        {
            return idNode.GetString() ?? "";
        }
    }
    catch
    {
        // ignore parse errors
    }

    return "";
}

static string Base64UrlDecode(string input)
{
    var normalized = input.Replace('-', '+').Replace('_', '/');
    var padding = 4 - (normalized.Length % 4);
    if (padding is > 0 and < 4)
    {
        normalized += new string('=', padding);
    }

    var bytes = Convert.FromBase64String(normalized);
    return Encoding.UTF8.GetString(bytes);
}

static bool IsOpenAiCodexProvider(string provider)
{
    var p = provider.Trim().ToLowerInvariant();
    return p is "openai/codex" or "openai-codex";
}

static string ExpandHome(string path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return path;
    }

    if (!path.StartsWith("~/", StringComparison.Ordinal))
    {
        return path;
    }

    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(home, path[2..]);
}

internal sealed class BotHostedService(BotAgent bot, ILogger<BotHostedService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("GalaxyNG Bot started");
        return bot.RunLoopAsync(ct);
    }
}
