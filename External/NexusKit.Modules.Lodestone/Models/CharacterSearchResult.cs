namespace NexusKit.Modules.Lodestone.Models;

public sealed class CharacterSearchResult
{
    public IReadOnlyList<CharacterSearchEntry> Results { get; set; } = Array.Empty<CharacterSearchEntry>();
}
