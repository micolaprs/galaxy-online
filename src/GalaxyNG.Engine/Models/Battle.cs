namespace GalaxyNG.Engine.Models;

public sealed record BattleShot(string AttackerRace, string DefenderRace, bool Killed);

/// <summary>Ship design snapshot per race for battle replay visualisation.</summary>
public sealed record ShipDesignSnapshot(
    double Weapons,
    double Shields,
    double Drive,
    double Cargo,
    int    Attacks
);

public sealed record BattleRecord(
    string PlanetName,
    string Winner,       // race name or "Draw"
    List<string> Participants,
    List<BattleShot> Protocol
)
{
    /// <summary>Ship counts per race at the start of the battle, for replay visualisation.</summary>
    public IReadOnlyDictionary<string, int> InitialShips { get; init; } =
        new Dictionary<string, int>();

    /// <summary>Dominant ship design per race for visual rendering in replay.</summary>
    public IReadOnlyDictionary<string, ShipDesignSnapshot> ShipDesigns { get; init; } =
        new Dictionary<string, ShipDesignSnapshot>();
};

public sealed record BombingRecord(
    string PlanetName,
    string AttackerRace,
    string? PreviousOwner,
    double OldPopulation,
    double OldIndustry,
    string NewProduction
);
