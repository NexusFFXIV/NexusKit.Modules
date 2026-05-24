namespace NexusKit.Modules.Lodestone.Models;

/// <summary>
/// Member-activity schedule a Free Company advertises on its Lodestone
/// recruitment page (field "Active" — distinct from the
/// <see cref="LodestoneFreeCompany.RecruitmentOpen"/> boolean on the same page).
/// <para>Lodestone offers a fixed picklist (DE: "Jeden Tag" / "Wochentags"
/// / "Wochenende" / "Keine Angabe"; EN: "Always" / "Weekdays" / "Weekends"
/// / "Not specified"). No in-game Lumina sheet exists — this is a
/// Lodestone-only classification.</para>
/// <para><see cref="Unknown"/> is the parser-fallback for unrecognised
/// strings and also the default for never-scraped persistence rows.
/// <see cref="NotSpecified"/> is the explicit Lodestone "no answer" choice —
/// kept distinct so consumers don't conflate "FC chose blank" with "we
/// don't know yet".</para>
/// </summary>
public enum FreeCompanyActiveState : byte
{
    Unknown = 0,
    NotSpecified = 1,
    Always = 2,
    Weekdays = 3,
    Weekends = 4,
}
