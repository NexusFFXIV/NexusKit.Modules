# Plugin-Bridge — design notes

How and why we wrap foreign Dalamud plugins.

## The constraint that shaped the design

**Dalamud cannot enumerate the IPCs a foreign plugin has registered.**
`IDalamudPluginInterface.InstalledPlugins` only exposes metadata
(`InternalName`, `Name`, `Version`, `IsLoaded`). There is no
`GetRegisteredIpcs(string pluginInternalName)`.

A consumer can call `GetIpcSubscriber<…>(fullName)` for any name —
Dalamud returns a subscriber object every time, even if nobody
registered the IPC. The failure surfaces only when you actually call
`InvokeFunc(...)`: it throws `IpcNotReadyError` (subscriber wasn't
registered) or `IpcError` (signature mismatch).

That means there are exactly two ways to "discover" whether you can use
a foreign plugin's IPC:

1. **Curated list.** Hardcode the IPC names the adapter needs. Trust
   that the foreign plugin's IPC surface is stable enough to track in
   source.
2. **Probe by calling.** Send a benign call. Doesn't work for side-effect
   IPCs (like `Lifestream.ExecuteCommand`).

We use (1). Adapter constants live in `<Plugin>IpcNames.cs` next to the
adapter; the adapter is the single source of truth for "what IPCs do we
need from this plugin".

## Comparison: Questionable

Questionable's [`Questionable/External/LifestreamIpc.cs`](https://github.com/WigglyMuffin/Questionable)
does the same thing minus a few safeguards:

```csharp
// LifestreamIpc.cs — direct bind, no availability check.
_aethernetTeleportByPlaceNameId = pluginInterface
    .GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportByPlaceNameId");
```

The "is Lifestream installed?" check lives in a separate file
(`Questionable/Windows/ConfigComponents/PluginConfigComponent.cs`) with
its own hardcoded list of expected plugins. Two sources of truth that
have to stay in sync by convention. The actual IPC calls just try-invoke
and hope.

PluginBridge folds both into one adapter:

```csharp
internal sealed class LifestreamAdapter : IExternalPluginAdapter, ILifestreamAdapter
{
    public string PluginInternalName => LifestreamIpcNames.PluginInternalName;

    public bool IsAvailable => mStatus.PluginLoaded && mStatus.AllRequiredIpcsAvailable;

    public bool TryExecuteCommand(string args)
    {
        if (!IsAvailable) return false;
        return mExecuteCommand.TryInvoke(args);
    }
}
```

The same `LifestreamAdapter` instance produces the Settings-UI status
row and serves the call site — they cannot diverge.

## Adapter probe semantics

`Refresh()` re-reads `IDalamudPluginProbe.GetInfo(pluginInternalName)`.
The resulting status:

- `PluginInstalled` — anything in `InstalledPlugins` with the matching
  `InternalName`.
- `PluginLoaded` — that plugin's `IsLoaded == true` (user can disable
  plugins without uninstalling).
- `AllRequiredIpcsAvailable` — V1 treats "plugin loaded" as evidence.
  See the constraint above. If a future Dalamud version exposes a
  per-IPC probe, this becomes a real check.
- `MissingIpcs` — populated only when `PluginLoaded == false`; lists
  the IPC names the adapter would need.

`Refresh` is cheap (one `InstalledPlugins` scan) and idempotent. The
Settings UI exposes a "Re-check" button per adapter and a "Re-check all"
button — the latter calls `IPluginBridgeRegistry.RefreshAll()`.

`GetStatus()` itself re-probes on every call in the bundled adapters —
the same scan that backs `Refresh` runs inline, so any caller (UI render,
`IsAvailable` getter, future hot-path) sees the current state of the
foreign plugin without waiting for the next background tick. The cached
`mStatus` field is still maintained so the self-check loop has something
to update and direct-field reads see a recent snapshot.

### Background self-check

`PluginBridgeRegistry` runs a background loop (started in its ctor,
bound to `IPluginLifetime.Stopping`) that calls `RefreshAll()` every
30 seconds. ImGui windows re-render every frame and read
`adapter.GetStatus()` / `IsAvailable` directly, so the refreshed status
propagates to all UI consumers on the next render with no event plumbing.
Per-adapter exceptions inside `RefreshAll` are isolated so a single bad
adapter cannot stall the loop.

Activation is via the `IPluginBackgroundService` marker (defined in
`NexusKit.Core`): `AddNexusKitPluginBridge` registers
`PluginBridgeRegistry` as both `IPluginBridgeRegistry` and
`IPluginBackgroundService`. `PluginHostBuilder.BuildAsync` iterates every
registered `IPluginBackgroundService` after the lifecycle is up, which
triggers the ctor — and the self-check loop with it — without the plugin
having to know the registry exists.

## Failure modes the adapter must handle

| Situation | How the adapter behaves |
|---|---|
| Plugin not installed | `IsAvailable = false`. All `Try…` methods return `false` without calling. |
| Plugin installed but disabled | Same as not installed — `IsLoaded == false`. |
| Plugin loaded but a specific IPC isn't registered | `TryInvoke` catches the `IpcNotReadyError` and returns `false`. Logged at `Warning`. The adapter additionally stamps a 60 s "recent-failure" grace window so subsequent `GetStatus` / `IsAvailable` reads report unavailable until Dalamud catches up and reports `IsLoaded == false` — closes the mid-unload race where Dalamud's plugin-list lags reality. |
| Foreign plugin's IPC signature changed | `TryInvoke` catches and returns `false`. Same grace-window downgrade as above; surface this through the version display + Re-check in the Settings UI for the persistent case. |

