using NexusKit.Modules.ExternalData.Mapping;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.FfxivCollect.Clients;

namespace NexusKit.Modules.ExternalData.Catalogs;

internal sealed class ExternalDataAchievementCatalog : IExternalDataAchievementCatalog
{
    private readonly IFfxivCollectClient mFfxivCollect;

    public ExternalDataAchievementCatalog(IFfxivCollectClient ffxivCollect)
    {
        mFfxivCollect = ffxivCollect;
    }

    public async Task<AchievementEntry?> GetAsync(int id, CancellationToken ct = default)
    {
        var src = await mFfxivCollect.GetAchievementAsync(id, ct).ConfigureAwait(false);
        return src?.ToModel();
    }

    public async Task<IReadOnlyList<AchievementEntry>> ListAsync(CancellationToken ct = default)
    {
        var src = await mFfxivCollect.GetAchievementCatalogAsync(ct).ConfigureAwait(false);
        if (src?.Results is not { Count: > 0 } results) return Array.Empty<AchievementEntry>();
        return results.Select(r => r.ToModel()).ToList();
    }
}
