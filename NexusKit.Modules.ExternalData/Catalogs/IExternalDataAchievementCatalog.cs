using NexusKit.Modules.ExternalData.Models;

namespace NexusKit.Modules.ExternalData.Catalogs;

public interface IExternalDataAchievementCatalog
{
    Task<AchievementEntry?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<AchievementEntry>> ListAsync(CancellationToken ct = default);
}
