using Dalamud.Plugin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Core;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Persistence;

namespace NexusKit.Modules.InternalData.Encounters;

/// <summary>
/// Territory-bounded encounter tracker. Subscribes to <c>TerritoryChanged</c>,
/// <see cref="IPluginLifetime.StateChanged"/>, and the player watcher's
/// <c>ObservationProcessed</c> event, and persists rows into
/// <c>nexus_internal_encounter</c> + <c>nexus_internal_player_encounter</c>.
/// <para>Lazy encounter creation: no row is written when the local player
/// enters a zone alone — the parent encounter row is only inserted on the
/// first sighting of a non-local character. Solo zone walks produce nothing.</para>
/// <para>One open encounter at a time. On in-game logout the encounter is
/// closed cleanly (ended_at stamped). On plugin unload the encounter is
/// left open with ended_at = null; the next session's orphan-close sweep
/// stamps it from the latest player_encounter activity. Same recovery
/// branch covers a hard game crash.</para>
/// </summary>
internal sealed class InternalDataEncounterTracker : IInternalDataEncounterTracker, IDisposable
{
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly IClientState mClientState;
    private readonly INexusDbContextFactory mDb;
    private readonly IPluginLifetime mLifetime;
    private readonly ILogger<InternalDataEncounterTracker> mLog;

    private readonly object mLock = new();
    // Currently-open encounter id. Null when no non-local sighting has
    // happened in the current zone yet. Reset on TerritoryChanged + on
    // lifecycle transitions out of Active (logout / plugin unload).
    private long? mCurrentEncounterId;
    private ushort mCurrentTerritoryId;
    // World the current encounter was opened on (snapshotted from the
    // local player's CurrentWorld at first sighting). Drives the world-
    // drift check in UpsertSightingAsync: a Lifestream world-visit can
    // land back in the SAME territory id on the destination world (e.g.
    // Limsa 129 → Limsa 129), so TerritoryChanged never fires and the
    // territory comparison alone would keep extending the old encounter.
    // Comparing the snapshot's worldId against this cache lets us close
    // and reopen on world transfer. null when no sighting has populated
    // it yet, or when local-player snapshots are missing.
    private ushort? mCurrentWorldId;
    private bool mDisposed;

    /// <summary>How long after a CLEANLY closed encounter (ended_at !=
    /// null, set by TerritoryChanged) we still allow it to be resumed
    /// when the plugin re-loads in the same zone. Short by design — a
    /// clean close means the plugin handled the transition, so anything
    /// beyond a /xlplugins reload window is genuinely a new session.</summary>
    private static readonly TimeSpan ResumeGrace = TimeSpan.FromSeconds(30);


    public event Action<ulong, long>? EncountersChanged;

    public InternalDataEncounterTracker(
        IInternalDataPlayerWatcher watcher,
        IClientState clientState,
        INexusDbContextFactory db,
        IPluginLifetime lifetime,
        ILogger<InternalDataEncounterTracker> log)
    {
        mWatcher = watcher;
        mClientState = clientState;
        mDb = db;
        mLifetime = lifetime;
        mLog = log;

        mCurrentTerritoryId = (ushort)mClientState.TerritoryType;

        // Zone changes are still picked up via IClientState.TerritoryChanged.
        // Logout used to come through IClientState.Logout — but that ONLY
        // fires for in-game character logout, not plugin unload or game
        // shutdown. Subscribing to the lifetime's StateChanged unifies all
        // three cases (Active → Idle = logout, Active → Stopped = unload)
        // and lets one handler distinguish them by the target state.
        mClientState.TerritoryChanged += OnTerritoryChanged;
        mLifetime.StateChanged += OnLifecycleStateChanged;

        // ObservationProcessed is wired up AFTER the startup sweep finishes.
        // Otherwise the watcher's very next tick (which fires within a
        // frame of the plugin loading) can race the async resume sweep
        // and create a brand-new encounter before the sweep gets a chance
        // to bind a freshly-closed one. The race observably produced two
        // duplicate encounter rows on plugin reload — see the regression
        // log for IDs 14580 (resumed) / 14581 (race victim).
        _ = Task.Run(async () =>
        {
            try
            {
                await CloseOrphanedEncountersAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!mDisposed)
                    mWatcher.ObservationProcessed += OnObservationProcessed;
            }
        });
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;

