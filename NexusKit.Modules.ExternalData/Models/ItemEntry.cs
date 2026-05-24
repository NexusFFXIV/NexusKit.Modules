namespace NexusKit.Modules.ExternalData.Models;

/// <summary>
/// Reserved catalog entry for in-game items / collectables that aren't part of the
/// existing mount / minion / achievement surface. Distinct from the equipment data
/// captured in <see cref="PlayerGearSlot"/> (which is the player's actively worn gear);
/// this is the "all known items" catalog for ownership tracking once a source is wired.
/// No source module populates it today — instances will only appear once a dedicated
/// external data source is added.
/// </summary>
public sealed record ItemEntry(
    int Id,
    string Name,
    string? Description = null,
    string? IconUrl = null);
