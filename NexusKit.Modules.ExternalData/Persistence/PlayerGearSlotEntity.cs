namespace NexusKit.Modules.ExternalData.Persistence;

/// <summary>
/// One row per player and equipment slot. Composite key: (LodestoneId, SlotIndex).
/// <see cref="ItemId"/> / <see cref="GlamourItemId"/> are Lumina <c>Item.RowId</c>.
/// <see cref="ColorsJson"/> and <see cref="MateriaJson"/> stay as JSON string arrays
/// for now — a follow-up round resolves them via the Stain / Materia sheets.
/// </summary>
public sealed class PlayerGearSlotEntity
{
    public ulong LodestoneId { get; set; }
    public int SlotIndex { get; set; }
    public uint ItemId { get; set; }
    public bool IsHq { get; set; }
    public uint? GlamourItemId { get; set; }
    public string? ColorsJson { get; set; }
    public string? MateriaJson { get; set; }
    public string? CreatorName { get; set; }
    public int? ItemLevel { get; set; }
    public DateTime UpdatedAt { get; set; }
}
