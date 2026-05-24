using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Core;
using NexusKit.GameData;
using NexusKit.GameData.ObjectTables;
using NexusKit.Modules.InternalData.Persistence;
using NexusKit.Persistence;

namespace NexusKit.Modules.InternalData.Players;

internal sealed class InternalDataPlayerWatcher : IInternalDataPlayerWatcher, IDisposable
{
    /// <summary>EF projection target for HydrateAsync. Mirrors the columns we
    /// need to construct the slim <see cref="ObservedPlayer"/> at startup; the
    /// raw Customize blob still flows through here so race/gender can be
    /// picked off (Bytes 0/1), but it's discarded right after construction
    /// instead of being held in <see cref="mObserved"/>.</summary>
    private sealed record HydrateRow(
        ulong ContentId,
        ulong? LodestoneId,
        string Name,
        uint HomeWorldId,
        uint ClassJobId,
        byte Level,
        byte[]? Customize,
        string? CompanyTag,
        uint? CurrentMountId,
        uint? CurrentMinionId,
        uint OnlineStatusId,
        DateTime UpdatedAt,
        bool HasNotes);


    // Scan once per ~1s — players nearby don't churn faster than that and we don't want
    // to pay an ObjectTable walk every single frame.
    private const int ScanIntervalFrames = 60;

    private readonly IFramework mFramework;
    private readonly IObjectTable mObjectTable;
    private readonly IClientState mClientState;
    private readonly ICondition mCondition;
    private readonly IGameDataLookups mGameData;
    private readonly ILocalPlayerContext mLocalPlayer;
    private readonly INexusDbContextFactory mDb;
    private readonly ILogger<InternalDataPlayerWatcher> mLog;

    private readonly object mLock = new();
    // Keyed by ContentId — stable across sessions and unique per character. Beats
    // (name, world) because two same-named characters on different shards collapse cleanly.
    private readonly Dictionary<ulong, ObservedPlayer> mObserved = new();
    // Replaced wholesale every scan tick — the ContentIds the local player can
    // currently see in the world. Read from the UI thread for the "Current" filter.
    private IReadOnlySet<ulong> mCurrentlyVisible = new HashSet<ulong>();
    // Serializes ProcessAsync invocations. The watcher fires ProcessAsync as
    // a Task.Run from every ScanIntervalFrames-th framework update; if one
    // run takes longer than the scan interval (e.g. cold DB pages on plugin
    // load, a slow disk tick), the next tick's call could otherwise overlap
    // with it and produce concurrent INSERTs for the same content_id —
    // SQLite then fails the loser with a UNIQUE constraint violation. We
    // tryacquire with zero timeout so a busy run just causes the new tick
    // to drop; the data isn't lost because the next scan ~1s later sees the
    // same visible players and applies the same upsert.
    private readonly SemaphoreSlim mProcessGate = new(initialCount: 1, maxCount: 1);
    private int mFrameCounter;
    private bool mDisposed;
    // Bumped every time the in-memory mObserved map mutates. Exposed via
    // IInternalDataPlayerWatcher.Revision so UI consumers (currently the
    // user-filter memoization in PlayerListPanel) can detect "list changed
    // since I last computed a filtered view" without snapshotting the whole
    // collection. Interlocked because reads can happen on any thread.
    private long mRevision;

    public event Action<ObservedPlayer>? Observed;
    public event Action<PlayerObservationEvent>? ObservationProcessed;

    public IReadOnlyList<ObservedPlayer> Recent
    {
        get
        {
            lock (mLock)
                return mObserved.Values
                    .OrderByDescending(o => o.LastSeen)
                    .ToList();
        }
    }

    public IReadOnlySet<ulong> CurrentlyVisible
    {
        get
        {
            lock (mLock) return mCurrentlyVisible;
        }
    }

    public bool TryGetObserved(ulong contentId, out ObservedPlayer? player)
    {
        lock (mLock)
        {
            if (mObserved.TryGetValue(contentId, out var p))
            {
                player = p;
                return true;
            }
        }
        player = null;
        return false;
    }

    public long Revision => Interlocked.Read(ref mRevision);

