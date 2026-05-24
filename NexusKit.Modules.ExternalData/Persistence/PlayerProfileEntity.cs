namespace NexusKit.Modules.ExternalData.Persistence;

public sealed class PlayerProfileEntity
{
    public ulong LodestoneId { get; set; }
    public string? Bio { get; set; }
    public string? Nameday { get; set; }
    public string? GuardianDeity { get; set; }
    public string? GuardianDeityIconUrl { get; set; }
    public string? StartingCity { get; set; }
    public string? StartingCityIconUrl { get; set; }
    public string? AvatarUrl { get; set; }
    public string? PortraitUrl { get; set; }
    public string? FreeCompanyLodestoneId { get; set; }
    public string? PvpTeamLodestoneId { get; set; }
    public string? PvpTeamName { get; set; }
    public byte? GrandCompanyId { get; set; }
    public byte? GrandCompanyRankId { get; set; }
    public bool? GrandCompanyRankIsFeminine { get; set; }
    public DateTime UpdatedAt { get; set; }
}
