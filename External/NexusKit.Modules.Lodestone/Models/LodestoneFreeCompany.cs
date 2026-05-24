using NexusKit.Core;

namespace NexusKit.Modules.Lodestone.Models;

/// <summary>
/// Lodestone Free Company aggregate. Identified by its Lodestone FC id.
/// </summary>
public sealed class LodestoneFreeCompany
{
    public string LodestoneId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Tag { get; set; }
    public string? Slogan { get; set; }
    public DateTime? FormedAt { get; set; }
    public string? World { get; set; }
    public int? Rank { get; set; }
    public int? ActiveMemberCount { get; set; }
    public FreeCompanyActiveState ActiveState { get; set; }
    public bool? RecruitmentOpen { get; set; }
    public string? GrandCompany { get; set; }

    public LodestoneFreeCompanyEstate? Estate { get; set; }
    public LodestoneFreeCompanyFocus? Focus { get; set; }
}

/// <summary>
/// Parsed estate metadata. <see cref="DistrictName"/> is the localized housing
/// district as it appears on Lodestone (e.g. <c>"Lavender Beds"</c>,
/// <c>"Dorf des Nebels"</c>) — the consumer-side <c>IGameDataResolver</c>
/// resolves it to a stable <c>TerritoryType.RowId</c> before persistence.
/// <para><see cref="Ward"/> and <see cref="PlotNumber"/> are 1-indexed (as
/// displayed in-game and on Lodestone).</para>
/// </summary>
public sealed class LodestoneFreeCompanyEstate
{
    public string? Name { get; set; }
    public string? Greeting { get; set; }
    public string? DistrictName { get; set; }
    public int? Ward { get; set; }
    public int? PlotNumber { get; set; }
    public HouseSize? HouseSize { get; set; }
    public bool IsSubdivision { get; set; }
}

public sealed class LodestoneFreeCompanyFocus
{
    public bool RolePlay { get; set; }
    public bool Leveling { get; set; }
    public bool Casual { get; set; }
    public bool Hardcore { get; set; }
    public bool Dungeons { get; set; }
    public bool Guildhests { get; set; }
    public bool Trials { get; set; }
    public bool Raids { get; set; }
    public bool PvP { get; set; }
}
