using NexusKit.Modules.ExternalData.Catalogs;
using NexusKit.Modules.ExternalData.FreeCompanies;
using NexusKit.Modules.ExternalData.Players;

namespace NexusKit.Modules.ExternalData;

/// <summary>
/// Umbrella entry point. The plugin can depend on this for convenience or inject the
/// specialized sub-services directly — both are registered in DI.
/// </summary>
public interface IExternalDataService
{
    IExternalDataPlayerService        Players       { get; }
    IExternalDataMountCatalog         Mounts        { get; }
    IExternalDataMinionCatalog        Minions       { get; }
    IExternalDataAchievementCatalog   Achievements  { get; }
    IExternalDataItemCatalog          Items         { get; }
    IExternalDataFreeCompanyService   FreeCompanies { get; }
}
