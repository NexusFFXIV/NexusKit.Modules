using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Modules.InternalData.Persistence;
using NexusKit.Modules.InternalData.Refresh;
using NexusKit.Persistence.Maintenance;

namespace NexusKit.Modules.PlayerEnrichment.Maintenance;

/// <summary>
/// Deletes <c>nexus_internal_refresh_queue</c> rows whose category has been
/// disabled via <see cref="IRefreshCategoryPolicy"/>. The worker already
/// filters them out of <c>PickNextAsync</c> so they'd never be processed; this
/// cleanup keeps the queue table bounded across long sessions where the user
/// toggles categories off and never turns them back on.
///
/// <para>Re-enabling a category later doesn't lose information: the next
/// stale-check (next sighting, next selection, next cascade) sees the
/// category's <c>UpdatedAt</c> as missing-or-stale and enqueues a fresh row.
/// The original priority / enqueued_at on the deleted row weren't useful
/// anyway — it had been sitting blocked indefinitely.</para>
///
/// <para>Separate contributor from <see cref="RefreshQueueMaintenanceContributor"/>
/// so each cleanup duty stays diagnosable independently in
/// <c>IDbMaintenanceService.GetLastRunSnapshotAsync</c>.</para>
/// </summary>
internal sealed class RefreshQueueDisabledCategoryPruneContributor : IDbMaintenanceContributor
{
    private readonly IRefreshCategoryPolicy mPolicy;
    private readonly ILogger<RefreshQueueDisabledCategoryPruneContributor> mLog;

    public RefreshQueueDisabledCategoryPruneContributor(
        IRefreshCategoryPolicy policy,
        ILogger<RefreshQueueDisabledCategoryPruneContributor> log)
    {
        mPolicy = policy;
        mLog = log;
    }

    public string Name => "refresh-queue-disabled-category-prune";

    /// <summary>6-hour cadence — same shape as the exhausted-row prune. A
    /// disabled-category row taking 6h to disappear after the user toggles
    /// the category off is fine; the worker already ignores it.</summary>
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(DbContext ctx, CancellationToken ct)
    {
        // Derive allowed set from the same classification + policy combo the
        // queue service uses for PickNextAsync. Mandatory categories
        // (RefreshCategoryClassification.Mandatory) always pass IsEnabled so
        // their rows are never pruned.
        var allowed = new List<RefreshCategory>(RefreshCategoryClassification.All.Count);
        foreach (var c in RefreshCategoryClassification.All)
            if (mPolicy.IsEnabled(c)) allowed.Add(c);

        // No-op fast path: nothing disabled → no rows to delete. Avoids an
        // unnecessary DELETE statement on the typical "all categories on"
        // configuration.
        if (allowed.Count == RefreshCategoryClassification.All.Count) return;

        var deleted = await ctx.Set<InternalRefreshQueueEntity>()
            .Where(e => !allowed.Contains(e.Category))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            mLog.LogInformation(
                "Refresh queue: pruned {Count} row(s) belonging to disabled categor{Suffix}.",
                deleted, deleted == 1 ? "y" : "ies");
    }
}
