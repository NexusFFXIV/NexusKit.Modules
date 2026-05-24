using NexusKit.Modules.InternalData.Refresh;

namespace NexusKit.Modules.InternalData.Persistence;

/// <summary>
/// Persistent queue row: one outstanding refresh task for a specific
/// (content_id, category) pair. <c>content_id</c> is the stable per-character
/// identifier from the observation pipeline — always known, even before the
/// Lodestone id has been resolved. The worker reads the matching
/// <c>observed_player</c> row at processing time to translate to (name, world)
/// for the <see cref="RefreshCategory.LodestoneId"/> task or to the resolved
/// lodestone_id for every other category.
/// <para>PK is composite — at most one outstanding task per (player, category).
/// Re-enqueue with a higher priority is allowed and tracked via an upsert that
/// keeps the more urgent priority.</para>
/// </summary>
public sealed class InternalRefreshQueueEntity
{
    public ulong ContentId { get; set; }
    public RefreshCategory Category { get; set; }
    public RefreshPriority Priority { get; set; }
    public DateTime EnqueuedAt { get; set; }
    public DateTime? LastAttemptedAt { get; set; }
    public DateTime? LastFailedAt { get; set; }
    public int AttemptCount { get; set; }
}
