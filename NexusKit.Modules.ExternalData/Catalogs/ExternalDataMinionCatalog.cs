using NexusKit.Modules.ExternalData.Mapping;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.FfxivCollect.Clients;

namespace NexusKit.Modules.ExternalData.Catalogs;

internal sealed class ExternalDataMinionCatalog : IExternalDataMinionCatalog
{
    private readonly IFfxivCollectClient mFfxivCollect;

    public ExternalDataMinionCatalog(IFfxivCollectClient ffxivCollect)
    {
        mFfxivCollect = ffxivCollect;
    }

    public async Task<MinionEntry?> GetAsync(int id, CancellationToken ct = default)
    {
        var src = await mFfxivCollect.GetMinionAsync(id, ct).ConfigureAwait(false);
        return src?.ToModel();
    }

    public async Task<IReadOnlyList<MinionEntry>> ListAsync(CancellationToken ct = default)
    {
        var src = await mFfxivCollect.GetMinionCatalogAsync(ct).ConfigureAwait(false);
        if (src?.Results is not { Count: > 0 } results) return Array.Empty<MinionEntry>();
        return results.Select(r => r.ToModel()).ToList();
    }
}
