using static GalaxyNG.Engine.Constants;

namespace GalaxyNG.Engine.Models;

public enum ProductionType { Capital, Materials, Drive, Weapons, Shields, Cargo, Ship }

public sealed class Planet
{
    public required string Name { get; set; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Size { get; init; }
    public required double Resources { get; init; }

    public double Population { get; set; }
    public double Industry { get; set; }
    public string? OwnerId { get; set; }   // Player.Id, null = uninhabited
    public Stockpiles Stockpiles { get; set; } = Stockpiles.Empty;

    // Production
    public ProductionType Producing { get; set; } = ProductionType.Capital;
    public string? ShipTypeName { get; set; }  // when Producing == Ship
    public double ExcessProd { get; set; }  // carried-over production

    // Routes: one per cargo type
    public Dictionary<string, string?> Routes { get; init; } = new()
    {
        [CargoCapital] = null,
        [CargoColonists] = null,
        [CargoMaterials] = null,
        [CargoEmpty] = null,
    };

    // Derived
    public double Production => Industry * ProdIndustryW + Population * ProdPopW;

    public bool IsOwned => OwnerId is not null;
    public bool IsHome { get; init; }

    public double DistanceTo(Planet other)
    {
        double dx = X - other.X, dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public Planet Clone() => (Planet)MemberwiseClone();
}
