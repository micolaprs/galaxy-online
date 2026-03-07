namespace GalaxyNG.Engine.Models;

public sealed class Game
{
    public required string              Id           { get; init; }
    public required string              Name         { get; set; }
    public required double              GalaxySize   { get; init; }
    public required int                 Turn         { get; set; }

    public Dictionary<string, Player>   Players      { get; init; } = [];
    public Dictionary<string, Planet>   Planets      { get; init; } = [];

    // Turn results (cleared each turn)
    public List<BattleRecord>           Battles      { get; set; } = [];
    public List<BombingRecord>          Bombings     { get; set; } = [];

    // Turn history (archived per-turn orders + events)
    public List<TurnHistoryEntry> TurnHistory { get; set; } = [];

    // LLM reasoning for the current in-progress turn (cleared when turn runs, persisted for resume)
    public Dictionary<string, string> CurrentTurnReasoning { get; set; } = [];

    // AI-generated summaries, one per turn
    public List<AiSummaryEntry> AiSummaries { get; set; } = [];

    // Diplomacy chat history (global + private)
    public List<DiplomacyMessage> DiplomacyMessages { get; set; } = [];

    // Config
    public bool   AutoRunOnAllSubmitted { get; set; } = false;
    public string HostPlayerId          { get; set; } = "";
    public int    MaxTurns              { get; set; } = 9999;
    public bool   IsFinished            { get; set; }
    public string? WinnerPlayerId       { get; set; }
    public string? WinnerName           { get; set; }
    public string? FinishReason         { get; set; }

    public DateTime CreatedAt           { get; init; } = DateTime.UtcNow;
    public DateTime? LastTurnRunAt      { get; set; }

    public Player? GetPlayer(string nameOrId) =>
        Players.Values.FirstOrDefault(p =>
            p.Id == nameOrId ||
            p.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));

    public Planet? GetPlanet(string name) =>
        Planets.TryGetValue(name, out var p) ? p :
        Planets.Values.FirstOrDefault(pl =>
            pl.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<Planet> PlanetsOwnedBy(string playerId) =>
        Planets.Values.Where(p => p.OwnerId == playerId);

    public IEnumerable<Group> GroupsOf(string playerId) =>
        Players.TryGetValue(playerId, out var pl) ? pl.Groups : [];

    /// <summary>
    /// Returns true when every active (non-eliminated) player — human OR bot —
    /// has explicitly submitted their orders. Bots must submit just like humans.
    /// </summary>
    public bool AllPlayersSubmitted() =>
        Players.Values.All(p => p.IsEliminated || p.Submitted);
}
