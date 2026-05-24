using NexusKit.Modules.ExternalData.Models;

namespace NexusKit.Modules.ExternalData.Catalogs;

public interface IExternalDataMountCatalog
{
    /// <summary>Returns the mount catalog entry for <paramref name="id"/>, or null if no source can serve it.</summary>
    Task<MountEntry?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>Returns the full mount catalog. Empty list when no source is available.</summary>
    Task<IReadOnlyList<MountEntry>> ListAsync(CancellationToken ct = default);
}
