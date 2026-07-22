namespace ForgeManager.Models;

public enum ParsedServerEventKind
{
    None,
    Kill,
    PlayerJoined,
    PlayerLeft,
    PlayerObserved,
    GameMaster
}

public sealed class ParsedServerEvent
{
    public ParsedServerEventKind Kind { get; init; }
    public string PlayerName { get; init; } = string.Empty;
    public string PlayerId { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string Killer { get; init; } = string.Empty;
    public string Victim { get; init; } = string.Empty;
    public string Weapon { get; init; } = string.Empty;
    public string Distance { get; init; } = string.Empty;
    public bool IsFriendlyFire { get; init; }
    public bool IsSuicide { get; init; }
    public string Details { get; init; } = string.Empty;
}
