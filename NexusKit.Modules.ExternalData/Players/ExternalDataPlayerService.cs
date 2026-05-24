using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.GameData;
using NexusKit.Modules.ExternalData.Catalogs;
using NexusKit.Modules.ExternalData.FreeCompanies;
using NexusKit.Modules.ExternalData.Mapping;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.ExternalData.Persistence;
using NexusKit.Modules.FfxivCollect.Clients;
using NexusKit.Modules.Lodestone.Clients;
using NexusKit.Persistence;
using FfxivCollectModels = NexusKit.Modules.FfxivCollect.Models;
using LodestoneModels = NexusKit.Modules.Lodestone.Models;

namespace NexusKit.Modules.ExternalData.Players;

internal sealed class ExternalDataPlayerService : IExternalDataPlayerService
{
    private readonly IFfxivCollectClient mFfxivCollect;
    private readonly ILodestoneClient mLodestone;
    private readonly IExternalDataFreeCompanyService mFreeCompanies;
    private readonly IExternalDataMountCatalog? mMountCatalog;
    private readonly IExternalDataMinionCatalog? mMinionCatalog;
    private readonly IExternalDataAchievementCatalog? mAchievementCatalog;
    private readonly IGameDataResolver? mGameDataResolver;
    private readonly IGameDataLookups? mGameDataLookups;
    private readonly ISheetsProvider? mSheets;
    private readonly INexusDbContextFactory mDb;
    private readonly IPlayerChangeRecorder? mChangeRecorder;
    private readonly ILogger<ExternalDataPlayerService> mLog;

    public ExternalDataPlayerService(
        IFfxivCollectClient ffxivCollect,
        ILodestoneClient lodestone,
        IExternalDataFreeCompanyService freeCompanies,
        INexusDbContextFactory db,
        ILogger<ExternalDataPlayerService> log,
        IExternalDataMountCatalog? mountCatalog = null,
        IExternalDataMinionCatalog? minionCatalog = null,
        IExternalDataAchievementCatalog? achievementCatalog = null,
        IGameDataResolver? gameDataResolver = null,
        IGameDataLookups? gameDataLookups = null,
        ISheetsProvider? sheets = null,
        IPlayerChangeRecorder? changeRecorder = null)
    {
        mFfxivCollect = ffxivCollect;
        mLodestone = lodestone;
        mFreeCompanies = freeCompanies;
        mMountCatalog = mountCatalog;
        mMinionCatalog = minionCatalog;
        mAchievementCatalog = achievementCatalog;
        mGameDataResolver = gameDataResolver;
        mGameDataLookups = gameDataLookups;
        mSheets = sheets;
        mDb = db;
        mChangeRecorder = changeRecorder;
        mLog = log;
    }

    private GameDataClientLanguage CurrentLanguage
        => mSheets?.CurrentLanguage ?? GameDataClientLanguage.English;

    public async Task<Player?> GetAsync(ulong lodestoneId,
                                        PlayerInclude include = PlayerInclude.None,
                                        bool forceLatest = false,
                                        CancellationToken ct = default)
    {
        var characterTask = mFfxivCollect.GetCharacterAsync(lodestoneId, forceLatest, ct);
        var profileTask = (include & PlayerInclude.Profile) != 0
            ? mLodestone.GetCharacterAsync(lodestoneId, ct)
            : Task.FromResult<LodestoneModels.CharacterSummary?>(null);
        var mountsTask = (include & PlayerInclude.Mounts) != 0
            ? mFfxivCollect.GetMountsAsync(lodestoneId, forceLatest, ct)
            : Task.FromResult<FfxivCollectModels.ListResponse<FfxivCollectModels.Mount>?>(null);
        var minionsTask = (include & PlayerInclude.Minions) != 0
            ? mFfxivCollect.GetMinionsAsync(lodestoneId, forceLatest, ct)
            : Task.FromResult<FfxivCollectModels.ListResponse<FfxivCollectModels.Minion>?>(null);
        var achievementsTask = (include & PlayerInclude.Achievements) != 0
            ? mFfxivCollect.GetAchievementsAsync(lodestoneId, forceLatest, ct)
            : Task.FromResult<FfxivCollectModels.ListResponse<FfxivCollectModels.Achievement>?>(null);
        var classJobsTask = (include & PlayerInclude.ClassJobs) != 0
            ? mLodestone.GetCharacterClassJobsAsync(lodestoneId, ct)
            : Task.FromResult<IReadOnlyList<LodestoneModels.LodestoneClassJob>?>(null);
        var lodestoneAchievementsTask = (include & PlayerInclude.Achievements) != 0
            ? mLodestone.GetCharacterAchievementsAsync(lodestoneId, ct)
            : Task.FromResult<IReadOnlyList<LodestoneModels.LodestoneAchievementEntry>?>(null);
        var lodestoneMountsTask = (include & PlayerInclude.Mounts) != 0
            ? mLodestone.GetCharacterMountsAsync(lodestoneId, ct)
            : Task.FromResult<IReadOnlyList<string>?>(null);
        var lodestoneMinionsTask = (include & PlayerInclude.Minions) != 0
            ? mLodestone.GetCharacterMinionsAsync(lodestoneId, ct)
            : Task.FromResult<IReadOnlyList<string>?>(null);
        var gearTask = (include & PlayerInclude.Gear) != 0
            ? mLodestone.GetCharacterGearAsync(lodestoneId, ct)
            : Task.FromResult<IReadOnlyList<LodestoneModels.LodestoneGearSlot>?>(null);

        await Task.WhenAll(characterTask, profileTask, mountsTask, minionsTask, achievementsTask,
            classJobsTask, lodestoneAchievementsTask, lodestoneMountsTask, lodestoneMinionsTask, gearTask).ConfigureAwait(false);

        var character = characterTask.Result;
        var profileSrc = profileTask.Result;
        var mounts = mountsTask.Result;
        var minions = minionsTask.Result;
        var achievements = achievementsTask.Result;
        var classJobsSrc = classJobsTask.Result;
        var lodestoneAchievements = lodestoneAchievementsTask.Result;
        var lodestoneMounts = lodestoneMountsTask.Result;
        var lodestoneMinions = lodestoneMinionsTask.Result;
        var gearSrc = gearTask.Result;

        // Identity resolution: Lodestone is the canonical Square Enix source —
        // it reflects renames and world transfers immediately. FFXIVCollect is
        // a third-party scrape that can lag behind those events by days (the
        // service's own re-sync window). We therefore prefer Lodestone here
        // and only fall back to FFXIVCollect when the Lodestone page didn't
        // come back with usable identity fields.
        // When the caller didn't include PlayerInclude.Profile, profileTask
        // resolved to null — do ONE sequential fetch so identity has a
        // chance to land from Lodestone. Lazy because eagerly fetching every
        // call triggers NetStone's per-IP rate limit on broken pages.
        if (profileSrc is null
            && (character is null
                || string.IsNullOrEmpty(character.Name)
                || string.IsNullOrEmpty(character.Server)))
        {
            profileSrc = await mLodestone.GetCharacterAsync(lodestoneId, ct).ConfigureAwait(false);
        }

        var name = !string.IsNullOrEmpty(profileSrc?.Name) ? profileSrc!.Name
                 : (!string.IsNullOrEmpty(character?.Name) ? character!.Name : null);
        var serverName = !string.IsNullOrEmpty(profileSrc?.Server) ? profileSrc!.Server
                       : (!string.IsNullOrEmpty(character?.Server) ? character!.Server : null);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(serverName))
        {
            return await ReadFromDbAsync(lodestoneId, include, ct).ConfigureAwait(false);
        }

