namespace ForgeManager.Models;

public sealed class AdminEventEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string EventType { get; init; } = "EVENT";
    public string Player { get; init; } = "—";
    public string Address { get; init; } = "—";
    public string Details { get; init; } = string.Empty;
    public string Raw { get; init; } = string.Empty;
}
