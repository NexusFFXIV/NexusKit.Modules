using NexusKit.Core.Modules;

namespace NexusKit.Modules.Lodestone;

public sealed class LodestoneSettings : IModuleSettings
{
    public const string StoreKey = "nexuskit.modules.lodestone.settings";

    public bool ModuleEnabled { get; set; } = true;

    public ModuleKind? Kind => ModuleKind.ExternalDataSource;

    public bool CacheEnabled { get; set; } = true;

    /// <summary>
    /// Lodestone refreshes slowly (typically hours between updates), so a long
    /// cache lifetime is comfortable. Defaults to one day.
    /// </summary>
    public int CacheTtlHours { get; set; } = 24;
}