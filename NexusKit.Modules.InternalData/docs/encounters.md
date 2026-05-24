# Encounter tracker

How `InternalDataEncounterTracker` records territory-bounded sessions of
the local player and the non-local characters visible during each one.

## Schema

```
nexus_internal_encounter (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    territory_type_id INTEGER NOT NULL,
    started_at TEXT NOT NULL,
    ended_at TEXT NULL
)
INDEX (started_at DESC)

nexus_internal_player_encounter (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    encounter_id INTEGER NOT NULL,        -- FK -> encounter.id
    content_id INTEGER NOT NULL,           -- FK -> observed_player.content_id
    job_id INTEGER NOT NULL,
    level INTEGER NOT NULL,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL
)
INDEX (content_id, first_seen_at DESC)
INDEX (encounter_id)
```

`encounter` is the parent (one row per territory-bounded session); roster
membership goes in `player_encounter` (one row per (encounter, sighted-character)).
Job and level are frozen at first sighting; only `last_seen_at` advances on
subsequent ticks.

## Lazy encounter creation

The tracker does NOT pre-create an encounter the moment the player enters
a zone. The parent row is only inserted on the **first sighting of a
non-local character**. Solo zone walks (logging in to a private estate,
crossing an empty zone to a duty queue) produce no row at all.

This keeps the encounter table tightly scoped to "where the player
actually saw other people".

## Subscriptions

| Event | Handler | Effect |
|---|---|---|
| `IClientState.TerritoryChanged` | `OnTerritoryChanged` | Closes the open encounter (stamps `ended_at = now`), nulls the in-memory id, swaps the territory cache. The next sighting opens a fresh row in the new zone. |
| `IPluginLifetime.StateChanged → Idle` | `OnLifecycleStateChanged` | In-game logout. Async-stamp `ended_at`. |
| `IPluginLifetime.StateChanged → Stopping` | same | Plugin unload begun, DI still alive, lifetime token NOT yet cancelled. SYNC `ended_at` stamp — `GetAwaiter().GetResult()` is safe because the host blocks on the callback returning before transitioning to `Stopped`. |
| `IPluginLifetime.StateChanged → Stopped` | same | Lifetime token already cancelled; just clear in-memory state. The `Stopping` callback above already drained the encounter id. |
| `IInternalDataPlayerWatcher.ObservationProcessed` | `OnObservationProcessed` | Per non-local sighting: upsert `player_encounter` for `(open encounter, ContentId)`. Opens a new `encounter` row if none is current. |

## Open-encounter contract

Exactly one encounter is "open" at any moment (`ended_at IS NULL`) per
plugin session. The in-memory `mCurrentEncounterId` is the single source of
truth; both upsert and close paths read/write it under `mLock`.

Mid-zone-load races (TerritoryChanged hasn't fired yet but the client is
partway through the load) are reconciled in `UpsertSightingAsync`: if the
cached territory drifts from the snapshot's, the snapshot wins and the
cached encounter is treated as stale.

## Startup recovery sweep

`CloseOrphanedEncountersAsync` runs once at construction, **before**
`ObservationProcessed` is subscribed. Two passes:

### Pass 1: Resume

Two recovery shapes are valid resume candidates in the current territory:

| Shape | Detection | Bound |
|---|---|---|
| Plugin reload | `ended_at != null && ended_at > now - 30s` | 30s grace — a clean close means the plugin handled the prior session's end, so anything beyond a `/xlplugins` reload window is genuinely new. |
| Game crash | `ended_at IS NULL` | No time bound — by definition the player is back in the crash zone, and territory match is sufficient evidence to continue. |

When a candidate is found, `ended_at` is reset to null, the encounter
becomes the current open encounter, and a log line records why.

A "different-zone open encounter" is NOT a resume candidate; pass 2
closes it.

### Pass 2: Close

Any encounter still `ended_at IS NULL` after pass 1 is either:

- An open encounter for a different territory than the player is currently
  in (crashed in zone A, now in zone B), or
- A leftover from an old crash.

The sweep stamps `ended_at = max(player_encounter.last_seen_at)` for each
such row, falling back to `started_at` when no roster row exists. This
prevents an apparently-still-active ancient encounter from confusing
recent-encounter reads.

## Why ObservationProcessed is wired after the sweep

The recovery sweep is async, and the watcher's very next tick fires within
a frame of the plugin loading. Without sequencing, the tick can race the
sweep and create a brand-new encounter before the sweep gets a chance to
bind the freshly-closed one — observably producing duplicate rows on
plugin reload. Wiring the subscription only in `CloseOrphanedEncountersAsync`'s
`finally` block makes the order deterministic.

## Cancellation discipline

Every DB call goes through `INexusDbContextFactory`, which auto-links the
plugin lifetime token. `UpsertSightingAsync` opens an explicit transaction
across the encounter + player_encounter inserts so a shutdown mid-flight
rolls both back instead of leaving an orphan parent.

`Dispose` deliberately does NOT stamp `ended_at` itself: in some Dalamud
unload paths the DI container backing `INexusDbContextFactory` is already
torn down by the time `Dispose` runs. The `Stopping` lifecycle callback
above is the supported final-write point; `Dispose` only unsubscribes and
nulls the in-memory id. If the `Stopping` write was missed (external
cancel path), the next startup's sweep closes the orphan from
`last_seen_at`.

## `seen_count` is gone

The `observed_player.seen_count` and `last_seen` columns were dropped.
Both are now derived:

- "How many times did I see this character?" =
  `IInternalDataEncounterTracker.GetEncounterCountAsync(contentId)` (one row
  per encounter the character appears in).
- "When did I last see them?" = `MAX(player_encounter.last_seen_at)` for
  that ContentId; the watcher folds it into `ObservedPlayer.LastSeen` at
  hydrate time.

The migration that retired the columns is in
`InternalDataMigrations` (look for the rebuild that drops `seen_count` /
`last_seen` from `observed_player`).

---

**Maintenance**: when you change the recovery-sweep ordering, add a new
shutdown path, or alter the encounter+roster transaction, update this doc
and the inline comments in `InternalDataEncounterTracker`.
