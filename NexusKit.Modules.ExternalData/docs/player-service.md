# Player service

`IExternalDataPlayerService` is the public entry point for per-character
data. Three methods, three very different cost profiles.

## `GetAsync(lodestoneId, include, ct)`

Fetches the player record. `include` is a `[Flags]` enum
(`PlayerInclude.None | Profile | ClassJobs | Gear | FreeCompany | Mounts |
Minions | Achievements | Items | All`); each flag toggles an independent
fan-out request against the relevant source.

Concretely (`ExternalDataPlayerService.GetAsync`):

| Flag | Fetched from | DB tables written |
|---|---|---|
| (always) | FFXIVCollect `/characters/{lid}` | `nexus_external_player` (core identity) |
| `Profile` | Lodestone character page | `nexus_external_player_profile` |
| `ClassJobs` | Lodestone class-job page | `nexus_external_player_class_job` |
| `Gear` | Lodestone character page (gear set) | `nexus_external_player_gear_slot` |
| `Mounts` | FFXIVCollect + Lodestone mount-list scrape | `_collection_stats` (kind=Mounts) + `_player_owned` rows |
| `Minions` | same | `_collection_stats` (kind=Minions) + `_player_owned` rows |
| `Achievements` | FFXIVCollect + Lodestone achievements page (for the timestamps) | `_collection_stats` (kind=Achievements) + `_player_owned` rows |
| `FreeCompany` | Lodestone FC page (via `profile.free_company_lodestone_id`) | `nexus_external_free_company` |

All sub-resource requests run in parallel via `Task.WhenAll`; the unified
`Player` record is built once everything settles. Network failures inside
one sub-request leave that field null on the returned record — they don't
fail the whole call.

### Identity fallback

If the FFXIVCollect character lookup returns nothing usable (no name /
server / data-center) the service falls back to `ReadFromDbAsync(lodestoneId,
include, ct)` which composes a `Player` purely from cached tables. Used for
characters whose remote profile is privated or temporarily unavailable — the
plugin still has *some* view of them after a previous successful fetch.

### World + Data Center as row ids

`nexus_external_player.home_world_id` and `data_center_id` store **Lumina
row ids**, not localised strings (see the migration history). At fetch
time the service resolves the localised name FFXIVCollect returned via
`IGameDataResolver.ResolveIdByName(name, kind, English)` and stores the
ids. The UI looks up the display name back through `IGameDataLookups` —
that's the "store ids, not strings" pattern documented in
[NexusKit.GameData/README.md](../../../NexusKit/NexusKit.GameData/README.md).

## `SearchAsync(name, world, ct)`

Lodestone search by `(name, world)`. Returns an `IReadOnlyList<PlayerSearchResult>`
of `(lodestoneId, name, server, avatarUrl)` matches. Used by the PlayerEnrichment
LodestoneId category to resolve a sighting's id from its name + home world.

Network-only — does not consult the cache. Empty list when no
search-capable source is enabled.

## `GetByNameAsync(name, homeWorldId, ct)`

**DB-only** lookup by `(name, home_world_id)`. Returns a minimal
`PlayerIdentity` tuple (`lodestoneId, name, homeWorldId, dataCenterId`)
when a previously-fetched player matches, otherwise null.

Never touches the network — use this from anywhere that just wants to know
"do I have this character cached?" without paying for an HTTP round-trip.

## Freshness / cache semantics

`GetAsync` always upserts whatever it fetched, stamping `UpdatedAt = now`
on the relevant rows. It does **not** itself check whether the cache is
fresh; callers either:

- Call it unconditionally to force a refresh (the explicit "Refresh"
  button path), or
- Let `NexusKit.Modules.PlayerEnrichment`'s refresh queue decide whether
  to enqueue — the queue's `EnqueueStaleAsync` compares `UpdatedAt`
  against the plugin-configured TTL and only enqueues categories
  actually past it.

## Concurrency

The service is registered as a singleton and is reentrant. Multiple
`GetAsync` calls for the same lodestone id run independently — there's
no in-flight deduplication, so callers that fan out heavily (e.g. opening
ten detail panels in succession) should rely on the refresh queue's
per-(content_id, category) PK to coalesce.

---

**Maintenance**: when you add a `PlayerInclude` flag or change which DB
table a sub-resource writes to, update the table at the top.
