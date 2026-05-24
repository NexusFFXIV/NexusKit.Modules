namespace NexusKit.Modules.ExternalData.Models;

/// <summary>
/// One equipped slot. <see cref="SlotIndex"/> 0..12 covers the current FFXIV
/// equipment layout (Mainhand, Offhand, Head, Body, Hands, Legs, Feet, Earrings,
/// Necklace, Bracelets, Ring1, Ring2, SoulCrystal); the deprecated Belt/Waist
/// slot is skipped by the scraper and the remaining slots shift down by one.
/// <para><see cref="ItemId"/> and <see cref="GlamourItemId"/> are Lumina
/// <c>Item.RowId</c> values — store IDs, look the name up on demand via
/// <c>IGameDataLookups.GetItemName</c> in whatever language you want.</para>
/// </summary>
public sealed record PlayerGearSlot(
    int SlotIndex,
    uint ItemId,
    bool IsHq,
    uint? GlamourItemId,
    IReadOnlyList<string> Colors,
    IReadOnlyList<string> Materia,
    string? CreatorName,
    int? ItemLevel);
