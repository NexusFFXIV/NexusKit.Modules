# Change history

`InternalDataHistoryService` writes one row to `nexus_internal_player_history`
per detected field change. Detection is event-driven: the service subscribes
to `IInternalDataPlayerWatcher.ObservationProcessed` in its constructor and
diffs `(Previous, Current)` on every upsert.

## Tracked kinds

`PlayerHistoryKind : byte` (in `History/PlayerHistoryKind.cs`):

| Value | Kind | Detected when |
|---:|---|---|
| 1 | `NameChange` | `prev.Name != curr.Name` (ordinal compare). |
| 2 | `HomeWorldChange` | `prev.HomeWorld != curr.HomeWorld`. The watcher stores the localised name, so this works regardless of plugin culture switches as long as both compare in the same locale. |
| 3 | `CustomizeChange` | Race byte (`Customize[0]`) or gender byte (`Customize[1]`) differ. Hair / face / colour bytes are intentionally **not** tracked — they'd flood the timeline. Race/gender map to Fantasia-grade changes. |
| 4 | `FreeCompanyChange` | `external_player_profile.free_company_lodestone_id` changes during a Lodestone refresh. Detected in `ExternalDataPlayerService.UpsertProfileAsync`, which awaits an `IExternalDataFreeCompanyService.GetAsync` on the new FC id before invoking the change recorder so the FC catalog row is in cache by the time `HistoryNotificationProducer` resolves it for the chat line (the refresh queue's per-category interleave would otherwise leave the catalog row missing for ≥1 minute). The live object-table tag is never used as a signal here. |

Each row stores pre-formatted display strings in `OldValue` / `NewValue`
(e.g. `"Hyur · Female"` for a customize change) so the UI doesn't have to
re-resolve Lumina names per render. The CustomizeChange fingerprint is
computed once at detection time. For `FreeCompanyChange`, new rows carry
FC Lodestone ids in `OldValue` / `NewValue`; legacy rows from the retired
tag-diff path still carry tag strings (e.g. `"Boop!"`) — both render
correctly through `HistoryFormatting.FormatChange`.

## Why FC change isn't detected from the live tag

Earlier iterations diffed `ObservedPlayer.CompanyTag` between successive
observations. Two problems killed that signal:

1. **Not unique.** A single `(tag, world)` pair can legitimately belong
   to multiple distinct FCs (e.g. four `«Boop!»` FCs on Twintania at the
   time of writing). A `tagA → tagA` diff to a different FC is
   undetectable; a `null → tag` "join" can't pin down which of the
   candidates was actually joined.
2. **Live data drops the tag.** Cutscenes / instance transitions /
   cross-world snapshots leave the tag transiently empty, so a symmetric
   "anything non-equal" differ would log phantom leave/rejoin pairs on
   every duty boundary.

The Lodestone-profile path keys on `free_company_lodestone_id` (a
globally-unique id), so both problems vanish — a real FC swap is one
diff, no phantoms, no ambiguity. The trade-off is latency: the change
only lands when the next Lodestone profile refresh runs (TTL-bounded).
For real-time visibility of the tag itself, see the live observation in
`ObservedPlayer.CompanyTag` — it's still available, just not logged as
a history event.

## Write path

`OnObservationProcessed` runs on the threadpool task that owns the
observation upsert (not the framework thread). The handler:

1. Skips when `Previous is null` (first-ever observation — nothing to diff).
2. Walks each tracked field, collecting `InternalPlayerHistoryEntity`
   rows for every detected change.
3. If at least one row was produced, fires `Task.Run(() => PersistAsync(rows))`
   which opens its own DbContext, inserts, saves, and finally fires
   `HistoryAdded(contentId, entries)`.

All rows in a single `PersistAsync` call share the same ContentId by
construction (one event → at most one batch). `HistoryAdded` also carries
the `PlayerHistoryEntry` rows written in that batch — subscribers (UI
unread-kind index, chat notifier) get full old/new values without a
re-query.

## Read path

```csharp
// Most-recent N entries for one character (default cap 200):
var rows = await history.GetForContentIdAsync(contentId, limit: 50, ct);

// Per-ContentId set of distinct kinds with at least one UNREAD row — used
// to dot list rows that have something new to look at AND to drive the
// dot's hover-tooltip ("Verlauf: Umbenennung, …"). Empty once every row
// for a character has been flipped read.
var byId = await history.GetUnreadHistoryKindsByContentIdAsync(ct);

// Bulk-flag every row for a character as read (called when the History
// tab opens for that character):
await history.MarkAllReadForContentIdAsync(contentId, ct);

// Bulk-flag every unread row across ALL characters in one DB roundtrip
// (called from the player-list's "mark all read" toolbar button):
await history.MarkAllReadAsync(ct);
```

The read path uses `AsNoTracking()` — these results are read-only views.

`HistoryAdded` is an `Action<ulong, IReadOnlyList<PlayerHistoryEntry>>?`
event handlers can subscribe to; `HistoryRead` is `Action<ulong>?` and
fires after `MarkAllReadForContentIdAsync` flips at least one row;
`AllHistoryRead` is `Action?` (no payload) and fires after
`MarkAllReadAsync` flips at least one row. The per-character event is
NOT fanned out by the global path — subscribers that care about both
should listen to both. All three run on the worker thread that wrote
the rows — UI code must marshal to the draw thread itself (a reference
assignment of an `IReadOnlyList<PlayerHistoryEntry>` field is enough in
practice).

## Schema reference

Defined in `InternalDataEntityModule.ConfigureEntities`:

```
nexus_internal_player_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    content_id INTEGER NOT NULL,
    kind INTEGER NOT NULL,
    changed_at TEXT NOT NULL,
    old_value TEXT NULL,
    new_value TEXT NULL,
    is_read INTEGER NOT NULL DEFAULT 0
)
INDEX (content_id, changed_at DESC)
INDEX (kind)
INDEX (content_id, is_read)
```

The composite `(content_id, changed_at DESC)` index covers the primary
read path (UI fetches the newest N for a character). `kind` supports
per-kind filtering. `(content_id, is_read)` covers the player-list dot
sweep (`WHERE is_read = 0 GROUP BY content_id, kind`), the per-character
bulk mark-as-read write triggered when the History tab opens, and the
global `MarkAllReadAsync` (`WHERE is_read = 0`).

---

**Maintenance**: when you add a `PlayerHistoryKind`, change a detection
rule, or alter the persisted display format, update this doc and the
inline comments in `InternalDataHistoryService.OnObservationProcessed`.
