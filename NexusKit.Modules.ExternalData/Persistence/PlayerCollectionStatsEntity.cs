namespace NexusKit.Modules.ExternalData.Persistence;

/// <summary>
/// One row per player per collection kind (mounts/minions/achievements).
/// Composite key: (LodestoneId, CollectionKind).
/// </summary>
public sealed class PlayerCollectionStatsEntity
{
    public ulong LodestoneId { get; set; }
    public CollectionKind Kind { get; set; }
    public int Count { get; set; }
    public int Total { get; set; }
    public int? Ranking { get; set; }
    public bool? IsPublic { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum CollectionKind
{
    Mounts        = 0,
    Minions       = 1,
    Achievements  = 2,
    Items         = 3,
}
