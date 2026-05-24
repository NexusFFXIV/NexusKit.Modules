using Microsoft.Extensions.DependencyInjection;
using NexusKit.Modules.ExternalData.Catalogs;
using NexusKit.Modules.ExternalData.FreeCompanies;
using NexusKit.Modules.ExternalData.Maintenance;
using NexusKit.Modules.ExternalData.Persistence;
using NexusKit.Modules.ExternalData.Players;
using NexusKit.Modules.FfxivCollect;
using NexusKit.Modules.Lodestone;
using NexusKit.Persistence;
using NexusKit.Persistence.Maintenance;

namespace NexusKit.Modules.ExternalData;

public static class ExternalDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the external-data aggregator plus every shipped ExternalDataSource module.
    /// The plugin depends only on this assembly; individual source modules stay encapsulated here.
    /// </summary>
    public static IServiceCollection AddNexusKitExternalData(this IServiceCollection services)
    {
        services.AddNexusKitFfxivCollect();
        services.AddNexusKitLodestone();

        // EstateAddressEntityModule MUST register before ExternalDataEntityModule:
        // FreeCompanyEntity's HasOne<EstateAddressEntity>().WithMany().HasForeignKey(...)
        // implicitly adds EstateAddressEntity to the model. PluginDbContext's
        // ApplyTablePrefix only prefixes entities the *current* module is the
        // first to register. If ExternalData runs first, the FK side-effect
        // pulls EstateAddress into the model with a non-prefixed table name;
        // the later EstateAddressEntityModule then sets ToTable("address") but
        // the prefix-application step skips it (already-configured). EF Core
        // ends up querying FROM "address" instead of nexus_estate_address.
        services.AddEntityModule<EstateAddressEntityModule>();
        services.AddEntityModule<ExternalDataEntityModule>();
        services.AddMigrationModule<ExternalDataMigrations>();

        services.AddSingleton<IExternalDataPlayerService, ExternalDataPlayerService>();
        services.AddSingleton<IExternalDataMountCatalog, ExternalDataMountCatalog>();
        services.AddSingleton<IExternalDataMinionCatalog, ExternalDataMinionCatalog>();
        services.AddSingleton<IExternalDataAchievementCatalog, ExternalDataAchievementCatalog>();
        services.AddSingleton<IExternalDataItemCatalog, ExternalDataItemCatalog>();
        services.AddSingleton<IExternalDataFreeCompanyService, ExternalDataFreeCompanyService>();

        services.AddSingleton<IExternalDataService, ExternalDataService>();

        services.AddMaintenanceContributor<OrphanedFreeCompanyPruneContributor>();

        return services;
    }
}