        mWatcher.ObservationProcessed -= OnObservationProcessed;
        mClientState.TerritoryChanged -= OnTerritoryChanged;
        mLifetime.StateChanged -= OnLifecycleStateChanged;

        // Deliberately do NOT stamp ended_at here. The DI container that
        // backs mDb is torn down BEFORE this Dispose runs in some Dalamud
        // unload paths (observed: ObjectDisposedException on the
        // IServiceProvider from inside CreateDbContext), so any attempt to
        // open a fresh DbContext fails. The active encounter stays open
        // with ended_at = null instead — the next plugin load's sweep
        // picks it up: same zone hits the crash-recovery branch and
        // resumes it, different zone falls through pass 2 and closes it
        // as an orphan. Pull mCurrentEncounterId out of the cache so any
        // in-flight UpsertSightingAsync that arrives before mDisposed
        // propagates sees a clean null and bails on the new-encounter
        // path it would otherwise take.
        lock (mLock) { mCurrentEncounterId = null; }

        // No need to cancel here — the host's PluginLifetime.RequestStop
        // already fired BEFORE this Dispose ran (see PluginHost.Dispose).
        // In-flight UpsertSightingAsync tasks are picking up the cancelled
        // IPluginLifetime.Stopping token mid-await and unwinding cleanly.
    }

    public async Task<IReadOnlyList<EncounterEntry>> GetForContentIdAsync(
        ulong contentId, int limit = 200, CancellationToken ct = default)
    {
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            // Single indexed join: (content_id, first_seen_at DESC) on the
            // child side, encounter row keyed by id on the parent.
            var rows = await (
                from pe in ctx.Set<InternalPlayerEncounterEntity>()
                join e in ctx.Set<InternalEncounterEntity>() on pe.EncounterId equals e.Id
                where pe.ContentId == contentId
                orderby pe.FirstSeenAt descending
                select new EncounterEntry(
                    pe.Id,
                    e.TerritoryTypeId,
                    e.WorldId,
                    e.StartedAt,
                    e.EndedAt,
                    pe.JobId,
                    pe.Level,
                    pe.FirstSeenAt,
                    pe.LastSeenAt))
                .Take(limit)
                .AsNoTracking()
                .ToListAsync(ct).ConfigureAwait(false);
            return rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Encounter read failed for {ContentId}", contentId);
            return Array.Empty<EncounterEntry>();
        }
    }

    public async Task<int> GetEncounterCountAsync(ulong contentId, CancellationToken ct = default)
    {
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await ctx.Set<InternalPlayerEncounterEntity>()
                .Where(p => p.ContentId == contentId)
                .CountAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Encounter count failed for {ContentId}", contentId);
            return 0;
        }
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        long? toClose;
        lock (mLock)
        {
            toClose = mCurrentEncounterId;
            mCurrentEncounterId = null;
            mCurrentTerritoryId = (ushort)territoryId;
        }
        if (toClose is { } id) _ = Task.Run(() => StampEndedAtAsync(id));
    }

    private void OnLifecycleStateChanged(PluginLifecycleState state)
    {
        // Three transitions out of an active session, three behaviors:
        //   Idle      = in-game logout, plugin keeps running → async stamp
        //   Stopping  = plugin unload begun, DI still alive, CT not yet
        //               cancelled → SYNC stamp (last-chance write window)
        //   Stopped   = CT cancelled, DI tearing down → only clear memory;
        //               the Stopping callback above already drained the
        //               encounter id, so mCurrentEncounterId is null here
        //               and the write branch is skipped. Keeping the case
        //               in the switch makes the state-machine intent
        //               explicit and survives the external-cancel path
        //               (where Stopping fires with the CT already gone,
        //               StampEndedAtAsync bails at its IsStopping guard,
        //               and we'd otherwise leak the in-memory id).
        if (state != PluginLifecycleState.Idle
            && state != PluginLifecycleState.Stopping
            && state != PluginLifecycleState.Stopped) return;

        long? toClose;
        lock (mLock)
        {
            toClose = mCurrentEncounterId;
            mCurrentEncounterId = null;
        }
        if (toClose is not { } id) return;

        switch (state)
        {
            case PluginLifecycleState.Idle:
                _ = Task.Run(() => StampEndedAtAsync(id));
                break;

            case PluginLifecycleState.Stopping:
                // Sync-over-async by design: the lifetime fires StateChanged
                // synchronously and the host blocks on this returning before
                // moving to Stopped + cancelling the CT. The write is a
                // single indexed UPDATE so blocking here is cheap. We swallow
                // exceptions — if the stamp fails, the next session's
                // orphan-close sweep still recovers from last_seen_at.
                try { StampEndedAtAsync(id).GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    mLog.LogWarning(ex, "Encounter stop-write failed for #{Id}", id);
                }
                break;

            // Stopped: CT is already cancelled; StampEndedAtAsync would bail
            // at its IsStopping guard anyway. Nothing to do beyond the
            // in-memory clear above.
        }
    }

    private void OnObservationProcessed(PlayerObservationEvent evt)
    {
        // The watcher's iteration already skips ObjectTable[0] (the local
        // player) via ObjectTableExtensions.GetVisiblePlayers, so every
        // event here is a sighting of someone else. Only defensive guard
        // remaining is the ContentId == 0 case (a torn snapshot or a
        // not-fully-loaded character entry).
        if (evt.Current.ContentId == 0) return;

        // Pull the current territory snapshot once. ClientState exposes a
        // uint TerritoryType; we cache it as ushort to match the Lumina
        // sheet's RowId width.
        var territoryNow = (ushort)mClientState.TerritoryType;
        // World id comes from the event payload — the watcher already
        // sampled it on the framework thread before dispatching us off-
        // thread. Reading IObjectTable / IClientState.LocalPlayer here
        // would race with the engine and reliably returns null (the bug
        // that left every encounter's world_id at NULL the first time we
        // wired this up). uint? from the event → ushort? in the entity:
        // FFXIV world row ids comfortably fit ushort.
        var worldNow = evt.LocalPlayerCurrentWorldId is { } wid
            ? (ushort)wid
            : (ushort?)null;
        var now = evt.ObservedAt;

        _ = Task.Run(() => UpsertSightingAsync(evt.Current.ContentId,
            evt.Current.ClassJobId, evt.Current.Level, territoryNow, worldNow, now));
    }

    private async Task UpsertSightingAsync(
        ulong contentId, uint jobId, byte level, ushort territoryId, ushort? worldId, DateTime now)
    {
        // Fast bail for tasks that hadn't started yet when shutdown fired.
        // The factory's lifetime token is set monotonically (false → true)
        // so a momentary stale read just lets through one extra tick, which
        // the per-await token checks below catch anyway.
        if (mDb.IsStopping) return;
        // CreateDbContextAsync without an explicit ct uses the factory's
        // LifetimeToken automatically; we still need the local `ct` for the
        // EF async operations below, which carry no ambient token.
        var ct = mDb.LifetimeToken;

        try
        {
            await using var ctx = await mDb.CreateDbContextAsync().ConfigureAwait(false);
            // Two-write operation (encounter then player_encounter — the
            // child needs the parent's auto-assigned id), so wrap in an
            // explicit transaction. Without it, a crash between the two
            // SaveChanges below would leave an orphan encounter row that no
            // player_encounter ever references — the row still satisfies
            // the table's schema constraints, so it would silently bloat
            // the encounter table forever.
            await using var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Resolve / open the parent encounter under the lock so two
            // simultaneous sightings can't race two encounter rows into the DB.
            long encounterId;
            bool openedNew = false;
            long? worldDriftStampId = null;
            lock (mLock)
            {
                // If the cached territory has drifted from the snapshot's
                // (TerritoryChanged hasn't fired yet but the client is
                // partway through a zone load), prefer the snapshot's value
                // and treat the cached encounter as stale.
                if (mCurrentTerritoryId != territoryId)
                {
                    mCurrentTerritoryId = territoryId;
                    mCurrentEncounterId = null;
                }
                // World-drift defensive check. Lifestream world visits can
                // land back in the SAME territory id on the destination
                // world (Limsa 129 → Limsa 129), so TerritoryChanged never
                // fires and the territory check above doesn't catch it. By
                // comparing the snapshot's worldId against the cache we
                // close the previous encounter the moment we see a sighting
                // from the new world, and the next openedNew branch below
                // opens a fresh row with WorldId = the new world. We only
                // close when BOTH sides are known and differ — a null
                // snapshot (local player momentarily unavailable, e.g.
                // mid-zoning) keeps the cache so we don't fragment an
                // encounter just because one tick couldn't read the world.
                // Unlike the territory case there's no Dalamud event that
                // fires on world transfer, so we have to stamp ended_at
                // ourselves; without that the previous encounter would
                // dangle as ended_at = NULL until the next plugin reload's
                // orphan-close sweep tidied it up.
                if (worldId is { } wNew)
                {
                    if (mCurrentWorldId is { } wOld && wNew != wOld
                        && mCurrentEncounterId is { } drifted)
                    {
                        worldDriftStampId = drifted;
                        mCurrentEncounterId = null;
                    }
                    mCurrentWorldId = wNew;
                }
                if (mCurrentEncounterId is not { } id)
                {
                    encounterId = -1; // sentinel — we'll INSERT below
                    openedNew = true;
                }
                else
                {
                    encounterId = id;
                }
            }

            // Stamp the world-drifted encounter's ended_at outside the
            // lock. Fire-and-forget mirrors the OnTerritoryChanged path
            // (it dispatches StampEndedAtAsync the same way after marking
            // the encounter id null).
            if (worldDriftStampId is { } toClose)
                _ = Task.Run(() => StampEndedAtAsync(toClose));

            if (openedNew)
            {
                var newEncounter = new InternalEncounterEntity
                {
                    TerritoryTypeId = territoryId,
                    WorldId = worldId,
                    StartedAt = now,
                    EndedAt = null,
                };
                ctx.Set<InternalEncounterEntity>().Add(newEncounter);
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                encounterId = newEncounter.Id;
                // NOTE: the in-memory mCurrentEncounterId slot is updated
                // *after* the transaction commits (below). Doing it here
                // would let a transaction rollback leave the cached id
                // pointing at a row that no longer exists, and the next
                // sighting's player_encounter INSERT would hit an FK
                // violation. The race-with-another-sighting check still
                // belongs here, but only to choose which encounterId this
                // tick should bind its player_encounter to.
                lock (mLock)
                {
                    if (mCurrentEncounterId is { } cached && mCurrentTerritoryId == territoryId)
                        encounterId = cached;
                }
            }

            // Upsert the per-player row inside the (encounter, content) pair.
            var existing = await ctx.Set<InternalPlayerEncounterEntity>()
                .FirstOrDefaultAsync(p => p.EncounterId == encounterId && p.ContentId == contentId, ct)
                .ConfigureAwait(false);
            if (existing is null)
            {
                ctx.Set<InternalPlayerEncounterEntity>().Add(new InternalPlayerEncounterEntity
                {
                    EncounterId = encounterId,
                    ContentId = contentId,
                    JobId = jobId,
                    Level = level,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                });
            }
            else
            {
                // Job / level intentionally frozen at first-sighting. Only
                // bump the last-seen timestamp so the duration column grows.
                existing.LastSeenAt = now;
            }
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            // Post-commit: only NOW publish the new encounter id to the
            // in-memory cache. A pre-commit assignment would have to be
            // rolled back on failure, and there's no clean way to "uncache"
            // an id another thread might already have read.
            if (openedNew)
            {
                lock (mLock)
                {
                    if (mCurrentEncounterId is null && mCurrentTerritoryId == territoryId)
                        mCurrentEncounterId = encounterId;
                }
            }
            EncountersChanged?.Invoke(contentId, encounterId);
        }
        catch (OperationCanceledException)
        {
            // Plugin shutdown signalled mid-await. Encounter / player_encounter
            // writes that haven't reached SaveChanges yet roll back with the
            // transaction; no orphan rows.
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Encounter upsert failed for {ContentId}", contentId);
        }
    }

    private async Task StampEndedAtAsync(long encounterId)
    {
        if (mDb.IsStopping) return;
        var ct = mDb.LifetimeToken;
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync().ConfigureAwait(false);
            var row = await ctx.Set<InternalEncounterEntity>()
                .FindAsync(new object[] { encounterId }, ct).ConfigureAwait(false);
            if (row is null || row.EndedAt is not null) return;
            row.EndedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* plugin shutdown */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Encounter close failed for #{Id}", encounterId);
        }
    }

    private async Task CloseOrphanedEncountersAsync()
    {
        if (mDb.IsStopping) return;
        var ct = mDb.LifetimeToken;
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync().ConfigureAwait(false);

            var currentTerritory = (ushort)mClientState.TerritoryType;
            // Lift the cache up if the constructor saw a 0 (Dalamud not
            // fully through its init during plugin (re-)load): the first
            // observation otherwise sees mCurrentTerritoryId == 0 and the
            // sighting's real territory, mistakes that for a zone change,
            // and wipes mCurrentEncounterId — preventing any encounter the
            // sweep might bind in the same code path below from sticking.
            if (currentTerritory != 0)
            {
                lock (mLock)
                {
                    if (mCurrentTerritoryId == 0)
                        mCurrentTerritoryId = currentTerritory;
                }
            }

            // ---- Pass 1: RESUME ------------------------------------------
            // Two recovery shapes:
            //   a) "Plugin reload" — encounter was cleanly closed (ended_at
            //      is populated by Dispose / TerritoryChanged / Logout) just
            //      moments ago in the current territory. Bounded by
            //      ResumeGrace because a clean close means the plugin knew
            //      the session ended.
            //   b) "Game crash" — encounter is still ended_at IS NULL
            //      because the prior process died without running Dispose,
            //      and the player started back in the same zone. No time
            //      bound: by definition the player is back in the crash
            //      zone, and territory match is sufficient evidence to
            //      continue the prior session.
            // A "different-zone open encounter" is NOT a resume candidate;
            // pass 2 closes it as an orphan.
            // We don't gate on IsLoggedIn: Dalamud's flag can flicker false
            // during a /xlplugins reload while the plugin re-initializes
            // even though the user is unambiguously in-world. Territory != 0
            // is the authoritative "we're somewhere" signal.
            if (currentTerritory != 0)
            {
                var resumeCutoff = DateTime.UtcNow - ResumeGrace;
                var candidate = await ctx.Set<InternalEncounterEntity>()
                    .Where(e => e.TerritoryTypeId == currentTerritory
                                && (e.EndedAt == null
                                    || e.EndedAt > resumeCutoff))
                    // Open encounters sort with the highest "effective"
                    // recency (null > any populated value), then by id so a
                    // deterministic tiebreaker exists when two clean closes
                    // happen to share an ended_at value.
                    .OrderByDescending(e => e.EndedAt == null)
                    .ThenByDescending(e => e.EndedAt)
                    .ThenByDescending(e => e.Id)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (candidate is not null)
                {
                    var reason = candidate.EndedAt is null
                        ? "crash-recovery (was still open)"
                        : $"plugin-reload (closed {(DateTime.UtcNow - candidate.EndedAt.Value).TotalSeconds:F0}s ago)";
                    candidate.EndedAt = null;
                    await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                    lock (mLock)
                    {
                        // Only bind as current if the territory the sweep
                        // observed still matches mCurrentTerritoryId — a
                        // TerritoryChanged event firing during the
                        // (asynchronous) sweep would already have moved the
                        // cache to the new zone, and we don't want to drag
                        // the resumed (old-zone) encounter into the new zone's
                        // context.
                        if (mCurrentTerritoryId == currentTerritory)
                            mCurrentEncounterId = candidate.Id;
                    }
                    mLog.LogInformation(
                        "Encounter tracker: resumed encounter #{Id} in territory {Tid} ({Reason}).",
                        candidate.Id, currentTerritory, reason);
                }
            }

            // ---- Pass 2: CLOSE -------------------------------------------
            // Any encounter still at ended_at IS NULL beyond this point is
            // either an open encounter for a DIFFERENT territory (the user
            // crashed in zone A and is now in zone B — treat as orphan), or
            // a leftover from a really old crash. Stamp ended_at with the
            // latest known activity so the next read isn't confused by an
            // apparently-still-active ancient row.
            var open = await ctx.Set<InternalEncounterEntity>()
                .Where(e => e.EndedAt == null && e.Id != (mCurrentEncounterId ?? -1))
                .ToListAsync(ct).ConfigureAwait(false);
            if (open.Count == 0) return;

            foreach (var e in open)
            {
                var lastSeen = await ctx.Set<InternalPlayerEncounterEntity>()
                    .Where(p => p.EncounterId == e.Id)
                    .Select(p => (DateTime?)p.LastSeenAt)
                    .OrderByDescending(d => d)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                e.EndedAt = lastSeen ?? e.StartedAt;
            }
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            mLog.LogInformation("Encounter tracker: closed {Count} orphan encounter(s) from prior session.", open.Count);
        }
        catch (OperationCanceledException) { /* plugin shutdown */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Encounter tracker: orphan close sweep failed.");
        }
    }
}
