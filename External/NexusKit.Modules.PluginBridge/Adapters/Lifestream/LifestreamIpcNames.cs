namespace NexusKit.Modules.PluginBridge.Adapters.Lifestream;

/// <summary>
/// Canonical IPC names published by NightmareXIV/Lifestream. Mirrored as
/// constants so the adapter is the single source of truth and the strings
/// don't get sprinkled through the codebase. Verified against
/// <c>NightmareXIV/Lifestream/Lifestream/IPC/IPCProvider.cs</c>.
/// </summary>
internal static class LifestreamIpcNames
{
    public const string PluginInternalName = "Lifestream";

    /// <summary>
    /// <c>Action&lt;string&gt;</c> — processes a <c>/li ...</c> command verbatim.
    /// Universal entry-point we rely on for V1 (no shared types needed).
    /// </summary>
    public const string ExecuteCommand = "Lifestream.ExecuteCommand";
}
