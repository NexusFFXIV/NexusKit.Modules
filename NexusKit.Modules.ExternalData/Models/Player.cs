namespace NexusKit.Modules.ExternalData.Models;

/// <summary>
/// Unified player view assembled by the player service. Core fields
/// (LodestoneId/Name/HomeWorldId/DataCenterId) are always present; sub-resources
/// are populated based on the requested <see cref="PlayerInclude"/> flags.
/// <para>Names of world / data center are resolved on demand via
/// <c>IGameDataLookups</c> — the persisted data stores only Lumina row ids.</para>
/// </summary>
public sealed record Player(
    ulong LodestoneId,
    string Name,
    uint HomeWorldId,
    uint DataCenterId,
    PlayerProfile? Profile = null,
    PlayerCollections? Collections = null,
    IReadOnlyList<PlayerClassJob>? ClassJobs = null,
    FreeCompany? FreeCompany = null,
    IReadOnlyList<PlayerGearSlot>? Gear = null)
{
    /// <summary>
    /// Average item level across all equipped slots that carry an item level, excluding
    /// the soul crystal (slot 12) which has no iLvl in FFXIV. Returns null when no gear
    /// is loaded or no qualifying slot has an iLvl set. Matches the in-game iLvl average.
    /// </summary>
    public int? GearScore
    {
        get
        {
            if (Gear is null) return null;
            var sum = 0;
            var count = 0;
            foreach (var slot in Gear)
            {
                if (slot.SlotIndex == 12) continue;
                if (slot.ItemLevel is not { } ilvl) continue;
                sum += ilvl;
                count++;
            }
            return count == 0 ? null : sum / count;
        }
    }
}
