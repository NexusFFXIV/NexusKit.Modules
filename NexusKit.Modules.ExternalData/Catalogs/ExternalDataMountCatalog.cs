using NexusKit.Modules.ExternalData.Mapping;
using NexusKit.Modules.ExternalData.Models;
using NexusKit.Modules.FfxivCollect.Clients;

namespace NexusKit.Modules.ExternalData.Catalogs;

internal sealed class ExternalDataMountCatalog : IExternalDataMountCatalog
{
    private readonly IFfxivCollectClient mFfxivCollect;

    public ExternalDataMountCatalog(IFfxivCollectClient ffxivCollect)
    {
        mFfxivCollect = ffxivCollect;
    }

    public async Task<MountEntry?> GetAsync(int id, CancellationToken ct = default)
    {
        var src = await mFfxivCollect.GetMountAsync(id, ct).ConfigureAwait(false);
        return src?.ToModel();
    }

    public async Task<IReadOnlyList<MountEntry>> ListAsync(CancellationToken ct = default)
    {
        var src = await mFfxivCollect.GetMountCatalogAsync(ct).ConfigureAwait(false);
        if (src?.Results is not { Count: > 0 } results) return Array.Empty<MountEntry>();
        return results.Select(r => r.ToModel()).ToList();
    }
}
