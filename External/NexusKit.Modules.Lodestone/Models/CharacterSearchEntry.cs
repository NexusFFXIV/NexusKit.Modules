namespace NexusKit.Modules.Lodestone.Models;

public sealed class CharacterSearchEntry
{
    public ulong LodestoneId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Server { get; set; }
}
