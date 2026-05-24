namespace NexusKit.Modules.Lodestone.Models;

/// <summary>
/// One earned achievement as parsed from the Lodestone achievement pages.
/// </summary>
public sealed class LodestoneAchievementEntry
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime? AchievedAt { get; set; }
}
