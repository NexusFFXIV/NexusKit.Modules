using FfxivCollectModels = NexusKit.Modules.FfxivCollect.Models;
using NexusKit.Modules.ExternalData.Models;

namespace NexusKit.Modules.ExternalData.Mapping;

internal static class CatalogMappings
{
    public static MountEntry ToModel(this FfxivCollectModels.Mount src)
        => new(src.Id, src.Name ?? string.Empty, src.Description, src.Icon, src.Patch, src.Seats);

    public static MinionEntry ToModel(this FfxivCollectModels.Minion src)
        => new(src.Id, src.Name ?? string.Empty, src.Description, src.Icon, src.Patch);

    public static AchievementEntry ToModel(this FfxivCollectModels.Achievement src)
        => new(src.Id, src.Name ?? string.Empty, src.Description, src.Icon, src.Patch, src.Points);
}
