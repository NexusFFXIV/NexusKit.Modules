using NexusKit.Modules.ExternalData.Models;

namespace NexusKit.Modules.ExternalData.Catalogs;

/// <summary>
/// Placeholder until an item/gear data source is wired up. Implementations return
/// null / empty without throwing so the plugin can already depend on the surface.
/// </summary>
public interface IExternalDataItemCatalog
{
    Task<ItemEntry?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<ItemEntry>> ListAsync(CancellationToken ct = default);
}