    public InternalDataPlayerWatcher(
        IFramework framework,
        IObjectTable objectTable,
        IClientState clientState,
        ICondition condition,
        IGameDataLookups gameData,
        ILocalPlayerContext localPlayer,
        INexusDbContextFactory db,
        ILogger<InternalDataPlayerWatcher> log)
    {
        mFramework = framework;
        mObjectTable = objectTable;
        mClientState = clientState;
        mCondition = condition;
        mGameData = gameData;
        mLocalPlayer = localPlayer;
        mDb = db;
        mLog = log;

        // IFramework.Update fires on Dalamud's framework thread — same thread that owns
        // the ObjectTable. The handler below therefore reads the table directly without
        // needing IFramework.RunOnFrameworkThread. Any work that touches the ObjectTable
        // from elsewhere (e.g. a UI click handler) MUST go through RunOnFrameworkThread.
        mFramework.Update += OnFrameworkUpdate;

        // Reload the previously-persisted observations into memory so the recent list
        // doesn't show empty on every plugin reload before the watcher's first tick.
        _ = HydrateAsync();
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mFramework.Update -= OnFrameworkUpdate;
        mProcessGate.Dispose();
    }

    private async Task HydrateAsync()
    {
        if (mDb.IsStopping) return;
        // CreateDbContextAsync without an explicit ct uses the factory's
        // LifetimeToken automatically; we still need the local `ct` to pass
        // into the EF async operations below, which carry no ambient token.
        var ct = mDb.LifetimeToken;
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync().ConfigureAwait(false);
            // Project at SELECT time to the slim shape we actually hold in
            // memory: race + gender bytes plus HasNotes indicator, instead of
            // the full 26-byte Customize array and the entire notes text.
            // For 14k+ rows that means EF never materializes the heavy fields,
            // saving GC pressure during startup and trimming steady-state
            // memory by roughly the size of all stored notes + customize
            // bytes (~1.5 MB savings on the user's current DB).
            var rows = await ctx.Set<InternalObservedPlayerEntity>()
                .Select(e => new HydrateRow(
                    e.ContentId,
                    e.LodestoneId,
                    e.Name,
                    e.HomeWorldId,
                    e.ClassJobId,
                    e.Level,
                    // SQLite stores Customize as a BLOB. Pulling only bytes 0..1
                    // via SQL isn't expressible through EF's column-binding
                    // surface, so we still fetch the full array per-row but
                    // immediately project to race/gender bytes below — the
                    // BLOB is GC'd once the projection lands.
                    e.Customize,
                    e.CompanyTag,
                    e.CurrentMountId,
                    e.CurrentMinionId,
                    e.OnlineStatusId,
                    e.UpdatedAt,
                    e.Notes != null && e.Notes.Length > 0))
                .ToListAsync(ct).ConfigureAwait(false);

            // FirstSeen / LastSeen come from the encounter table now — one
            // grouped read so the per-character probe doesn't fan out into
            // 14k SELECTs. Empty aggregates (no encounter rows) fall back to
            // UpdatedAt, which tracks "we last touched this observed_player
            // row" closely enough for sort order during the gap between a
            // migration and the first new sighting.
            var aggregates = await ctx.Set<Encounters.InternalPlayerEncounterEntity>()
                .GroupBy(p => p.ContentId)
                .Select(g => new
                {
                    ContentId = g.Key,
                    First = g.Min(p => p.FirstSeenAt),
                    Last = g.Max(p => p.LastSeenAt),
                })
                .ToDictionaryAsync(a => a.ContentId, a => (a.First, a.Last), ct)
                .ConfigureAwait(false);

            lock (mLock)
            {
                foreach (var row in rows)
                {
                    var worldName = mGameData.GetWorldName(row.HomeWorldId) ?? string.Empty;
                    var (first, last) = aggregates.TryGetValue(row.ContentId, out var agg)
                        ? agg
                        : (row.UpdatedAt, row.UpdatedAt);
                    mObserved[row.ContentId] = new ObservedPlayer(
                        ContentId: row.ContentId,
                        LodestoneId: row.LodestoneId,
                        Name: row.Name,
                        HomeWorld: worldName,
                        HomeWorldId: row.HomeWorldId,
                        ClassJobId: row.ClassJobId,
                        Level: row.Level,
                        Race: row.Customize is { Length: >= 1 } cz ? cz[0] : (byte)0,
                        Gender: row.Customize is { Length: >= 2 } gz ? gz[1] : (byte)0,
                        CompanyTag: row.CompanyTag,
                        CurrentMountId: row.CurrentMountId,
                        CurrentMinionId: row.CurrentMinionId,
                        OnlineStatusId: row.OnlineStatusId,
                        FirstSeen: first,
                        LastSeen: last,
                        HasNotes: row.HasNotes);
                }
            }
            if (rows.Count > 0) Interlocked.Increment(ref mRevision);
        }
        catch (OperationCanceledException) { /* plugin shutdown */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "InternalData: hydrate from DB failed.");
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (++mFrameCounter < ScanIntervalFrames) return;
        mFrameCounter = 0;

