namespace NexusKit.Modules.InternalData.Persistence;

/// <summary>One normalized player snapshot kept for historical comparisons.</summary>
public sealed class InternalPlayerSnapshotEntity
{
    public long Id { get; set; }
    public ulong ContentId { get; set; }
    public DateTime CapturedAt { get; set; }
    public string ValuesJson { get; set; } = string.Empty;
}
