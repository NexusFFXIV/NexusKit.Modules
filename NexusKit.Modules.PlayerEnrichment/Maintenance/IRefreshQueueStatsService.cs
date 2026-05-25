namespace NexusKit.Modules.PlayerEnrichment.Maintenance;

/// <summary>One row in the top-N "biggest queue depth per character" view.
/// <see cref="Name"/> is null when the queue contains a content_id we no
/// longer have an <c>observed_player</c> row for (stale leak — shouldn't
/// happen but defensible).
///
/// <para><see cref="EarliestNextRetryAtUtc"/> and
/// <see cref="EarliestDeletionAtUtc"/> are mutually-exclusive in practice
/// for the UI: the section picks the deletion countdown when
/// <see cref="MaxAttemptCount"/> hits the cap, otherwise the retry
/// countdown. Both are nullable so the UI can fall back to "retry
/// pending" / "cleanup pending" placeholders when no concrete time exists
/// (no failed row yet, no row past the cap yet).</para></summary>
public sealed record RefreshQueueTopContent(
    ulong ContentId,
    string? Name,
    int Rows,
    int MaxAttemptCount,
    DateTime? EarliestNextRetryAtUtc,
    DateTime? EarliestDeletionAtUtc);

/// <summary>Aggregated state of <c>nexus_internal_refresh_queue</c> for the
/// Settings-UI queue diagnostics panel. All counts are computed in a single
/// GatherAsync call; the snapshot is meant to be displayed and discarded.
///
/// <para>Two ETAs by design: <see cref="EstimatedDrain"/> covers only the
/// rows the worker can pick *right now* (<see cref="EligibleNowRows"/>),
/// so it drops to zero when the worker is idle. <see cref="EarliestRetryAt"/>
/// covers the next backoff-waiting row's cooldown clearance — null when
/// nothing is waiting. Together they let the UI distinguish "actively
/// processing" from "idle, just waiting on retries", which the old single
/// ETA (Active × WorkerGap) couldn't.</para></summary>
public sealed record RefreshQueueStatsSnapshot(
    int TotalRows,
    int ActiveRows,
    int ExhaustedRows,
    int EligibleNowRows,
    int BackoffWaitingRows,
    TimeSpan EstimatedDrain,
    DateTime? EarliestRetryAt,
    IReadOnlyDictionary<int, int> RowsByPriority,
    IReadOnlyDictionary<int, int> RowsByCategory,
    IReadOnlyList<RefreshQueueTopContent> TopContents);

/// <summary>Reads aggregate stats off <c>nexus_internal_refresh_queue</c>.
/// Lives in <c>NexusKit.Modules.PlayerEnrichment</c> because the worker's
/// timing constants (the WorkerGap polite spacing, the MaxAttempts hard
/// cap) drive the drain-time ETA — duplicating them in the UI would
/// silently drift when the worker is tuned.</summary>
public interface IRefreshQueueStatsService
{
    Task<RefreshQueueStatsSnapshot> GatherAsync(CancellationToken ct = default);
}
