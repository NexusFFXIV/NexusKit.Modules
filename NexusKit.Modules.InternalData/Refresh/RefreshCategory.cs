namespace NexusKit.Modules.InternalData.Refresh;

/// <summary>
/// Granularity at which the refresh queue tracks per-character work. Each
/// category corresponds to one logical sub-resource served by
/// <c>IExternalDataPlayerService</c>; the worker pulls a queue row by
/// <see cref="RefreshCategory"/> and fetches only that resource so a player who
/// has stale mounts but fresh class-jobs only re-fetches what's actually stale.
/// <para>Stored as a byte in <c>nexus_internal_refresh_queue.category</c> —
/// adding new categories is safe; renumbering existing ones is not.</para>
/// </summary>
public enum RefreshCategory : byte
{
    /// <summary>Resolve a previously-unknown Lodestone id for the observed
    /// (content_id, name, home_world). Runs first within its priority lane
    /// because every other category needs the resulting id to do its work —
    /// the worker orders <c>ORDER BY priority ASC, category ASC, …</c> so a
    /// fresh sighting's LodestoneId task naturally beats a stale-Mounts task
    /// queued at the same priority.</summary>
    LodestoneId = 0,

    /// <summary>Lodestone character page: bio, nameday, avatar, FC link, GC, …</summary>
    Profile = 1,

    /// <summary>Lodestone class-job page.</summary>
    ClassJobs = 2,

    /// <summary>Current Lodestone gear set.</summary>
    Gear = 3,

    /// <summary>Free Company details — only enqueued when the profile carries an FC link.</summary>
    FreeCompany = 4,

    /// <summary>Mount collection (FFXIVCollect + Lodestone).</summary>
    Mounts = 5,

    /// <summary>Minion collection (FFXIVCollect + Lodestone).</summary>
    Minions = 6,

    /// <summary>Achievement collection (FFXIVCollect + Lodestone timestamps).</summary>
    Achievements = 7,
}

/// <summary>
/// Three priority lanes for the refresh worker. Stored as a byte; lower value
/// wins. Worker pulls <c>ORDER BY priority ASC, category ASC, enqueued_at ASC</c>
/// so an Immediate from a user click jumps ahead of an in-range refresh, which
/// jumps ahead of a stale-after-TTL background sweep — and within a lane the
/// LodestoneId category always wins, since every other category needs the
/// resolved id first.
/// </summary>
public enum RefreshPriority : byte
{
    /// <summary>User opened the detail panel or hit Refresh — bypasses backoff.</summary>
    Immediate = 0,

    /// <summary>Character is currently visible in range.</summary>
    High = 1,

    /// <summary>Background sweep — fill stale entries we already know about.</summary>
    Low = 2,
}
