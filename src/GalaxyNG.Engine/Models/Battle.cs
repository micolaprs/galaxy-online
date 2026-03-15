namespace GalaxyNG.Engine.Models;

public sealed record BattleShot(string AttackerRace, string DefenderRace, bool Killed);

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
};

public sealed record BombingRecord(
    string PlanetName,
    string AttackerRace,
    string? PreviousOwner,
    double OldPopulation,
    double OldIndustry,
    string NewProduction
);
