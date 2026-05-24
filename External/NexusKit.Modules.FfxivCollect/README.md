# NexusKit.Modules.FfxivCollect

Reusable NexusKit module: a typed HTTP client for the
[ffxivcollect.com](https://ffxivcollect.com/api) REST API with a SQLite-backed
response cache and JSON-payload IPC providers for cross-plugin consumers.

**Dalamud-free.** Uses `Microsoft.Extensions.Http` + `System.Text.Json`.

## Public API

| Type | File | Purpose |
|---|---|---|
| `IFfxivCollectClient` | `Clients/IFfxivCollectClient.cs` | `GetCharacterAsync`, `GetMountsAsync`, `GetMinionsAsync`, `GetAchievementsAsync` — all by Lodestone ID. |
| `FfxivCollectClient` (internal) | `Clients/FfxivCollectClient.cs` | Implementation. Honours the module's `ModuleEnabled` flag (returns `null` when off), reads/writes the cache, then talks to the API. |
| `FfxivCollectSettings` | `FfxivCollectSettings.cs` | `IModuleSettings` POCO: `ModuleEnabled`, `BaseUrl`, `CacheEnabled`, `CacheTtlHours`. |
| `Models/*` | `Models/` | Plain POCOs — `Character`, `Mount`, `Minion`, `Achievement`, `ListResponse<T>`, `CategorySummary`. |
| `FfxivCollectCacheEntity` + `FfxivCollectCacheEntityModule` | `Persistence/` | The `nexus_ffxivcollect_cache` table contributed via `IEntityModule`. |
| `FfxivCollectIpcProvider` (internal) | `Ipc/FfxivCollectIpcProvider.cs` | Publishes the four endpoints as IPCs (JSON payloads). |

## Registration

```csharp
services.AddNexusKitFfxivCollect();
```

Registers: settings schema, cache entity module, the module's own
`ILocalizationSource` (English + German labels), the HTTP-client factory
binding (named `"nexuskit-ffxivcollect"`), the `IFfxivCollectClient`
singleton, the IPC provider, and `FfxivCollectCacheMaintenanceContributor`
— a `IDbMaintenanceContributor` that drops expired cache rows on the
framework's daily maintenance tick.

The schema lands in the "Modules" section of the auto-settings UI with
group order 100.

## Dependencies

- NuGet: `Microsoft.Extensions.Http` 10.0.7,
  `Microsoft.Extensions.Logging.Abstractions` 10.0.7
- ProjectRefs: `NexusKit.Core`, `NexusKit.Persistence`

## Settings (`nexuskit.modules.ffxivcollect.settings` row in the `settings` table)

| Property | Default | UI label key |
|---|---:|---|
| `ModuleEnabled` | `true` | `nexuskit.module.enabled.label` (rendered only in the "Modules" overview tab; `.Hidden()` in detail tab) |
| `BaseUrl` | `https://ffxivcollect.com/api` | `nexuskit.modules.ffxivcollect.base_url.label` |
| `CacheEnabled` | `true` | `nexuskit.modules.ffxivcollect.cache_enabled.label` |
| `CacheTtlHours` | `24` | `nexuskit.modules.ffxivcollect.cache_ttl.label` |

While `ModuleEnabled` is `false`, every public method on `IFfxivCollectClient`
short-circuits to `null` — no HTTP, no cache read.

## Published IPCs

Full names assume the plugin's name is `PlayerNexusTracker`; the actual
prefix is `IPluginContext.PluginName`.

| IPC name | Signature | Returns |
|---|---|---|
| `PlayerNexusTracker.FfxivCollect.GetCharacterJson` | `Func<ulong, Task<string?>>` | JSON of `Character` |
| `PlayerNexusTracker.FfxivCollect.GetMountsJson` | `Func<ulong, Task<string?>>` | JSON of `ListResponse<Mount>` |
| `PlayerNexusTracker.FfxivCollect.GetMinionsJson` | `Func<ulong, Task<string?>>` | JSON of `ListResponse<Minion>` |
| `PlayerNexusTracker.FfxivCollect.GetAchievementsJson` | `Func<ulong, Task<string?>>` | JSON of `ListResponse<Achievement>` |

Foreign plugins consume via:

```csharp
var func = pi.GetIpcSubscriber<ulong, Task<string?>>(
    "PlayerNexusTracker.FfxivCollect.GetCharacterJson");
var json = await func.InvokeFunc(lodestoneId);
```

## Example: read mounts directly

```csharp
public sealed class MountReader
{
    private readonly IFfxivCollectClient client;
    public MountReader(IFfxivCollectClient c) { client = c; }

    public async Task<int> CountMountsAsync(ulong lodestoneId, CancellationToken ct)
    {
        var resp = await client.GetMountsAsync(lodestoneId, ct);
        return resp?.Count ?? 0;
    }
}
```

## Translations

Module-specific labels live in `Resources/Strings.resx` (EN) and
`Strings.de.resx` (DE). Add a new language by dropping
`Strings.<culture>.resx` alongside and re-building.

The plugin can override any key by registering its own
`ILocalizationSource` — layered localizer iterates plugin sources first.

---

**Maintenance**: when you add a new endpoint, settings property, IPC, or
cache-table column, update the tables above. Plugin authors will read this
README before opening C# files.
