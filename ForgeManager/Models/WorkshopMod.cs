namespace ForgeManager.Models;

public sealed class WorkshopMod
{
    public string ModId { get; init; } = string.Empty;
    public string Name { get; init; } = "Unknown addon";
    public string Author { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string GameVersion { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string Downloads { get; init; } = string.Empty;
    public string Subscribers { get; init; } = string.Empty;
    public string Rating { get; init; } = string.Empty;
    public string LastModified { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string WorkshopUrl { get; init; } = string.Empty;
    public Uri? ThumbnailUri { get; init; }
    public IReadOnlyList<string> DependencyIds { get; init; } = [];
    public bool Available { get; init; }
    public bool IsRoot { get; set; }
    public bool IsConfigured { get; set; }
    public string Role => IsRoot ? "CONFIGURED" : "DEPENDENCY";
    public string AvailabilityText => Available ? "AVAILABLE" : "UNAVAILABLE";
    public string DependencySummary => DependencyIds.Count == 0 ? "No listed dependencies" : $"{DependencyIds.Count} dependencies";
    public string PopularitySummary
    {
        get
        {
            var values = new[] { Subscribers, Downloads, Rating }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            return values.Length == 0 ? "—" : string.Join(" · ", values);
        }
    }
    public bool CanAdd => Available && !IsConfigured;
    public bool CanRemove => IsRoot && IsConfigured;
    public string Error { get; init; } = string.Empty;

    public WorkshopMod Clone() => new()
    {
        ModId = ModId,
        Name = Name,
        Author = Author,
        Version = Version,
        GameVersion = GameVersion,
        Size = Size,
        Downloads = Downloads,
        Subscribers = Subscribers,
        Rating = Rating,
        LastModified = LastModified,
        Summary = Summary,
        WorkshopUrl = WorkshopUrl,
        ThumbnailUri = ThumbnailUri,
        DependencyIds = DependencyIds.ToArray(),
        Available = Available,
        IsRoot = IsRoot,
        IsConfigured = IsConfigured,
        Error = Error
    };
}
