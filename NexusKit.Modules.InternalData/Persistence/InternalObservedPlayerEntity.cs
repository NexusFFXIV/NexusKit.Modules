namespace NexusKit.Modules.InternalData.Persistence;

/// <summary>
/// One row per character the local player has observed via the in-game object
/// table. ContentId is the primary key — stable across sessions and unique
/// per character even when a player renames or world-transfers.
/// <para><see cref="LodestoneId"/> is filled by the watcher's enrichment pass
/// once Lodestone search resolves the character; it's null until then.</para>
/// <para>Captures the snapshot fields that come straight from the object table
/// (no Lodestone hop required). Usage counters (FirstSeen / LastSeen /
/// SeenCount) used to live here as denormalized aggregates; they're now
/// derived on the fly from <c>nexus_internal_player_encounter</c> (MIN /
/// MAX / COUNT keyed by content_id) so there's exactly one source of
/// truth and write-throughput drops one column update per tick. The
/// in-memory <c>ObservedPlayer</c> record still surfaces those values
/// — the watcher computes them at hydrate-time and keeps them current
/// from observation ticks.</para>
/// </summary>
public sealed class InternalObservedPlayerEntity
{
    public ulong ContentId { get; set; }

    /// <summary>Lodestone character id once enrichment resolved it; null otherwise.</summary>
    public ulong? LodestoneId { get; set; }

    public string Name { get; set; } = string.Empty;
    public uint HomeWorldId { get; set; }
    public uint CurrentWorldId { get; set; }
    public uint ClassJobId { get; set; }
    public byte Level { get; set; }

    /// <summary>Raw appearance byte array. Used to diff for "appearance changed"
    /// alerts and to reconstruct the in-game look offline.</summary>
    public byte[]? Customize { get; set; }

    /// <summary>FC tag worn at the time of observation. Null when not in an FC.</summary>
    public string? CompanyTag { get; set; }

    /// <summary>Currently summoned mount Lumina id (null = none).</summary>
    public uint? CurrentMountId { get; set; }

    /// <summary>Currently summoned minion Lumina id (null = none).</summary>
    public uint? CurrentMinionId { get; set; }

    /// <summary>Lumina <c>OnlineStatus.RowId</c> (0 = no status).</summary>
    public uint OnlineStatusId { get; set; }

    /// <summary>User-authored notes about this character. Null when the user hasn't
    /// written anything yet. Distinct from "" so observation-tick upserts that don't
    /// touch notes leave existing text alone.</summary>
    public string? Notes { get; set; }

    public DateTime UpdatedAt { get; set; }
}
