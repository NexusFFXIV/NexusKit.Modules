namespace NexusKit.Modules.InternalData.History;

/// <summary>Compares two normalized snapshots without depending on UI or
/// localization. Missing and null values are treated as absent.</summary>
public sealed class PlayerSnapshotDiffService
{
    public PlayerSnapshotDiff Compare(PlayerSnapshot previous, PlayerSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        if (previous.ContentId != current.ContentId)
            throw new ArgumentException("Snapshots must belong to the same content id.", nameof(current));

        var fields = previous.Values.Keys
            .Concat(current.Values.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static field => field, StringComparer.Ordinal);
        var changes = new List<PlayerSnapshotChange>();

        foreach (var field in fields)
        {
            previous.Values.TryGetValue(field, out var oldValue);
            current.Values.TryGetValue(field, out var newValue);
            var oldPresent = !string.IsNullOrEmpty(oldValue);
            var newPresent = !string.IsNullOrEmpty(newValue);

            if (!oldPresent && !newPresent) continue;
            if (!oldPresent)
                changes.Add(new(field, SnapshotChangeKind.Added, null, newValue));
            else if (!newPresent)
                changes.Add(new(field, SnapshotChangeKind.Removed, oldValue, null));
            else if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                changes.Add(new(field, SnapshotChangeKind.Changed, oldValue, newValue));
        }

        return new PlayerSnapshotDiff(
            previous.ContentId,
            previous.CapturedAt,
            current.CapturedAt,
            changes);
    }
}
