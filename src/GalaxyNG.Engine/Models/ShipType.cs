using static GalaxyNG.Engine.Constants;

namespace GalaxyNG.Engine.Models;

public sealed record ShipType
{
    public required string Name { get; init; }
    public required double Drive { get; init; }
    public required int Attacks { get; init; }
    public required double Weapons { get; init; }
    public required double Shields { get; init; }
    public required double Cargo { get; init; }

    // Derived — computed once on creation
    public double Mass => Drive + Weapons + Shields + Cargo
                        + (Attacks > 1 ? (Attacks - 1) * (Weapons / 2.0) : 0.0);

    /// <summary>Base cargo capacity at cargo tech 1.0 (no cargo tech bonus yet)</summary>
    public double BaseCargoCapacity => Cargo + (Cargo * Cargo / 10.0);

    /// <summary>Speed at given drive tech, no cargo loaded.</summary>
    public double SpeedEmpty(double driveTech) =>
        Mass == 0 ? 0 : DriveMultiplier * driveTech * (Drive / Mass);

    /// <summary>Speed with actual cargo load.</summary>
    public double SpeedLoaded(double driveTech, double cargoTech, double cargoQty)
    {
        double effectiveCargo = cargoTech == 0 ? 0 : cargoQty / cargoTech;
        double totalMass = Mass + effectiveCargo;
        return totalMass == 0 ? 0 : DriveMultiplier * driveTech * (Drive / totalMass);
    }

    /// <summary>Attack strength at given weapons tech.</summary>
    public double AttackStrength(double wpnTech) => Weapons * wpnTech;

    /// <summary>Defense strength at given shields tech and loaded cargo.</summary>
    public double DefenseStrength(double shieldTech, double cargoTech, double cargoQty)
    {
        double effCargo = cargoTech == 0 ? 0 : cargoQty / cargoTech;
        double totalMass = Mass + effCargo;
        double cubeRoot = Math.Cbrt(totalMass / DefenseCubeFactor);
        return cubeRoot == 0 ? 0 : (Shields * shieldTech) / cubeRoot;
    }

    public bool IsWarship => Attacks > 0 && Weapons > 0;

    public ShipType WithName(string name) => this with { Name = name };
}
