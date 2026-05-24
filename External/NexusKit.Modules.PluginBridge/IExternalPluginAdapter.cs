namespace NexusKit.Modules.PluginBridge;

/// <summary>
/// Contract for an adapter that wraps one foreign Dalamud plugin's IPC surface
/// behind a normalized service. Adapters probe the plugin at startup and on
/// demand; consumers should query <see cref="GetStatus"/> before acting and
/// rely on the adapter's own failsoft methods (return <c>bool</c>, never throw
/// for "plugin not there").
/// </summary>
public interface IExternalPluginAdapter
{
    /// <summary>Stable identifier (e.g. "Lifestream"). Used as the dictionary key in the registry.</summary>
    string AdapterKey { get; }

    /// <summary>Dalamud plugin internal name to probe via <c>InstalledPlugins</c>.</summary>
    string PluginInternalName { get; }

    /// <summary>Localization key resolved by the UI layer (e.g. "nexuskit.bridges.lifestream.display_name").</summary>
    string DisplayNameKey { get; }

    /// <summary>Localization key for a short description of what this bridge enables.</summary>
    string DescriptionKey { get; }

    /// <summary>Returns the adapter's current status. Implementations are
    /// free to re-probe on each call when the underlying check is cheap
    /// (the bundled adapters do, since the Dalamud plugin-list scan is
    /// in-memory). The periodic self-check loop in
    /// <see cref="IPluginBridgeRegistry"/> still calls <see cref="Refresh"/>
    /// to keep any cached state warm for direct-field reads.</summary>
    ExternalPluginAdapterStatus GetStatus();

    /// <summary>Re-probe the foreign plugin. Idempotent. Cheap to call.</summary>
    void Refresh();
}
