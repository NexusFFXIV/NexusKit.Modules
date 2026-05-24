using NexusKit.GameData;
using FfxivCollectModels = NexusKit.Modules.FfxivCollect.Models;
using LodestoneModels = NexusKit.Modules.Lodestone.Models;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.ExternalData.Persistence;

namespace NexusKit.Modules.ExternalData.Mapping;

internal static class PlayerMappings
{
    public static PlayerProfile ToModel(this LodestoneModels.CharacterSummary src,
        IGameDataResolver? resolver, GameDataClientLanguage lang)
    {
        var rank = ResolveRank(resolver, src.GrandCompanyRank, lang);
        return new(
            Bio: src.Bio,
            Nameday: src.Nameday,
            GuardianDeity: src.GuardianDeityName,
            GuardianDeityIconUrl: src.GuardianDeityIconUrl,
            StartingCity: src.StartingCityName,
            StartingCityIconUrl: src.StartingCityIconUrl,
            AvatarUrl: src.AvatarUrl,
            PortraitUrl: src.PortraitUrl,
            FreeCompanyLodestoneId: src.FreeCompanyLodestoneId,
            PvpTeamLodestoneId: src.PvpTeamLodestoneId,
            PvpTeamName: src.PvpTeamName,
            GrandCompanyId: ResolveGrandCompany(resolver, src.GrandCompanyName, lang),
            GrandCompanyRankId: rank?.RankId,
            GrandCompanyRankIsFeminine: rank?.IsFeminine);
    }

    public static PlayerCollectionStats? ToModel(this FfxivCollectModels.CategorySummary? src)
        => src is null ? null : new(src.Count, src.Total, src.Ranking, src.IsPublic);

    public static PlayerProfileEntity ToEntity(this PlayerProfile m, ulong lodestoneId, DateTime now) => new()
    {
        LodestoneId = lodestoneId,
        Bio = m.Bio,
        Nameday = m.Nameday,
        GuardianDeity = m.GuardianDeity,
        GuardianDeityIconUrl = m.GuardianDeityIconUrl,
        StartingCity = m.StartingCity,
        StartingCityIconUrl = m.StartingCityIconUrl,
        AvatarUrl = m.AvatarUrl,
        PortraitUrl = m.PortraitUrl,
        FreeCompanyLodestoneId = m.FreeCompanyLodestoneId,
        PvpTeamLodestoneId = m.PvpTeamLodestoneId,
        PvpTeamName = m.PvpTeamName,
        GrandCompanyId = m.GrandCompanyId,
        GrandCompanyRankId = m.GrandCompanyRankId,
        GrandCompanyRankIsFeminine = m.GrandCompanyRankIsFeminine,
        UpdatedAt = now,
    };

    public static PlayerProfile ToModel(this PlayerProfileEntity e) => new(
        e.Bio, e.Nameday,
        e.GuardianDeity, e.GuardianDeityIconUrl,
        e.StartingCity, e.StartingCityIconUrl,
        e.AvatarUrl, e.PortraitUrl, e.FreeCompanyLodestoneId,
        e.PvpTeamLodestoneId, e.PvpTeamName,
        e.GrandCompanyId, e.GrandCompanyRankId, e.GrandCompanyRankIsFeminine);

    public static PlayerCollectionStatsEntity ToEntity(this PlayerCollectionStats stats, ulong lodestoneId, CollectionKind kind, DateTime now) => new()
    {
        LodestoneId = lodestoneId,
        Kind = kind,
        Count = stats.Count,
        Total = stats.Total,
        Ranking = stats.Ranking,
        IsPublic = stats.IsPublic,
        UpdatedAt = now,
    };

    public static PlayerCollectionStats ToModel(this PlayerCollectionStatsEntity e)
        => new(e.Count, e.Total, e.Ranking, e.IsPublic);

