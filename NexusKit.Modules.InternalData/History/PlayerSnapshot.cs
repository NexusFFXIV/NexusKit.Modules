namespace NexusKit.Modules.InternalData.History;

/// <summary>Normalized values used by snapshot comparison. Values are kept as
/// data, not localized display strings, so a language change cannot create a
/// false history change.</summary>
public sealed record PlayerSnapshot(
    ulong ContentId,
    DateTime CapturedAt,
    IReadOnlyDictionary<string, string?> Values);

/// <summary>Persisted snapshot identity used by the historical comparison UI.</summary>
public sealed record PlayerSnapshotRecord(long Id, PlayerSnapshot Snapshot);

public enum SnapshotChangeKind : byte
{
    Added = 0,
    Removed = 1,
    Changed = 2,
}

public sealed record PlayerSnapshotChange(
    string Field,
    SnapshotChangeKind Kind,
    string? OldValue,
    string? NewValue);

public sealed record PlayerSnapshotDiff(
    ulong ContentId,
    DateTime PreviousCapturedAt,
    DateTime CurrentCapturedAt,
    IReadOnlyList<PlayerSnapshotChange> Changes)
{
    public bool HasChanges => Changes.Count != 0;
}
