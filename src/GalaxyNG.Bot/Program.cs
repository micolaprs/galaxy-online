using GalaxyNG.Bot;
using GalaxyNG.Bot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging((ctx, logging) =>
    {
        // Forward every bot log line to the server's web console (fire-and-forget)
        var serverUrl = ctx.Configuration["Bot:ServerUrl"] ?? "http://localhost:5055";
        var raceName  = ctx.Configuration["Bot:RaceName"]  ?? "Bot";
        logging.AddProvider(new RemoteLoggerProvider(serverUrl, raceName));
    })
    .ConfigureServices((ctx, services) =>
    {
        var cfg = ctx.Configuration;

        var botConfig = new BotConfig
        {
            GameId    = cfg["Bot:GameId"]    ?? throw new InvalidOperationException("Bot:GameId required"),
            RaceName  = cfg["Bot:RaceName"]  ?? throw new InvalidOperationException("Bot:RaceName required"),
            Password  = cfg["Bot:Password"]  ?? throw new InvalidOperationException("Bot:Password required"),
            ServerUrl = cfg["Bot:ServerUrl"] ?? "http://localhost:5000",
            Llm = new LlmConfig
            {
                BaseUrl     = cfg["Bot:Llm:BaseUrl"]     ?? "http://localhost:1234/v1",
                Model       = cfg["Bot:Llm:Model"]       ?? "qwen/qwen3.5-9b",
                Temperature = double.TryParse(cfg["Bot:Llm:Temperature"], out double t) ? t : 0.7,
                MaxTokens   = int.TryParse(cfg["Bot:Llm:MaxTokens"], out int m) ? m : 4096,
                ApiKey      = cfg["Bot:Llm:ApiKey"]      ?? "lm-studio",
            },
        };

        services.AddSingleton(botConfig);
        services.AddSingleton(botConfig.Llm);

        services.AddHttpClient("llm", (sp, client) =>
        {
            var c = sp.GetRequiredService<LlmConfig>();
            client.BaseAddress = new Uri(c.BaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {c.ApiKey}");
        });

        services.AddHttpClient("server");

        services.AddSingleton<LlmClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http    = factory.CreateClient("llm");
            var cfg     = sp.GetRequiredService<LlmConfig>();
            var log     = sp.GetRequiredService<ILogger<LlmClient>>();
            return new LlmClient(http, cfg, log);
        });
        services.AddSingleton<BotAgent>();
        services.AddHostedService<BotHostedService>();
    })
    .Build();

await host.RunAsync();

internal sealed class BotHostedService(BotAgent bot, ILogger<BotHostedService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("GalaxyNG Bot started");
        return bot.RunLoopAsync(ct);
    }
}
