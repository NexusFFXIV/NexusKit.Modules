namespace NexusKit.Modules.PluginBridge;

/// <summary>
/// Aggregates every registered <see cref="IExternalPluginAdapter"/>. The
/// Settings UI iterates here; consumer code talks to specific adapter
/// interfaces (e.g. <c>ILifestreamAdapter</c>) directly via DI.
/// </summary>
public interface IPluginBridgeRegistry
{
    IReadOnlyList<IExternalPluginAdapter> All();

    IExternalPluginAdapter? Get(string adapterKey);

    /// <summary>Re-probe every adapter. Used by the "Re-check" button in the settings UI.</summary>
    void RefreshAll();
}
