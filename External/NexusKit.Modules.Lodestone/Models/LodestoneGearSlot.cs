namespace NexusKit.Modules.Lodestone.Models;

/// <summary>
/// One equipped slot from a Lodestone character page. Slot ordering: 0..13 follows
/// the FFXIV standard layout (Mainhand, Offhand, Head, Body, Hands, Belt, Legs, Feet,
/// Earrings, Necklace, Bracelets, Ring1, Ring2, SoulCrystal).
/// </summary>
public sealed class LodestoneGearSlot
{
    public int SlotIndex { get; set; }
    public string SlotName { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public bool IsHq { get; set; }
    public string? GlamourName { get; set; }

    /// <summary>
    /// Applied dye colors. FFXIV supports up to two dyes per slot ("Farbe 1" / "Farbe 2"
    /// on Lodestone). Empty when the item / glamour isn't dyed.
    /// </summary>
    public List<string> Colors { get; set; } = new();

    public List<string> Materia { get; set; } = new();
    public string? CreatorName { get; set; }
    public int? ItemLevel { get; set; }
}