        // Skip while in duty recorder playback — the ObjectTable lies in that mode and
        // we'd otherwise record ghosts of the recording's participants.
        if (mCondition[ConditionFlag.DutyRecorderPlayback]) return;
        if (!mClientState.IsLoggedIn) return;

        var now = DateTime.UtcNow;
        var snapshots = mObjectTable.GetVisiblePlayers().ToList();

        // Diff against the previous tick's set to flag "fresh sightings" — characters
        // that just became visible. SeenCount only increments on this transition (not
        // on every tick the player stays in range), so a 5-minute idle next to someone
        // doesn't end up as 300 seen events.
        var visibleIds = snapshots.Select(s => s.ContentId).ToHashSet();
        IReadOnlySet<ulong> previouslyVisible;
        lock (mLock)
        {
            previouslyVisible = mCurrentlyVisible;
            mCurrentlyVisible = visibleIds;
        }

        var tagged = snapshots
            .Select(s => (Snapshot: s, IsFreshSighting: !previouslyVisible.Contains(s.ContentId)))
            .ToList();

        // Compute the trust flag on the framework thread — ICondition reads are only
        // safe here. Snapshots taken in a duty / cutscene / zone transition drop the
        // FC tag (and may obscure other transient fields), so the history differ uses
        // this to suppress writes that would otherwise be phantoms.
        var canTrackChange = CanTrackChange();

        // Snapshot the local player's CurrentWorld here on the framework
        // thread. Subscribers downstream (encounter tracker etc.) run from
        // ProcessAsync's worker-thread dispatch and can't legally read
        // IObjectTable themselves — every value derived from the local
        // player has to be sampled before the off-thread hop. Routed
        // through ILocalPlayerContext for consistency with the plugin's
        // other local-player consumers; the impl is identical to
        // mObjectTable.GetSelf()?.CurrentWorldId. null when the table
        // doesn't yet expose a valid local player (zoning, not logged in);
        // subscribers store that as "world unknown".
        var localPlayerWorldId = mLocalPlayer.GetLocation()?.WorldId;

