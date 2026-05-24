using NexusKit.Modules.Lodestone.Models;

namespace NexusKit.Modules.Lodestone.Clients;

public interface ILodestoneClient
{
    Task<CharacterSummary?> GetCharacterAsync(ulong lodestoneId, CancellationToken ct = default);

    Task<CharacterSearchResult?> SearchCharacterAsync(string name, string world, CancellationToken ct = default);

    Task<IReadOnlyList<LodestoneClassJob>?> GetCharacterClassJobsAsync(ulong lodestoneId, CancellationToken ct = default);

    Task<IReadOnlyList<LodestoneAchievementEntry>?> GetCharacterAchievementsAsync(ulong lodestoneId, CancellationToken ct = default);

    Task<LodestoneFreeCompany?> GetFreeCompanyAsync(string freeCompanyLodestoneId, CancellationToken ct = default);

    Task<IReadOnlyList<LodestoneGearSlot>?> GetCharacterGearAsync(ulong lodestoneId, CancellationToken ct = default);

    /// <summary>
    /// Owned mount names scraped from Lodestone in the active plugin culture. The aggregator
    /// pairs these with the FFXIVCollect catalog (same language) to resolve names to IDs
    /// when FFXIVCollect's per-character mount endpoint isn't available for the character.
    /// </summary>
    Task<IReadOnlyList<string>?> GetCharacterMountsAsync(ulong lodestoneId, CancellationToken ct = default);

    /// <inheritdoc cref="GetCharacterMountsAsync"/>
    Task<IReadOnlyList<string>?> GetCharacterMinionsAsync(ulong lodestoneId, CancellationToken ct = default);
}
