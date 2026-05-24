using NexusKit.Modules.ExternalData.Models;

namespace NexusKit.Modules.ExternalData.Players;

public interface IExternalDataPlayerService
{
    /// <summary>
    /// Fetches a player by lodestone id. Returns null if no enabled source can supply
    /// the core identity (name/world/datacenter). Sub-resources gated by <paramref name="include"/>;
    /// missing sources silently leave the corresponding fields null.
    /// <para>When <paramref name="forceLatest"/> is true (refresh-queue worker path),
    /// FFXIVCollect calls add <c>?latest=true</c> so the upstream re-syncs from
    /// Lodestone before answering — minimises stale name/world after transfers or
    /// renames. Leave false for opportunistic reads.</para>
    /// </summary>
    Task<Player?> GetAsync(ulong lodestoneId,
                           PlayerInclude include = PlayerInclude.None,
                           bool forceLatest = false,
                           CancellationToken ct = default);

    /// <summary>
    /// Searches lodestone by character name + world. Empty list when no search-capable
    /// source is enabled.
    /// </summary>
    Task<IReadOnlyList<PlayerSearchResult>> SearchAsync(string name, string world,
                                                       CancellationToken ct = default);

    /// <summary>
    /// DB-only lookup by (name, home world id). Returns the minimal identity tuple
    /// (id + name + world id + data center id) when a previously-fetched player
    /// matches, otherwise null. Never touches the network — use this from the
    /// game-object watcher to skip Lodestone searches for characters we've already
    /// enriched.
    /// </summary>
    Task<PlayerIdentity?> GetByNameAsync(string name, uint homeWorldId,
                                         CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent <c>UpdatedAt</c> across every persisted sub-resource
    /// for the given Lodestone id (profile, class jobs, gear, collections, FC link).
    /// Drives the "Updated Xh ago" badge in the detail header so the user can see
    /// at a glance how fresh the Lodestone view is. Returns null when no rows exist
    /// for the id (never fetched, or pre-enrichment).
    /// </summary>
    Task<DateTime?> GetLastRefreshedAtAsync(ulong lodestoneId, CancellationToken ct = default);

    /// <summary>
    /// Returns the per-sub-resource <c>UpdatedAt</c> breakdown so the UI can show
    /// what's actually fresh and what's stale, not just the maximum. Drives the
    /// tooltip on the detail-panel's "Updated Xh ago" line. Cheap indexed lookups
    /// — same shape as <see cref="GetLastRefreshedAtAsync"/> but keeps each
    /// timestamp distinct instead of reducing to a single max.
    /// </summary>
    Task<Models.PlayerRefreshBreakdown> GetRefreshBreakdownAsync(ulong lodestoneId, CancellationToken ct = default);

    /// <summary>
    /// DB-only read of a previously-enriched player. Never touches the network,
    /// never bumps any <c>UpdatedAt</c>. Use this when surfacing cached data in
    /// the UI: selecting a player should show whatever's persisted, and remote
    /// refresh decisions go through PlayerRefreshQueueService's staleness check
    /// (which honors IRefreshTtlProvider). Calling <see cref="GetAsync"/> instead
    /// would silently re-fetch every category and reset all seven timestamps on
    /// every click, regardless of TTL.
    /// </summary>
    Task<Player?> GetCachedAsync(ulong lodestoneId,
                                 PlayerInclude include = PlayerInclude.None,
                                 CancellationToken ct = default);
}
