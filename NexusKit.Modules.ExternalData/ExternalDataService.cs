using NexusKit.Modules.ExternalData.Catalogs;
using NexusKit.Modules.ExternalData.FreeCompanies;
using NexusKit.Modules.ExternalData.Players;

namespace NexusKit.Modules.ExternalData;

internal sealed class ExternalDataService : IExternalDataService
{
    public ExternalDataService(
        IExternalDataPlayerService players,
        IExternalDataMountCatalog mounts,
        IExternalDataMinionCatalog minions,
        IExternalDataAchievementCatalog achievements,
        IExternalDataItemCatalog items,
        IExternalDataFreeCompanyService freeCompanies)
    {
        Players = players;
        Mounts = mounts;
        Minions = minions;
        Achievements = achievements;
        Items = items;
        FreeCompanies = freeCompanies;
    }

    public IExternalDataPlayerService        Players       { get; }
    public IExternalDataMountCatalog         Mounts        { get; }
    public IExternalDataMinionCatalog        Minions       { get; }
    public IExternalDataAchievementCatalog   Achievements  { get; }
    public IExternalDataItemCatalog          Items         { get; }
    public IExternalDataFreeCompanyService   FreeCompanies { get; }
}
