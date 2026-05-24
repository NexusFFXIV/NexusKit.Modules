using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.GameData;
using NexusKit.Modules.ExternalData.Mapping;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.ExternalData.Persistence;
using NexusKit.Modules.Lodestone.Clients;
using NexusKit.Persistence;

namespace NexusKit.Modules.ExternalData.FreeCompanies;

internal sealed class ExternalDataFreeCompanyService : IExternalDataFreeCompanyService
{
    private readonly ILodestoneClient mLodestone;
    private readonly INexusDbContextFactory mDb;
    private readonly IGameDataResolver? mGameDataResolver;
    private readonly ISheetsProvider? mSheets;
    private readonly IGameDataLookups? mLookups;
    private readonly ILogger<ExternalDataFreeCompanyService> mLog;

    public event Action<string>? FreeCompanyChanged;
    public event Action<string>? FreeCompanyAdded;

    public ExternalDataFreeCompanyService(
        ILodestoneClient lodestone,
        INexusDbContextFactory db,
        ILogger<ExternalDataFreeCompanyService> log,
        IGameDataResolver? gameDataResolver = null,
        ISheetsProvider? sheets = null,
        IGameDataLookups? lookups = null)
    {
        mLodestone = lodestone;
        mDb = db;
        mLog = log;
        mGameDataResolver = gameDataResolver;
        mSheets = sheets;
        mLookups = lookups;
    }

