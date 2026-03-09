using GalaxyNG.Engine.Services;
using GalaxyNG.Server.Data;
using GalaxyNG.Server.Hubs;
using GalaxyNG.Server.Mcp;
using GalaxyNG.Server.Services;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// SignalR log broadcaster — created before Build() so it can be used as a log provider
var logBroadcaster = new LogBroadcastService();
builder.Services.AddSingleton(logBroadcaster);
builder.Logging.AddProvider(new SignalRLoggerProvider(logBroadcaster));

// Engine services
builder.Services.AddSingleton<GalaxyGenerator>();
builder.Services.AddSingleton<CombatResolver>();
builder.Services.AddSingleton<ProductionEngine>();
builder.Services.AddSingleton<MovementEngine>();
builder.Services.AddSingleton<ReportGenerator>();
builder.Services.AddSingleton<TurnProcessor>(sp => new TurnProcessor(
    sp.GetRequiredService<CombatResolver>(),
    sp.GetRequiredService<ProductionEngine>(),
    sp.GetRequiredService<MovementEngine>()
));

// Server services
builder.Services.AddSingleton<GameStore>();
var llmProvider = (builder.Configuration["Llm:Provider"] ?? "openai/codex").Trim().ToLowerInvariant();
if (llmProvider is "openai/codex" or "openai-codex")
{
    builder.Services.AddSingleton<ILlmProvider, CodexLlmProvider>();
}
else if (llmProvider is "lmstudio" or "lm-studio")
{
    builder.Services.AddSingleton<ILlmProvider, LmStudioLlmProvider>();
}
else
{
    throw new InvalidOperationException($"Unsupported Llm:Provider '{llmProvider}'. Supported: openai/codex, lmstudio.");
}

builder.Services.AddSingleton<LlmService>();
builder.Services.AddSingleton<LlmQueueService>();
builder.Services.AddSingleton<GameService>();

// ASP.NET
builder.Services.AddControllers();
builder.Services.AddSignalR();

// MCP Server (Streamable HTTP transport)
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<GameTools>();

// CORS — allow the HTML frontend (dev)
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// Give the log broadcaster a reference to the hub context so it can forward logs
logBroadcaster.Initialize(app.Services.GetRequiredService<IHubContext<GameHub>>());

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();   // Serves GalaxyNG.Web dist files from wwwroot

app.MapControllers();
app.MapHub<GameHub>("/hubs/game");
app.MapMcp("/mcp");     // MCP endpoint for bots

// On startup: resume auto-run for any game where all players already submitted
// (handles server restart mid-game)
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(500); // brief pause to let SignalR hub initialize
            var gameService = app.Services.GetRequiredService<GameService>();
            var sysLog = app.Services.GetRequiredService<ILogger<GameService>>();
            var games = await gameService.ListGamesAsync();
            foreach (var game in games)
            {
                if (game.AutoRunOnAllSubmitted && game.AllPlayersSubmitted())
                {
                    sysLog.LogInformation(
                        "🔄 Возобновляем авто-ход для игры {Id} (все игроки уже сдали приказы)", game.Id);
                    await gameService.RunTurnAsync(game.Id);
                }
            }
        }
        catch (Exception ex)
        {
            var sysLog = app.Services.GetRequiredService<ILogger<GameService>>();
            sysLog.LogWarning(ex, "Ошибка при проверке авто-хода при старте");
        }
    });
});

app.Run();
