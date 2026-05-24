# Refresh queue

A persistent, priority-ordered queue of "fetch X for this character" tasks.
Sits between `IInternalDataPlayerWatcher.Observed` (a player came into
range) and `IExternalDataPlayerService.GetAsync` (do the actual Lodestone
/ FFXIVCollect call). Persisting the queue lets unfinished work survive
plugin reloads; partitioning by category lets a player with stale mounts
but fresh class-jobs only re-fetch what's actually stale.

## Identifier model

Queue rows are keyed by `(ContentId, RefreshCategory)`. `ContentId` is the
stable per-character id from `nexus_internal_observed_player` — always
known, including *before* Lodestone resolution. The worker reads
`observed_player.lodestone_id` at processing time to translate to the
external-side id whenever the category needs one.

## Categories

`RefreshCategory : byte` (see `NexusKit.Modules.InternalData.Refresh.RefreshCategory`).

| Value | Name | Worker action |
|---:|---|---|
| 0 | `LodestoneId` | `IExternalDataPlayerService.SearchAsync(name, world)`; on hit, call `IInternalDataPlayerWatcher.SetLodestoneIdAsync` and cascade stale sub-resources. |
| 1 | `Profile` | `GetAsync(lid, PlayerInclude.Profile)` — Lodestone character page (bio, nameday, avatar, FC link, GC). |
| 2 | `ClassJobs` | `GetAsync(lid, PlayerInclude.ClassJobs)`. |
| 3 | `Gear` | `GetAsync(lid, PlayerInclude.Gear)`. |
| 4 | `FreeCompany` | `GetAsync(lid, PlayerInclude.FreeCompany)` — only enqueued when the profile carries an FC link. |
| 5 | `Mounts` | `GetAsync(lid, PlayerInclude.Mounts)` — FFXIVCollect + Lodestone. |
| 6 | `Minions` | `GetAsync(lid, PlayerInclude.Minions)`. |
| 7 | `Achievements` | `GetAsync(lid, PlayerInclude.Achievements)`. |

The integer values are persisted; renumber only via a migration that
rewrites the rows. Adding a new category at the end is safe.

### Per-category enable policy

`IRefreshCategoryPolicy.IsEnabled(category)` gates every enqueue, every
stale-check, and the worker's pick query. The **structural** facts about
which categories are mandatory live in
`RefreshCategoryClassification` (single source of truth — the policy, the
queue service's `BuildAllowedCategories`, and
`RefreshQueueDisabledCategoryPruneContributor` all derive their sets from
it):

- **Mandatory** (`RefreshCategoryClassification.Mandatory`): `LodestoneId`
  and `FreeCompany`. Always enabled regardless of stored settings. The
  worker needs the id for every sub-resource, and the FC link drives the
  strong-match `FreeCompanyChange` history pipeline.
- **Toggleable** (`RefreshCategoryClassification.Toggleable`): everything
  else (currently `Profile`, `ClassJobs`, `Gear`, `Mounts`, `Minions`,
  `Achievements`). User-controlled via the refresh-queue settings section.
  A disabled category is never queued, never fetched (even via the
  explicit "Refresh from Lodestone" button), and excluded from the
  queue-status badge.

To add a new mandatory category: extend
`RefreshCategoryClassification.IsMandatory`. To add a new toggleable
category: add the enum value, a `RefreshCategorySettings` property, an
`IsEnabled` mapping case, a settings-section checkbox, and a resx label.

Re-enabling a category later is passive: the next stale-check (next sighting,
next detail selection, next LodestoneId cascade) sees the category's
`UpdatedAt` as missing-or-stale and queues a catch-up fetch at the priority
that path normally uses.

Queue rows that exist for a now-disabled category are deleted by
`RefreshQueueDisabledCategoryPruneContributor` on the same 6h cadence as the
exhausted-row prune. The worker would skip them anyway (the `WHERE`
clause filters by allowed-category set), so the prune is purely a
table-size hygiene measure.

## Priorities

`RefreshPriority : byte`. Lower wins.

| Value | Name | When used |
|---:|---|---|
| 0 | `Immediate` | User click: `MainWindowState.Select` (only stale categories) or `Refresh` button (all categories, bypasses freshness). |
| 1 | `High` | A visible-in-range sighting via `Watcher.Observed`. |
| 2 | `Low` | Reserved for the future background sweep (not currently wired). |

## Worker order

```sql
SELECT *
FROM nexus_internal_refresh_queue
WHERE attempt_count < 5
  AND (last_failed_at IS NULL OR last_failed_at < @cutoff)
ORDER BY priority ASC, category ASC, enqueued_at ASC
LIMIT 1
```

