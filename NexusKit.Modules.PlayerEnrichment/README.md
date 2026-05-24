# NexusKit.Modules.PlayerEnrichment

Cross-cutting layer between `NexusKit.Modules.InternalData` (live ObjectTable
observation) and `NexusKit.Modules.ExternalData` (Lodestone + FFXIVCollect).
Owns the persistent **refresh queue** that keeps cached external data fresh
for characters the watcher sees, and folds Lodestone-id resolution into the
queue as its highest-priority category.

**No Dalamud reference** of its own — it composes types from the two modules.
A plugin that wants the combined experience references *only* this project
and gets both data layers transitively.

## Public API

| Type | File | Purpose |
|---|---|---|
| `IPlayerRefreshQueueService` | `IPlayerRefreshQueueService.cs` | Enqueue refresh work by `(ContentId, RefreshCategory)`; subscribe to `Enqueued` / `Completed` / `Exhausted` events. |
| `IRefreshTtlProvider` | `IRefreshTtlProvider.cs` | Plugin-injectable freshness TTL — service consults it on every stale check. |
| `DefaultRefreshTtlProvider` | `IRefreshTtlProvider.cs` | Fallback 7-day TTL used when the host plugin doesn't register its own provider (`TryAddSingleton`-registered, so a plugin-side provider wins). |
| `IRefreshCategoryPolicy` | `IRefreshCategoryPolicy.cs` | Per-category on/off gate consulted at every enqueue and worker-pick boundary. Mandatory categories always report enabled; the toggleable subset is user-controlled via the refresh-queue settings section. |
| `DefaultRefreshCategoryPolicy` | `IRefreshCategoryPolicy.cs` | Fallback policy that enables everything — used by library consumers that don't wire the settings-backed implementation. |
| `RefreshCategorySettings` | `RefreshCategoryPolicy.cs` | Persisted POCO behind the policy. One `bool` per toggleable category; defaults are all-on. |
| `RefreshCategoryClassification` | `RefreshCategoryClassification.cs` | Single source of truth for the structural facts about `RefreshCategory`: which are mandatory (`LodestoneId`, `FreeCompany`), which are user-toggleable, which qualify as sub-resources for the cascade / force-refresh paths. Every other caller derives its sets from here. |
| `IRefreshQueueStatsService` | `Maintenance/IRefreshQueueStatsService.cs` | Read-only snapshot of the queue. Counts: total / active / exhausted / eligible-now / backoff-waiting. Two ETAs: `EstimatedDrain` covers only the rows the worker can pick *right now* (drops to zero when idle), and `EarliestRetryAt` is when the next cooldown row clears (null when nothing is waiting). Used by the refresh-queue diagnostics tab. |
| `PlayerEnrichmentServiceCollectionExtensions` | `PlayerEnrichmentServiceCollectionExtensions.cs` | `AddNexusKitPlayerEnrichment()` — single composition entry; pulls Internal + External and registers the queue + bridges + maintenance contributor + diagnostics section. |

Internal bridges (registered by `AddNexusKitPlayerEnrichment` but not part
of the public surface):

| Type | File | Role |
|---|---|---|
| `HistoryChangeRecorder` | `Bridges/HistoryChangeRecorder.cs` | Implements ExternalData's `IPlayerChangeRecorder` seam: Lodestone-detected player changes (name / world / FC) land in the InternalData history log. The seam is the only place ExternalData and InternalData touch — bridge sits in PlayerEnrichment because it's the only assembly allowed to know both bundles. |
| `LiveTagChangeRefreshTrigger` | `Bridges/LiveTagChangeRefreshTrigger.cs` | Subscribes to live `ObservationProcessed` events; a `tagA → tagB` flip fast-tracks a Profile + FreeCompany refresh at `Immediate` priority. Lets the strong-match Lodestone diff emit a precise `FreeCompanyChange` history row seconds after a swap instead of waiting for the next TTL sweep. |
| `RefreshQueueMaintenanceContributor` | `Maintenance/` | `IDbMaintenanceContributor` that prunes exhausted refresh-queue rows (`attempt_count ≥ 5` past their backoff). Runs daily via the persistence framework's maintenance loop — replaces the inline pruning that used to live in the worker. |
| `RefreshQueueDisabledCategoryPruneContributor` | `Maintenance/` | `IDbMaintenanceContributor` that deletes queued rows whose category has been disabled via `IRefreshCategoryPolicy`. Same 6h cadence as the exhausted-row prune; no-ops when every category is enabled. |
| `RefreshQueueSettingsSection` | `Maintenance/RefreshQueueSettingsSection.cs` | `IAutoSettingsSection` contributing the refresh-queue diagnostics tab plus the per-category enable toggles. |
| `RefreshCategoryPolicy` | `RefreshCategoryPolicy.cs` | Settings-backed `IRefreshCategoryPolicy` implementation; reads `RefreshCategorySettings` from `ISettingsStore` and exposes a live mutable cache to the settings section. |

The queue's enum types (`RefreshCategory`, `RefreshPriority`) and entity
(`InternalRefreshQueueEntity`) live in `NexusKit.Modules.InternalData` —
the table is part of the internal DB schema. PlayerEnrichment owns the
*service* that operates on them.

## Registration

```csharp
// Single entry — pulls InternalData + ExternalData transitively.
services.AddNexusKitPlayerEnrichment();

// (Optional) override the TTL provider before the call above; the
// queue uses TryAddSingleton so a plugin-side provider wins.
services.AddSingleton<IRefreshTtlProvider, MySettingsBackedTtlProvider>();

// Eagerly resolve so the worker thread + Observed subscription start
// on plugin load instead of waiting for first UI access.
host.Services.GetRequiredService<IPlayerRefreshQueueService>();
```

## How the queue runs

The service constructor subscribes to `IInternalDataPlayerWatcher.Observed`
and starts a single `Task.Run` worker. Each sighting with a new ContentId
triggers `EnqueueStaleAsync` once per session (deduped in-memory). The
worker pulls rows ordered by `(priority ASC, category ASC, enqueued_at ASC)`
— so an Immediate from a user click jumps ahead of an in-range refresh,
and within a lane `RefreshCategory.LodestoneId = 0` runs before any
sub-resource that would need the id anyway.

Failed attempts get a 60-min backoff via `last_failed_at`; rows are capped
at 5 attempts and stay parked at that point for manual inspection. Between
items the worker waits 2 s as defense-in-depth on top of NetStone's own
throttling.

## Dependencies

- ProjectRefs: `NexusKit.Core`, `NexusKit.Persistence`,
  `NexusKit.Modules.InternalData`, `NexusKit.Modules.ExternalData`
- No NuGet of its own

## Where to read next

- [docs/refresh-queue.md](docs/refresh-queue.md) — full queue design:
  category ordering, priority lanes, backoff/retry, the LodestoneId
  cascade, where the staleness check reads each sub-resource's
  `UpdatedAt` timestamp.

---

**Maintenance**: when you add a `RefreshCategory`, change the worker's
pick query, or restructure the cascade, update this README and the
refresh-queue doc.