    public async Task<IReadOnlyList<FreeCompany>> FindCandidatesByTagAndWorldAsync(
        string tag, uint worldId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tag) || worldId == 0) return Array.Empty<FreeCompany>();
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            // (tag, world_id) is *not* unique — multiple FCs can share both on a
            // single world (e.g. four distinct «Boop!» on Twintania). Return all
            // matches so the UI can render an explicit candidate list with an
            // ambiguity warning. Authoritative resolution stays on
            // external_player_profile.free_company_lodestone_id.
            var rows = await ctx.Set<FreeCompanyEntity>()
                .Where(f => f.Tag == tag && f.WorldId == worldId)
                .ToListAsync(ct).ConfigureAwait(false);
            return await HydrateAsync(ctx, rows, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "FindCandidatesByTagAndWorldAsync failed for tag={Tag} world={World}", tag, worldId);
            return Array.Empty<FreeCompany>();
        }
    }

    public async Task<FreeCompany?> GetAsync(string lodestoneFcId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lodestoneFcId)) return null;

        var src = await mLodestone.GetFreeCompanyAsync(lodestoneFcId, ct).ConfigureAwait(false);
        if (src is not null)
        {
            var lang = mSheets?.CurrentLanguage ?? GameDataClientLanguage.English;
            var model = src.ToModel(mGameDataResolver, lang);
            await PersistAsync(model, ct).ConfigureAwait(false);
            return model;
        }

        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await ctx.Set<FreeCompanyEntity>().FindAsync([lodestoneFcId], ct).ConfigureAwait(false);
            if (row is null) return null;
            var addr = row.EstateAddressId is { } aid
                ? await ctx.Set<EstateAddressEntity>().FindAsync([aid], ct).ConfigureAwait(false)
                : null;
            return row.ToModel(addr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Failed to read free company {Id} from DB fallback", lodestoneFcId);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, FreeCompany>> GetManyCachedAsync(
        IReadOnlyCollection<string> lodestoneIds, CancellationToken ct = default)
    {
        if (lodestoneIds.Count == 0) return new Dictionary<string, FreeCompany>(0);

        // De-dupe + drop empties before the IN-clause; SQLite parameterises
        // each value, so size matters even for a cache read.
        var distinctIds = lodestoneIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinctIds.Count == 0) return new Dictionary<string, FreeCompany>(0);

        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await ctx.Set<FreeCompanyEntity>()
                .Where(f => distinctIds.Contains(f.LodestoneId))
                .ToListAsync(ct).ConfigureAwait(false);
            var hydrated = await HydrateAsync(ctx, rows, ct).ConfigureAwait(false);
            return hydrated.ToDictionary(m => m.LodestoneId, m => m, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "GetManyCachedAsync failed for {Count} ids", distinctIds.Count);
            return new Dictionary<string, FreeCompany>(0);
        }
    }

    /// <summary>
    /// Builds models for a batch of FC rows, batch-loading their
    /// <see cref="EstateAddressEntity"/> by FK to avoid an N+1 query.
    /// </summary>
    private static async Task<IReadOnlyList<FreeCompany>> HydrateAsync(
        DbContext ctx, IReadOnlyList<FreeCompanyEntity> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<FreeCompany>();
        var addrIds = rows
            .Select(r => r.EstateAddressId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var addrById = addrIds.Count == 0
            ? new Dictionary<long, EstateAddressEntity>(0)
            : await ctx.Set<EstateAddressEntity>()
                .Where(a => addrIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, ct).ConfigureAwait(false);

        var result = new List<FreeCompany>(rows.Count);
        foreach (var r in rows)
        {
            EstateAddressEntity? addr = null;
            if (r.EstateAddressId is { } id) addrById.TryGetValue(id, out addr);
            result.Add(r.ToModel(addr));
        }
        return result;
    }

    private async Task PersistAsync(FreeCompany fc, CancellationToken ct)
    {
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            var existing = await ctx.Set<FreeCompanyEntity>().FindAsync([fc.LodestoneId], ct).ConfigureAwait(false);
            EstateAddressEntity? existingAddr = null;
            if (existing?.EstateAddressId is { } existingAddrId)
            {
                existingAddr = await ctx.Set<EstateAddressEntity>().FindAsync([existingAddrId], ct).ConfigureAwait(false);
            }

            // Patch DataCenterId on the incoming address row from the world →
            // we couldn't resolve it inside the (static) mapping helper.
            var incomingAddr = fc.Estate;
            if (incomingAddr is { WorldId: { } wId, DataCenterId: null }
                && mLookups is not null)
            {
                // GetDataCenterNameByWorldId is what's exposed; the lookup is
                // by-row-id which we don't have a direct method for, but the
                // sheets provider gives us the world row's DataCenter.RowId.
                var dcId = TryResolveDcIdFromWorld(wId);
                if (dcId is not null)
                {
                    incomingAddr = incomingAddr with { DataCenterId = dcId };
                }
            }

            var fieldsChanged = false;
            var isNewInsert = existing is null;
            if (existing is null)
            {
                // First-time insert — not a "change" by the user-facing
                // definition; FreeCompanyChanged stays silent on creation
                // (FreeCompanyAdded fires below instead).
                var entity = fc.ToEntity(now);
                if (incomingAddr is not null)
                {
                    var addr = incomingAddr.ToEntity(now);
                    ctx.Set<EstateAddressEntity>().Add(addr);
                    await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                    entity.EstateAddressId = addr.Id;
                }
                ctx.Set<FreeCompanyEntity>().Add(entity);
            }
            else
            {
                fieldsChanged = HasAnyFieldChanged(existing, existingAddr, fc, incomingAddr);
                existing.Name = fc.Name;
                existing.Tag = fc.Tag;
                existing.Slogan = fc.Slogan;
                existing.FormedAt = fc.FormedAt;
                existing.WorldId = fc.WorldId;
                existing.Rank = fc.Rank;
                existing.ActiveMemberCount = fc.ActiveMemberCount;
                existing.ActiveState = fc.ActiveState;
                existing.RecruitmentOpen = fc.RecruitmentOpen;
                existing.GrandCompanyId = fc.GrandCompanyId;
                existing.FocusRolePlay   = fc.Focus?.RolePlay   ?? false;
                existing.FocusLeveling   = fc.Focus?.Leveling   ?? false;
                existing.FocusCasual     = fc.Focus?.Casual     ?? false;
                existing.FocusHardcore   = fc.Focus?.Hardcore   ?? false;
                existing.FocusDungeons   = fc.Focus?.Dungeons   ?? false;
                existing.FocusGuildhests = fc.Focus?.Guildhests ?? false;
                existing.FocusTrials     = fc.Focus?.Trials     ?? false;
                existing.FocusRaids      = fc.Focus?.Raids      ?? false;
                existing.FocusPvP        = fc.Focus?.PvP        ?? false;
                existing.UpdatedAt = now;

                if (incomingAddr is not null)
                {
                    if (existingAddr is null)
                    {
                        // FC previously had no estate row; INSERT one and link.
                        var addr = incomingAddr.ToEntity(now);
                        ctx.Set<EstateAddressEntity>().Add(addr);
                        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                        existing.EstateAddressId = addr.Id;
                    }
                    else
                    {
                        // UPDATE the existing row in place — preserves the id,
                        // keeps the FK stable.
                        incomingAddr.ApplyTo(existingAddr, now);
                    }
                }
                // Note: we deliberately keep the FK + row even if the new scrape
                // has no estate block. A missing scrape doesn't prove the FC sold
                // the house — Lodestone is sometimes partial. Explicit clearing
                // can come as a separate maintenance task.
            }
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

            if (isNewInsert)
            {
                try { FreeCompanyAdded?.Invoke(fc.LodestoneId); }
                catch (Exception ex)
                {
                    mLog.LogWarning(ex,
                        "FreeCompanyAdded subscriber threw for {Id}", fc.LodestoneId);
                }
            }
            else if (fieldsChanged)
            {
                try { FreeCompanyChanged?.Invoke(fc.LodestoneId); }
                catch (Exception ex)
                {
                    mLog.LogWarning(ex,
                        "FreeCompanyChanged subscriber threw for {Id}", fc.LodestoneId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Failed to persist free company {Id}", fc.LodestoneId);
        }
    }

    private uint? TryResolveDcIdFromWorld(uint worldId)
    {
        // mLookups is the abstract surface — we avoid touching Lumina directly
        // so this module stays Dalamud/Lumina-free.
        try { return mLookups?.GetDataCenterIdByWorldId(worldId); }
        catch { return null; }
    }

    /// <summary>Returns true when any persisted column of <paramref name="existing"/>
    /// would differ from the incoming <paramref name="incoming"/> after upsert.
    /// Compares every field we write in the assignment block above —
    /// <see cref="FreeCompanyEntity.UpdatedAt"/> excluded since that timestamp
    /// is touched every upsert regardless of payload.</summary>
    private static bool HasAnyFieldChanged(
        FreeCompanyEntity existing, EstateAddressEntity? existingAddr,
        FreeCompany incoming, EstateAddress? incomingAddr)
    {
        if (!string.Equals(existing.Name, incoming.Name, StringComparison.Ordinal)) return true;
        if (!string.Equals(existing.Tag, incoming.Tag, StringComparison.Ordinal)) return true;
        if (!string.Equals(existing.Slogan, incoming.Slogan, StringComparison.Ordinal)) return true;
        if (existing.FormedAt != incoming.FormedAt) return true;
        if (existing.WorldId != incoming.WorldId) return true;
        if (existing.Rank != incoming.Rank) return true;
        if (existing.ActiveMemberCount != incoming.ActiveMemberCount) return true;
        if (existing.ActiveState != incoming.ActiveState) return true;
        if (existing.RecruitmentOpen != incoming.RecruitmentOpen) return true;
        if (existing.GrandCompanyId != incoming.GrandCompanyId) return true;
        if (existing.FocusRolePlay   != (incoming.Focus?.RolePlay   ?? false)) return true;
        if (existing.FocusLeveling   != (incoming.Focus?.Leveling   ?? false)) return true;
        if (existing.FocusCasual     != (incoming.Focus?.Casual     ?? false)) return true;
        if (existing.FocusHardcore   != (incoming.Focus?.Hardcore   ?? false)) return true;
        if (existing.FocusDungeons   != (incoming.Focus?.Dungeons   ?? false)) return true;
        if (existing.FocusGuildhests != (incoming.Focus?.Guildhests ?? false)) return true;
        if (existing.FocusTrials     != (incoming.Focus?.Trials     ?? false)) return true;
        if (existing.FocusRaids      != (incoming.Focus?.Raids      ?? false)) return true;
        if (existing.FocusPvP        != (incoming.Focus?.PvP        ?? false)) return true;

        // Estate-block diff. Treats null-vs-non-null transitions as changes so a
        // newly-discovered estate fires FreeCompanyChanged just like a renamed
        // FC does.
        if (existingAddr is null && incomingAddr is null) return false;
        if (existingAddr is null || incomingAddr is null) return true;
        if (!string.Equals(existingAddr.Name, incomingAddr.Name, StringComparison.Ordinal)) return true;
        if (!string.Equals(existingAddr.Greeting, incomingAddr.Greeting, StringComparison.Ordinal)) return true;
        if (existingAddr.DataCenterId != incomingAddr.DataCenterId) return true;
        if (existingAddr.WorldId != incomingAddr.WorldId) return true;
        if (existingAddr.DistrictTerritoryId != incomingAddr.DistrictTerritoryId) return true;
        if (existingAddr.Ward != incomingAddr.Ward) return true;
        if (existingAddr.PlotNumber != incomingAddr.PlotNumber) return true;
        if (existingAddr.IsApartment != incomingAddr.IsApartment) return true;
        if (existingAddr.ApartmentWing != incomingAddr.ApartmentWing) return true;
        if (existingAddr.HouseSize != incomingAddr.HouseSize) return true;
        if (existingAddr.IsSubdivision != incomingAddr.IsSubdivision) return true;
        return false;
    }
}
