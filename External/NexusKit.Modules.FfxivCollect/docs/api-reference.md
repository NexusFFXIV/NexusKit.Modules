# FFXIVCollect Module — API Reference

Complete reference for everything `NexusKit.Modules.FfxivCollect` exposes.

## `IFfxivCollectClient`

```csharp
public interface IFfxivCollectClient
{
    Task<Character?> GetCharacterAsync(ulong lodestoneId, CancellationToken ct = default);
    Task<ListResponse<Mount>?> GetMountsAsync(ulong lodestoneId, CancellationToken ct = default);
    Task<ListResponse<Minion>?> GetMinionsAsync(ulong lodestoneId, CancellationToken ct = default);
    Task<ListResponse<Achievement>?> GetAchievementsAsync(ulong lodestoneId, CancellationToken ct = default);
}
```

Each method maps to a single HTTP GET against the configured base URL.
Endpoints (relative to `BaseUrl`):

| Method | Path |
|---|---|
| `GetCharacterAsync` | `characters/{lodestoneId}` |
| `GetMountsAsync` | `characters/{lodestoneId}/mounts` |
| `GetMinionsAsync` | `characters/{lodestoneId}/minions` |
| `GetAchievementsAsync` | `characters/{lodestoneId}/achievements` |

Default `BaseUrl`: `https://ffxivcollect.com/api`.

### Behaviour matrix

| `ModuleEnabled` | `CacheEnabled` | Cache state | What happens |
|---|---|---|---|
| `false` | * | * | Returns `null`. No HTTP, no cache touch. |
| `true` | `true` | Fresh hit | Returns deserialised cached value. No HTTP. |
| `true` | `true` | Expired or miss | HTTP fetch → store in cache → return. |
| `true` | `true` | Cached body unparseable | Logs a warning, refetches via HTTP. |
| `true` | `false` | * | Always HTTP fetch. No cache read or write. |

Any HTTP exception (timeout, DNS failure, 4xx/5xx) is logged at `Warning`
and the method returns `null`. Cancellation propagates as
`OperationCanceledException`.

## Models

All models live in `Models/` as POCOs with `JsonPropertyName` mapping.

### `Character`

```csharp
public sealed class Character
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Server { get; set; }
    public string? DataCenter { get; set; }
    public string? Portrait { get; set; }
    public DateTime? LastParsed { get; set; }
    public CategorySummary? Mounts { get; set; }
    public CategorySummary? Minions { get; set; }
    public CategorySummary? Achievements { get; set; }
}

public sealed class CategorySummary
{
    public int Count { get; set; }
    public int Total { get; set; }
    public int? Ranking { get; set; }
    public bool? IsPublic { get; set; }   // mapped from "public"
}
```

### `Mount`, `Minion`, `Achievement`

Same shape — `Id`, `Name`, `Description`, `Patch`, `Owned`, `Icon`, plus
`Seats` on `Mount` and `Points` on `Achievement`.

### `ListResponse<T>`

```csharp
public sealed class ListResponse<T>
{
    public int Count { get; set; }
    public int Total { get; set; }
    public IReadOnlyList<T> Results { get; set; } = Array.Empty<T>();
}
```

Returned by all list endpoints.

## Settings

POCO: `FfxivCollectSettings : IModuleSettings`. Store key:
`nexuskit.modules.ffxivcollect.settings`.

| Property | Default | Notes |
|---|---|---|
| `ModuleEnabled` | `true` | When false, every client call is a no-op returning null. Rendered only in the "Modules" overview tab (the property is `.Hidden()` in detail tabs). |
| `BaseUrl` | `"https://ffxivcollect.com/api"` | Override only when you mirror the API. |
| `CacheEnabled` | `true` | Disable to always hit the live API. |
| `CacheTtlHours` | `24` | Slider range in UI: 1–168. Used as `Math.Max(1, value)` for safety. |

Schema registered in `FfxivCollectServiceCollectionExtensions`:
group `nexuskit.module.group` (order 100), title key
`nexuskit.modules.ffxivcollect.title`.

## Cache table (`nexus_ffxivcollect_cache`)

```sql
CREATE TABLE nexus_ffxivcollect_cache (
    key         TEXT PRIMARY KEY,    -- "ffxivcollect:<endpoint>"
    response    TEXT NOT NULL,        -- raw JSON body
    fetched_at  TEXT NOT NULL,
    expires_at  TEXT NOT NULL
);
```

Key format examples:
- `ffxivcollect:characters/12345`
- `ffxivcollect:characters/12345/mounts`
- `ffxivcollect:characters/12345/minions`
- `ffxivcollect:characters/12345/achievements`

A row whose `expires_at` ≤ `DateTime.UtcNow` is treated as a cache miss.
Rows are upserted on every successful fetch.

## Published IPCs

Names assume the plugin is `PlayerNexusTracker`; replace with your plugin
name otherwise.

| Full name | Signature | Returns |
|---|---|---|
| `PlayerNexusTracker.FfxivCollect.GetCharacterJson` | `Func<ulong, Task<string?>>` | JSON of `Character` |
| `PlayerNexusTracker.FfxivCollect.GetMountsJson` | `Func<ulong, Task<string?>>` | JSON of `ListResponse<Mount>` |
| `PlayerNexusTracker.FfxivCollect.GetMinionsJson` | `Func<ulong, Task<string?>>` | JSON of `ListResponse<Minion>` |
| `PlayerNexusTracker.FfxivCollect.GetAchievementsJson` | `Func<ulong, Task<string?>>` | JSON of `ListResponse<Achievement>` |

All four pipe through `IFfxivCollectClient`, so they respect the same
`ModuleEnabled` / `CacheEnabled` matrix above. A foreign plugin invoking an
IPC while our `ModuleEnabled = false` gets `null`.

## HTTP client configuration

A named `HttpClient` registered via `Microsoft.Extensions.Http`:

```csharp
services.AddHttpClient(FfxivCollectClient.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("NexusKit.Modules.FfxivCollect/0.1");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
```

`HttpClientName` = `"nexuskit-ffxivcollect"`. The client itself is a
singleton; per-call `HttpClient` instances come from the factory pool.

## Localization keys

Module-shipped keys (see `Resources/Strings.resx` and `Strings.de.resx`):

| Key | Used for |
|---|---|
| `nexuskit.modules.ffxivcollect.title` | Settings tab label |
| `nexuskit.modules.ffxivcollect.base_url.label` | Base-URL field label |
| `nexuskit.modules.ffxivcollect.base_url.description` | Tooltip |
| `nexuskit.modules.ffxivcollect.base_url.placeholder` | Empty-state ghost text |
| `nexuskit.modules.ffxivcollect.cache_enabled.label` | Cache toggle label |
| `nexuskit.modules.ffxivcollect.cache_enabled.description` | Tooltip |
| `nexuskit.modules.ffxivcollect.cache_ttl.label` | TTL slider label |
| `nexuskit.modules.ffxivcollect.cache_ttl.description` | Tooltip |

The `ModuleEnabled` toggle uses `nexuskit.module.enabled.label` / `.description`
from the framework's own `.resx` (Framework.resx).

---

**Maintenance**: when you add an endpoint, change a model field, alter the
cache key format, or publish/remove an IPC, update the corresponding
tables in this file.
