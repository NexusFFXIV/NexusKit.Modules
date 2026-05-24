# NexusKit.Modules.InternalData

Live in-game observation pipeline. Watches Dalamud's `IObjectTable` for
visible players, persists each sighting into `nexus_internal_observed_player`,
tracks per-field change history in `nexus_internal_player_history`, and
records territory-bounded encounter rosters in
`nexus_internal_encounter` + `nexus_internal_player_encounter`.

**This module knows nothing about Lodestone or FFXIVCollect.** The bridge
to remote data sources lives in `NexusKit.Modules.PlayerEnrichment`, which
references both this module and `ExternalData`. That separation lets a
downstream plugin pick up *only* the observation layer if that's all it
wants.

## Public API

| Type | File | Purpose |
|---|---|---|
| `IInternalDataPlayerWatcher` | `Players/IInternalDataPlayerWatcher.cs` | Live observation feed: `Recent` snapshot, `CurrentlyVisible` set, `Observed` / `ObservationProcessed` events, `SetLodestoneIdAsync` for the enrichment layer to write resolved ids back. |
| `IInternalDataHistoryService` | `History/IInternalDataHistoryService.cs` | Append-only change log + unread tracking: `GetForContentIdAsync(contentId)`, `GetUnreadHistoryKindsByContentIdAsync()`, `MarkAllReadForContentIdAsync(contentId)`, `MarkAllReadAsync()` (global bulk), `InsertIfNewAsync(...)` (used by external recorders), `HistoryAdded` / `HistoryRead` / `AllHistoryRead` events. |
| `IInternalDataEncounterTracker` | `Encounters/IInternalDataEncounterTracker.cs` | Territory-bounded session log: `GetForContentIdAsync(contentId)`, `GetEncounterCountAsync(contentId)` (replaces the retired `observed_player.seen_count`), `EncountersChanged(contentId, encounterId)` event. |
| `ObservedPlayer` (record) | `Players/ObservedPlayer.cs` | Per-sighting view: ContentId, LodestoneId, Name, HomeWorld, ClassJobId, Level, Customize, CompanyTag, mount/minion/online-status. `FirstSeen`/`LastSeen`/`SeenCount` are derived from the encounter aggregate, not stored on the row anymore. |
| `PlayerObservationEvent` | `Players/PlayerObservationEvent.cs` | `(Previous, Current, ObservedAt, CanTrackChange)` tuple fired after each upsert — feeds the history differ + live-tag refresh trigger. `CanTrackChange` is false during duties/cutscenes/zoning so consumers can suppress noisy diffs. |
| `EncounterEntry` (record) | `Encounters/EncounterEntry.cs` | One `(EncounterId, TerritoryTypeId, StartedAt, EndedAt, JobId, Level, FirstSeenAt, LastSeenAt)` row returned by the tracker — joins encounter + player_encounter for the UI. |
| `PlayerHistoryEntry` (record) | `History/PlayerHistoryEntry.cs` | One row in the history log: `(Id, ContentId, Kind, ChangedAt, OldValue, NewValue, IsRead)`. |
| `PlayerHistoryKind` (enum) | `History/PlayerHistoryKind.cs` | `NameChange`, `HomeWorldChange`, `CustomizeChange`, `FreeCompanyChange`. |
| `RefreshCategory`, `RefreshPriority` (enums) | `Refresh/RefreshCategory.cs` | Discriminators on the persistent refresh-queue table — used by `PlayerEnrichment` but the enums + entity live here because the table does. |
| `InternalRefreshQueueEntity` | `Persistence/InternalRefreshQueueEntity.cs` | The queue row (composite PK `content_id + category`). |
| `InternalDataEntityModule`, `InternalDataMigrations` | `Persistence/` | Schema contribution + forward-only migrations registered with the persistence framework. |
| `PlayerFilterViewBuilder` | `Persistence/PlayerFilterViewBuilder.cs` | `IDatabaseViewBuilder` that materialises the denormalised `nexus_filter_player` view (joins observed_player + profile + class jobs + history aggregates) for the plugin's player-filter SQL pre-narrow. |

## Registration

```csharp
services.AddNexusKitInternalData();

// Eagerly resolve so the framework-thread subscription wires up before
// anyone opens the UI. The history service + encounter tracker also
// subscribe in their constructors.
host.Services.GetRequiredService<IInternalDataPlayerWatcher>();
host.Services.GetRequiredService<IInternalDataHistoryService>();
host.Services.GetRequiredService<IInternalDataEncounterTracker>();
```

## Dependencies

- ProjectRefs: `NexusKit.Core`, `NexusKit.Persistence`, `NexusKit.GameData`
- Requires Dalamud handles already registered in DI: `IFramework`,
  `IObjectTable`, `IClientState`, `ICondition`, `IClientState.TerritoryChanged`

## Database

Five tables under the `nexus_internal_` prefix:

| Table | Purpose |
|---|---|
| `nexus_internal_observed_player` | One row per character the local player has ever seen. PK = `content_id`. Indexed on name, lodestone_id. `LastSeen` / `SeenCount` columns were retired — both are now derived from `player_encounter` aggregates. |
| `nexus_internal_player_history` | Append-only change log. PK = auto-incrementing id; indexed on `(content_id, changed_at DESC)`, `kind`, and `(content_id, is_read)` for the unread-dot sweep. |
| `nexus_internal_refresh_queue` | Refresh-queue rows (used by PlayerEnrichment). PK = `(content_id, category)`. |
| `nexus_internal_encounter` | One row per territory-bounded session of the local player. PK = auto-id; `started_at` indexed DESC for the recent-encounters scan. |
| `nexus_internal_player_encounter` | Roster table: one row per (encounter, sighted-character) with first/last-seen, job, level. PK = auto-id; indexed on `(content_id, first_seen_at DESC)` for "encounters for player X" and on `encounter_id` for the join from encounter → roster. |

## Example: react to a new sighting

```csharp
public sealed class GreetVisibleCharacters : IDisposable
{
    private readonly IInternalDataPlayerWatcher watcher;
    public GreetVisibleCharacters(IInternalDataPlayerWatcher w)
    {
        watcher = w;
        watcher.Observed += OnObserved;
    }

    private void OnObserved(ObservedPlayer p)
    {
        // Handler runs on the framework thread; keep it cheap or
        // marshal to threadpool with Task.Run.
        Console.WriteLine($"Saw {p.Name} (cid={p.ContentId})");
    }

    public void Dispose() => watcher.Observed -= OnObserved;
}
```

## Example: read history for a character

```csharp
var entries = await history.GetForContentIdAsync(contentId, limit: 50);
foreach (var e in entries)
    Console.WriteLine($"{e.ChangedAt:yyyy-MM-dd} {e.Kind}: {e.OldValue} → {e.NewValue}");
```

## Where to read next

- [docs/observation.md](docs/observation.md) — scan tick cadence,
  framework/threadpool split, `SetLodestoneIdAsync` contract.
- [docs/history.md](docs/history.md) — diff rules per `PlayerHistoryKind`,
  the `tag → null` suppression rationale, write-through model.
- [docs/encounters.md](docs/encounters.md) — territory-change handling,
  the open-encounter shutdown stamping, why `seen_count` is now derived.

---

**Maintenance**: when you add a `PlayerHistoryKind`, change the scan
cadence, or move which thread fires which event, update this README and
the matching doc.
