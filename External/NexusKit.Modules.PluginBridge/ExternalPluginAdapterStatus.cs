namespace NexusKit.Modules.PluginBridge;

/// <summary>
/// Snapshot describing whether a foreign plugin is reachable.
/// <para>
/// <see cref="AllRequiredIpcsAvailable"/> is the adapter's verdict; it is
/// <c>true</c> only when the plugin is loaded AND every IPC the adapter
/// considers mandatory looks bindable. Dalamud cannot prove an IPC will
/// actually invoke without calling it, so adapters that cannot side-effect-free
/// probe a method treat "plugin loaded" as sufficient evidence — a runtime
/// invocation failure (subscriber throws) is reported by the call site, not
/// here.
/// </para>
/// </summary>
public sealed record ExternalPluginAdapterStatus(
    bool PluginInstalled,
    bool PluginLoaded,
    Version? PluginVersion,
    bool AllRequiredIpcsAvailable,
    IReadOnlyList<string> MissingIpcs);
