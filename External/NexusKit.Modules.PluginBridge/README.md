# NexusKit.Modules.PluginBridge

Reusable NexusKit module that integrates foreign Dalamud plugins through
their published IPCs and exposes them behind normalized, failsoft adapter
interfaces. Consumers (plugin UI code, other modules) inject a specific
adapter (`ILifestreamAdapter`, …) and call methods that return `bool` —
they never have to know whether the underlying plugin is installed or
which IPC name backs the call.

**Dalamud-free.** The Dalamud-side plumbing (`InstalledPlugins` probe,
`ICallGate` subscribers) is consumed via `IDalamudPluginProbe` and
`IIpcRegistry` from `NexusKit.Core` / `NexusKit.Ipc`.

## Why a bridge module?

Most cross-plugin IPC consumers today (e.g. `Questionable/External/LifestreamIpc.cs`)
hardcode IPC names directly at the call site and bind unconditionally —
which throws at runtime when the foreign plugin isn't installed. The
"is this plugin available?" check (Questionable's Dependencies tab) lives
in a separate file with its own hardcoded plugin list, easy to get
out-of-sync.

This module collapses both into a single adapter per foreign plugin:
- The adapter owns the foreign plugin's `InternalName` and the canonical
  IPC name constants.
- The adapter probes at startup, on demand, and on a periodic background
  self-check (every 30 s) so the cached status keeps up with the user
  enabling / disabling foreign plugins mid-session.
- The adapter exposes a normalized API (`Try…` methods) that returns
  `false` instead of throwing when the plugin isn't reachable.
- The Settings UI iterates `IPluginBridgeRegistry.All()` to render one
  status row per adapter — same source of truth as the call sites.

## Public API

| Type | File | Purpose |
|---|---|---|
| `IExternalPluginAdapter` | `IExternalPluginAdapter.cs` | Contract every adapter implements. Exposes `AdapterKey`, `PluginInternalName`, `DisplayNameKey`, `DescriptionKey`, `GetStatus()`, `Refresh()`. |
| `ExternalPluginAdapterStatus` | `ExternalPluginAdapterStatus.cs` | Status snapshot record: `PluginInstalled`, `PluginLoaded`, `PluginVersion`, `AllRequiredIpcsAvailable`, `MissingIpcs`. |
| `IPluginBridgeRegistry` | `IPluginBridgeRegistry.cs` | Aggregator over all registered adapters. The Settings UI iterates this. |
| `ILifestreamAdapter` | `Adapters/Lifestream/ILifestreamAdapter.cs` | Normalized Lifestream API: `IsAvailable`, `TryExecuteCommand(string)`, `TryGotoFreeCompanyHouse(...)`, and `PreviewGotoFreeCompanyHouse(...)` returning an optional `ActionRenderHint` for UI tinting / variant-aware tooltips. |

## Registration

```csharp
services.AddNexusKitPluginBridge();
```

Registers: `PluginBridgeRegistry`, the `LifestreamAdapter` (under both
`IExternalPluginAdapter` and `ILifestreamAdapter`), and the module's own
`ILocalizationSource` (EN + DE).

Depends on `IDalamudPluginProbe` and `IIpcRegistry` from `NexusKit.Ipc` —
add `services.AddNexusKitIpc()` if not already present.

## Bundled adapters

### Lifestream (`AdapterKey = "Lifestream"`, plugin internal name `Lifestream`)

V1 binds only to `Lifestream.ExecuteCommand` (`Action<string>`) — the
universal `/li …` entry point. Everything else (housing navigation, FC
teleport, address-book lookups) is built on top of this single IPC by
constructing the right command string, which keeps the adapter free of
cross-plugin type sharing (Lifestream's `AddressBookEntryTuple` isn't
needed in V1).

| Method | What it does |
|---|---|
| `IsAvailable` | `true` when Lifestream is loaded AND the required IPCs are bindable. |
| `TryExecuteCommand(args)` | Runs `/li <args>`. Returns `false` if the IPC throws or Lifestream isn't available. |
| `TryGotoFreeCompanyHouse(world, city, ward, plot, isSubdivision)` | Best-effort: constructs a `/li`-compatible command string and delegates to `TryExecuteCommand`. Lifestream's free-form housing parser may need a saved address-book entry name — if structured args don't resolve, fall back to passing a known entry name via `TryExecuteCommand`. |
| `PreviewGotoFreeCompanyHouse(currentLocation, address)` | Same arguments as `TryGotoFreeCompanyHouse`; returns an `ActionRenderHint?` describing which of the three travel variants would fire (same-world / world-visit / cross-DC) with a variant id, localized tooltip key, and optional accent color. `null` when travel isn't possible — the UI uses that as its "hide the button" predicate without re-validating address fields. See [`docs/plugin-bridge.md`](docs/plugin-bridge.md#action-render-hints-opt-in). |

## Adding a new adapter

1. Pick a `PluginInternalName` (matches `IDalamudPluginInterface.InstalledPlugins`).
2. Add an `Adapters/<PluginName>/` folder with the adapter class + a
   normalized interface (`I<PluginName>Adapter`).
3. Hardcode the IPC names you need as `internal static class <PluginName>IpcNames`.
4. In the ctor, call `mIpc.GetFunc<…>(name)` / `GetAction<…>(name)` to
   obtain proxies; store them in fields.
5. Implement `IExternalPluginAdapter.Probe()`: read
   `IDalamudPluginProbe.GetInfo(<internalName>)` and build a status. Treat
   "plugin loaded" as sufficient evidence for IPC availability — Dalamud
   has no way to side-effect-free probe individual IPCs.
6. In every public method: short-circuit on `!IsAvailable`, otherwise call
   the cached `IIpcFunc<…>.TryInvoke(...)` and return its boolean result
   (or wrap a value).
7. Register in `PluginBridgeServiceCollectionExtensions.AddNexusKitPluginBridge`:
   `AddSingleton<MyAdapter>` + two `AddSingleton<>(sp => sp.GetRequiredService<MyAdapter>())`
   forward registrations for `IExternalPluginAdapter` and `IMyAdapter`.
8. Add localization keys `nexuskit.bridges.<key>.display_name` /
   `description` to `Resources/Strings.resx` and `Strings.de.resx`.

The Settings UI picks the new adapter up automatically — no UI changes
needed.

## Dependencies

- NuGet: `Microsoft.Extensions.Logging.Abstractions` 10.0.7
- ProjectRef: `NexusKit.Core`

## Translations

Module labels live in `Resources/Strings.resx` (EN) and `Strings.de.resx`
(DE). The plugin can override any key by registering a higher-priority
`ILocalizationSource`.

## See also

- [`docs/plugin-bridge.md`](docs/plugin-bridge.md) — design rationale,
  the "why is there no IPC auto-discovery?" answer, comparison to
  Questionable's pattern.

---

**Maintenance**: when you add a new adapter, change the adapter contract,
or add a probed IPC to an existing adapter, update this README and
`docs/plugin-bridge.md` in the same commit.
