namespace NexusKit.Modules.ExternalData.Models;

/// <summary>
/// Collection ownership for a single player. A null stats field means the corresponding
/// data source did not return that category (module disabled or the player has it private).
/// </summary>
public sealed record PlayerCollections(
    PlayerCollectionStats? Mounts,
    PlayerCollectionStats? Minions,
    PlayerCollectionStats? Achievements,
    IReadOnlyList<int> OwnedMountIds,
    IReadOnlyList<int> OwnedMinionIds,
    IReadOnlyList<OwnedAchievement> OwnedAchievements,
    IReadOnlyList<int> OwnedItemIds);
