namespace NexusKit.Modules.ExternalData.Models;

public sealed record MountEntry(
    int Id,
    string Name,
    string? Description = null,
    string? IconUrl = null,
    string? Patch = null,
    int? Seats = null);
