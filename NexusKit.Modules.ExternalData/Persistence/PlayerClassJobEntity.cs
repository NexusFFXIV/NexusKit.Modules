namespace NexusKit.Modules.ExternalData.Persistence;

/// <summary>
/// One row per player and class-job. Composite key: (LodestoneId, ClassJobId).
/// <see cref="ClassJobId"/> is the Lumina <c>ClassJob.RowId</c>.
/// </summary>
public sealed class PlayerClassJobEntity
{
    public ulong LodestoneId { get; set; }
    public uint ClassJobId { get; set; }
    public int Level { get; set; }
    public DateTime UpdatedAt { get; set; }
}
