using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Core;
using NexusKit.Modules.InternalData.Persistence;
using NexusKit.Modules.InternalData.Refresh;
using NexusKit.Persistence;

namespace NexusKit.Modules.PlayerEnrichment;

public sealed class RefreshRunSummaryService : IRefreshRunSummaryService, IDisposable
{
    private readonly IPlayerRefreshQueueService mQueue;
    private readonly RefreshMetricsCollector mMetrics;
    private readonly INexusDbContextFactory mDb;
    private readonly IPluginLifetime mLifetime;
    private readonly ILogger<RefreshRunSummaryService> mLog;
    private readonly object mGate = new();
    private readonly HashSet<(ulong ContentId, RefreshCategory Category)> mPending = new();
    private RefreshRunSummaryBuilder? mBuilder;
    private long mRunVersion;
    private bool mDisposed;

    public RefreshRunSummaryService(IPlayerRefreshQueueService queue,
        RefreshMetricsCollector metrics, INexusDbContextFactory db,
        IPluginLifetime lifetime,
        ILogger<RefreshRunSummaryService> log)
    {
        mQueue = queue;
        mMetrics = metrics;
        mDb = db;
        mLifetime = lifetime;
        mLog = log;
        mQueue.Enqueued += OnEnqueued;
        mQueue.OutcomeProduced += Record;
        mLifetime.StateChanged += OnLifecycleChanged;
    }

    public RefreshRunSummary? Latest { get; private set; }

    public RefreshRunSummary Start()
    {
        lock (mGate)
        {
            mBuilder = new RefreshRunSummaryBuilder();
            mPending.Clear();
            mRunVersion++;
            return mBuilder.Complete();
        }
    }

    public void Record(RefreshOutcome outcome)
    {
        mMetrics.Record(outcome);
        // Background refreshes must still be diagnosable when no UI button
        // explicitly started a run; the next explicit Complete closes it.
        var drained = false;
        lock (mGate)
        {
            (mBuilder ??= new RefreshRunSummaryBuilder()).Add(outcome);
            // A failed attempt can be retried without another Enqueued event;
            // keep the batch open until the terminal/exhausted outcome arrives.
            if (outcome.Status != RefreshOutcomeStatus.Failed || outcome.IsExhausted)
                mPending.Remove((outcome.ContentId, outcome.Category));
            drained = mPending.Count == 0;
        }
        if (drained)
        {
            long version;
            lock (mGate) version = mRunVersion;
            _ = CompleteWhenQueueSettlesAsync(version);
        }
    }

    private void OnEnqueued(ulong contentId, RefreshCategory category)
    {
        lock (mGate)
        {
            mBuilder ??= new RefreshRunSummaryBuilder();
            mPending.Add((contentId, category));
            mRunVersion++;
        }
    }

    private async Task CompleteWhenQueueSettlesAsync(long version)
    {
        // Enqueue batches are commonly published back-to-back. A short grace
        // period avoids persisting a partial run between two queue events.
        await Task.Delay(100).ConfigureAwait(false);
        lock (mGate)
        {
            if (mDisposed || version != mRunVersion || mPending.Count != 0 || mBuilder is null) return;
        }
        await CompleteAndPersistSafelyAsync().ConfigureAwait(false);
    }

    private async Task CompleteAndPersistSafelyAsync()
    {
        try { await CompleteAsync().ConfigureAwait(false); }
        catch (Exception ex) { mLog.LogWarning(ex, "Refresh run persistence failed"); }
    }

    private void OnLifecycleChanged(PluginLifecycleState state)
    {
        if (state != PluginLifecycleState.Stopping) return;
        lock (mGate)
        {
            if (mBuilder is null || mDisposed) return;
        }

        // Stopping is the documented final-write window: the lifetime token is
        // still live and DI has not started disposing the database yet. Wait
        // here so a plugin unload cannot discard an open run in fire-and-forget
        // work after the token is cancelled.
        try { CompleteAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) { mLog.LogWarning(ex, "Refresh run finalization failed during shutdown"); }
    }

    public RefreshRunSummary Complete()
    {
        lock (mGate)
        {
            var builder = mBuilder ?? new RefreshRunSummaryBuilder();
            Latest = builder.Complete();
            mBuilder = null;
            mPending.Clear();
            mRunVersion++;
            return Latest;
        }
    }

    public async Task<RefreshRunSummary> CompleteAsync(CancellationToken ct = default)
    {
        var summary = Complete();
        await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
        var run = new InternalRefreshRunEntity
        {
            StartedAt = summary.StartedAtUtc,
            CompletedAt = summary.CompletedAtUtc,
            Total = summary.Total,
            Succeeded = summary.Succeeded,
            Failed = summary.Failed,
            Skipped = summary.Skipped,
            Exhausted = summary.Exhausted,
        };
        ctx.Set<InternalRefreshRunEntity>().Add(run);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var item in summary.Items)
        {
            ctx.Set<InternalRefreshRunItemEntity>().Add(new InternalRefreshRunItemEntity
            {
                RunId = run.Id,
                ContentKey = item.ContentKey,
                Category = item.Category,
                Status = (byte)item.Status,
                Attempt = item.Attempt,
                ErrorCode = item.ErrorCode,
                DurationMs = (long)item.Duration.TotalMilliseconds,
                IsExhausted = item.IsExhausted,
            });
        }
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return summary;
    }

    public string? ExportLatestJson()
        => Latest is null ? null : System.Text.Json.JsonSerializer.Serialize(Latest);

    public async Task<int> PruneAsync(int keep = 20, CancellationToken ct = default)
    {
        keep = Math.Clamp(keep, 1, 200);
        await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
        var old = await ctx.Set<InternalRefreshRunEntity>()
            .OrderByDescending(x => x.StartedAt)
            .Skip(keep)
            .ToListAsync(ct).ConfigureAwait(false);
        if (old.Count == 0) return 0;
        var ids = old.Select(x => x.Id).ToArray();
        await ctx.Set<InternalRefreshRunItemEntity>()
            .Where(x => ids.Contains(x.RunId))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        ctx.Set<InternalRefreshRunEntity>().RemoveRange(old);
        return await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        mLifetime.StateChanged -= OnLifecycleChanged;
        var shouldFinalize = false;
        lock (mGate)
        {
            mDisposed = true;
            shouldFinalize = mBuilder is not null;
        }
        mQueue.Enqueued -= OnEnqueued;
        mQueue.OutcomeProduced -= Record;
        if (shouldFinalize)
        {
            try { CompleteAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) { mLog.LogWarning(ex, "Refresh run finalization failed during dispose"); }
        }
    }
}
