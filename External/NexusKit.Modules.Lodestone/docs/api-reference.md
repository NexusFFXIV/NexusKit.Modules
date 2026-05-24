# Lodestone Module — API Reference

Complete reference for everything `NexusKit.Modules.Lodestone` exposes.

## `ILodestoneClient`

```csharp
public interface ILodestoneClient
{
    Task<CharacterSummary?> GetCharacterAsync(ulong lodestoneId, CancellationToken ct = default);
    Task<CharacterSearchResult?> SearchCharacterAsync(string name, string world, CancellationToken ct = default);
}
```

Both methods proxy to NetStone after applying our caching layer.

### `GetCharacterAsync`

Hits NetStone's `LodestoneClient.GetCharacter(id.ToString())` and maps the
resulting `LodestoneCharacter` into our `CharacterSummary` POCO.

### `SearchCharacterAsync`

Hits NetStone's `LodestoneClient.SearchCharacter(new CharacterSearchQuery
{ CharacterName = name, World = world })`, then maps the results.

### Behaviour matrix

Same shape as FFXIVCollect:

| `ModuleEnabled` | `CacheEnabled` | Cache state | Behaviour |
|---|---|---|---|
| `false` | * | * | Returns `null` |
| `true` | `true` | Fresh hit | Returns cached POCO |
| `true` | `true` | Expired / miss | NetStone fetch → store → return |
| `true` | `true` | Cache body unparseable | Log + refetch |
| `true` | `false` | * | Always NetStone fetch |

`OperationCanceledException` propagates; any other NetStone or HTTP error
is logged at `Warning` and the method returns `null`.

## NetStone client initialisation

`NetStone.LodestoneClient` is constructed via the async static
`GetClientAsync()`. We lazily initialise it on the first call (any of the
public methods may trigger it) and cache the instance on the singleton.
Concurrent first-callers are guarded by a `SemaphoreSlim` so initialisation
happens once.

If initialisation throws (network error fetching CSS selectors, etc.) we
log + return `null`; later calls retry the initialisation. This means a
transient network failure during plugin startup doesn't permanently break
the client.

## Models

POCOs in `Models/` — projections of NetStone's parsed types into JSON-friendly
shapes for our cache and IPCs.

### `CharacterSummary`

```csharp
public sealed class CharacterSummary
{
    public ulong LodestoneId { get; set; }
    public string? Bio { get; set; }
    public string? Nameday { get; set; }
    public string? GuardianDeityName { get; set; }
    public string? GuardianDeityIconUrl { get; set; }
    public string? StartingCityName { get; set; }
    public string? StartingCityIconUrl { get; set; }
    public string? AvatarUrl { get; set; }
    public string? PortraitUrl { get; set; }
    public string? FreeCompanyLodestoneId { get; set; }
}
```

Mapped via direct property access on `LodestoneCharacter`, except for
`FreeCompany.Id` which is fetched via reflection (`TryReadFreeCompanyId`).
NetStone restructured the FC property across versions; reflection insulates
us from that.

### `CharacterSearchEntry`, `CharacterSearchResult`

```csharp
public sealed class CharacterSearchEntry
{
    public ulong LodestoneId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Server { get; set; }
}

public sealed class CharacterSearchResult
{
    public IReadOnlyList<CharacterSearchEntry> Results { get; set; } = Array.Empty<CharacterSearchEntry>();
}
```

`Server` is also read via reflection (`TryReadString(r, "Server") ?? TryReadString(r, "World")`)
to absorb NetStone's naming inconsistency between versions.

## Settings

POCO: `LodestoneSettings : IModuleSettings`. Store key:
`nexuskit.modules.lodestone.settings`.

| Property | Default | Notes |
|---|---|---|
| `ModuleEnabled` | `true` | When false, both methods return null. Rendered only in the "Modules" overview tab (the property is `.Hidden()` in detail tabs). |
| `CacheEnabled` | `true` | Disable to always hit the live Lodestone. |
| `CacheTtlHours` | `24` | UI slider range: 1–168. |

Schema group: `nexuskit.module.group` (order 200, after FFXIVCollect's 100).
Title key: `nexuskit.modules.lodestone.title`.

There is no `BaseUrl` setting — NetStone owns Lodestone URL routing
internally per supported region.

## Cache table (`nexus_lodestone_cache`)

```sql
CREATE TABLE nexus_lodestone_cache (
    key         TEXT PRIMARY KEY,
    response    TEXT NOT NULL,
    fetched_at  TEXT NOT NULL,
    expires_at  TEXT NOT NULL
);
```

Key formats:
- `lodestone:char:<lodestoneId>`
- `lodestone:search:<name>|<world>` (both lowercased before hashing into the key)

`response` holds our own DTO serialised as JSON (not NetStone's parsed
types — those don't round-trip).

## Published IPCs

| Full name | Signature | Returns |
|---|---|---|
| `<Plugin>.Lodestone.GetCharacterJson` | `Func<ulong, Task<string?>>` | JSON of `CharacterSummary` |
| `<Plugin>.Lodestone.SearchCharacterJson` | `Func<string, string, Task<string?>>` | JSON of `CharacterSearchResult` |

Both honour the `ModuleEnabled` / `CacheEnabled` matrix above.

## NetStone version notes

We currently depend on **NetStone 1.4.1**. Two known shape drifts we
work around:

| Symbol | NetStone version drift | Our handling |
|---|---|---|
| `LodestoneCharacter.FreeCompany.Id` | Property name / nesting changed across releases | `TryReadFreeCompanyId(...)` uses reflection |
| Search entry's world property | Sometimes `Server`, sometimes `World` | `TryReadString(r, "Server") ?? TryReadString(r, "World")` |

When upgrading NetStone:
1. Build and watch for compile errors against `LodestoneCharacter` direct
   property access (we currently use `Bio`, `Nameday`, `GuardianDeityName`,
   `GuardianDeityIcon`, `TownName`, `TownIcon`, `Avatar`, `Portrait`).
2. Run the search test path end-to-end if you can — reflection failures
   silently turn `Server` into null.

## Localization keys

| Key | Used for |
|---|---|
| `nexuskit.modules.lodestone.title` | Settings tab label |
| `nexuskit.modules.lodestone.cache_enabled.label` | Cache toggle label |
| `nexuskit.modules.lodestone.cache_enabled.description` | Tooltip |
| `nexuskit.modules.lodestone.cache_ttl.label` | TTL slider label |
| `nexuskit.modules.lodestone.cache_ttl.description` | Tooltip |

The `ModuleEnabled` toggle uses the framework's `nexuskit.module.enabled.*` keys.

---

**Maintenance**: when you bump NetStone, change a DTO field, alter cache
keys, or add/remove an IPC, update this file.
