namespace NexusKit.Modules.ExternalData.Models;

/// <summary>
/// One earned achievement on a player. <see cref="AchievedAt"/> comes from the
/// Lodestone scrape and may be null when only FFXIVCollect data is available
/// (FFXIVCollect doesn't expose per-character timestamps).
/// </summary>
public sealed record OwnedAchievement(int Id, DateTime? AchievedAt);
