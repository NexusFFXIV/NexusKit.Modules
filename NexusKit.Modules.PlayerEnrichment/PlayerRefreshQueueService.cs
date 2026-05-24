using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Core.Cache;
using NexusKit.GameData;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.ExternalData.Persistence;
using NexusKit.Modules.ExternalData.Players;
using NexusKit.Modules.InternalData.Persistence;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Modules.InternalData.Refresh;
using NexusKit.Persistence;

namespace NexusKit.Modules.PlayerEnrichment;

internal sealed class PlayerRefreshQueueService : IPlayerRefreshQueueService, IDisposable
{
    private readonly INexusDbContextFactory mDb;
    private readonly IExternalDataPlayerService mPlayers;
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly IGameDataLookups mLookups;
    private readonly IReadOnlyList<IExternalDataCacheResetter> mResetters;
    private readonly ILogger<PlayerRefreshQueueService> mLog;
    private readonly IRefreshTtlProvider mTtl;
    private readonly IRefreshCategoryPolicy mPolicy;
    private readonly SemaphoreSlim mWakeup = new(initialCount: 0, maxCount: 1);

    // Serializes UpsertAsync. The method does a read-then-(insert|update)
    // on (content_id, category) across a short-lived DbContext, so two
    // concurrent callers can both see "no existing row" and both queue an
    // INSERT — the second SaveChangesAsync then trips the UNIQUE index.
    // Concurrent callers really do happen: OnObserved fires on the
    // threadpool while the worker's CascadeStaleSubResourcesAsync runs in
    // parallel, and a UI Refresh click via EnqueueAllAsync can overlap
    // either. Drop-on-busy (the pattern used by InternalDataPlayerWatcher)
    // is wrong here because EnqueueAllAsync carries user intent and the
    // cascade path is one-shot, so we wait — the upsert is fast and
    // contention stays bounded.
    private readonly SemaphoreSlim mUpsertGate = new(initialCount: 1, maxCount: 1);

    private readonly Task mWorker;

    // Per-session "already enqueued via Observed" set — keyed by ContentId.
    // Keeps the watcher from triggering a fresh stale-check on every 1Hz scan
    // tick for the same character. Cleared on disposal; a plugin reload
    // re-evaluates everyone (which is what we want for the in-range cohort).
    private readonly HashSet<ulong> mObservedThisSession = new();

    private bool mDisposed;

    // 60 minutes — matches the old plugin's per-attempt cooldown so a failed
    // Lodestone fetch doesn't hammer the endpoint right away.
    // <para><c>internal</c> so the queue-stats service can mirror the same
    // backoff window when computing "next retry" without duplicating the
    // constant.</para>
    internal static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(60);

    // Hard cap on retries — protects against permanent failures (deleted
    // character, FFXIVCollect outage). The row stays in the queue at this
    // point so the user can inspect it manually if needed. Bumped from 5
    // to 10 to give the self-heal path five more attempts on a freshly
    // cleared cache after a cache-poison signal at the soft cap.
    // <para><c>internal</c> so the maintenance-side
    // <c>RefreshQueueMaintenanceContributor</c> can reuse the same
    // constant for its exhausted-row prune filter.</para>
    internal const int MaxAttempts = 10;

    // Soft cap. When AttemptCount crosses this and the last attempt looked
    // like a cache-fed instant fail (last_attempted vs last_failed gap
    // below FastFailThreshold), we evict the per-category Lodestone-cache
    // rows for this player and let the remaining 5 attempts run against a
    // clean cache. Triggers exactly once per row because the equality
    // check only matches at the crossing instant.
    private const int SoftAttemptCap = 5;

    // Real Lodestone HTTP calls land at 200ms+ even with warm DNS and
    // connection reuse; a cache hit + bookkeeping plus the per-method
    // DbContext lifetime adds up to under 200ms even on a slow disk. 500ms
    // sits cleanly between the two so a "fast" attempt clearly didn't talk
    // to Lodestone — almost always a degenerate cache row.
    private static readonly TimeSpan FastFailThreshold = TimeSpan.FromMilliseconds(500);

    // Polite spacing between consecutive queue items. The Lodestone client
    // throttles its own requests too; this is defense in depth.
    // <para><c>internal</c> so the queue-stats service can compute an
    // accurate drain-time ETA without duplicating the constant.</para>
    internal static readonly TimeSpan WorkerGap = TimeSpan.FromSeconds(2);

    // Exhausted-row retention is owned by RefreshQueueMaintenanceContributor
    // now — the service itself only cares about MaxAttempts (above) for the
    // "stop picking this row" gate. The contributor's prune window
    // (currently 24h, see Maintenance/RefreshQueueMaintenanceContributor.cs)
    // determines when aged-out rows get GC'd.

    public event Action<ulong, RefreshCategory>? Enqueued;

    public event Action<ulong, RefreshCategory>? Completed;

    public event Action<ulong, RefreshCategory>? ExhaustedAttempts;