The triple-ordering rule plays out as: most-urgent priority first;
within a priority `LodestoneId = 0` jumps ahead of all sub-resource
categories that would need the resolved id; same `(priority, category)`
is FIFO by enqueue time. Re-enqueueing with a more urgent priority
upserts the row and resets `enqueued_at` so the bumped task moves to
the front of its new lane.

## The LodestoneId cascade

When `RefreshCategory.LodestoneId` lands at the head of the queue:

1. Read `observed_player` for the ContentId.
2. Resolve the world name via `IGameDataLookups.GetWorldName(HomeWorldId)`.
3. `mPlayers.SearchAsync(name, worldName)` — Lodestone search.
4. Filter results by exact name (case-insensitive) and matching server
   (or null server, since Lodestone occasionally drops that column).
5. On hit:
   - `mWatcher.SetLodestoneIdAsync(contentId, resolvedId)` — writes the
     id to the observed-player row, updates in-memory cache, and fires
     `Observed` again with the resolved record.
   - `CascadeStaleSubResourcesAsync` — runs the freshness check against
     the ExternalData tables and enqueues whichever sub-resource
     categories are missing or past TTL. The worker picks them up on
     subsequent ticks at the same priority.
6. On miss: `MarkFailedAsync` — the row stays in the queue with
   `last_failed_at = now`, eligible for retry after the 60-min backoff.

## Failure handling

- **Backoff**: 60 minutes between attempts for failed rows. Tracked via
  `last_failed_at`; the worker's `WHERE` clause filters on it.
- **Attempt cap**: 5. After the 5th failure the row stays parked (still
  in the table for manual inspection) and the worker's `WHERE` clause
  excludes it. `IPlayerRefreshQueueService.Exhausted(contentId, category)`
  fires once when a row reaches this state — the plugin's
  `RefreshFailureNotificationProducer` subscribes and surfaces the
  failure as a chat notification.
- **Pruning**: exhausted rows used to be pruned inline by the worker.
  That responsibility moved to `RefreshQueueMaintenanceContributor`
  (`Maintenance/`), which runs once per day via the framework's
  `DbMaintenanceService`. The central loop avoids hammering the queue
  table during normal pick activity and keeps every cleanup task on
  one auditable cadence — see
  [NexusKit.Persistence/docs/maintenance.md](../../../NexusKit/NexusKit.Persistence/docs/maintenance.md).
- **Inter-item gap**: 2 s between consecutive picks. NetStone has its
  own throttle; this is defense in depth.
- **Wake-up**: a `SemaphoreSlim` lets the worker sleep until either the
  next enqueue lands or 5 minutes elapse (whichever comes first).

## Staleness check

`ComputeStaleSubResourcesAsync(lodestoneId)` reads `UpdatedAt` columns
from the relevant ExternalData tables and compares against
`mTtl.GetTtl()` (default 7 days, plugin-overridable).

| Category | Source table | Aggregation |
|---|---|---|
| `Profile` | `nexus_external_player_profile.updated_at` | single row per character |
| `ClassJobs` | `nexus_external_player_class_job.updated_at` | newest of all rows |
| `Gear` | `nexus_external_player_gear_slot.updated_at` | newest of all rows |
| `FreeCompany` | `nexus_external_free_company.updated_at` | via `profile.free_company_lodestone_id`; skipped when profile has no FC |
| `Mounts` | `nexus_external_player_collection_stats.updated_at` | `Kind = Mounts` row |
| `Minions` | same | `Kind = Minions` |
| `Achievements` | same | `Kind = Achievements` |

Missing rows count as stale.

## Public events

- `Enqueued(contentId, category)` — fires after a row is inserted *or*
  its priority is bumped.
- `Completed(contentId, category)` — fires after a row is successfully
  processed and deleted. UI caches subscribe to invalidate.
- `Exhausted(contentId, category)` — fires once when a row reaches
  `attempt_count == 5` (the attempt cap). `RefreshFailureNotificationProducer`
  in the plugin subscribes and emits a chat-notification line.

Handlers run on the worker thread; UI code must marshal to the draw
thread itself (a reference assignment is enough in practice).

## Schema reference

The table is defined by `InternalDataEntityModule` and created by the
`RebuildRefreshQueueOnContentId` migration in `InternalDataMigrations`:

```
nexus_internal_refresh_queue (
    content_id INTEGER NOT NULL,
    category INTEGER NOT NULL,
    priority INTEGER NOT NULL,
    enqueued_at TEXT NOT NULL,
    last_attempted_at TEXT NULL,
    last_failed_at TEXT NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (content_id, category)
)
INDEX (priority, category, enqueued_at)
INDEX (last_failed_at)
```

---

**Maintenance**: when you change the pick query, add a category, alter
the backoff or attempt cap, or move which table holds a sub-resource's
`UpdatedAt`, update this doc.
