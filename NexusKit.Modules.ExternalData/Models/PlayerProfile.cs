namespace NexusKit.Modules.ExternalData.Models;

/// <summary>
/// Biographical fields populated from Lodestone (NetStone scraping). All optional —
/// players may hide them or the source module may be disabled.
/// <para><see cref="GrandCompanyId"/> is the player's personal Grand Company allegiance
/// (Lumina <c>GrandCompany.RowId</c>, 1/2/3). <see cref="GrandCompanyRankId"/> is the
/// rank tier within that GC (Lumina <c>GCRank…Text.RowId</c>, 0-19). The localized rank
/// label varies between masculine and feminine forms, so <see cref="GrandCompanyRankIsFeminine"/>
/// picks which sheet variant <c>IGameDataLookups.GetGrandCompanyRankName</c> reads back.</para>
/// </summary>
public sealed record PlayerProfile(
    string? Bio,
    string? Nameday,
    string? GuardianDeity,
    string? GuardianDeityIconUrl,
    string? StartingCity,
    string? StartingCityIconUrl,
    string? AvatarUrl,
    string? PortraitUrl,
    string? FreeCompanyLodestoneId,
    string? PvpTeamLodestoneId,
    string? PvpTeamName,
    byte? GrandCompanyId,
    byte? GrandCompanyRankId,
    bool? GrandCompanyRankIsFeminine);
