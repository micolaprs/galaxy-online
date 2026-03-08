using GalaxyNG.Engine.Models;
using static GalaxyNG.Engine.Constants;

namespace GalaxyNG.Engine.Services;

public sealed class GalaxyGenerator(Random? rng = null)
{
    private readonly Random _rng = rng ?? Random.Shared;

    // Fighter reference: drive=2, attacks=2, weapons=2, shields=1, cargo=0
    //   mass = 2+2+1+max(0,2-1)×1 = 6; speed = 20×1×2/6 ≈ 6.667 u/turn
    // We intentionally target earlier contact than "crossable in maxTurns":
    //   gs = maxTurns × FighterSpeed × ContactWindowShare × sqrt(playerCount / BaselinePlayers)
    // Typical defaults:
    //   3p, 30t→125  3p, 60t→249  5p, 60t→322  6p, 60t→353
    private const double FighterSpeed      = 20.0 * 2 / 6;  // ~6.667 u/turn
    private const double ContactWindowShare = 0.72;
    private const int    BaselinePlayers   = 4;

    public static GalaxyGeneratorOptions DefaultOptions(int playerCount, int maxTurns = 60)
    {
        double gs = Math.Round(maxTurns * FighterSpeed * ContactWindowShare *
                               Math.Sqrt(Math.Max(1, playerCount) / (double)BaselinePlayers));
        gs = Math.Max(gs, 70);  // absolute minimum
        return new()
        {
            GalaxySize    = gs,
            PlayerCount   = playerCount,
            MinDist       = Math.Round(gs * 0.15),
            EmptyRadius   = DefaultEmptyRadius,
            StuffPlanets  = DefaultStuffPlanets,
            HomePlanets   = DefaultHomePlanets,
        };
    }

    public Game Generate(string gameId, string gameName, IReadOnlyList<(string id, string name, string password, bool isBot)> players, GalaxyGeneratorOptions? opts = null)
    {
        opts ??= DefaultOptions(players.Count);
        var game = new Game
        {
            Id         = gameId,
            Name       = gameName,
            GalaxySize = opts.GalaxySize,
            Turn       = 0,
        };

        // --- Place home planets ---
        var homePlanetPositions = PlaceHomeworlds(players.Count, opts);

        int planetIndex = 1;
        foreach (var ((id, name, password, isBot), homePos) in players.Zip(homePlanetPositions))
        {
            var player = new Player
            {
                Id       = id,
                Name     = name,
                Password = password,
                IsBot    = isBot,
            };

            // Primary home planet
            var homeName   = $"P{planetIndex++}";
            var homePlanet = new Planet
            {
                Name        = homeName,
                X           = homePos.x,
                Y           = homePos.y,
                Size        = HomeSize,
                Resources   = HomeResources,
                Population  = HomeSize,
                Industry    = HomeSize * 0.5,
                OwnerId     = id,
                IsHome      = true,
                Producing   = ProductionType.Capital,
                Stockpiles  = new Stockpiles(100, 100, 0),
            };
            game.Planets[homePlanet.Name] = homePlanet;

            // Secondary homeworlds
            for (int h = 0; h < opts.HomePlanets; h++)
            {
                var secPos  = RandomNear(homePos, opts.EmptyRadius);
                var secName = $"P{planetIndex++}";
                var size    = 200 + _rng.NextDouble() * 300;
                var sec     = new Planet
                {
                    Name        = secName,
                    X           = Clamp(secPos.x, 0, opts.GalaxySize),
                    Y           = Clamp(secPos.y, 0, opts.GalaxySize),
                    Size        = size,
                    Resources   = 5.0 + _rng.NextDouble() * 5.0,
                    Population  = 0,
                    Industry    = 0,
                    OwnerId     = null,
                };
                game.Planets[sec.Name] = sec;
            }

            game.Players[id] = player;
        }

        // --- Scatter stuff planets ---
        int stuffCount = opts.StuffPlanets * players.Count;
        for (int i = 0; i < stuffCount; i++)
        {
            var pos  = RandomPos(opts.GalaxySize);
            var name = $"P{planetIndex++}";
            var size = _rng.NextDouble() < 0.5
                ? _rng.NextDouble() * 200          // small
                : 200 + _rng.NextDouble() * 800;   // large

            game.Planets[name] = new Planet
            {
                Name       = name,
                X          = pos.x,
                Y          = pos.y,
                Size       = Math.Round(size, 2),
                Resources  = Math.Round(0.5 + _rng.NextDouble() * 9.5, 2),
                Population = 0,
                Industry   = 0,
                OwnerId    = null,
            };
        }

        return game;
    }

    // ------------ helpers ------------

    private List<(double x, double y)> PlaceHomeworlds(int count, GalaxyGeneratorOptions opts)
    {
        var positions = new List<(double x, double y)>();
        double cx = opts.GalaxySize / 2.0;
        double cy = opts.GalaxySize / 2.0;
        double minR = opts.GalaxySize * 0.28;
        double maxR = opts.GalaxySize * 0.42;
        double sectorAngle = Math.Tau / Math.Max(1, count);

        for (int i = 0; i < count; i++)
        {
            double baseAngle = sectorAngle * i - Math.PI / 2;
            double jitter    = (_rng.NextDouble() - 0.5) * sectorAngle * 0.55;
            double angle     = baseAngle + jitter;
            double r         = minR + _rng.NextDouble() * (maxR - minR);
            positions.Add((cx + Math.Cos(angle) * r, cy + Math.Sin(angle) * r));
        }
        return positions;
    }

    private (double x, double y) RandomPos(double size) =>
        (_rng.NextDouble() * size, _rng.NextDouble() * size);

    private (double x, double y) RandomNear((double x, double y) center, double radius)
    {
        double angle = _rng.NextDouble() * Math.Tau;
        double dist  = _rng.NextDouble() * radius;
        return (center.x + Math.Cos(angle) * dist, center.y + Math.Sin(angle) * dist);
    }

    private static double Dist((double x, double y) a, (double x, double y) b)
    {
        double dx = a.x - b.x, dy = a.y - b.y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Clamp(double v, double min, double max) =>
        Math.Max(min, Math.Min(max, v));
}

public sealed record GalaxyGeneratorOptions
{
    public double GalaxySize   { get; init; } = DefaultGalaxySize;
    public int    PlayerCount  { get; init; } = 2;
    public double MinDist      { get; init; } = DefaultMinDist;
    public double EmptyRadius  { get; init; } = DefaultEmptyRadius;
    public int    StuffPlanets { get; init; } = DefaultStuffPlanets;
    public int    HomePlanets  { get; init; } = DefaultHomePlanets;
}
