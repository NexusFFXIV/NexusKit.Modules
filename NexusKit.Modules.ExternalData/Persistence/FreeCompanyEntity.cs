using NexusKit.Modules.Lodestone.Models;

namespace NexusKit.Modules.ExternalData.Persistence;

public sealed class FreeCompanyEntity
{
    public string LodestoneId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Tag { get; set; }
    public string? Slogan { get; set; }
    public DateTime? FormedAt { get; set; }
    public uint? WorldId { get; set; }
    public int? Rank { get; set; }
    public int? ActiveMemberCount { get; set; }
    public FreeCompanyActiveState ActiveState { get; set; }
    public bool? RecruitmentOpen { get; set; }
    public byte? GrandCompanyId { get; set; }

    /// <summary>
    /// FK to <see cref="EstateAddressEntity"/>. Null when no estate is known
    /// for this FC (Lodestone scrape missing housing block).
    /// <para>The legacy columns <c>estate_name</c>, <c>estate_plot</c>,
    /// <c>estate_greeting</c> still exist in the DB until DbInspect normalizes
    /// them into the new table — see <c>20260522</c> migration. No C# property
    /// reads them anymore; EF Core ignores unmapped columns.</para>
    /// </summary>
    public long? EstateAddressId { get; set; }

    public bool FocusRolePlay { get; set; }
    public bool FocusLeveling { get; set; }
    public bool FocusCasual { get; set; }
    public bool FocusHardcore { get; set; }
    public bool FocusDungeons { get; set; }
    public bool FocusGuildhests { get; set; }
    public bool FocusTrials { get; set; }
    public bool FocusRaids { get; set; }
    public bool FocusPvP { get; set; }

    public DateTime UpdatedAt { get; set; }
}