    public PlayerRefreshQueueService(
        INexusDbContextFactory db,
        IExternalDataPlayerService players,
        IInternalDataPlayerWatcher watcher,
        IGameDataLookups lookups,
        IEnumerable<IExternalDataCacheResetter> resetters,
        IRefreshTtlProvider ttl,
        IRefreshCategoryPolicy policy,
        ILogger<PlayerRefreshQueueService> log)
    {
        mDb = db;
        mPlayers = players;
        mWatcher = watcher;
        mLookups = lookups;
        // Materialise once — DI hands back a fresh enumerable per resolution,
        // and we want the same set every time the self-heal path fires.
        mResetters = resetters.ToList();
        mTtl = ttl;
        mPolicy = policy;
        mLog = log;
        mWatcher.Observed += OnObserved;
        mWorker = Task.Run(WorkerLoopAsync);
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mWatcher.Observed -= OnObserved;
        try { mWakeup.Release(); } catch { }
        try { mWorker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        mWakeup.Dispose();
        mUpsertGate.Dispose();
    }

    private void OnObserved(ObservedPlayer p)
    {
        if (p.ContentId == 0) return;
        lock (mObservedThisSession)
        {
            if (!mObservedThisSession.Add(p.ContentId)) return;
        }
        // Fire-and-forget on the threadpool — the watcher's framework-thread
        // path mustn't block on EF queries.
        _ = Task.Run(() => EnqueueStaleAsync(p.ContentId, RefreshPriority.High));
    }

    // ─── Enqueue API ────────────────────────────────────────────────────────

    public async Task EnqueueAsync(ulong contentId, RefreshCategory category,
                                   RefreshPriority priority, CancellationToken ct = default)
    {
        if (contentId == 0) return;
        // Policy gate: a disabled category is never queued, even via direct
        // EnqueueAsync from foreign code. Re-enabling later flips IsEnabled
        // and a subsequent stale-check picks up the row.
        if (!mPolicy.IsEnabled(category)) return;
        await UpsertAsync(contentId, category, priority, ct).ConfigureAwait(false);
        Enqueued?.Invoke(contentId, category);
        Wake();
    }

    public async Task<IReadOnlyList<RefreshCategory>> EnqueueStaleAsync(
        ulong contentId, RefreshPriority priority, CancellationToken ct = default)
    {
        if (contentId == 0) return Array.Empty<RefreshCategory>();

        // Bump anything already sitting in the queue for this player up to the
        // requested priority FIRST. The staleness check below only flags
        // categories whose freshness window is past TTL; if the background
        // sweep had previously queued e.g. FreeCompany at Low because no FC
        // row existed yet, that row would stay at Low forever even after the
        // user opened the detail panel — because the freshness check doesn't
        // see "queued but not yet processed" as a reason to re-enqueue. The
        // promotion bridges that asymmetry: anything currently in the queue
        // for this player rides up alongside the freshly-detected stale rows.
        var promoted = await PromoteExistingRowsAsync(contentId, priority, ct).ConfigureAwait(false);

        var obs = await LookupObservationAsync(contentId, ct).ConfigureAwait(false);
        if (obs is null)
        {
            if (promoted.Count > 0)
            {
                foreach (var cat in promoted) Enqueued?.Invoke(contentId, cat);
                Wake();
            }
            return Array.Empty<RefreshCategory>();
        }

        // No lodestone id yet → resolution task is the ONLY thing we need now.
        // Sub-resource categories will be enqueued by the worker after it
        // resolves the id (see ProcessLodestoneIdAsync).
        if (obs.LodestoneId is not { } lid)
        {
            await UpsertAsync(contentId, RefreshCategory.LodestoneId, priority, ct).ConfigureAwait(false);
            Enqueued?.Invoke(contentId, RefreshCategory.LodestoneId);
            foreach (var cat in promoted)
                if (cat != RefreshCategory.LodestoneId) Enqueued?.Invoke(contentId, cat);
            Wake();
            return new[] { RefreshCategory.LodestoneId };
        }

        var stale = await ComputeStaleSubResourcesAsync(lid, ct).ConfigureAwait(false);
        var staleSet = stale.ToHashSet();

        foreach (var cat in stale)
            await UpsertAsync(contentId, cat, priority, ct).ConfigureAwait(false);

        if (stale.Count == 0 && promoted.Count == 0) return stale;

        foreach (var cat in stale)
            Enqueued?.Invoke(contentId, cat);
        // Promoted rows whose category wasn't also in the stale set still
        // changed state (priority bump) and the UI's queue-status view
        // wants to know — fire the event for them too.
        foreach (var cat in promoted)
            if (!staleSet.Contains(cat)) Enqueued?.Invoke(contentId, cat);
        Wake();

        return stale;
    }

    /// <summary>Bulk-promotes every queue row for <paramref name="contentId"/>
    /// whose current priority is strictly less urgent than
    /// <paramref name="priority"/> (i.e. numerically larger). Matches the
    /// strict-bump branch of <see cref="UpsertCoreAsync"/>: priority + enqueued_at
    /// move forward and the failure bookkeeping is reset, so a row that hit
    /// MaxAttempts on a Low-priority background sweep gets a fresh shot when
    /// the user explicitly selects the player.</summary>
    private async Task<IReadOnlyList<RefreshCategory>> PromoteExistingRowsAsync(
        ulong contentId, RefreshPriority priority, CancellationToken ct)
    {
        try { await mUpsertGate.WaitAsync(ct).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return Array.Empty<RefreshCategory>(); }
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await ctx.Set<InternalRefreshQueueEntity>()
                .Where(e => e.ContentId == contentId && (byte)e.Priority > (byte)priority)
                .ToListAsync(ct).ConfigureAwait(false);
            if (rows.Count == 0) return Array.Empty<RefreshCategory>();

            var now = DateTime.UtcNow;
            foreach (var row in rows)
            {
                row.Priority = priority;
                row.EnqueuedAt = now;
                row.AttemptCount = 0;
                row.LastFailedAt = null;
            }
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            return rows.Select(r => r.Category).ToList();
        }
        finally
        {
            try { mUpsertGate.Release(); }
            catch (ObjectDisposedException) { /* disposed during shutdown */ }
        }
    }

