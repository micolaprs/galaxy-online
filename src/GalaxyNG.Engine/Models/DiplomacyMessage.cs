namespace GalaxyNG.Engine.Models;

public sealed class DiplomacyMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Turn { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public string SenderId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public List<string> RecipientIds { get; set; } = [];
    public string Text { get; set; } = "";
}
