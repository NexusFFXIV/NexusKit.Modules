using NexusKit.Modules.ExternalData.Models;

namespace NexusKit.Modules.ExternalData.Catalogs;

/// <summary>
/// No live source ships item data yet. Always returns null / empty without throwing so
/// the plugin can already depend on the surface; when a future source is added, this
/// implementation gets replaced rather than the API contract.
/// </summary>
internal sealed class ExternalDataItemCatalog : IExternalDataItemCatalog
{
    public Task<ItemEntry?> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult<ItemEntry?>(null);

    public Task<IReadOnlyList<ItemEntry>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ItemEntry>>(Array.Empty<ItemEntry>());
}
