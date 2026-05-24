using NexusKit.Modules.ExternalData.Models;

namespace NexusKit.Modules.ExternalData.Catalogs;

public interface IExternalDataMinionCatalog
{
    Task<MinionEntry?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<MinionEntry>> ListAsync(CancellationToken ct = default);
}
