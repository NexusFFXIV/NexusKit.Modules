using NexusKit.Core;
using NexusKit.Core.Actions;

namespace NexusKit.Modules.PluginBridge.Adapters.Lifestream;

/// <summary>
/// Normalized API for Lifestream-driven navigation. All methods are failsoft:
/// they return <c>false</c> (and log) when Lifestream is unavailable or the
/// IPC call throws, never propagating exceptions to the caller.
/// </summary>
public interface ILifestreamAdapter
{
    /// <summary>True when Lifestream is loaded and the required IPCs are bindable.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Execute an arbitrary <c>/li ...</c> command. <paramref name="commandArguments"/>
    /// is the part AFTER <c>/li</c> (e.g. <c>"Excalibur"</c> for a world transfer,
    /// <c>"fc"</c> for own-FC teleport).
    /// </summary>
    bool TryExecuteCommand(string commandArguments);

    /// <summary>
    /// Navigate to the housing plot described by <paramref name="address"/>.
    /// Resolves <see cref="IHousingAddress.DistrictTerritoryId"/> to the
    /// English <c>TerritoryType.PlaceName</c> via the GameData resolver and
    /// dispatches a Lifestream <c>/li</c> command whose shape depends on
    /// <paramref name="currentLocation"/>:
    /// <list type="bullet">
    /// <item><c>"&lt;district&gt; &lt;ward&gt; &lt;plot&gt;"</c> when the
    /// player is already on the target world (e.g. <c>"Mist 17 15"</c>).</item>
    /// <item><c>"&lt;targetWorld&gt; &lt;district&gt; &lt;ward&gt; &lt;plot&gt;"</c>
    /// when the target is on a different world (e.g.
    /// <c>"Shiva Mist 17 15"</c>). Lifestream's own configuration decides
    /// whether the world-visit can cross datacenter boundaries.</item>
    /// </list>
    /// Returns <c>false</c> when Lifestream isn't available, any of
    /// district/ward/plot is missing, or the territory id isn't one of the
    /// five known residential districts. When <paramref name="currentLocation"/>
    /// is <c>null</c> or carries no world id the adapter falls back to the
    /// same-world (no world prefix) form.
    /// </summary>
    bool TryGotoFreeCompanyHouse(PlayerLocation? currentLocation, IHousingAddress address);

    /// <summary>
    /// Optional UI preview for <see cref="TryGotoFreeCompanyHouse"/>: returns a
    /// hint describing which travel variant the call would fire (same-world
    /// teleport / world-visit / cross-DC) without actually executing it. The UI
    /// uses the hint to tint the button and pick a variant-specific tooltip so
    /// the user can see at a glance what's about to happen.
    /// <para>Returns <c>null</c> when <see cref="TryGotoFreeCompanyHouse"/>
    /// would itself short-circuit (Lifestream unavailable, missing district /
    /// ward / plot, non-residential territory). UI callers can use a
    /// non-<c>null</c> return as their "show the button at all" predicate
    /// without duplicating the address-validity check.</para>
    /// <para>This method is the opt-in part of the
    /// <see cref="ActionRenderHint"/> pattern — adapters that don't need
    /// UI-side differentiation simply don't expose a <c>Preview…</c>
    /// counterpart and the UI falls back to its default rendering.</para>
    /// </summary>
    ActionRenderHint? PreviewGotoFreeCompanyHouse(PlayerLocation? currentLocation, IHousingAddress address);
}
