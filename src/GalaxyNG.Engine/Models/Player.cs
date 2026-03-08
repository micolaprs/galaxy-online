namespace GalaxyNG.Engine.Models;

public sealed class Player
{
    public required string Id         { get; init; }
    public required string Name       { get; set; }
    public required string Password   { get; set; }

    public string?  Email             { get; set; }
    public string?  RealName          { get; set; }

    public TechLevels Tech            { get; set; } = TechLevels.Initial;

    // Tech research carry-over (partial progress toward next +1)
    public double DriveResearch       { get; set; }
    public double WeaponsResearch     { get; set; }
    public double ShieldsResearch     { get; set; }
    public double CargoResearch       { get; set; }

    public Dictionary<string, ShipType> ShipTypes { get; init; } = [];
    public List<Group>                  Groups     { get; init; } = [];
    public HashSet<string>              FleetNames { get; init; } = [];

    // Diplomacy: other player IDs
    public HashSet<string> Allies     { get; init; } = [];
    public Dictionary<string, int> AllianceUntilTurn { get; init; } = [];
    public HashSet<string> AtWar      { get; init; } = [];

    // Options
    public bool AutoUnload            { get; set; } = true;
    public bool SortGroups            { get; set; } = true;
    public bool IsBot                 { get; set; }

    // Game state
    public bool   Submitted           { get; set; }
    public bool   IsEliminated        { get; set; }
    public int    MissedTurns         { get; set; }
    public string PendingOrders       { get; set; } = "";

    // Pending orders parsed this turn
    public List<ParsedOrder> Orders   { get; set; } = [];

    public int NextGroupNumber()
    {
        if (Groups.Count == 0) return 1;
        return Groups.Max(g => g.Number) + 1;
    }
}
