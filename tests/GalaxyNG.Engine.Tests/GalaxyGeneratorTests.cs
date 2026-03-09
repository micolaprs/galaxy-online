using FluentAssertions;
using GalaxyNG.Engine.Services;

namespace GalaxyNG.Engine.Tests;

public sealed class GalaxyGeneratorTests
{
    [Fact]
    public void Generate_creates_correct_player_count()
    {
        var gen = new GalaxyGenerator(new Random(42));
        var players = new[] { ("P1", "Alice", "pw1", false), ("P2", "Bob", "pw2", false) };
        var game = gen.Generate("G1", "Test", players);

        game.Players.Should().HaveCount(2);
    }

    [Fact]
    public void Each_player_has_home_planet()
    {
        var gen = new GalaxyGenerator(new Random(42));
        var players = new[] { ("P1", "Alice", "pw1", false), ("P2", "Bob", "pw2", false) };
        var game = gen.Generate("G1", "Test", players);

        foreach (var (id, _, _, _) in players)
        {
            game.Planets.Values.Should().Contain(p => p.OwnerId == id && p.IsHome);
        }
    }

    [Fact]
    public void Homeworlds_respect_minimum_distance()
    {
        var gen = new GalaxyGenerator(new Random(42));
        var players = new[] { ("P1", "A", "p", false), ("P2", "B", "p", false), ("P3", "C", "p", false) };
        var game = gen.Generate("G1", "Test", players);

        var homes = game.Planets.Values.Where(p => p.IsHome).ToList();
        for (int i = 0; i < homes.Count; i++)
        {
            for (int j = i + 1; j < homes.Count; j++)
            {
                homes[i].DistanceTo(homes[j]).Should().BeGreaterThan(Constants.DefaultMinDist - 0.01);
            }
        }
    }

    [Fact]
    public void Galaxy_has_stuff_planets()
    {
        var gen = new GalaxyGenerator(new Random(42));
        var players = new[] { ("P1", "A", "p", false) };
        var opts = new GalaxyGeneratorOptions { PlayerCount = 1, StuffPlanets = 3 };
        var game = gen.Generate("G1", "Test", players, opts);

        // Home (1) + secondary homeworlds (2) + stuff (3) = 6 minimum
        game.Planets.Should().HaveCountGreaterThan(3);
    }
}
