namespace NexusKit.Modules.Lodestone.Persistence;

public sealed class LodestoneCacheEntity
{
    public string Key { get; set; } = null!;
    public string Response { get; set; } = null!;
    public DateTime FetchedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