    public async Task EnqueueAllAsync(ulong contentId, RefreshPriority priority, CancellationToken ct = default)
    {
        if (contentId == 0) return;
        var obs = await LookupObservationAsync(contentId, ct).ConfigureAwait(false);
        if (obs is null) return;

        // Resolution still owed → queue that first, then EVERY sub-resource
        // category (so the worker re-fetches them all after the id arrives).
        if (obs.LodestoneId is null)
        {
            await UpsertAsync(contentId, RefreshCategory.LodestoneId, priority, ct).ConfigureAwait(false);
            Enqueued?.Invoke(contentId, RefreshCategory.LodestoneId);
        }

        // Policy gate: even an explicit "Refresh" (Immediate priority) skips
        // disabled categories. The user's settings choice wins over the
        // force-fetch intent — matches the contract that disabled means "we
        // never pull this data, period".
        foreach (var cat in RefreshCategoryClassification.SubResources)
            if (mPolicy.IsEnabled(cat))
                await UpsertAsync(contentId, cat, priority, ct).ConfigureAwait(false);
        foreach (var cat in RefreshCategoryClassification.SubResources)
            if (mPolicy.IsEnabled(cat))
                Enqueued?.Invoke(contentId, cat);
        Wake();
    }

    private async Task UpsertAsync(ulong contentId, RefreshCategory category,
                                   RefreshPriority priority, CancellationToken ct)
    {
        try { await mUpsertGate.WaitAsync(ct).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }
        try
        {
            await UpsertCoreAsync(contentId, category, priority, ct).ConfigureAwait(false);
        }
        finally
        {
            try { mUpsertGate.Release(); }
            catch (ObjectDisposedException) { /* disposed during shutdown */ }
        }
    }