        var lang = CurrentLanguage;
        // Build the profile model only when the caller asked for it — the
        // Lodestone page was fetched above for identity regardless, but
        // persisting profile fields outside of an explicit Profile request
        // would surprise the queue's per-category UpdatedAt bookkeeping.
        var profile = (include & PlayerInclude.Profile) != 0
            ? profileSrc?.ToModel(mGameDataResolver, lang)
            : null;
        var ownedMountIds = ResolveOwnedMountIds(mounts, lodestoneMounts);
        var ownedMinionIds = ResolveOwnedMinionIds(minions, lodestoneMinions);
        var classJobs = classJobsSrc?
            .Select(j => new PlayerClassJob(ResolveClassJobId(j.Name), j.Level))
            .Where(j => j.ClassJobId != 0)
            .ToList();

        // FC fetch needs FreeCompanyLodestoneId, which normally rides along on
        // this tick's profile fetch. The refresh queue, however, processes each
        // category as its own GetAsync call (PlayerInclude.FreeCompany only) —
        // so when the queue gets to the FC row in isolation there's no fresh
        // profile to source the link from. Fall back to the cached profile row
        // so the FC fetch (and its UpdatedAt bump on the catalog row) actually
        // happens. The Profile category is enum-ordered before FreeCompany, so
        // on a full force-refresh the cached link has already been refreshed
        // by the prior tick.
        string? fcLodestoneId = profile?.FreeCompanyLodestoneId;
        if ((include & PlayerInclude.FreeCompany) != 0 && string.IsNullOrEmpty(fcLodestoneId))
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            fcLodestoneId = await ctx.Set<PlayerProfileEntity>()
                .Where(p => p.LodestoneId == lodestoneId)
                .Select(p => p.FreeCompanyLodestoneId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }

        FreeCompany? freeCompany = null;
        if ((include & PlayerInclude.FreeCompany) != 0 && !string.IsNullOrEmpty(fcLodestoneId))
            freeCompany = await mFreeCompanies.GetAsync(fcLodestoneId, ct).ConfigureAwait(false);

        var gear = gearSrc?.Select(g => new PlayerGearSlot(
            SlotIndex: g.SlotIndex,
            ItemId: ResolveItemId(g.ItemName, lang),
            IsHq: g.IsHq,
            GlamourItemId: string.IsNullOrEmpty(g.GlamourName) ? null : ResolveItemId(g.GlamourName, lang),
            Colors: g.Colors,
            Materia: g.Materia,
            CreatorName: g.CreatorName,
            ItemLevel: g.ItemLevel)).ToList();

        // Owned items derive from gear (equipped + glamour items). All future item-source
        // contributors would extend this set; today the only producer is the gear scrape.
        var ownedItemIds = BuildOwnedItemIdsFromGear(gear);
        // Collections build always when an include flag asks for one — even
        // when FFXIVCollect didn't return a character (404 / not indexed).
        // The waterfall inside BuildCollectionsAsync synthesizes per-Kind
        // stats from the Lodestone-derived owned-id counts and the
        // FFXIVCollect catalog totals so a player who only exists on
        // Lodestone still lights up Mounts / Minions in the UI.
        var collections = await BuildCollectionsAsync(
            character, mounts, minions, achievements, lodestoneAchievements,
            ownedMountIds, ownedMinionIds, ownedItemIds, include, ct).ConfigureAwait(false);

