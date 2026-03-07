namespace GalaxyNG.Engine.Models;

public sealed class AiSummaryEntry
{
    public int      Turn        { get; set; }
    public string   Summary     { get; set; } = "";
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
