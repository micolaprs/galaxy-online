namespace GalaxyNG.Engine.Models;

public sealed record BattleShot(string AttackerRace, string DefenderRace, bool Killed);

public sealed record BattleRecord(
    string PlanetName,
    string Winner,       // race name or "Draw"
    List<string> Participants,
    List<BattleShot> Protocol
);

public sealed record BombingRecord(
    string PlanetName,
    string AttackerRace,
    string? PreviousOwner,
    double OldPopulation,
    double OldIndustry,
    string NewProduction
);