## Action render hints (opt-in)

An adapter method can optionally expose a sibling `Preview<X>(…)` method
that takes the same arguments as its `Try<X>(…)` counterpart and returns
a `NexusKit.Core.Actions.ActionRenderHint?`. The hint carries:

- a **variant id** (opaque to the UI — `"lifestream.fc_house.cross_dc"`,
  etc.) for telemetry / tests,
- an optional **tooltip key** (resolved against the layered `ILocalizer`)
  so each variant can describe what's about to happen — and when the
  hint is disabled, why it can't run right now,
- an optional **accent color** (`Vector4`) the UI pushes onto
  `ImGuiCol.Text` so the icon glyph picks up the variant color while
  the button frame stays consistent with neighbouring buttons,
- a `CanExecute` flag (default `true`) that decides whether the slot
  is enabled or greyed out,
- an optional raw **detail text** the UI appends to the tooltip on a
  second line. Use this for dynamic, non-localizable strings — for
  Lifestream the detail is the literal `/li …` command the adapter
  would dispatch, computed via the same builder `TryGoto` uses, so the
  tooltip shows the user exactly what's about to be sent.

### Three rendering states

The hint encodes a small state machine that lets adapters distinguish
"this action doesn't belong here" from "this action belongs here but
can't run right now":

| Preview returns | Meaning | UI behaviour |
|---|---|---|
| `null` | The adapter has no opinion / the action can never run from this context (foreign plugin unloaded, address fundamentally invalid, …). | **Hide the slot entirely.** No header button rendered. |
| Hint with `CanExecute = true` | The action will run on click. | Render tinted + variant tooltip. |
| Hint with `CanExecute = false` | The slot belongs here but the action can't fire right now (transient busy state, address known but partially missing, foreign-plugin sub-feature disabled, …). | Render tinted **and disabled** (`BeginDisabled` around the button); the tooltip text typically explains why. |

The `null` case is exactly the old `canTravel` predicate the plugin's
`FreeCompanyTab` used to compute by hand (`IsAvailable && district !=
null && ward != null && plot != null`). Folding that into the
adapter's preview keeps the visibility decision in one place and lets
adapters introduce a "disabled with explanation" state without UI
churn.

UI callers query the preview before rendering the button: a non-null
hint is the "show the button" predicate (no input re-validation), the
tooltip key picks the variant-specific tooltip, the accent color tints
the background, and the `CanExecute` flag decides enabled vs disabled.
`NexusIconButton.Draw` has an overload that accepts the hint directly
and handles PushStyleColor / PopStyleColor + BeginDisabled bookkeeping.

This is purely opt-in — adapters that don't need UI differentiation
simply omit the `Preview…` method. The `Try<X>` side is unaffected;
both methods share a private "decide variant" helper inside the
adapter so they cannot drift.

### Example: Lifestream housing travel

`LifestreamAdapter` exposes `PreviewGotoFreeCompanyHouse` alongside
`TryGotoFreeCompanyHouse`. The preview classifies the call into one of
three variants:

| Variant | Command shape | Tooltip key | Tint |
|---|---|---|---|
| `lifestream.fc_house.same_world` | `<district> <ward> <plot>` | `.travel.tooltip.same_world` | none (default button) |
| `lifestream.fc_house.world_visit` | `<world> <district> <ward> <plot>` | `.travel.tooltip.world_visit` | cyan |
| `lifestream.fc_house.cross_dc` | `<dataCenterName>` (DC hop only) | `.travel.tooltip.cross_dc` | amber |

`DetailText` on each hint is the literal command prefixed with `/li `
(e.g. `"/li Shiva Mist 17 15"`); the UI shows it as the tooltip's
second line so the user can see the exact dispatch before clicking.

The cross-DC variant intentionally signals more effort: the user is
committing to a multi-step journey and needs to click again after the
DC hop to land on the world + housing.

## V1 limitations to revisit later

- **30 s self-check granularity.** Adapter status is re-probed every
  30 seconds; mid-tick enable/disable of a foreign plugin only reflects
  on the next sweep. Subscribing to a Dalamud "InstalledPlugins changed"
  event (if/when one exists) would make this push-driven instead of
  polled.
- **`AddressBookEntryTuple` / `GoToHousingAddress`.** Lifestream's
  structured housing API uses a `ValueTuple` alias whose shape would
  need to be mirrored on the consumer side. V1 ducks this by routing
  everything through `ExecuteCommand(string)` and trusting Lifestream's
  `/li` parser. If the parser proves too fragile for typical Lodestone-
  scraped address strings, V2 mirrors the tuple and binds the structured
  IPC.
- **Adapter-specific tests.** V1 has no mocked-DalamudIpcRegistry tests —
  reaches and overall surface are small enough to verify manually.

## Style

- Adapter class is `internal sealed`. The public surface is the
  normalized interface (`I<PluginName>Adapter`).
- Constructor injects `IDalamudPluginProbe`, `IIpcRegistry`,
  `ILogger<T>`. No `IDalamudPluginInterface` direct reference — keeps the
  module Dalamud-free.
- Private fields use the `m` prefix per repo conventions.
- Localization keys: `nexuskit.bridges.<key>.display_name`,
  `nexuskit.bridges.<key>.description`. Defined in the module's
  `Resources/Strings.resx`; plugin keys override module keys via the
  layered localizer.

---

**Maintenance**: when you change adapter probe semantics, change how
the registry exposes status, or revisit the V1 limitations above,
update this file in the same commit.
