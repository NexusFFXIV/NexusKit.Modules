namespace NexusKit.Modules.FfxivCollect.Persistence;

public sealed class FfxivCollectCacheEntity
{
    public string Key { get; set; } = null!;
    public string Response { get; set; } = null!;
    public DateTime FetchedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
