namespace NexusKit.Modules.InternalData.Persistence;

public sealed class InternalRefreshRunEntity
{
    public long Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Exhausted { get; set; }
}

public sealed class InternalRefreshRunItemEntity
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string ContentKey { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public byte Status { get; set; }
    public int Attempt { get; set; }
    public string? ErrorCode { get; set; }
    public long DurationMs { get; set; }
    public bool IsExhausted { get; set; }
}