        // Resolve identity to Lumina ids. Names are language-invariant brand
        // strings so English-locale lookup is correct regardless of the
        // user's UI language.
        var homeWorldId = mGameDataResolver?.ResolveIdByName(
            serverName, GameDataKind.World, GameDataClientLanguage.English) ?? 0;
        uint dataCenterId = 0;
        if (!string.IsNullOrEmpty(character?.DataCenter))
            dataCenterId = mGameDataResolver?.ResolveIdByName(
                character!.DataCenter, GameDataKind.DataCenter, GameDataClientLanguage.English) ?? 0;
        // When FFXIVCollect didn't supply a DC name, derive it from the
        // home-world id via Lumina (sheet lookup, no network).
        if (dataCenterId == 0 && homeWorldId != 0 && mGameDataLookups is not null)
        {
            var dcName = mGameDataLookups.GetDataCenterNameByWorldId(homeWorldId);
            if (!string.IsNullOrEmpty(dcName))
                dataCenterId = mGameDataResolver?.ResolveIdByName(
                    dcName, GameDataKind.DataCenter, GameDataClientLanguage.English) ?? 0;
        }

        var player = new Player(
            LodestoneId: lodestoneId,
            Name: name,
            HomeWorldId: homeWorldId,
            DataCenterId: dataCenterId,
            Profile: profile,
            Collections: collections,
            ClassJobs: classJobs,
            FreeCompany: freeCompany,
            Gear: gear);

