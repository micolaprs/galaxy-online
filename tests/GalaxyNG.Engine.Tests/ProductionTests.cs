using FluentAssertions;
using GalaxyNG.Engine.Models;
using GalaxyNG.Engine.Services;

namespace GalaxyNG.Engine.Tests;

public sealed class ProductionTests
{
    private static Planet MakePlanet(double pop, double ind, double res,
        ProductionType prod = ProductionType.Capital, string? owner = "P1") =>
        new()
        {
            Name = "Home",
            X = 0,
            Y = 0,
            Size = 1000,
            Resources = res,
            Population = pop,
            Industry = ind,
            OwnerId = owner,
            Producing = prod,
            Stockpiles = new Stockpiles(100, 100, 0),
        };

    private static Player MakePlayer() => new()
    {
        Id = "P1",
        Name = "Test",
        Password = "pw",
        Tech = TechLevels.Initial,
    };

    [Fact]
    public void Production_formula_is_correct()
    {
        // Production = ind × 0.75 + pop × 0.25
        var planet = MakePlanet(pop: 200, ind: 100, res: 1);
        planet.Production.Should().BeApproximately(100 * 0.75 + 200 * 0.25, 0.001);
    }

    [Fact]
    public void Materials_production_uses_resources()
    {
        var planet = MakePlanet(200, 100, 5.0, ProductionType.Materials);
        var player = MakePlayer();
        var game = new Game { Id = "G", Name = "T", GalaxySize = 200, Turn = 0 };
        game.Players["P1"] = player;
        game.Planets["Home"] = planet;

        double initialMat = planet.Stockpiles.Materials;
        double expectedProd = planet.Production;

        new ProductionEngine().RunProduction(game);

        planet.Stockpiles.Materials.Should().BeApproximately(
            initialMat + expectedProd * 5.0, 0.1);
    }

    [Fact]
    public void Population_grows_eight_percent_per_turn()
    {
        var planet = MakePlanet(100, 50, 1);
        planet.Stockpiles = new Stockpiles(0, 100, 0);
        var player = MakePlayer();
        var game = new Game { Id = "G", Name = "T", GalaxySize = 200, Turn = 0 };
        game.Players["P1"] = player;
        game.Planets["Home"] = planet;

        new ProductionEngine().GrowPopulation(game);
        planet.Population.Should().BeApproximately(108.0, 0.5);
    }
}
