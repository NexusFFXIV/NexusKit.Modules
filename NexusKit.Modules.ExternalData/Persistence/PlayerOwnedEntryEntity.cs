namespace NexusKit.Modules.ExternalData.Persistence;

/// <summary>
/// Join row: which catalog entry (Kind+EntryId) a given player owns.
/// Composite key: (LodestoneId, Kind, EntryId).
/// </summary>
public sealed class PlayerOwnedEntryEntity
{
    public ulong LodestoneId { get; set; }
    public CollectionKind Kind { get; set; }

    /// <summary>
    /// Lumina RowId for the entry: <c>Mount</c>, <c>Companion</c>, <c>Achievement</c>,
    /// or <c>Item</c> depending on <see cref="Kind"/>. Names are resolved via
    /// <c>IGameDataLookups</c> in whatever language the consumer wants.
    /// </summary>
    public int EntryId { get; set; }

    /// <summary>
    /// When the entry was earned. Populated for achievements via the Lodestone scrape;
    /// always null for mounts/minions/items (those don't carry per-character dates).
    /// </summary>
    public DateTime? AchievedAt { get; set; }
}