    public static bool IsOwned(string? marker)
        => !string.IsNullOrEmpty(marker) && !marker.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Build a <see cref="FreeCompany"/> model from the Lodestone scrape. The
    /// resulting <see cref="EstateAddress"/> (if any) has <c>Id == 0</c> — the
    /// service decides whether to INSERT a new row (FK currently null on the FC
    /// entity) or UPDATE an existing one (FK already set).
    /// </summary>
    public static FreeCompany ToModel(this LodestoneModels.LodestoneFreeCompany src,
        IGameDataResolver? resolver, GameDataClientLanguage lang)
    {
        var worldId = ResolveWorld(resolver, src.World, lang);
        return new(
            LodestoneId: src.LodestoneId,
            Name: src.Name,
            Tag: src.Tag,
            Slogan: src.Slogan,
            FormedAt: src.FormedAt,
            WorldId: worldId,
            Rank: src.Rank,
            ActiveMemberCount: src.ActiveMemberCount,
            ActiveState: src.ActiveState,
            RecruitmentOpen: src.RecruitmentOpen,
            GrandCompanyId: ResolveGrandCompany(resolver, src.GrandCompany, lang),
            Estate: BuildEstate(src.Estate, resolver, lang, worldId),
            Focus: src.Focus is null ? null : new FreeCompanyFocus(
                src.Focus.RolePlay, src.Focus.Leveling, src.Focus.Casual, src.Focus.Hardcore,
                src.Focus.Dungeons, src.Focus.Guildhests, src.Focus.Trials, src.Focus.Raids, src.Focus.PvP));
    }

    /// <summary>
    /// Builds an in-memory <see cref="EstateAddress"/> from the parsed Lodestone
    /// estate block. <c>Id</c> stays 0 until the service writes the row; data
    /// center is resolved from the world. Returns null when no estate is known.
    /// </summary>
    private static EstateAddress? BuildEstate(
        LodestoneModels.LodestoneFreeCompanyEstate? src,
        IGameDataResolver? resolver,
        GameDataClientLanguage lang,
        uint? worldId)
    {
        if (src is null) return null;
        if (src.Name is null && src.Greeting is null
            && src.DistrictName is null && src.Ward is null && src.PlotNumber is null
            && src.HouseSize is null && !src.IsSubdivision)
        {
            return null;
        }

        var districtId = ResolveDistrict(resolver, src.DistrictName, lang);
        var dcId = ResolveDataCenter(resolver, worldId);
        return new EstateAddress(
            Id: 0,
            Name: src.Name,
            Greeting: src.Greeting,
            DataCenterId: dcId,
            WorldId: worldId,
            DistrictTerritoryId: districtId,
            Ward: src.Ward,
            PlotNumber: src.PlotNumber,
            IsApartment: false,                   // FCs never own apartments
            ApartmentWing: null,
            HouseSize: src.HouseSize,
            IsSubdivision: src.IsSubdivision);
    }

    /// <summary>
    /// Maps the in-memory FC to its persisted entity. <see cref="FreeCompany.Estate"/>
    /// is intentionally NOT translated to <c>EstateAddressId</c> here — the
    /// service writes the address row first, then sets the FK on the FC entity
    /// after EF assigns an auto-increment id.
    /// </summary>
    public static FreeCompanyEntity ToEntity(this FreeCompany m, DateTime now) => new()
    {
        LodestoneId = m.LodestoneId,
        Name = m.Name,
        Tag = m.Tag,
        Slogan = m.Slogan,
        FormedAt = m.FormedAt,
        WorldId = m.WorldId,
        Rank = m.Rank,
        ActiveMemberCount = m.ActiveMemberCount,
        ActiveState = m.ActiveState,
        RecruitmentOpen = m.RecruitmentOpen,
        GrandCompanyId = m.GrandCompanyId,
        EstateAddressId = null,                  // service patches this after EstateAddress row exists
        FocusRolePlay   = m.Focus?.RolePlay   ?? false,
        FocusLeveling   = m.Focus?.Leveling   ?? false,
        FocusCasual     = m.Focus?.Casual     ?? false,
        FocusHardcore   = m.Focus?.Hardcore   ?? false,
        FocusDungeons   = m.Focus?.Dungeons   ?? false,
        FocusGuildhests = m.Focus?.Guildhests ?? false,
        FocusTrials     = m.Focus?.Trials     ?? false,
        FocusRaids      = m.Focus?.Raids      ?? false,
        FocusPvP        = m.Focus?.PvP        ?? false,
        UpdatedAt = now,
    };

    /// <summary>
    /// Read the FC entity back into a model. The optional <paramref name="estate"/>
    /// row is loaded by the service via <c>Include()</c> and threaded in; null
    /// when the FC has no <c>estate_address_id</c> set.
    /// </summary>
    public static FreeCompany ToModel(this FreeCompanyEntity e, EstateAddressEntity? estate = null) => new(
        LodestoneId: e.LodestoneId,
        Name: e.Name,
        Tag: e.Tag,
        Slogan: e.Slogan,
        FormedAt: e.FormedAt,
        WorldId: e.WorldId,
        Rank: e.Rank,
        ActiveMemberCount: e.ActiveMemberCount,
        ActiveState: e.ActiveState,
        RecruitmentOpen: e.RecruitmentOpen,
        GrandCompanyId: e.GrandCompanyId,
        Estate: estate?.ToModel(),
        Focus: new FreeCompanyFocus(
            e.FocusRolePlay, e.FocusLeveling, e.FocusCasual, e.FocusHardcore,
            e.FocusDungeons, e.FocusGuildhests, e.FocusTrials, e.FocusRaids, e.FocusPvP));

