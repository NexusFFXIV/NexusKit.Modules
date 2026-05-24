using NexusKit.Modules.InternalData.Refresh;

namespace NexusKit.Modules.PlayerEnrichment;

/// <summary>
/// Persistent priority queue of "fetch X for this character" work items —
/// the bridge between live observation (InternalData) and remote data sources
/// (ExternalData / Lodestone / FFXIVCollect). The watcher and UI fire-and-forget
/// enqueues; a background worker drains the queue and invokes
/// <c>IExternalDataPlayerService</c> with the right include flags.
/// <para>The queue is keyed by <c>ContentId</c> — the stable per-character
/// identifier from the observation pipeline. Lodestone-id resolution itself
/// is just another category (see <see cref="RefreshCategory.LodestoneId"/>),
/// so the worker handles fresh sightings (no lodestone id yet) and stale
/// sub-resources (Mounts, Achievements, …) through the same pipeline with the
/// same persistence, backoff and retry semantics.</para>
/// </summary>
public interface IPlayerRefreshQueueService
{
    /// <summary>Add one (player, category) row at the given priority. If a
    /// row already exists, its priority is lowered to <paramref name="priority"/>
    /// only when the new value is more urgent.</summary>
    Task EnqueueAsync(ulong contentId, RefreshCategory category,
                      RefreshPriority priority, CancellationToken ct = default);

    /// <summary>Inspect the character's current state and enqueue whatever's
    /// missing or stale. If the lodestone_id is unresolved, only the
    /// <see cref="RefreshCategory.LodestoneId"/> task is queued — the worker
    /// will resolve it and trigger the follow-up sub-resource enqueues itself.
    /// When the id is already known, every sub-resource older than the
    /// configured TTL is queued. Returns the categories that ended up in the
    /// queue.</summary>
    Task<IReadOnlyList<RefreshCategory>> EnqueueStaleAsync(
        ulong contentId, RefreshPriority priority, CancellationToken ct = default);

    /// <summary>Unconditional enqueue of every category — bypasses freshness
    /// and backoff so an explicit user "Refresh" forces a full re-fetch.
    /// Skips <see cref="RefreshCategory.LodestoneId"/> when the id is already
    /// known.</summary>
    Task EnqueueAllAsync(ulong contentId, RefreshPriority priority, CancellationToken ct = default);

    /// <summary>Fires after a queue row was inserted or its priority bumped.
    /// Carries (contentId, category). The worker subscribes so its idle sleep
    /// ends on new work; UI code can subscribe to reflect "refresh pending"
    /// indicators.</summary>
    event Action<ulong, RefreshCategory>? Enqueued;

    /// <summary>Fires after a queue row was successfully processed and
    /// deleted. Carries (contentId, category). UI caches can invalidate on
    /// this.</summary>
    event Action<ulong, RefreshCategory>? Completed;

    /// <summary>Fires when a queue row has consumed its retry budget — the
    /// last attempt failed and <c>AttemptCount</c> just hit <c>MaxAttempts</c>.
    /// The row stays in the queue (so a user-initiated Refresh can revive it
    /// via the bump-reset in <c>UpsertAsync</c>); the worker won't pick it
    /// again on its own until then. Chat / UI surfaces can subscribe to let
    /// the user know a fetch quietly gave up.</summary>
    event Action<ulong, RefreshCategory>? ExhaustedAttempts;

    /// <summary>Snapshot of where a character sits in the worker's pick order:
    /// how many of its own rows are pending, and how many other rows are picked
    /// before its earliest one. Drives the "queued behind N items" UI badge.
    /// Returns <c>PendingForContentId == 0</c> when the character has nothing
    /// in the queue.</summary>
    Task<QueueStatusForContent> GetQueueStatusForAsync(ulong contentId, CancellationToken ct = default);
}

/// <summary>
/// Result of <see cref="IPlayerRefreshQueueService.GetQueueStatusForAsync"/>.
/// All counts are eligible rows only — rows past the attempt cap or still
/// inside their failure-backoff cooldown are excluded, matching what the
/// worker actually sees on its next pick.
/// </summary>
/// <param name="PendingForContentId">How many queue rows belong to the
/// queried character — zero when nothing is queued for it.</param>
/// <param name="RowsAhead">How many OTHER characters' rows the worker would
/// process before the queried character's first eligible row. Zero when this
/// character is next.</param>
/// <param name="NextCategory">The category of this character's earliest
/// eligible row — i.e. the sub-resource the worker will fetch next for this
/// character. Null when <paramref name="PendingForContentId"/> is zero.</param>
public readonly record struct QueueStatusForContent(
    int PendingForContentId, int RowsAhead, RefreshCategory? NextCategory);
