# Observation pipeline

How `InternalDataPlayerWatcher` turns Dalamud's `IObjectTable` into a
persisted feed of `ObservedPlayer` snapshots.

## Scan cadence

The watcher subscribes to `IFramework.Update` in its constructor and
counts frames in `mFrameCounter`. A full scan runs every
`ScanIntervalFrames = 60` ticks — roughly once per second. Nearby
character churn is slower than that and the per-tick cost of an
`ObjectTable` walk isn't worth paying every frame.

Two early-outs skip the scan:

- `mCondition[ConditionFlag.DutyRecorderPlayback]` — recorder mode lies
  about the object table; we'd capture ghosts of the recording's
  participants and pollute history.
- `!mClientState.IsLoggedIn` — no observations make sense pre-login.

## Threading model

| Action | Thread |
|---|---|
| `IFramework.Update` callback, ObjectTable read, snapshot build | Framework thread (Dalamud) |
| `ProcessAsync` (DB upsert + event fires) | Threadpool (`Task.Run`) |
| `Observed` event handlers | Whatever thread fired it — framework on the hot path, threadpool when `SetLodestoneIdAsync` re-fires |
| `ObservationProcessed` event handlers | Threadpool (always, inside `ProcessAsync`) |

The in-memory `mObserved` dictionary is `lock(mLock)`-guarded; the
`mCurrentlyVisible` set is replaced wholesale per scan so reads from
the UI thread see either the prior or new instance, never a torn one.

## Per-tick flow

1. Framework callback fires; counter advances. On the 60th tick:
2. Walk the ObjectTable, build a fresh `IReadOnlySet<ulong>` of visible
   ContentIds, store it under the lock.
3. For each visible PC, build a snapshot record (Customize bytes,
   CompanyTag, current mount/minion, online status, ClassJobId / Level).
4. Hand off to `ProcessAsync` on the threadpool: per snapshot, upsert
   the matching `InternalObservedPlayerEntity` (insert or update
   `LastSeen`, `SeenCount`, current fields).
5. Build a fresh `ObservedPlayer` record, write it into `mObserved`,
   fire `Observed` (subscribers: UI state, refresh-queue resolver).
6. Fire `ObservationProcessed(Previous, Current, At)`. The history
   service subscribes here and persists any detected field changes —
   see [history.md](history.md).

## Hydration on startup

`HydrateAsync` runs once when the watcher is constructed and loads
every row from `nexus_internal_observed_player` into the in-memory
`mObserved` map. The "All Players" filter in the UI lists from this
cache; without hydration it would show empty until the next scan tick.

`LastSeen` and `SeenCount` are no longer stored on `observed_player` —
both are derived from `player_encounter` aggregates (max `last_seen_at`
and `COUNT(*)` per ContentId). Hydration joins the encounter roster
read in the same query and folds the aggregates onto each `ObservedPlayer`
record; the in-memory sort by `LastSeen DESC` then happens client-side
instead of via a now-deleted SQL index.

The cap that used to live here (`Take(500)`) is gone. 14k rows is
~3 MB and still loads in well under a second on a cold start; capping
would silently amputate the list.

## `SetLodestoneIdAsync` — the enrichment write-back

When the PlayerEnrichment layer resolves a LodestoneId for a previously
unresolved character it calls:

```csharp
await watcher.SetLodestoneIdAsync(contentId, lodestoneId, ct);
```

The watcher then:

1. Looks up the `InternalObservedPlayerEntity` by ContentId. No-op if
   missing or if the id already matches.
2. Sets `LodestoneId` and `UpdatedAt = now`. Saves.
3. Under the lock, updates the in-memory `ObservedPlayer` record with
   the resolved id (`record with { LodestoneId = … }`).
4. Re-fires `Observed` so subscribers see the resolved record without
   waiting for the next scan tick.

This means: anyone subscribed to `Observed` should be idempotent against
multiple firings for the same character — they may see the record once
with `LodestoneId = null` and then again with a resolved id, possibly
within the same second.

## `ObservedPlayer` snapshot shape

The record is built per scan from the ObjectTable and the persisted row;
fields like `FirstSeen` survive from the DB while `Customize` and
`Level` come from the live object. `LastSeen` / `SeenCount` are folded
in from the encounter-tracker aggregate (see
[encounters.md](encounters.md)). See `ObservedPlayer.cs` for the full
list.

`HomeWorld` is the **localised display name** (resolved through
`IGameDataLookups.GetWorldName(HomeWorldId)` at hydrate / snapshot
time). The id itself lives in `HomeWorldId`; UI code that wants a
language-stable identifier should use that.

---

**Maintenance**: when you change `ScanIntervalFrames`, add a new field
to `ObservedPlayer`, or move work between framework / threadpool,
update this doc and the inline comment in `InternalDataPlayerWatcher`.
