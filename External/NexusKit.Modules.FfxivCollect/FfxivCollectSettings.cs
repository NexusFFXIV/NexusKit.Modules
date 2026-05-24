using NexusKit.Core.Modules;

namespace NexusKit.Modules.FfxivCollect;

public sealed class FfxivCollectSettings : IModuleSettings
{
    public const string StoreKey = "nexuskit.modules.ffxivcollect.settings";

    public bool ModuleEnabled { get; set; } = true;

    public ModuleKind? Kind => ModuleKind.ExternalDataSource;

    public string BaseUrl { get; set; } = "https://ffxivcollect.com/api";

    public int CacheTtlHours { get; set; } = 24;

    public bool CacheEnabled { get; set; } = true;
}