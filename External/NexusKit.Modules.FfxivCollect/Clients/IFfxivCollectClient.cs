using NexusKit.Modules.FfxivCollect.Models;

namespace NexusKit.Modules.FfxivCollect.Clients;

public interface IFfxivCollectClient
{
    /// <summary>
    /// Per-character endpoints accept an optional <c>forceLatest</c> flag. When true,
    /// FFXIVCollect's <c>?latest=true</c> query is appended so the upstream server
    /// pulls a fresh Lodestone sync before answering. Use from the refresh-queue
    /// worker path where the user (or the TTL sweep) explicitly asked for fresh
    /// data; leave false for opportunistic reads that are fine with cached values.
    /// </summary>
    Task<Character?> GetCharacterAsync(ulong lodestoneId, bool forceLatest = false, CancellationToken ct = default);
    Task<ListResponse<Mount>?> GetMountsAsync(ulong lodestoneId, bool forceLatest = false, CancellationToken ct = default);
    Task<ListResponse<Minion>?> GetMinionsAsync(ulong lodestoneId, bool forceLatest = false, CancellationToken ct = default);
    Task<ListResponse<Achievement>?> GetAchievementsAsync(ulong lodestoneId, bool forceLatest = false, CancellationToken ct = default);

    Task<ListResponse<Mount>?> GetMountCatalogAsync(CancellationToken ct = default);
    Task<ListResponse<Minion>?> GetMinionCatalogAsync(CancellationToken ct = default);
    Task<ListResponse<Achievement>?> GetAchievementCatalogAsync(CancellationToken ct = default);

    Task<Mount?> GetMountAsync(int id, CancellationToken ct = default);
    Task<Minion?> GetMinionAsync(int id, CancellationToken ct = default);
    Task<Achievement?> GetAchievementAsync(int id, CancellationToken ct = default);
}
