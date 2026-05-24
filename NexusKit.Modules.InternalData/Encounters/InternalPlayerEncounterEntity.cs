namespace NexusKit.Modules.InternalData.Encounters;

/// <summary>
/// One sighted character within an <see cref="InternalEncounterEntity"/>.
/// Inserted on the first observation of that character in the encounter's
/// territory; <see cref="LastSeenAt"/> is bumped on every subsequent
/// visibility tick. Job / level are FROZEN at first sighting — if the
/// player swaps jobs mid-encounter the row keeps the original snapshot.
/// </summary>
public sealed class InternalPlayerEncounterEntity
{
    public long Id { get; set; }

    /// <summary>FK to the parent <see cref="InternalEncounterEntity.Id"/>.</summary>
    public long EncounterId { get; set; }

    /// <summary>Stable per-character id of the SIGHTED player. Never equals
    /// the local player's content id — the watcher's
    /// <c>ObjectTableExtensions.GetVisiblePlayers</c> skips index 0 (the
    /// local character) before observations reach us.</summary>
    public ulong ContentId { get; set; }

    /// <summary>Lumina <c>ClassJob.RowId</c> at first sighting.</summary>
    public uint JobId { get; set; }

    /// <summary>Job level at first sighting.</summary>
    public byte Level { get; set; }

    /// <summary>UTC timestamp of the first observation of this character in
    /// this encounter.</summary>
    public DateTime FirstSeenAt { get; set; }

    /// <summary>UTC timestamp of the most recent visibility tick. The UI's
    /// "duration" column renders <see cref="LastSeenAt"/> minus
    /// <see cref="FirstSeenAt"/> — the per-player visible window, sharper
    /// than the encounter-wide duration the legacy plugins displayed.</summary>
    public DateTime LastSeenAt { get; set; }
}
