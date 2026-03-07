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

app.Run();
