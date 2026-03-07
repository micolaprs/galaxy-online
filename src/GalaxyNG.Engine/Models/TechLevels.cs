namespace GalaxyNG.Engine.Models;

public record struct TechLevels(double Drive = 1.0, double Weapons = 1.0, double Shields = 1.0, double Cargo = 1.0)
{
    public static readonly TechLevels Initial = new(1.0, 1.0, 1.0, 1.0);
}
