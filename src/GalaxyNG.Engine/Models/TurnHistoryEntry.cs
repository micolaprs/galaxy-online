namespace GalaxyNG.Engine.Models;

public sealed class TurnHistoryEntry
{
    public int    Turn    { get; set; }
    public DateTime RunAt { get; set; } = DateTime.UtcNow;

    /// <summary>Raw order text per player: raceName → ordersText.</summary>
    public Dictionary<string, string> PlayerOrders { get; set; } = [];

    /// <summary>LLM reasoning text per bot player: raceName → reasoningText.</summary>
    public Dictionary<string, string> PlayerReasoning { get; set; } = [];

    /// <summary>AI short summaries per player for this turn: raceName → summaryText.</summary>
    public Dictionary<string, string> PlayerSummaries { get; set; } = [];

    /// <summary>Human-readable battle descriptions.</summary>
    public List<string> Battles  { get; set; } = [];

    /// <summary>Human-readable bombing descriptions.</summary>
    public List<string> Bombings { get; set; } = [];
}
