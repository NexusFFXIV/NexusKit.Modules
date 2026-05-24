# NexusKit.Modules.ExternalData

Aggregator over `NexusKit.Modules.Lodestone` and `NexusKit.Modules.FfxivCollect`
that produces a unified `Player` / `FreeCompany` / catalog view backed by an
SQLite cache. Domain code talks to this module, not to the underlying scrapers
or REST clients directly.

**No Dalamud reference.** The module depends only on the two external-source
modules and the persistence layer; consumers see EF Core / plain POCOs.

## Public API

| Type | File | Purpose |
|---|---|---|
| `IExternalDataService` | `IExternalDataService.cs` | Umbrella entry point exposing every sub-service. Plugins can depend on this or inject the specialised ones directly. |
| `IExternalDataPlayerService` | `Players/IExternalDataPlayerService.cs` | `GetAsync(lodestoneId, PlayerInclude)`, `SearchAsync(name, world)`, `GetByNameAsync(name, homeWorldId)` (DB-only). |
| `IExternalDataFreeCompanyService` | `FreeCompanies/IExternalDataFreeCompanyService.cs` | Lodestone FC page fetcher; auto-caches into `nexus_external_free_company`. |
| `IExternalDataMountCatalog` | `Catalogs/IExternalDataMountCatalog.cs` | `GetAsync(id)` / `ListAsync()` over the global mount catalog. |
| `IExternalDataMinionCatalog` | `Catalogs/IExternalDataMinionCatalog.cs` | Same shape for minions. |
| `IExternalDataAchievementCatalog` | `Catalogs/IExternalDataAchievementCatalog.cs` | Same shape for achievements. |
| `IExternalDataItemCatalog` | `Catalogs/IExternalDataItemCatalog.cs` | Same shape for items. |
| `Player` (record) | `Models/Player.cs` | Unified per-character view; sub-resources gated by `PlayerInclude`. |
| `PlayerInclude` (flags) | `Models/PlayerInclude.cs` | `Profile`, `ClassJobs`, `Gear`, `FreeCompany`, `Mounts`, `Minions`, `Achievements`, `Items`, `All`. |
| `PlayerCollections`, `OwnedAchievement`, `PlayerSearchResult`, `PlayerIdentity` | `Models/` | Supporting record types. |
| `IPlayerChangeRecorder` (seam) | `Players/IPlayerChangeRecorder.cs` | Optional cross-module hook fired by `ExternalDataPlayerService` when an upsert detects a name / home-world / FC change for a previously-seen player. Implementations live OUTSIDE this module (the plugin's `HistoryChangeRecorder` in `NexusKit.Modules.PlayerEnrichment` is the only consumer today). Lets InternalData's history log learn about strong-match Lodestone diffs without ExternalData taking a dependency on InternalData. |

## Registration

```csharp
services.AddNexusKitExternalData();
```

Pulls in the underlying sources (`AddNexusKitLodestone()` + `AddNexusKitFfxivCollect()`),
registers the entity module, and wires every catalog + the player / FC services.

## Dependencies

- ProjectRefs: `NexusKit.Core`, `NexusKit.Persistence`, `NexusKit.GameData`,
  `NexusKit.Modules.Lodestone`, `NexusKit.Modules.FfxivCollect`
- No direct NuGet (the underlying source modules pull NetStone / HttpClient)

## Database

Tables live under the `nexus_external_` prefix (see `ExternalDataEntityModule`):

| Table | Purpose |
|---|---|
| `nexus_external_player` | Core identity row (lodestone_id, name, home_world_id, data_center_id, updated_at). |
| `nexus_external_player_profile` | Bio, nameday, avatar URLs, FC link, GC ids. |
| `nexus_external_player_class_job` | One row per (lodestone_id, class_job_id) with the player's level. |
| `nexus_external_player_gear_slot` | One row per gear slot (item, glamour, colours, materia, item-level, creator). |
| `nexus_external_player_collection_stats` | (lodestone_id, kind) → (count, total, ranking, public). |
| `nexus_external_player_owned` | (lodestone_id, kind, entry_id) → achieved_at. Mounts / minions / achievements / items. |
| `nexus_external_free_company` | FC details (name, tag, slogan, estate, focus flags, …). |

All `updated_at` columns are stamped by the service on every write; the
PlayerEnrichment refresh queue reads them to compute per-category staleness.

## DB row ↔ JSON mapping

The `Mapping/` folder holds the conversion code between live FFXIVCollect
/ Lodestone JSON payloads and the EF Core entity rows the service writes:

- `Mapping/PlayerMappings.cs` — turns a `Character` (FFXIVCollect) + a
  `CharacterSummary` (Lodestone) into the unified `Player` record
  consumers see, plus the corresponding entity writes.
- `Mapping/CatalogMappings.cs` — the same for global catalogs (mount,
  minion, achievement, item). Keeps the mapping concerns out of the
  service classes so the latter stay readable.

## Example: fetch a player

```csharp
public sealed class WhoIsThat
{
    private readonly IExternalDataPlayerService players;
    public WhoIsThat(IExternalDataPlayerService p) => players = p;

    public async Task PrintMountsAsync(ulong lodestoneId, CancellationToken ct)
    {
        var player = await players.GetAsync(
            lodestoneId,
            PlayerInclude.Profile | PlayerInclude.Mounts,
            ct);
        if (player is null) return;

        Console.WriteLine($"{player.Name} @ {player.HomeWorldId}");
        var mounts = player.Collections?.Mounts;
        if (mounts is not null)
            Console.WriteLine($"  Mounts: {mounts.Count}/{mounts.Total}");
    }
}
```

`GetAsync` runs all enabled sources in parallel for the requested
`PlayerInclude` flags, merges the results, and upserts the new data into
the cache. Subsequent calls within freshness can be served from the cache
alone without hitting the network — the PlayerEnrichment refresh queue is
what schedules the actual network refreshes.

## Where to read next

- [docs/player-service.md](docs/player-service.md) — `GetAsync` composition,
  per-flag fetch behaviour, world/datacenter row-id mapping, DB cache
  fallback semantics.

---

**Maintenance**: when you add a `PlayerInclude` flag, change a catalog
interface, or alter what `GetAsync` writes back to the DB, update this
README and `docs/player-service.md`.