        await PersistAsync(player, include, ct).ConfigureAwait(false);
        return player;
    }

    public async Task<IReadOnlyList<PlayerSearchResult>> SearchAsync(string name, string world, CancellationToken ct = default)
    {
        var result = await mLodestone.SearchCharacterAsync(name, world, ct).ConfigureAwait(false);
        if (result is null) return Array.Empty<PlayerSearchResult>();

        return result.Results
            .Select(r => new PlayerSearchResult(r.LodestoneId, r.Name, r.Server ?? string.Empty))
            .ToList();
    }

    public async Task<PlayerIdentity?> GetByNameAsync(string name, uint homeWorldId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || homeWorldId == 0)
            return null;

        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await ctx.Set<PlayerEntity>()
                .Where(p => p.Name == name && p.HomeWorldId == homeWorldId)
                .Select(p => new { p.LodestoneId, p.Name, p.HomeWorldId, p.DataCenterId })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return row is null
                ? null
                : new PlayerIdentity(row.LodestoneId, row.Name, row.HomeWorldId, row.DataCenterId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "GetByNameAsync failed for {Name}@{World}", name, homeWorldId);
            return null;
        }
    }

    public async Task<DateTime?> GetLastRefreshedAtAsync(ulong lodestoneId, CancellationToken ct = default)
    {
        if (lodestoneId == 0) return null;
        try
        {
            var b = await GetRefreshBreakdownAsync(lodestoneId, ct).ConfigureAwait(false);
            DateTime? max = null;
            foreach (var v in new[] { b.ProfileAt, b.ClassJobsAt, b.GearAt, b.FreeCompanyAt,
                                       b.MountsAt, b.MinionsAt, b.AchievementsAt })
                if (v is { } d && (max is null || d > max)) max = d;
            return max;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "GetLastRefreshedAtAsync failed for {Lid}", lodestoneId);
            return null;
        }
    }

    public async Task<PlayerRefreshBreakdown> GetRefreshBreakdownAsync(ulong lodestoneId, CancellationToken ct = default)
    {
        if (lodestoneId == 0) return Empty;
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);

            // Each query is a tiny indexed lookup keyed on LodestoneId; running them
            // sequentially keeps the LINQ translation simple and avoids a UNION across
            // heterogeneous tables. The handful of roundtrips fits inside one Select()
            // tick and is cheap enough that the tooltip stays snappy.
            //
            // The `UpdatedAt > MinValue` clause normalises rows that landed with a
            // default DateTime (0001-01-01) — those exist in older DBs where a write
            // path skipped stamping the column. Without it the tooltip rendered "2026J"
            // (FormatRelativeTime computing ~2026 years since year 0001) instead of
            // the intended "—" for "no real data yet". The text/ISO column ordering
            // collapses to a string compare server-side, which works because
            // "0001-..." sorts before any plausible real timestamp.
            var profileAt = await ctx.Set<PlayerProfileEntity>()
                .Where(p => p.LodestoneId == lodestoneId && p.UpdatedAt > DateTime.MinValue)
                .Select(p => (DateTime?)p.UpdatedAt)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            var classJobsAt = await ctx.Set<PlayerClassJobEntity>()
                .Where(j => j.LodestoneId == lodestoneId && j.UpdatedAt > DateTime.MinValue)
                .Select(j => (DateTime?)j.UpdatedAt)
                .OrderByDescending(d => d)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            var gearAt = await ctx.Set<PlayerGearSlotEntity>()
                .Where(g => g.LodestoneId == lodestoneId && g.UpdatedAt > DateTime.MinValue)
                .Select(g => (DateTime?)g.UpdatedAt)
                .OrderByDescending(d => d)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            // FC link is on the profile; resolve to the FC catalog row's UpdatedAt
            // so the tooltip can distinguish "we have no FC link" (FreeCompanyAt
            // stays null) from "the link is fresh but the catalog row is stale".
            var fcLodestoneId = await ctx.Set<PlayerProfileEntity>()
                .Where(p => p.LodestoneId == lodestoneId)
                .Select(p => p.FreeCompanyLodestoneId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            DateTime? fcAt = null;
            if (!string.IsNullOrEmpty(fcLodestoneId))
            {
                fcAt = await ctx.Set<FreeCompanyEntity>()
                    .Where(f => f.LodestoneId == fcLodestoneId && f.UpdatedAt > DateTime.MinValue)
                    .Select(f => (DateTime?)f.UpdatedAt)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            }

            var mountsAt = await CollectionAtAsync(ctx, lodestoneId, CollectionKind.Mounts, ct).ConfigureAwait(false);
            var minionsAt = await CollectionAtAsync(ctx, lodestoneId, CollectionKind.Minions, ct).ConfigureAwait(false);
            var achievementsAt = await CollectionAtAsync(ctx, lodestoneId, CollectionKind.Achievements, ct).ConfigureAwait(false);

            return new PlayerRefreshBreakdown(profileAt, classJobsAt, gearAt, fcAt,
                                              mountsAt, minionsAt, achievementsAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "GetRefreshBreakdownAsync failed for {Lid}", lodestoneId);
            return Empty;
        }
    }

    private static readonly PlayerRefreshBreakdown Empty =
        new(null, null, null, null, null, null, null);

    private static Task<DateTime?> CollectionAtAsync(
        PluginDbContext ctx, ulong lodestoneId, CollectionKind kind, CancellationToken ct)
        => ctx.Set<PlayerCollectionStatsEntity>()
            .Where(s => s.LodestoneId == lodestoneId && s.Kind == kind
                        && s.UpdatedAt > DateTime.MinValue)
            .Select(s => (DateTime?)s.UpdatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Build the per-player collection bundle (Mounts / Minions / Achievements
    /// stats and owned-id lists). Waterfall per-Kind: FFXIVCollect Character
    /// endpoint first for Stats (Count/Total/Ranking/IsPublic in one shot),
    /// fallback to <c>(Count: ownedIds.Count, Total: catalog.Count, Ranking:
    /// null, IsPublic: null)</c> when FFXIVCollect didn't index the player.
    /// The fallback's Total comes from the FFXIVCollect catalog services
    /// (Plugin-Startup load), Count from the owned-id list whose own
    /// waterfall already prefers FFXIVCollect's per-character endpoint and
    /// falls back to Lodestone names via Lumina (see <see cref="ResolveOwnedIds"/>).
    /// </summary>
    private async Task<PlayerCollections?> BuildCollectionsAsync(
        FfxivCollectModels.Character? character,
        FfxivCollectModels.ListResponse<FfxivCollectModels.Mount>? mounts,
        FfxivCollectModels.ListResponse<FfxivCollectModels.Minion>? minions,
        FfxivCollectModels.ListResponse<FfxivCollectModels.Achievement>? achievements,
        IReadOnlyList<LodestoneModels.LodestoneAchievementEntry>? lodestoneAchievements,
        IReadOnlyList<int> ownedMountIds,
        IReadOnlyList<int> ownedMinionIds,
        IReadOnlyList<int> ownedItemIds,
        PlayerInclude include,
        CancellationToken ct)
    {
        if ((include & (PlayerInclude.Mounts | PlayerInclude.Minions | PlayerInclude.Achievements | PlayerInclude.Items)) == 0)
            return null;

        var ownedAchievements = BuildOwnedAchievements(achievements, lodestoneAchievements);

        PlayerCollectionStats? mountStats = null;
        if ((include & PlayerInclude.Mounts) != 0)
            mountStats = await ResolveStatsAsync(
                character?.Mounts, ownedMountIds.Count,
                mMountCatalog is null ? null : async c => (await mMountCatalog.ListAsync(c).ConfigureAwait(false)).Count,
                "Mount", ct).ConfigureAwait(false);

        PlayerCollectionStats? minionStats = null;
        if ((include & PlayerInclude.Minions) != 0)
            minionStats = await ResolveStatsAsync(
                character?.Minions, ownedMinionIds.Count,
                mMinionCatalog is null ? null : async c => (await mMinionCatalog.ListAsync(c).ConfigureAwait(false)).Count,
                "Minion", ct).ConfigureAwait(false);

        PlayerCollectionStats? achievementStats = null;
        if ((include & PlayerInclude.Achievements) != 0)
            achievementStats = await ResolveStatsAsync(
                character?.Achievements, ownedAchievements.Count,
                mAchievementCatalog is null ? null : async c => (await mAchievementCatalog.ListAsync(c).ConfigureAwait(false)).Count,
                "Achievement", ct).ConfigureAwait(false);

        return new PlayerCollections(
            Mounts: mountStats,
            Minions: minionStats,
            Achievements: achievementStats,
            OwnedMountIds: ownedMountIds,
            OwnedMinionIds: ownedMinionIds,
            OwnedAchievements: ownedAchievements,
            OwnedItemIds: ownedItemIds);
    }

    /// <summary>Stats waterfall helper. Returns FFXIVCollect's pre-canned
    /// CategorySummary when available (gives Count/Total/Ranking/IsPublic in
    /// one shot); otherwise synthesizes from the owned-id count and the
    /// FFXIVCollect catalog total. Returns null only when both sources have
    /// nothing to offer — the caller treats that as "no data for this Kind".</summary>
    private async Task<PlayerCollectionStats?> ResolveStatsAsync(
        FfxivCollectModels.CategorySummary? ffxiv,
        int ownedCount,
        Func<CancellationToken, Task<int>>? catalogTotal,
        string kindForLog,
        CancellationToken ct)
    {
        if (ffxiv is not null) return ffxiv.ToModel();
        var total = 0;
        if (catalogTotal is not null)
        {
            try { total = await catalogTotal(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                mLog.LogDebug(ex, "{Kind} catalog list failed during stats fallback", kindForLog);
            }
        }
        if (ownedCount == 0 && total == 0) return null;
        return new PlayerCollectionStats(ownedCount, total, Ranking: null, IsPublic: null);
    }

    /// <summary>Derive owned items from gear: every equipped item plus every glamour
    /// item the player has applied. Dedupe by RowId so two ring slots holding the same
    /// item produce one ownership row.</summary>
    private static IReadOnlyList<int> BuildOwnedItemIdsFromGear(IReadOnlyList<PlayerGearSlot>? gear)
    {
        if (gear is null) return Array.Empty<int>();

        var seen = new HashSet<int>();
        var result = new List<int>();
        foreach (var slot in gear)
        {
            AddId((int)slot.ItemId);
            if (slot.GlamourItemId is { } gid) AddId((int)gid);
        }
        return result;

        void AddId(int id)
        {
            if (id <= 0) return;
            if (seen.Add(id)) result.Add(id);
        }
    }

    /// <summary>Prefer FFXIVCollect's per-character mount list when available (gives owned
    /// IDs directly); fall back to the Lodestone name scrape resolved via Lumina sheets
    /// through <see cref="IGameDataResolver"/>. Both paths produce the same canonical
    /// Lumina RowIds — FFXIVCollect's IDs are Lumina IDs — so the two are
    /// interchangeable on the persistence side.</summary>
    private IReadOnlyList<int> ResolveOwnedMountIds(
        FfxivCollectModels.ListResponse<FfxivCollectModels.Mount>? ffxivCollect,
        IReadOnlyList<string>? lodestoneNames)
        => ResolveOwnedIds(ffxivCollect?.Results, m => m.Id, m => m.Owned,
            lodestoneNames, GameDataKind.Mount);

    private IReadOnlyList<int> ResolveOwnedMinionIds(
        FfxivCollectModels.ListResponse<FfxivCollectModels.Minion>? ffxivCollect,
        IReadOnlyList<string>? lodestoneNames)
        => ResolveOwnedIds(ffxivCollect?.Results, m => m.Id, m => m.Owned,
            lodestoneNames, GameDataKind.Minion);

    private IReadOnlyList<int> ResolveOwnedIds<T>(
        IEnumerable<T>? ffxivCollectItems,
        Func<T, int> id,
        Func<T, string?> ownedMarker,
        IReadOnlyList<string>? lodestoneNames,
        GameDataKind kind)
    {
        if (ffxivCollectItems is not null)
        {
            // Materialize once: if non-empty we use it, if empty we still consider the
            // FFXIVCollect endpoint authoritative and skip the Lodestone fallback.
            var owned = CollectOwnedIdsByOwnedFlag(ffxivCollectItems, id, ownedMarker);
            if (owned.Count > 0) return owned;
        }

        // FFXIVCollect didn't serve the character (404 typical for non-indexed players).
        // Resolve Lodestone-scraped names through Lumina — needs GameData registered.
        if (lodestoneNames is { Count: > 0 } && mGameDataResolver is not null)
        {
            var language = CurrentLanguage;
            var seen = new HashSet<int>();
            var result = new List<int>();
            foreach (var name in lodestoneNames)
            {
                if (mGameDataResolver.ResolveIdByName(name, kind, language) is not { } rowId) continue;
                if (seen.Add((int)rowId)) result.Add((int)rowId);
            }
            return result;
        }

        return Array.Empty<int>();
    }

    private static IReadOnlyList<int> CollectOwnedIdsByOwnedFlag<T>(
        IEnumerable<T> items, Func<T, int> id, Func<T, string?> owned)
    {
        var seen = new HashSet<int>();
        var result = new List<int>();
        foreach (var item in items)
        {
            if (!PlayerMappings.IsOwned(owned(item))) continue;
            var i = id(item);
            if (seen.Add(i)) result.Add(i);
        }
        return result;
    }

    /// <summary>
    /// Prefer Lodestone (carries per-achievement timestamps). Fall back to FFXIVCollect
    /// IDs only when Lodestone is disabled / unavailable.
    /// </summary>
    private static IReadOnlyList<OwnedAchievement> BuildOwnedAchievements(
        FfxivCollectModels.ListResponse<FfxivCollectModels.Achievement>? ffxivCollect,
        IReadOnlyList<LodestoneModels.LodestoneAchievementEntry>? lodestone)
    {
        if (lodestone is { Count: > 0 })
            return lodestone.Select(a => new OwnedAchievement((int)a.Id, a.AchievedAt)).ToList();
        if (ffxivCollect?.Results is { Count: > 0 } items)
            return items.Where(a => PlayerMappings.IsOwned(a.Owned))
                        .Select(a => new OwnedAchievement(a.Id, null)).ToList();
        return Array.Empty<OwnedAchievement>();
    }

    private uint ResolveItemId(string? name, GameDataClientLanguage lang)
    {
        if (string.IsNullOrWhiteSpace(name) || mGameDataResolver is null) return 0;
        return mGameDataResolver.ResolveIdByName(name, GameDataKind.Item, lang) ?? 0;
    }

    /// <summary>NetStone exposes class-job levels via C# property names like
    /// <c>WhiteMage</c> / <c>BlueMage</c> / <c>Paladin</c>. Those match Lumina's
    /// English ClassJob.Name with spaces removed, so we let the resolver's
    /// normalized-name lookup do the matching against the English ClassJob sheet.</summary>
    private uint ResolveClassJobId(string netStoneName)
    {
        if (string.IsNullOrWhiteSpace(netStoneName) || mGameDataResolver is null) return 0;
        return mGameDataResolver.ResolveIdByNormalizedName(
            netStoneName, GameDataKind.ClassJob, GameDataClientLanguage.English) ?? 0;
    }

    private async Task PersistAsync(Player player, PlayerInclude include, CancellationToken ct)
    {
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var now = DateTime.UtcNow;

            await UpsertCoreAsync(ctx, player, now, ct).ConfigureAwait(false);
            if (player.Profile is not null)
                await UpsertProfileAsync(ctx, player.LodestoneId, player.Profile, now, ct).ConfigureAwait(false);
            if (player.Collections is not null)
                await UpsertCollectionsAsync(ctx, player.LodestoneId, player.Collections, include, now, ct).ConfigureAwait(false);
            if (player.ClassJobs is not null)
                await UpsertClassJobsAsync(ctx, player.LodestoneId, player.ClassJobs, now, ct).ConfigureAwait(false);
            if (player.Gear is not null)
                await UpsertGearAsync(ctx, player.LodestoneId, player.Gear, now, ct).ConfigureAwait(false);

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Failed to persist player {LodestoneId}", player.LodestoneId);
        }
    }

    private async Task UpsertCoreAsync(PluginDbContext ctx, Player p, DateTime now, CancellationToken ct)
    {
        var existing = await ctx.Set<PlayerEntity>().FindAsync([p.LodestoneId], ct).ConfigureAwait(false);
        if (existing is null)
        {
            ctx.Set<PlayerEntity>().Add(new PlayerEntity
            {
                LodestoneId = p.LodestoneId,
                Name = p.Name,
                HomeWorldId = p.HomeWorldId,
                DataCenterId = p.DataCenterId,
                UpdatedAt = now,
            });
            return;
        }

        // Emit change notifications BEFORE mutating the row so subscribers see
        // the pre-change state via the entity if they peek. The recorder is
        // fire-and-forget (returns Task but we don't await) — its own dedup
        // suppresses duplicates against history rows the live-watcher path
        // already wrote.
        if (mChangeRecorder is not null)
        {
            if (!string.Equals(existing.Name, p.Name, StringComparison.Ordinal))
                _ = mChangeRecorder.RecordPlayerChangeAsync(
                    p.LodestoneId, PlayerChangeKind.Name, existing.Name, p.Name, now, ct);

            if (existing.HomeWorldId != p.HomeWorldId)
            {
                // Use world *names* (not ids) so dedup matches the live-watcher
                // path, which writes resolved display strings into history.
                var oldName = mGameDataLookups?.GetWorldName(existing.HomeWorldId)
                              ?? existing.HomeWorldId.ToString();
                var newName = mGameDataLookups?.GetWorldName(p.HomeWorldId)
                              ?? p.HomeWorldId.ToString();
                _ = mChangeRecorder.RecordPlayerChangeAsync(
                    p.LodestoneId, PlayerChangeKind.HomeWorld, oldName, newName, now, ct);
            }
        }

        existing.Name = p.Name;
        existing.HomeWorldId = p.HomeWorldId;
        existing.DataCenterId = p.DataCenterId;
        existing.UpdatedAt = now;
    }

    private async Task UpsertProfileAsync(PluginDbContext ctx, ulong lodestoneId, PlayerProfile profile, DateTime now, CancellationToken ct)
    {
        var existing = await ctx.Set<PlayerProfileEntity>().FindAsync([lodestoneId], ct).ConfigureAwait(false);
        if (existing is null)
        {
            ctx.Set<PlayerProfileEntity>().Add(profile.ToEntity(lodestoneId, now));
        }
        else
        {
            // FC link diff: pure id-vs-id compare. The history log carries FC
            // Lodestone ids directly — UI resolves them to names on render.
            // Treat null and empty the same (the recorder's NormaliseForCompare
            // does the same on its side, but normalising here lets us skip the
            // call entirely on no-op transitions).
            if (mChangeRecorder is not null)
            {
                var prevFc = string.IsNullOrEmpty(existing.FreeCompanyLodestoneId) ? null : existing.FreeCompanyLodestoneId;
                var nextFc = string.IsNullOrEmpty(profile.FreeCompanyLodestoneId) ? null : profile.FreeCompanyLodestoneId;
                if (!string.Equals(prevFc, nextFc, StringComparison.Ordinal))
                {
                    // Pre-warm the FC catalog before the change recorder fires
                    // so downstream notifications (HistoryNotificationProducer
                    // + the FC-history catch-all) can render the new FC's
                    // «TAG» Name instead of falling back to "FC#<id>".
                    // Without this, a Profile-only refresh tick from the queue
                    // writes the FC-change history row before the separate
                    // FreeCompany category tick (≥1 min later) persists the
                    // catalog row — so the chat line names "FC#xxx" and the
                    // catch-all "FC was added" lands later as a stranded
                    // follow-up. Awaited so the FC entity is committed to
                    // the cache before the (still fire-and-forget) recorder
                    // task gets to insert the history row.
                    if (!string.IsNullOrEmpty(nextFc))
                    {
                        try { await mFreeCompanies.GetAsync(nextFc, ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            mLog.LogDebug(ex,
                                "FC pre-warm fetch failed for {Id} during FC link change for {Lid}",
                                nextFc, lodestoneId);
                        }
                    }

                    _ = mChangeRecorder.RecordPlayerChangeAsync(
                        lodestoneId, PlayerChangeKind.FreeCompany, prevFc, nextFc, now, ct);
                }
            }

            existing.Bio = profile.Bio;
            existing.Nameday = profile.Nameday;
            existing.GuardianDeity = profile.GuardianDeity;
            existing.GuardianDeityIconUrl = profile.GuardianDeityIconUrl;
            existing.StartingCity = profile.StartingCity;
            existing.StartingCityIconUrl = profile.StartingCityIconUrl;
            existing.AvatarUrl = profile.AvatarUrl;
            existing.PortraitUrl = profile.PortraitUrl;
            existing.FreeCompanyLodestoneId = profile.FreeCompanyLodestoneId;
            existing.PvpTeamLodestoneId = profile.PvpTeamLodestoneId;
            existing.PvpTeamName = profile.PvpTeamName;
            existing.GrandCompanyId = profile.GrandCompanyId;
            existing.GrandCompanyRankId = profile.GrandCompanyRankId;
            existing.GrandCompanyRankIsFeminine = profile.GrandCompanyRankIsFeminine;
            existing.UpdatedAt = now;
        }
    }

    private static async Task UpsertCollectionsAsync(PluginDbContext ctx, ulong lodestoneId, PlayerCollections c, PlayerInclude include, DateTime now, CancellationToken ct)
    {
        if ((include & PlayerInclude.Mounts) != 0)
            await UpsertCollectionAsync(ctx, lodestoneId, CollectionKind.Mounts, c.Mounts,
                c.OwnedMountIds.Select(id => new OwnedEntry(id, null)).ToList(), now, ct).ConfigureAwait(false);
        if ((include & PlayerInclude.Minions) != 0)
            await UpsertCollectionAsync(ctx, lodestoneId, CollectionKind.Minions, c.Minions,
                c.OwnedMinionIds.Select(id => new OwnedEntry(id, null)).ToList(), now, ct).ConfigureAwait(false);
        if ((include & PlayerInclude.Achievements) != 0)
            await UpsertCollectionAsync(ctx, lodestoneId, CollectionKind.Achievements, c.Achievements,
                c.OwnedAchievements.Select(a => new OwnedEntry(a.Id, a.AchievedAt)).ToList(), now, ct).ConfigureAwait(false);
    }

    private readonly record struct OwnedEntry(int Id, DateTime? AchievedAt);

    private static async Task UpsertCollectionAsync(PluginDbContext ctx, ulong lodestoneId, CollectionKind kind, PlayerCollectionStats? stats, IReadOnlyList<OwnedEntry> ownedEntries, DateTime now, CancellationToken ct)
    {
        if (stats is not null)
        {
            var existing = await ctx.Set<PlayerCollectionStatsEntity>().FindAsync([lodestoneId, kind], ct).ConfigureAwait(false);
            if (existing is null)
            {
                ctx.Set<PlayerCollectionStatsEntity>().Add(stats.ToEntity(lodestoneId, kind, now));
            }
            else
            {
                existing.Count = stats.Count;
                existing.Total = stats.Total;
                existing.Ranking = stats.Ranking;
                existing.IsPublic = stats.IsPublic;
                existing.UpdatedAt = now;
            }
        }

        var current = await ctx.Set<PlayerOwnedEntryEntity>()
            .Where(o => o.LodestoneId == lodestoneId && o.Kind == kind)
            .ToListAsync(ct).ConfigureAwait(false);

        // Achievements are the only kind whose owned-entries carry a meaningful
        // AchievedAt (only Lodestone supplies it). When the new fetch came
        // through FFXIVCollect-fallback (no timestamps) the incoming AchievedAt
        // is null; replacing the row outright would nuke the timestamp the
        // earlier Lodestone fetch had landed. Index the existing rows by
        // EntryId once so a later sync that DOES carry Lodestone can backfill
        // missing timestamps without losing what's already there.
        var existingByEntryId = current.ToDictionary(o => o.EntryId);
        ctx.Set<PlayerOwnedEntryEntity>().RemoveRange(current);
        foreach (var entry in ownedEntries)
        {
            var achievedAt = entry.AchievedAt;
            if (achievedAt is null
                && existingByEntryId.TryGetValue(entry.Id, out var prior)
                && prior.AchievedAt is { } priorAt)
            {
                achievedAt = priorAt;
            }
            ctx.Set<PlayerOwnedEntryEntity>().Add(new PlayerOwnedEntryEntity
            {
                LodestoneId = lodestoneId,
                Kind = kind,
                EntryId = entry.Id,
                AchievedAt = achievedAt,
            });
        }
    }

    private static async Task UpsertGearAsync(PluginDbContext ctx, ulong lodestoneId, IReadOnlyList<PlayerGearSlot> slots, DateTime now, CancellationToken ct)
    {
        var current = await ctx.Set<PlayerGearSlotEntity>()
            .Where(g => g.LodestoneId == lodestoneId)
            .ToListAsync(ct).ConfigureAwait(false);
        ctx.Set<PlayerGearSlotEntity>().RemoveRange(current);
        foreach (var slot in slots)
        {
            ctx.Set<PlayerGearSlotEntity>().Add(new PlayerGearSlotEntity
            {
                LodestoneId = lodestoneId,
                SlotIndex = slot.SlotIndex,
                ItemId = slot.ItemId,
                IsHq = slot.IsHq,
                GlamourItemId = slot.GlamourItemId,
                ColorsJson = slot.Colors.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(slot.Colors),
                MateriaJson = slot.Materia.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(slot.Materia),
                CreatorName = slot.CreatorName,
                ItemLevel = slot.ItemLevel,
                UpdatedAt = now,
            });
        }

        // Gear items also feed player_owned (kind=Items): each equipped item + glamour
        // represents proof of ownership. Dedupe by ItemId first.
        await UpsertOwnedItemsFromGearAsync(ctx, lodestoneId, slots, ct).ConfigureAwait(false);
    }

    private static async Task UpsertOwnedItemsFromGearAsync(PluginDbContext ctx, ulong lodestoneId, IReadOnlyList<PlayerGearSlot> slots, CancellationToken ct)
    {
        var seen = new HashSet<int>();
        var entries = new List<int>();
        foreach (var slot in slots)
        {
            AddId((int)slot.ItemId);
            if (slot.GlamourItemId is { } gid) AddId((int)gid);
        }

        var existing = await ctx.Set<PlayerOwnedEntryEntity>()
            .Where(o => o.LodestoneId == lodestoneId && o.Kind == CollectionKind.Items)
            .ToListAsync(ct).ConfigureAwait(false);
        ctx.Set<PlayerOwnedEntryEntity>().RemoveRange(existing);

        foreach (var id in entries)
        {
            ctx.Set<PlayerOwnedEntryEntity>().Add(new PlayerOwnedEntryEntity
            {
                LodestoneId = lodestoneId,
                Kind = CollectionKind.Items,
                EntryId = id,
            });
        }

        void AddId(int id)
        {
            if (id <= 0) return;
            if (seen.Add(id)) entries.Add(id);
        }
    }

    private static async Task UpsertClassJobsAsync(PluginDbContext ctx, ulong lodestoneId, IReadOnlyList<PlayerClassJob> jobs, DateTime now, CancellationToken ct)
    {
        var current = await ctx.Set<PlayerClassJobEntity>()
            .Where(j => j.LodestoneId == lodestoneId)
            .ToListAsync(ct).ConfigureAwait(false);
        ctx.Set<PlayerClassJobEntity>().RemoveRange(current);
        foreach (var job in jobs)
        {
            if (job.ClassJobId == 0) continue;
            ctx.Set<PlayerClassJobEntity>().Add(new PlayerClassJobEntity
            {
                LodestoneId = lodestoneId,
                ClassJobId = job.ClassJobId,
                Level = job.Level,
                UpdatedAt = now,
            });
        }
    }

    private static IReadOnlyList<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<string>();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public Task<Player?> GetCachedAsync(ulong lodestoneId, PlayerInclude include = PlayerInclude.None, CancellationToken ct = default)
        => ReadFromDbAsync(lodestoneId, include, ct);

    private async Task<Player?> ReadFromDbAsync(ulong lodestoneId, PlayerInclude include, CancellationToken ct)
    {
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);

            var core = await ctx.Set<PlayerEntity>().FindAsync([lodestoneId], ct).ConfigureAwait(false);
            if (core is null) return null;

            PlayerProfile? profile = null;
            if ((include & PlayerInclude.Profile) != 0)
            {
                var pe = await ctx.Set<PlayerProfileEntity>().FindAsync([lodestoneId], ct).ConfigureAwait(false);
                if (pe is not null) profile = pe.ToModel();
            }

            PlayerCollections? collections = null;
            if ((include & (PlayerInclude.Mounts | PlayerInclude.Minions | PlayerInclude.Achievements | PlayerInclude.Items)) != 0)
            {
                var stats = await ctx.Set<PlayerCollectionStatsEntity>()
                    .Where(s => s.LodestoneId == lodestoneId)
                    .ToDictionaryAsync(s => s.Kind, ct).ConfigureAwait(false);
                var owned = await ctx.Set<PlayerOwnedEntryEntity>()
                    .Where(o => o.LodestoneId == lodestoneId)
                    .ToListAsync(ct).ConfigureAwait(false);

                collections = new PlayerCollections(
                    Mounts: stats.TryGetValue(CollectionKind.Mounts, out var ms) ? ms.ToModel() : null,
                    Minions: stats.TryGetValue(CollectionKind.Minions, out var mn) ? mn.ToModel() : null,
                    Achievements: stats.TryGetValue(CollectionKind.Achievements, out var ac) ? ac.ToModel() : null,
                    OwnedMountIds: owned.Where(o => o.Kind == CollectionKind.Mounts).Select(o => o.EntryId).ToList(),
                    OwnedMinionIds: owned.Where(o => o.Kind == CollectionKind.Minions).Select(o => o.EntryId).ToList(),
                    OwnedAchievements: owned.Where(o => o.Kind == CollectionKind.Achievements)
                        .Select(o => new OwnedAchievement(o.EntryId, o.AchievedAt)).ToList(),
                    OwnedItemIds: owned.Where(o => o.Kind == CollectionKind.Items)
                        .Select(o => o.EntryId).ToList());
            }

            IReadOnlyList<PlayerClassJob>? classJobs = null;
            if ((include & PlayerInclude.ClassJobs) != 0)
            {
                var jobRows = await ctx.Set<PlayerClassJobEntity>()
                    .Where(j => j.LodestoneId == lodestoneId)
                    .ToListAsync(ct).ConfigureAwait(false);
                if (jobRows.Count > 0)
                    classJobs = jobRows.Select(j => new PlayerClassJob(j.ClassJobId, j.Level)).ToList();
            }

            FreeCompany? freeCompany = null;
            if ((include & PlayerInclude.FreeCompany) != 0 && profile?.FreeCompanyLodestoneId is { } fcId)
            {
                var fcRow = await ctx.Set<FreeCompanyEntity>().FindAsync([fcId], ct).ConfigureAwait(false);
                if (fcRow is not null)
                {
                    EstateAddressEntity? estate = null;
                    if (fcRow.EstateAddressId is { } aid)
                        estate = await ctx.Set<EstateAddressEntity>().FindAsync([aid], ct).ConfigureAwait(false);
                    freeCompany = fcRow.ToModel(estate);
                }
            }

            IReadOnlyList<PlayerGearSlot>? gear = null;
            if ((include & PlayerInclude.Gear) != 0)
            {
                var gearRows = await ctx.Set<PlayerGearSlotEntity>()
                    .Where(g => g.LodestoneId == lodestoneId)
                    .OrderBy(g => g.SlotIndex)
                    .ToListAsync(ct).ConfigureAwait(false);
                if (gearRows.Count > 0)
                    gear = gearRows.Select(g => new PlayerGearSlot(
                        g.SlotIndex, g.ItemId, g.IsHq, g.GlamourItemId,
                        DeserializeStringList(g.ColorsJson),
                        DeserializeStringList(g.MateriaJson),
                        g.CreatorName, g.ItemLevel)).ToList();
            }

            return new Player(core.LodestoneId, core.Name, core.HomeWorldId, core.DataCenterId, profile, collections, classJobs, freeCompany, gear);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Failed to read player {LodestoneId} from DB fallback", lodestoneId);
            return null;
        }
    }
}