    private async Task UpsertCoreAsync(ulong contentId, RefreshCategory category,
                                       RefreshPriority priority, CancellationToken ct)
    {
        await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await ctx.Set<InternalRefreshQueueEntity>()
            .FirstOrDefaultAsync(e => e.ContentId == contentId && e.Category == category, ct)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            ctx.Set<InternalRefreshQueueEntity>().Add(new InternalRefreshQueueEntity
            {
                ContentId = contentId,
                Category = category,
                Priority = priority,
                EnqueuedAt = now,
                AttemptCount = 0,
            });
        }
        else if ((byte)priority < (byte)existing.Priority)
        {
            existing.Priority = priority;
            existing.EnqueuedAt = now;
            // A priority bump is a "try this again, sooner" signal. Reset the
            // failure bookkeeping so the worker actually picks the row up
            // instead of treating it as still inside the backoff cooldown or
            // past the attempt cap — without this, a row that hit MaxAttempts
            // on a flaky background sweep stayed locked out even after the
            // user explicitly clicked Refresh, because PickNextAsync's filter
            // applies to every priority lane equally.
            existing.AttemptCount = 0;
            existing.LastFailedAt = null;
        }
        else if (priority == RefreshPriority.Immediate)
        {
            // Same-priority re-upsert at Immediate is the user-recovery path
            // for an exhausted row: the existing row is already at Immediate
            // (so the strict-bump branch above wouldn't fire), but the caller
            // is re-asking — typically a second Refresh click on an exhausted
            // row, or a re-select of the detail panel. Treat it as a fresh
            // "try now" signal and clear retry bookkeeping. We don't apply
            // this reset to non-Immediate same-priority re-upserts because
            // those are routine background calls (the watcher re-fires
            // EnqueueStaleAsync on every session refresh) and shouldn't
            // disturb a retry cycle that's mid-backoff.
            existing.EnqueuedAt = now;
            existing.AttemptCount = 0;
            existing.LastFailedAt = null;
        }
        else
        {
            return;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private void Wake()
    {
        try { mWakeup.Release(); } catch { /* already at 1 or disposed */ }
    }

    // ─── Worker loop ────────────────────────────────────────────────────────

    private static readonly TimeSpan IdleFallbackWait = TimeSpan.FromMinutes(5);

    /// <summary>Computes how long the worker can sleep while PickNextAsync
    /// has nothing eligible. Caps at <see cref="IdleFallbackWait"/> but
    /// shortens to the time until the earliest cooldown-waiting row clears
    /// its backoff window — so the worker resumes on its own once a row
    /// transitions back into eligibility, instead of waiting for a Wake()
    /// from an unrelated enqueue path.
    /// <para>If no cooldown-waiting candidates exist (everything is
    /// exhausted, or every non-Immediate row has never failed), falls back
    /// to the full window. A short safety margin is added so the cutoff
    /// comparison in PickNextAsync (strict less-than) reliably sees the
    /// row as past-cutoff after the wait.</para></summary>
    private async Task<TimeSpan> ComputeIdleWaitAsync(CancellationToken ct)
    {
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var allowed = BuildAllowedCategories();
            var earliestFail = await ctx.Set<InternalRefreshQueueEntity>()
                .Where(e => e.AttemptCount < MaxAttempts
                            && e.Priority != RefreshPriority.Immediate
                            && e.LastFailedAt != null
                            && allowed.Contains(e.Category))
                .MinAsync(e => (DateTime?)e.LastFailedAt, ct)
                .ConfigureAwait(false);
            if (earliestFail is null) return IdleFallbackWait;
            var wakeAt = earliestFail.Value + FailureBackoff + TimeSpan.FromMilliseconds(100);
            var wait = wakeAt - DateTime.UtcNow;
            if (wait <= TimeSpan.Zero) return TimeSpan.Zero;
            return wait < IdleFallbackWait ? wait : IdleFallbackWait;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "ComputeIdleWaitAsync failed; falling back to {Wait}", IdleFallbackWait);
            return IdleFallbackWait;
        }
    }

    private async Task WorkerLoopAsync()
    {
        var ct = mDb.LifetimeToken;
        while (!ct.IsCancellationRequested)
        {
            InternalRefreshQueueEntity? next;
            try
            {
                next = await PickNextAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                mLog.LogWarning(ex, "Refresh queue pick failed; sleeping.");
                await SafeDelay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                continue;
            }

            if (next is null)
            {
                // Bound the idle wait to the earliest cooldown clearance so a
                // queue of backoff-waiting rows actually gets processed when
                // its 60-min window expires. Wake() is only fired on enqueue
                // paths — without this, the worker would sleep for the full
                // 5-min fallback even with rows whose cooldown has lapsed
                // half a minute ago, and the diagnostics UI would correctly
                // show eligibleNow > 0 while nothing actually moves.
                var idleWait = await ComputeIdleWaitAsync(ct).ConfigureAwait(false);
                try { await mWakeup.WaitAsync(idleWait, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await ProcessOneAsync(next, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                mLog.LogWarning(ex, "Refresh worker crashed on {Cid}/{Cat}",
                    next.ContentId, next.Category);
                await MarkFailedAsync(next.ContentId, next.Category, ct).ConfigureAwait(false);
            }

            try { await Task.Delay(WorkerGap, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<QueueStatusForContent> GetQueueStatusForAsync(
        ulong contentId, CancellationToken ct = default)
    {
        if (contentId == 0) return new QueueStatusForContent(0, 0, null);
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var cutoff = DateTime.UtcNow - FailureBackoff;
            var allowed = BuildAllowedCategories();
            // Eligibility mirrors PickNextAsync exactly so the count we report
            // reflects what the worker can actually pull on its next tick —
            // including the per-category policy filter so the badge stops
            // counting orphan rows for newly-disabled categories.
            var eligible = ctx.Set<InternalRefreshQueueEntity>()
                .Where(e => e.AttemptCount < MaxAttempts
                            && (e.Priority == RefreshPriority.Immediate
                                || e.LastFailedAt == null
                                || e.LastFailedAt < cutoff)
                            && allowed.Contains(e.Category));

            var ownRows = await eligible
                .Where(e => e.ContentId == contentId)
                .OrderBy(e => e.Priority).ThenBy(e => e.Category).ThenBy(e => e.EnqueuedAt)
                .Select(e => new { e.Priority, e.Category, e.EnqueuedAt })
                .ToListAsync(ct).ConfigureAwait(false);
            if (ownRows.Count == 0) return new QueueStatusForContent(0, 0, null);

            var head = ownRows[0];
            // "Ahead of head" means strictly higher in the same ORDER BY tuple.
            // SQLite-translatable form below uses lexicographic comparison via
            // OR-cascades. Anything not for this character that beats the head
            // is counted as a row ahead.
            var ahead = await eligible
                .Where(e => e.ContentId != contentId)
                .Where(e =>
                    e.Priority < head.Priority
                    || (e.Priority == head.Priority && e.Category < head.Category)
                    || (e.Priority == head.Priority && e.Category == head.Category
                        && e.EnqueuedAt < head.EnqueuedAt))
                .CountAsync(ct).ConfigureAwait(false);

            return new QueueStatusForContent(ownRows.Count, ahead, head.Category);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "GetQueueStatusForAsync failed for {Cid}", contentId);
            return new QueueStatusForContent(0, 0, null);
        }
    }

    private async Task<InternalRefreshQueueEntity?> PickNextAsync(CancellationToken ct)
    {
        await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
        var cutoff = DateTime.UtcNow - FailureBackoff;
        var allowed = BuildAllowedCategories();
        // Immediate is documented to "bypass backoff" — honor that here, but
        // ONLY for the failure cooldown. The attempt cap is universal: an
        // Immediate row that has failed MaxAttempts times stops being picked
        // automatically; UpsertAsync's same-priority Immediate branch lets
        // the user click Refresh again to reset its bookkeeping and revive it.
        // Without the universal cap an Immediate row that keeps failing would
        // be re-picked every WorkerGap forever (this is the regression that
        // produced the attempt_count=1250 stuck-row bug).
        // <para>The allowed-category filter excludes rows whose category was
        // turned off after they were queued. Those rows linger in the table
        // (intentional — they remember the original priority/enqueued_at)
        // and become eligible again the moment the user re-enables the
        // category. The maintenance contributor leaves them alone because
        // AttemptCount stays at 0.</para>
        return await ctx.Set<InternalRefreshQueueEntity>()
            .Where(e => e.AttemptCount < MaxAttempts
                        && (e.Priority == RefreshPriority.Immediate
                            || e.LastFailedAt == null
                            || e.LastFailedAt < cutoff)
                        && allowed.Contains(e.Category))
            .OrderBy(e => e.Priority)
            .ThenBy(e => e.Category)
            .ThenBy(e => e.EnqueuedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Builds the set of currently-enabled categories by combining
    /// the static "what's mandatory" classification with the runtime policy.
    /// Re-evaluated per pick / per status query so a settings toggle takes
    /// effect on the next worker iteration without a service restart.</summary>
    private RefreshCategory[] BuildAllowedCategories()
    {
        var list = new List<RefreshCategory>(RefreshCategoryClassification.All.Count);
        foreach (var c in RefreshCategoryClassification.All)
            if (mPolicy.IsEnabled(c)) list.Add(c);
        return list.ToArray();
    }

    private async Task ProcessOneAsync(InternalRefreshQueueEntity row, CancellationToken ct)
    {
        // Stamp the attempt up front so a process crash doesn't loop forever.
        await using (var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            ctx.Attach(row);
            row.LastAttemptedAt = DateTime.UtcNow;
            row.AttemptCount++;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (row.Category == RefreshCategory.LodestoneId)
        {
            await ProcessLodestoneIdAsync(row, ct).ConfigureAwait(false);
            return;
        }

        await ProcessSubResourceAsync(row, ct).ConfigureAwait(false);
    }

    private async Task ProcessLodestoneIdAsync(InternalRefreshQueueEntity row, CancellationToken ct)
    {
        var obs = await LookupObservationAsync(row.ContentId, ct).ConfigureAwait(false);
        if (obs is null)
        {
            // Observation went away between enqueue and pick — drop the task.
            await MarkDoneAsync(row.ContentId, row.Category, ct).ConfigureAwait(false);
            return;
        }

        // Already resolved (race: the watcher or another path landed an id
        // between enqueue and pick). Treat as done and let the rest of the
        // queue continue.
        if (obs.LodestoneId is not null)
        {
            await CascadeStaleSubResourcesAsync(row.ContentId, obs.LodestoneId.Value, row.Priority, ct)
                .ConfigureAwait(false);
            await MarkDoneAsync(row.ContentId, row.Category, ct).ConfigureAwait(false);
            return;
        }

        var worldName = mLookups.GetWorldName(obs.HomeWorldId);
        if (string.IsNullOrEmpty(worldName) || string.IsNullOrEmpty(obs.Name))
        {
            // Can't search without a world name — most likely Lumina wasn't
            // ready yet or HomeWorldId is unknown. Stamp the failure so the
            // backoff cooldown applies on the next attempt and the attempt
            // cap eventually stops the row. Returning silently here used to
            // leave LastFailedAt unchanged, which let the worker re-pick the
            // row every WorkerGap until the cap was hit — a tight loop.
            await MarkFailedAsync(row.ContentId, row.Category, ct).ConfigureAwait(false);
            return;
        }

        var results = await mPlayers.SearchAsync(obs.Name, worldName, ct).ConfigureAwait(false);
        // Lodestone's search response often omits the server column; accept
        // null/empty server as "trust the search query we already constrained
        // server-side". Name match is case-insensitive too.
        var match = results.FirstOrDefault(r =>
            string.Equals(r.Name, obs.Name, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(r.Server)
                || string.Equals(r.Server, worldName, StringComparison.OrdinalIgnoreCase)));

        if (match is null)
        {
            // No hit — leave the row failed; backoff will retry it later in
            // case the character shows up on Lodestone after a relog.
            await MarkFailedAsync(row.ContentId, row.Category, ct).ConfigureAwait(false);
            return;
        }

        await mWatcher.SetLodestoneIdAsync(row.ContentId, match.LodestoneId, ct).ConfigureAwait(false);
        await CascadeStaleSubResourcesAsync(row.ContentId, match.LodestoneId, row.Priority, ct)
            .ConfigureAwait(false);

        await MarkDoneAsync(row.ContentId, row.Category, ct).ConfigureAwait(false);
        Completed?.Invoke(row.ContentId, row.Category);
    }

    private async Task ProcessSubResourceAsync(InternalRefreshQueueEntity row, CancellationToken ct)
    {
        var obs = await LookupObservationAsync(row.ContentId, ct).ConfigureAwait(false);
        if (obs is null || obs.LodestoneId is not { } lid)
        {
            // No lodestone id (yet) → can't do remote fetch. Mark failed so
            // backoff kicks in; the LodestoneId task should land first and
            // unblock us.
            await MarkFailedAsync(row.ContentId, row.Category, ct).ConfigureAwait(false);
            return;
        }

        var include = MapCategoryToInclude(row.Category);
        // ExternalDataPlayerService.GetAsync upserts the relevant DB rows as a
        // side effect of fetching. A null return means both data sources
        // (FFXIVCollect + Lodestone profile) failed to land identity and
        // GetAsync bailed to ReadFromDbAsync — no fresh data hit the disk.
        //
        // forceLatest=true tells FFXIVCollect to pull a fresh sync from
        // Lodestone before answering — this is the explicit-refresh path
        // (user clicked, or TTL sweep flagged the row stale), so we want
        // the upstream to round-trip Lodestone instead of returning its
        // own cached snapshot from before a transfer / rename.
        var fetched = await mPlayers.GetAsync(lid, include, forceLatest: true, ct).ConfigureAwait(false);
        if (fetched is null)
        {
            await MarkFailedAsync(row.ContentId, row.Category, ct).ConfigureAwait(false);
            return;
        }

        // Per-category success check: identity can resolve via either
        // FFXIVCollect or Lodestone, but the specific sub-resource the queue
        // asked for has its own source that can fail independently. Without
        // this, a successful identity resolve via FFXIVCollect would mark
        // Profile done even when Lodestone returned null and no profile row
        // landed — that's the exact path that left player 15388229 stuck on
        // the cached "Knuff" FC. Treat per-category null as a transient
        // failure: backoff + retry (and eventually exhaust at MaxAttempts
        // for genuinely-permanent-missing data like a private profile).
        if (!CategorySucceeded(fetched, row.Category, obs))
        {
            await MarkFailedAsync(row.ContentId, row.Category, ct).ConfigureAwait(false);
            return;
        }

        await MarkDoneAsync(row.ContentId, row.Category, ct).ConfigureAwait(false);
        Completed?.Invoke(row.ContentId, row.Category);
    }

    private async Task CascadeStaleSubResourcesAsync(
        ulong contentId, ulong lodestoneId, RefreshPriority priority, CancellationToken ct)
    {
        var stale = await ComputeStaleSubResourcesAsync(lodestoneId, ct).ConfigureAwait(false);
        if (stale.Count == 0) return;
        foreach (var cat in stale)
            await UpsertAsync(contentId, cat, priority, ct).ConfigureAwait(false);
        foreach (var cat in stale)
            Enqueued?.Invoke(contentId, cat);
        Wake();
    }

    private async Task MarkDoneAsync(ulong contentId, RefreshCategory category, CancellationToken ct)
    {
        await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await ctx.Set<InternalRefreshQueueEntity>()
            .FirstOrDefaultAsync(e => e.ContentId == contentId && e.Category == category, ct)
            .ConfigureAwait(false);
        if (existing is null) return;
        ctx.Set<InternalRefreshQueueEntity>().Remove(existing);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task MarkFailedAsync(ulong contentId, RefreshCategory category, CancellationToken ct)
    {
        var exhausted = false;
        var triggerSelfHeal = false;
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var existing = await ctx.Set<InternalRefreshQueueEntity>()
                .FirstOrDefaultAsync(e => e.ContentId == contentId && e.Category == category, ct)
                .ConfigureAwait(false);
            if (existing is null) return;
            existing.LastFailedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            // ProcessOneAsync stamps AttemptCount BEFORE the category-specific
            // logic runs, so by the time we mark a failure here the counter
            // already reflects this attempt. Fire ExhaustedAttempts exactly
            // once — at the transition to MaxAttempts — so subscribers don't
            // see repeats on every subsequent backoff-cleared retry.
            exhausted = existing.AttemptCount == MaxAttempts;

            // Self-heal trigger: just crossed the soft cap AND the gap
            // between last_attempted_at and last_failed_at is too small to
            // have included a real network call. That signature is the
            // calling card of a degenerate cache row routing GetAsync into
            // an instant MarkFailedAsync. Triggers exactly once per row
            // because subsequent failures (attempts 6..10) won't match the
            // == 5 check, and the just-cleared cache means attempts 6..10
            // can no longer fast-fail through the same path.
            if (existing.AttemptCount == SoftAttemptCap
                && existing.LastFailedAt is { } lf
                && existing.LastAttemptedAt is { } la
                && (lf - la).Duration() < FastFailThreshold)
            {
                triggerSelfHeal = true;
            }
        }
        catch { /* worker mustn't crash on bookkeeping */ }

        if (triggerSelfHeal)
            await TrySelfHealAsync(contentId, category, ct).ConfigureAwait(false);
        if (exhausted) ExhaustedAttempts?.Invoke(contentId, category);
    }

    /// <summary>
    /// One-shot cache evict for a row that hit <see cref="SoftAttemptCap"/>
    /// with the cache-poison fast-fail signature. Deletes the per-category
    /// Lodestone-cache rows so attempts <c>SoftAttemptCap+1 .. MaxAttempts</c>
    /// run against a clean cache; if the underlying fetch is still broken
    /// (NetStone scrape failure, character truly deleted on Lodestone, …)
    /// the row will exhaust normally at <see cref="MaxAttempts"/>.
    /// <para>Termination guarantee: the trigger condition checks
    /// <c>AttemptCount == SoftAttemptCap</c>, so it fires exactly once per
    /// row regardless of how many subsequent failures occur. Post-heal
    /// attempts hit live endpoints (no cache to short-circuit through), so
    /// the timestamp gap will naturally grow past <see cref="FastFailThreshold"/>
    /// and the heal can't recur even if the count check is altered.</para>
    /// </summary>
    private async Task TrySelfHealAsync(ulong contentId, RefreshCategory category, CancellationToken ct)
    {
        try
        {
            var obs = await LookupObservationAsync(contentId, ct).ConfigureAwait(false);
            if (obs?.LodestoneId is not { } lid)
            {
                // LodestoneId category fast-fails before the id is resolved
                // — we don't know what to reset. The queue's MaxAttempts cap
                // still terminates the retry loop, just without a cache flush.
                return;
            }

            // Build the context once and hand it to every resetter. World
            // name comes from Lumina (mLookups); when it's not available
            // the Lodestone resetter just skips its search-cache branch and
            // still cleans the id-keyed families.
            var worldName = obs.HomeWorldId != 0 ? mLookups.GetWorldName(obs.HomeWorldId) : null;
            var resetCtx = new ResetContext(lid, obs.Name, worldName);

            // Fan out to every registered resetter. Each module owns its
            // own cache-key shape; we just hand over the context and
            // accept whatever count the module reports. SQLite's single
            // writer serialises deletes anyway, so no point parallelising.
            var total = 0;
            foreach (var resetter in mResetters)
                total += await resetter.ResetAsync(resetCtx, ct).ConfigureAwait(false);

            mLog.LogInformation(
                "Refresh queue self-heal for (cid={Cid}, cat={Cat}, lid={Lid}): evicted {Count} cache row(s) across {Modules} module(s); {Remaining} more attempts allowed before MaxAttempts.",
                contentId, category, lid, total, mResetters.Count, MaxAttempts - SoftAttemptCap);
        }
        catch (OperationCanceledException) { /* plugin shutdown */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Refresh queue self-heal failed for (cid={Cid}, cat={Cat})", contentId, category);
        }
    }

    /// <summary>Maps the queue's per-category success criteria to the
    /// corresponding nullable field on the returned <see cref="Player"/>.
    /// "Success" means the fetch landed enough data to populate the field;
    /// when the field stays null the persistence side skipped the upsert and
    /// the queue should treat the attempt as failed so backoff/retry kicks
    /// in. The FreeCompany branch is the only one that returns success on a
    /// null field: a player with no CompanyTag legitimately has no FC, so
    /// there's nothing to fetch and nothing to write — the queue task is
    /// already complete.</summary>
    private static bool CategorySucceeded(
        NexusKit.Modules.ExternalData.Models.Player fetched,
        RefreshCategory category,
        InternalObservedPlayerEntity obs) => category switch
        {
            RefreshCategory.Profile => fetched.Profile is not null,
            RefreshCategory.ClassJobs => fetched.ClassJobs is { Count: > 0 },
            RefreshCategory.Gear => fetched.Gear is not null,
            RefreshCategory.FreeCompany =>
                string.IsNullOrEmpty(obs.CompanyTag) || fetched.FreeCompany is not null,
            RefreshCategory.Mounts => fetched.Collections?.Mounts is not null,
            RefreshCategory.Minions => fetched.Collections?.Minions is not null,
            RefreshCategory.Achievements => fetched.Collections?.Achievements is not null,
            _ => true,
        };

    private async Task<InternalObservedPlayerEntity?> LookupObservationAsync(
        ulong contentId, CancellationToken ct)
    {
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await ctx.Set<InternalObservedPlayerEntity>()
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.ContentId == contentId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Observation lookup failed for ContentId {Cid}", contentId);
            return null;
        }
    }


    private static PlayerInclude MapCategoryToInclude(RefreshCategory category) => category switch
    {
        RefreshCategory.Profile => PlayerInclude.Profile,
        RefreshCategory.ClassJobs => PlayerInclude.ClassJobs,
        RefreshCategory.Gear => PlayerInclude.Gear,
        RefreshCategory.FreeCompany => PlayerInclude.FreeCompany,
        RefreshCategory.Mounts => PlayerInclude.Mounts,
        RefreshCategory.Minions => PlayerInclude.Minions,
        RefreshCategory.Achievements => PlayerInclude.Achievements,
        _ => PlayerInclude.None,
    };

    // ─── Freshness ──────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<RefreshCategory>> ComputeStaleSubResourcesAsync(
        ulong lodestoneId, CancellationToken ct)
    {
        var ttl = mTtl.GetTtl();
        var cutoff = DateTime.UtcNow - ttl;
        var stale = new List<RefreshCategory>(7);

        await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Policy gate: disabled categories are never reported stale. Their
        // UpdatedAt rows still exist in the DB (we don't delete on toggle off),
        // so re-enabling later naturally surfaces them as stale on the next
        // sweep if past TTL. The DB roundtrips below are skipped per-category
        // to keep this path cheap when the user turns several off.
        if (mPolicy.IsEnabled(RefreshCategory.Profile))
        {
            var profileAt = await ctx.Set<PlayerProfileEntity>()
                .Where(p => p.LodestoneId == lodestoneId)
                .Select(p => (DateTime?)p.UpdatedAt)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (profileAt is null || profileAt < cutoff) stale.Add(RefreshCategory.Profile);
        }

        if (mPolicy.IsEnabled(RefreshCategory.ClassJobs))
        {
            var classJobsAt = await ctx.Set<PlayerClassJobEntity>()
                .Where(j => j.LodestoneId == lodestoneId)
                .Select(j => (DateTime?)j.UpdatedAt)
                .OrderByDescending(d => d)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (classJobsAt is null || classJobsAt < cutoff) stale.Add(RefreshCategory.ClassJobs);
        }

        if (mPolicy.IsEnabled(RefreshCategory.Gear))
        {
            var gearAt = await ctx.Set<PlayerGearSlotEntity>()
                .Where(g => g.LodestoneId == lodestoneId)
                .Select(g => (DateTime?)g.UpdatedAt)
                .OrderByDescending(d => d)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (gearAt is null || gearAt < cutoff) stale.Add(RefreshCategory.Gear);
        }

        // FreeCompany is mandatory in the policy, so we always check it. The
        // existing has-FC gate on the profile still applies — players without
        // a company tag don't get an FC row enqueued.
        var fcId = await ctx.Set<PlayerProfileEntity>()
            .Where(p => p.LodestoneId == lodestoneId)
            .Select(p => p.FreeCompanyLodestoneId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(fcId))
        {
            var fcAt = await ctx.Set<FreeCompanyEntity>()
                .Where(f => f.LodestoneId == fcId)
                .Select(f => (DateTime?)f.UpdatedAt)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (fcAt is null || fcAt < cutoff) stale.Add(RefreshCategory.FreeCompany);
        }

        if (mPolicy.IsEnabled(RefreshCategory.Mounts))
            await CheckCollectionAsync(ctx, lodestoneId, CollectionKind.Mounts,
                cutoff, RefreshCategory.Mounts, stale, ct).ConfigureAwait(false);
        if (mPolicy.IsEnabled(RefreshCategory.Minions))
            await CheckCollectionAsync(ctx, lodestoneId, CollectionKind.Minions,
                cutoff, RefreshCategory.Minions, stale, ct).ConfigureAwait(false);
        if (mPolicy.IsEnabled(RefreshCategory.Achievements))
            await CheckCollectionAsync(ctx, lodestoneId, CollectionKind.Achievements,
                cutoff, RefreshCategory.Achievements, stale, ct).ConfigureAwait(false);

        return stale;
    }

    private static async Task CheckCollectionAsync(
        PluginDbContext ctx, ulong lodestoneId, CollectionKind kind,
        DateTime cutoff, RefreshCategory category,
        List<RefreshCategory> sink, CancellationToken ct)
    {
        var at = await ctx.Set<PlayerCollectionStatsEntity>()
            .Where(s => s.LodestoneId == lodestoneId && s.Kind == kind)
            .Select(s => (DateTime?)s.UpdatedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (at is null || at < cutoff) sink.Add(category);
    }

    private static async Task SafeDelay(TimeSpan span, CancellationToken ct)
    {
        try { await Task.Delay(span, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}