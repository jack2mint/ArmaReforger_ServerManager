namespace ForgeManager.Models;

public sealed class KillFeedEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string Killer { get; init; } = "Unknown";
    public string Victim { get; init; } = "Unknown";
    public string Weapon { get; init; } = "Unknown";
    public string Distance { get; init; } = "—";
    public string Result { get; init; } = "KILL";
    public string Raw { get; init; } = string.Empty;
}
