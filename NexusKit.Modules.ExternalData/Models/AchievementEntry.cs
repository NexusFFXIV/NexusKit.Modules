namespace NexusKit.Modules.ExternalData.Models;

public sealed record AchievementEntry(
    int Id,
    string Name,
    string? Description = null,
    string? IconUrl = null,
    string? Patch = null,
    int? Points = null);