    public static EstateAddress ToModel(this EstateAddressEntity e) => new(
        Id: e.Id,
        Name: e.Name,
        Greeting: e.Greeting,
        DataCenterId: e.DataCenterId,
        WorldId: e.WorldId,
        DistrictTerritoryId: e.DistrictTerritoryId,
        Ward: e.Ward,
        PlotNumber: e.PlotNumber,
        IsApartment: e.IsApartment,
        ApartmentWing: e.ApartmentWing,
        HouseSize: e.HouseSize,
        IsSubdivision: e.IsSubdivision);

    /// <summary>Build a fresh entity row from a model (used on the INSERT path).</summary>
    public static EstateAddressEntity ToEntity(this EstateAddress m, DateTime now) => new()
    {
        // Id intentionally left default — EF will assign on INSERT.
        Name = m.Name,
        Greeting = m.Greeting,
        DataCenterId = m.DataCenterId,
        WorldId = m.WorldId,
        DistrictTerritoryId = m.DistrictTerritoryId,
        Ward = m.Ward,
        PlotNumber = m.PlotNumber,
        IsApartment = m.IsApartment,
        ApartmentWing = m.ApartmentWing,
        HouseSize = m.HouseSize,
        IsSubdivision = m.IsSubdivision,
        UpdatedAt = now,
    };

    /// <summary>Copy address fields from a model onto an existing entity (UPDATE path).
    /// Touches <c>UpdatedAt</c> so cache freshness reflects the new write.</summary>
    public static void ApplyTo(this EstateAddress m, EstateAddressEntity target, DateTime now)
    {
        target.Name = m.Name;
        target.Greeting = m.Greeting;
        target.DataCenterId = m.DataCenterId;
        target.WorldId = m.WorldId;
        target.DistrictTerritoryId = m.DistrictTerritoryId;
        target.Ward = m.Ward;
        target.PlotNumber = m.PlotNumber;
        target.IsApartment = m.IsApartment;
        target.ApartmentWing = m.ApartmentWing;
        target.HouseSize = m.HouseSize;
        target.IsSubdivision = m.IsSubdivision;
        target.UpdatedAt = now;
    }

    private static byte? ResolveGrandCompany(IGameDataResolver? resolver, string? name, GameDataClientLanguage lang)
    {
        if (resolver is null || string.IsNullOrWhiteSpace(name)) return null;
        return resolver.ResolveIdByName(name, GameDataKind.GrandCompany, lang) is { } id ? (byte)id : null;
    }

    private static GrandCompanyRankRef? ResolveRank(IGameDataResolver? resolver, string? rankName, GameDataClientLanguage lang)
    {
        if (resolver is null || string.IsNullOrWhiteSpace(rankName)) return null;
        return resolver.ResolveGrandCompanyRank(rankName, lang);
    }

    private static uint? ResolveWorld(IGameDataResolver? resolver, string? name, GameDataClientLanguage lang)
    {
        if (resolver is null || string.IsNullOrWhiteSpace(name)) return null;
        return resolver.ResolveIdByName(name, GameDataKind.World, lang);
    }

    private static uint? ResolveDistrict(IGameDataResolver? resolver, string? districtName, GameDataClientLanguage lang)
    {
        if (resolver is null || string.IsNullOrWhiteSpace(districtName)) return null;
        // Lodestone publishes leading articles in some locales ("The Goblet").
        // The resolver iterates only 5 known rows so collisions are impossible —
        // a straight lookup with trimmed name is enough; if it misses, try the
        // normalized fallback for whitespace/punctuation drift.
        return resolver.ResolveIdByName(districtName, GameDataKind.HousingDistrict, lang)
            ?? resolver.ResolveIdByNormalizedName(districtName, GameDataKind.HousingDistrict, lang);
    }

    private static uint? ResolveDataCenter(IGameDataResolver? resolver, uint? worldId)
    {
        // DataCenter is deterministic from the world — but the resolver doesn't
        // expose a world→DC API, so callers that want it cached on the address
        // row must go through ISheetsProvider/IGameDataLookups separately. For
        // now we return null here; the service can patch it before INSERT.
        _ = resolver;
        _ = worldId;
        return null;
    }
}
