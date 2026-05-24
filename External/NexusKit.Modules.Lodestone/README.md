# NexusKit.Modules.Lodestone

Reusable NexusKit module: a typed wrapper around
[NetStone](https://github.com/NotNite/NetStone) for scraping FFXIV's Lodestone
website, with a SQLite-backed response cache and JSON-payload IPC providers.

**Dalamud-free.** Uses NetStone + `System.Text.Json`.

## Public API

| Type | File | Purpose |
|---|---|---|
| `ILodestoneClient` | `Clients/ILodestoneClient.cs` | `GetCharacterAsync(lodestoneId)`, `SearchCharacterAsync(name, world)`. |
| `LodestoneClient` (internal) | `Clients/LodestoneClient.cs` | Implementation. Lazy-initialises `NetStone.LodestoneClient` once (semaphore-guarded); honours `ModuleEnabled`; reads/writes the cache. |
| `LodestoneSettings` | `LodestoneSettings.cs` | `IModuleSettings` POCO: `ModuleEnabled`, `CacheEnabled`, `CacheTtlHours`. |
| `Models/*` | `Models/` | `CharacterSummary`, `CharacterSearchResult`, `CharacterSearchEntry` — POCOs mapped from NetStone's parsed types. |
| `LodestoneCacheEntity` + `LodestoneCacheEntityModule` | `Persistence/` | The `nexus_lodestone_cache` table. |
| `LodestoneIpcProvider` (internal) | `Ipc/LodestoneIpcProvider.cs` | Publishes the two endpoints as IPCs (JSON payloads). |

## Registration

```csharp
services.AddNexusKitLodestone();
```

Registers: settings schema, cache entity module, `ILocalizationSource`,
`ILodestoneClient` singleton, IPC provider, and
`LodestoneCacheMaintenanceContributor` — a `IDbMaintenanceContributor`
that drops expired cache rows on the framework's daily maintenance tick.
Schema lands in the "Modules" section with group order 200 (after
FfxivCollect at 100).

## Dependencies

- NuGet: `NetStone` 1.4.1, `Microsoft.Extensions.Logging.Abstractions` 10.0.7
- ProjectRefs: `NexusKit.Core`, `NexusKit.Persistence`

## Settings (`nexuskit.modules.lodestone.settings` row in the `settings` table)

| Property | Default | UI label key |
|---|---:|---|
| `ModuleEnabled` | `true` | `nexuskit.module.enabled.label` (rendered only in the "Modules" overview tab; `.Hidden()` in detail tab) |
| `CacheEnabled` | `true` | `nexuskit.modules.lodestone.cache_enabled.label` |
| `CacheTtlHours` | `24` | `nexuskit.modules.lodestone.cache_ttl.label` |

Lodestone refreshes slowly in practice (hours between page updates), so the
default cache TTL of one day is comfortable.

## Published IPCs

| IPC name | Signature | Returns |
|---|---|---|
| `<Plugin>.Lodestone.GetCharacterJson` | `Func<ulong, Task<string?>>` | JSON of `CharacterSummary` |
| `<Plugin>.Lodestone.SearchCharacterJson` | `Func<string, string, Task<string?>>` | JSON of `CharacterSearchResult` |

## NetStone field caveats

NetStone's classes change shape across versions. `LodestoneClient`
defensively maps:
- `LodestoneCharacter.FreeCompany` — accessed via reflection, since its
  property names differ between NetStone releases.
- Search results' `Server`/`World` — same reason.

When upgrading NetStone, re-verify these in
`Clients/LodestoneClient.cs:TryReadFreeCompanyId` and `TryReadString`.

## Example: open a character and read bio

```csharp
public sealed class BioReader
{
    private readonly ILodestoneClient client;
    public BioReader(ILodestoneClient c) { client = c; }

    public async Task<string?> ReadBioAsync(ulong lodestoneId, CancellationToken ct)
    {
        var summary = await client.GetCharacterAsync(lodestoneId, ct);
        return summary?.Bio;
    }
}
```

---

**Maintenance**: when you bump NetStone, add an endpoint, or change a
settings property, update the tables above and the NetStone field-caveats
section.
