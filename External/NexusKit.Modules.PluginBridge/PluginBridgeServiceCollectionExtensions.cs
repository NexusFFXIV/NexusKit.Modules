using Microsoft.Extensions.DependencyInjection;
using NexusKit.Core;
using NexusKit.Core.Localization;
using NexusKit.Modules.PluginBridge.Adapters.Lifestream;
using NexusKit.Modules.PluginBridge.Resources;

namespace NexusKit.Modules.PluginBridge;

public static class PluginBridgeServiceCollectionExtensions
{
    public static IServiceCollection AddNexusKitPluginBridge(this IServiceCollection services)
    {
        // Register the concrete registry once, then forward both the public
        // contract and the IPluginBackgroundService marker to the same
        // instance. The host eager-resolves IPluginBackgroundService during
        // BuildAsync, which is what kicks off the self-check loop in the
        // registry's ctor — so the loop is alive from plugin load without
        // any plugin-side wiring.
        services.AddSingleton<PluginBridgeRegistry>();
        services.AddSingleton<IPluginBridgeRegistry>(sp => sp.GetRequiredService<PluginBridgeRegistry>());
        services.AddSingleton<IPluginBackgroundService>(sp => sp.GetRequiredService<PluginBridgeRegistry>());

        // LifestreamAdapter implements both contracts; register once and expose
        // through two service-type entries so the registry sees an
        // IExternalPluginAdapter and consumer UI code can inject ILifestreamAdapter
        // directly.
        services.AddSingleton<LifestreamAdapter>();
        services.AddSingleton<IExternalPluginAdapter>(sp => sp.GetRequiredService<LifestreamAdapter>());
        services.AddSingleton<ILifestreamAdapter>(sp => sp.GetRequiredService<LifestreamAdapter>());

        // Module-shipped translations. Plugin can still override individual
        // keys with a higher-priority ILocalizationSource.
        services.AddResourceLocalizer<Strings>();

        return services;
    }
}
