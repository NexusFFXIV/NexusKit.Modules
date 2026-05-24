namespace NexusKit.Modules.Lodestone.Models;

/// <summary>
/// One levelled class/job for a character as scraped from the Lodestone class &amp;
/// job page via NetStone. <see cref="Name"/> is NetStone's property name
/// (e.g. <c>Paladin</c>, <c>Warrior</c>, <c>Carpenter</c>, <c>BlueMage</c>), kept as a
/// stable string so this module doesn't need a game-data dependency to expose IDs.
/// </summary>
public sealed class LodestoneClassJob
{
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
}
