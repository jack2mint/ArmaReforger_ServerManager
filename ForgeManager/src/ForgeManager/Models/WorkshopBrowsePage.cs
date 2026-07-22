namespace ForgeManager.Models;

public sealed class WorkshopBrowsePage
{
    public int Page { get; init; } = 1;
    public int TotalResults { get; init; }
    public IReadOnlyList<WorkshopMod> Mods { get; init; } = [];
}
