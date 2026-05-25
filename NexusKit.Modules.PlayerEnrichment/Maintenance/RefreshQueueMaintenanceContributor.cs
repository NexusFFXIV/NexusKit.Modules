using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Modules.InternalData.Persistence;
using NexusKit.Persistence;
using NexusKit.Persistence.Maintenance;

namespace NexusKit.Modules.PlayerEnrichment.Maintenance;

/// <summary>
/// Exhausted-row cleanup for <c>nexus_internal_refresh_queue</c>. Rows that
/// have hit <see cref="PlayerRefreshQueueService.MaxAttempts"/> and have
/// been sitting idle past <see cref="ExhaustedRetention"/> are deleted so
/// the queue table stays bounded over long-running plugin sessions.
///
/// <para>Pre-D6 this lived inline in <c>PlayerRefreshQueueService</c>'s
/// worker loop, gated by its own <c>mLastPruneAt</c> timer. It's now a
/// regular maintenance contributor — same SQL, same retention semantics,
/// but scheduled by the shared <see cref="IDbMaintenanceService"/>.</para>
/// </summary>
internal sealed class RefreshQueueMaintenanceContributor : IDbMaintenanceContributor
{
    /// <summary>Aged-out exhausted rows are kept around for 24h before
    /// being deleted — gives the user a window to manually revive a row
    /// via the detail-panel Refresh button (which resets the bookkeeping
    /// via UpsertAsync's same-priority Immediate branch).
    /// <para>Internal because <c>RefreshQueueStatsService</c> needs the same
    /// retention to render the per-row "Cleanup in …" countdown in the
    /// Settings UI — duplicating the value as a magic number would silently
    /// drift if this contributor is retuned.</para></summary>
    internal static readonly TimeSpan ExhaustedRetention = TimeSpan.FromHours(24);

    private readonly ILogger<RefreshQueueMaintenanceContributor> mLog;

    public RefreshQueueMaintenanceContributor(ILogger<RefreshQueueMaintenanceContributor> log)
    {
        mLog = log;
    }

    public string Name => "refresh-queue-exhausted-prune";

    /// <summary>6-hour cadence — matches the previous worker-inline timer.
    /// Aged-out exhausted rows aren't time-critical; even 24h late on the
    /// cleanup keeps the table size sane.</summary>
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(DbContext ctx, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - ExhaustedRetention;
        // AttemptCount >= MaxAttempts implies LastAttemptedAt is non-null
        // (ProcessOneAsync stamps it before incrementing), but EF's SQLite
        // provider expects the .Value access on the nullable, so guard the
        // null case explicitly. The aged-out filter gives the user a 24h
        // window to revive a row via the detail-panel Refresh button.
        var deleted = await ctx.Set<InternalRefreshQueueEntity>()
            .Where(e => e.AttemptCount >= PlayerRefreshQueueService.MaxAttempts
                        && e.LastAttemptedAt != null
                        && e.LastAttemptedAt < cutoff)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            mLog.LogInformation(
                "Refresh queue: pruned {Count} exhausted row(s) older than {Hours}h.",
                deleted, ExhaustedRetention.TotalHours);
    }
}
