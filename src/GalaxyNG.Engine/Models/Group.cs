namespace GalaxyNG.Engine.Models;

public sealed class Group
{
    public required int Number { get; set; }
    public required string ShipTypeName { get; set; }
    public required int Ships { get; set; }
    public required TechLevels Tech { get; set; }

    // Location
    public required string At { get; set; }   // planet name where group currently is (or was)
    public string? Destination { get; set; }   // planet name target (null = stationary)
    public string? Origin { get; set; }   // planet name of departure
    public double Distance { get; set; }   // remaining distance to destination
    public bool InHyperspace => Destination is not null && Distance > 0;
    public string? LastRouteOrigin { get; set; }
    public string? LastRouteDestination { get; set; }
    public int LastRouteTurn { get; set; }
    public double LastRouteSpeed { get; set; }

    // Cargo
    public string? CargoType { get; set; }   // CAP | COL | MAT | null
    public double CargoLoad { get; set; }

    // Fleet membership
    public string? FleetName { get; set; }

    // Flags
    public bool HasFired { get; set; }   // used during combat round

    public Group Clone() => (Group)MemberwiseClone();
}