        // Capture happens on framework thread; persistence and enrichment off-thread.
        _ = Task.Run(() => ProcessAsync(tagged, now, canTrackChange, localPlayerWorldId));
    }

    private bool CanTrackChange()
    {
        // BoundByDuty (+ its alternate variants): inside any instanced content.
        // Cutscenes: in-engine or quest-event cutscene playback hides the FC slot.
        // BetweenAreas: zoning — the ObjectTable is mid-rebuild.
        // Mirrors the conditions documented in Sirensong/SimpleTweaks helpers — see
        // ConditionHelper.IsBoundByDuty / IsInCutscene / IsBetweenAreas references.
        return !mCondition[ConditionFlag.BoundByDuty]
            && !mCondition[ConditionFlag.BoundByDuty56]
            && !mCondition[ConditionFlag.BoundByDuty95]
            && !mCondition[ConditionFlag.WatchingCutscene]
            && !mCondition[ConditionFlag.WatchingCutscene78]
            && !mCondition[ConditionFlag.OccupiedInCutSceneEvent]
            && !mCondition[ConditionFlag.BetweenAreas]
            && !mCondition[ConditionFlag.BetweenAreas51];
    }

    private async Task ProcessAsync(
        IReadOnlyList<(VisiblePlayer Snapshot, bool IsFreshSighting)> snapshots,
        DateTime now,
        bool canTrackChange,
        uint? localPlayerCurrentWorldId)
    {
        if (mDb.IsStopping) return;
        // Try-acquire the serialization gate. If a previous tick's scan is
        // still running, drop this tick — the next ~1s scan re-sees the
        // same players and applies the same upsert, so nothing is lost.
        // Without this gate two overlapping scans both materialize the
        // "no existing row" branch for the same content_id and the second
        // SaveChangesAsync fails with a UNIQUE constraint violation.
        if (!await mProcessGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var ct = mDb.LifetimeToken;
            await using var ctx = await mDb.CreateDbContextAsync().ConfigureAwait(false);

            // IsFreshSighting is still computed on the framework thread (kept
            // in the tuple for any future consumer that wants it) but the
            // foreach body doesn't need it now that SeenCount is gone — the
            // encounter tracker derives "fresh sighting in this encounter"
            // from the (encounter_id, content_id) pair existence instead.
            var pendingEvents = new List<(ObservedPlayer Current, ObservedPlayer? Previous)>(snapshots.Count);
            foreach (var (snapshot, _) in snapshots)
            {
                var worldName = mGameData.GetWorldName(snapshot.HomeWorldId) ?? string.Empty;

                // Upsert the observation row. FirstSeen / LastSeen / SeenCount
                // are no longer persisted here — they're derived from the
                // encounter tables. The in-memory ObservedPlayer below still
                // carries FirstSeen / LastSeen; we keep them current using
                // the previously-cached in-memory entry (first sighting in
                // this session = now, otherwise inherit) so the player-list
                // sort doesn't have to re-query aggregates per tick.
                var existing = await ctx.Set<InternalObservedPlayerEntity>()
                    .FirstOrDefaultAsync(o => o.ContentId == snapshot.ContentId, ct).ConfigureAwait(false);
                var lodestoneId = existing?.LodestoneId;
                ObservedPlayer? cachedPrior;
                lock (mLock) { mObserved.TryGetValue(snapshot.ContentId, out cachedPrior); }
                var firstSeen = cachedPrior?.FirstSeen ?? now;

                // Live FC tag drops to empty during cutscenes, instances and cross-world
                // snapshots. Clobbering the stored tag with null on every transient causes
                // the history differ to fire a phantom null→tag "joined FC" row on the next
                // clean tick (the suppression in InternalDataHistoryService only stops the
                // matching tag→null row; the state still gets corrupted). Preserve the
                // last-known tag instead. The game enforces a 24h cooldown between leaving
                // and rejoining any FC, so two identical tag sightings less than 24h apart
                // cannot be a real leave+rejoin — preserving across the gap is safe. Genuine
                // "left FC" (with or without rejoin to same/different FC after 24h) is only
                // reliably detectable via a Lodestone-profile diff; live snapshots can't
                // tell a real cycle apart from "tag was hidden the whole time".
                var snapshotTag = string.IsNullOrEmpty(snapshot.CompanyTag) ? null : snapshot.CompanyTag;
                var effectiveTag = snapshotTag ?? existing?.CompanyTag;

                if (existing is null)
                {
                    ctx.Set<InternalObservedPlayerEntity>().Add(new InternalObservedPlayerEntity
                    {
                        ContentId = snapshot.ContentId,
                        Name = snapshot.Name,
                        HomeWorldId = snapshot.HomeWorldId,
                        CurrentWorldId = snapshot.CurrentWorldId,
                        ClassJobId = snapshot.ClassJobId,
                        Level = snapshot.Level,
                        Customize = snapshot.Customize,
                        CompanyTag = effectiveTag,
                        CurrentMountId = snapshot.CurrentMountId == 0 ? null : snapshot.CurrentMountId,
                        CurrentMinionId = snapshot.CurrentMinionId == 0 ? null : snapshot.CurrentMinionId,
                        OnlineStatusId = snapshot.OnlineStatusId,
                        UpdatedAt = now,
                    });
                }
                else
                {
                    existing.Name = snapshot.Name;
                    existing.HomeWorldId = snapshot.HomeWorldId;
                    existing.CurrentWorldId = snapshot.CurrentWorldId;
                    existing.ClassJobId = snapshot.ClassJobId;
                    existing.Level = Math.Max(existing.Level, snapshot.Level);
                    existing.Customize = snapshot.Customize;
                    existing.CompanyTag = effectiveTag;
                    existing.CurrentMountId = snapshot.CurrentMountId == 0 ? null : snapshot.CurrentMountId;
                    existing.CurrentMinionId = snapshot.CurrentMinionId == 0 ? null : snapshot.CurrentMinionId;
                    existing.OnlineStatusId = snapshot.OnlineStatusId;
                    existing.UpdatedAt = now;
                }

                // HasNotes is derived from the persisted column (the source of
                // truth); for brand-new rows the existing entity is null so we
                // fall back to the in-memory cached projection, which was set
                // to false on first observation and only flips after a
                // UpdateNotesAsync round-trip.
                var hasNotes = existing is not null
                    ? !string.IsNullOrEmpty(existing.Notes)
                    : cachedPrior?.HasNotes ?? false;

                var entry = new ObservedPlayer(
                    ContentId: snapshot.ContentId,
                    LodestoneId: lodestoneId,
                    Name: snapshot.Name,
                    HomeWorld: worldName,
                    HomeWorldId: snapshot.HomeWorldId,
                    ClassJobId: snapshot.ClassJobId,
                    Level: snapshot.Level,
                    Race: snapshot.Customize is { Length: >= 1 } rc ? rc[0] : (byte)0,
                    Gender: snapshot.Customize is { Length: >= 2 } gc ? gc[1] : (byte)0,
                    CompanyTag: effectiveTag,
                    CurrentMountId: snapshot.CurrentMountId == 0 ? null : snapshot.CurrentMountId,
                    CurrentMinionId: snapshot.CurrentMinionId == 0 ? null : snapshot.CurrentMinionId,
                    OnlineStatusId: snapshot.OnlineStatusId,
                    FirstSeen: firstSeen,
                    LastSeen: now,
                    HasNotes: hasNotes);

                ObservedPlayer? previousEntry;
                lock (mLock)
                {
                    mObserved.TryGetValue(snapshot.ContentId, out previousEntry);
                    mObserved[snapshot.ContentId] = entry;
                }
                pendingEvents.Add((entry, previousEntry));
            }

            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

            // Events fire only after the commit lands. Subscribers like
            // PlayerRefreshQueueService.OnObserved re-read the observation
            // row via a separate DbContext (LookupObservationAsync); SQLite's
            // per-connection isolation would hide our uncommitted INSERT and
            // the subscriber would silently skip the enqueue.
            foreach (var (current, previous) in pendingEvents)
            {
                Observed?.Invoke(current);
                ObservationProcessed?.Invoke(new PlayerObservationEvent(previous, current, now, canTrackChange, localPlayerCurrentWorldId));
            }

            if (snapshots.Count > 0) Interlocked.Increment(ref mRevision);
        }
        catch (OperationCanceledException) { /* plugin shutdown */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "InternalData: observation persist failed.");
        }
        finally
        {
            // Release outside the catch so an in-flight Dispose doesn't see
            // the gate held; ObjectDisposedException from Release after a
            // disposed semaphore is swallowed for the same reason the gate
            // is only really useful during the steady-state observation
            // pipeline.
            try { mProcessGate.Release(); }
            catch (ObjectDisposedException) { /* disposed during shutdown */ }
        }
    }

    public async Task<bool> UpdateNotesAsync(ulong contentId, string? notes, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(notes) ? null : notes;
        var hasNotes = normalized is not null;
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await ctx.Set<InternalObservedPlayerEntity>()
                .FindAsync(new object[] { contentId }, ct).ConfigureAwait(false);
            if (row is null) return false;
            if (string.Equals(row.Notes, normalized, StringComparison.Ordinal)) return true;
            row.Notes = normalized;
            // UpdatedAt is *not* bumped here on purpose — UpdatedAt drives
            // observation-freshness logic (hydrate fallback for FirstSeen/LastSeen).
            // A notes-only save shouldn't masquerade as a fresh sighting.
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "InternalData: failed to persist notes for ContentId {Cid}", contentId);
            return false;
        }

        ObservedPlayer? updated = null;
        lock (mLock)
        {
            if (mObserved.TryGetValue(contentId, out var current) && current.HasNotes != hasNotes)
            {
                updated = current with { HasNotes = hasNotes };
                mObserved[contentId] = updated;
            }
        }
        if (updated is not null)
        {
            Interlocked.Increment(ref mRevision);
            Observed?.Invoke(updated);
        }
        return true;
    }

    public async Task<ObservedPlayerDetail?> GetDetailAsync(ulong contentId, CancellationToken ct = default)
    {
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            // Single FindAsync hits the PK index; we only read two columns
            // off the materialized entity. EF would have to fetch the whole
            // row regardless of which fields we touch (SQLite has no per-
            // column projection on FindAsync), so the explicit FindAsync is
            // both clearer and one round-trip away from cheaper.
            var row = await ctx.Set<InternalObservedPlayerEntity>()
                .FindAsync(new object[] { contentId }, ct).ConfigureAwait(false);
            if (row is null) return null;
            return new ObservedPlayerDetail(contentId, row.Customize, row.Notes);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "InternalData: failed to load detail for ContentId {Cid}", contentId);
            return null;
        }
    }


    public async Task SetLodestoneIdAsync(ulong contentId, ulong lodestoneId, CancellationToken ct = default)
    {
        if (lodestoneId == 0) return;
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await ctx.Set<InternalObservedPlayerEntity>().FindAsync(new object[] { contentId }, ct).ConfigureAwait(false);
            if (row is null) return;
            if (row.LodestoneId == lodestoneId) return;

            row.LodestoneId = lodestoneId;
            row.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "InternalData: failed to persist LodestoneId {Lid} for ContentId {Cid}",
                lodestoneId, contentId);
            return;
        }

        ObservedPlayer? updated = null;
        lock (mLock)
        {
            if (mObserved.TryGetValue(contentId, out var current) && current.LodestoneId != lodestoneId)
            {
                updated = current with { LodestoneId = lodestoneId };
                mObserved[contentId] = updated;
            }
        }
        if (updated is not null)
        {
            Interlocked.Increment(ref mRevision);
            Observed?.Invoke(updated);
        }
    }
}
