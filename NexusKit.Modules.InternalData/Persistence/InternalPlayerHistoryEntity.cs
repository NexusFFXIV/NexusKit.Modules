using NexusKit.Modules.InternalData.History;

namespace NexusKit.Modules.InternalData.Persistence;

/// <summary>
/// One append-only row in <c>nexus_internal_player_history</c>. The watcher's
/// observation pipeline emits an event after each upsert; <c>InternalDataHistoryService</c>
/// diffs the previous vs. current observation and writes rows here when fields
/// of interest changed.
/// </summary>
public sealed class InternalPlayerHistoryEntity
{
    public long Id { get; set; }
    public ulong ContentId { get; set; }
    public PlayerHistoryKind Kind { get; set; }
    public DateTime ChangedAt { get; set; }

    /// <summary>Pre-formatted display string for the prior value (e.g. <c>"Twintania"</c>).
    /// The watcher / diff service resolves Lumina lookups (race name, world name, …) at
    /// detection time so the UI render is just a string format.</summary>
    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    /// <summary>True once the user has opened the History tab for the owning
    /// character with this row present — drives the "unread history" indicator
    /// in the player list (yellow dot disappears once every row is read).
    /// Existing rows from before the column was added are backfilled to
    /// <c>true</c> by the migration: they were seen in earlier sessions.</summary>
    public bool IsRead { get; set; }
}
