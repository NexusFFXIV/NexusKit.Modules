using NexusKit.Modules.Lodestone.Models;

namespace NexusKit.Modules.ExternalData.Models;

/// <summary>
/// Free Company aggregate fetched from Lodestone. Identified by its Lodestone FC id.
/// <see cref="WorldId"/> is the Lumina <c>World.RowId</c>; <see cref="GrandCompanyId"/>
/// is the Lumina <c>GrandCompany.RowId</c> (1=Maelstrom / 2=Order of the Twin Adder /
/// 3=Immortal Flames). Localized names are resolved via <c>IGameDataLookups</c>.
/// <para><see cref="Estate"/> points at a row in <c>nexus_estate_address</c> — the
/// physical address plus owner metadata (name/greeting/size). Null when the FC
/// has no housing.</para>
/// </summary>
public sealed record FreeCompany(
    string LodestoneId,
    string Name,
    string? Tag,
    string? Slogan,
    DateTime? FormedAt,
    uint? WorldId,
    int? Rank,
    int? ActiveMemberCount,
    FreeCompanyActiveState ActiveState,
    bool? RecruitmentOpen,
    byte? GrandCompanyId,
    EstateAddress? Estate,
    FreeCompanyFocus? Focus);

public sealed record FreeCompanyFocus(
    bool RolePlay,
    bool Leveling,
    bool Casual,
    bool Hardcore,
    bool Dungeons,
    bool Guildhests,
    bool Trials,
    bool Raids,
    bool PvP);
