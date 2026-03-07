using FluentAssertions;
using GalaxyNG.Engine.Models;
using GalaxyNG.Engine;

namespace GalaxyNG.Engine.Tests;

public sealed class ShipTypeTests
{
    // Pure drive ship: mass = 1.0, speed = 20 × 1.0 × (1/1) = 20
    [Fact]
    public void Drone_mass_and_speed_are_correct()
    {
        var drone = new ShipType { Name = "Drone", Drive = 1, Attacks = 0, Weapons = 0, Shields = 0, Cargo = 0 };
        drone.Mass.Should().BeApproximately(1.0, 0.001);
        drone.SpeedEmpty(1.0).Should().BeApproximately(20.0, 0.001);
    }

    // Fighter: drive=2.48, attacks=1, weapons=1.20, shields=1.27, cargo=0
    // mass = 2.48 + 1.20 + 1.27 + 0 = 4.95
    // speed = 20 × 1 × 2.48/4.95 ≈ 10.02
    [Fact]
    public void Fighter_stats_match_wiki_example()
    {
        var f = new ShipType { Name = "Fighter", Drive = 2.48, Attacks = 1, Weapons = 1.20, Shields = 1.27, Cargo = 0 };
        f.Mass.Should().BeApproximately(4.95, 0.01);
        f.SpeedEmpty(1.0).Should().BeApproximately(10.02, 0.05);
    }

    // Hauler: drive=2, attacks=0, weapons=0, shields=0, cargo=1
    // mass = 3, speed = 20 × 1 × 2/3 ≈ 13.33
    [Fact]
    public void Hauler_speed_correct()
    {
        var h = new ShipType { Name = "Hauler", Drive = 2, Attacks = 0, Weapons = 0, Shields = 0, Cargo = 1 };
        h.Mass.Should().BeApproximately(3.0, 0.001);
        h.SpeedEmpty(1.0).Should().BeApproximately(13.33, 0.01);
    }

    // BattleCruiser: drive=49.50, attacks=25, weapons=3.0, shields=9.50, cargo=1.0
    // extra attacks mass = 24 × (3.0/2) = 36
    // mass = 49.50 + 3.0 + 9.50 + 1.0 + 36 = 99.0
    [Fact]
    public void BattleCruiser_mass_is_99()
    {
        var bc = new ShipType { Name = "BC", Drive = 49.50, Attacks = 25, Weapons = 3.0, Shields = 9.50, Cargo = 1.0 };
        bc.Mass.Should().BeApproximately(99.0, 0.01);
    }

    // Cargo capacity: cargo=10 → 10 + 100/10 = 20
    [Fact]
    public void CargoCapacity_formula_correct()
    {
        var h = new ShipType { Name = "Hauler", Drive = 2, Attacks = 0, Weapons = 0, Shields = 0, Cargo = 10 };
        h.BaseCargoCapacity.Should().BeApproximately(20.0, 0.001);
    }

    [Fact]
    public void SpeedLoaded_slower_than_empty()
    {
        var h = new ShipType { Name = "H", Drive = 2, Attacks = 0, Weapons = 0, Shields = 0, Cargo = 1 };
        double empty  = h.SpeedEmpty(1.0);
        double loaded = h.SpeedLoaded(1.0, 1.0, 5.0);
        loaded.Should().BeLessThan(empty);
    }
}
