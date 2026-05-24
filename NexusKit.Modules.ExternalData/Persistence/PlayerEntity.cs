namespace NexusKit.Modules.ExternalData.Persistence;

public sealed class PlayerEntity
{
    public ulong LodestoneId { get; set; }
    public string Name { get; set; } = null!;
    /// <summary>Lumina <c>World.RowId</c> of the character's home world.</summary>
    public uint HomeWorldId { get; set; }
    /// <summary>Lumina <c>WorldDCGroupType.RowId</c> of the home world's data center.</summary>
    public uint DataCenterId { get; set; }
    public DateTime UpdatedAt { get; set; }
}
