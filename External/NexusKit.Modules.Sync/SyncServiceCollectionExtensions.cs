using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusKit.Sync.Protocol;

namespace NexusKit.Modules.Sync;

/// <summary>
/// DI registration for sync connections.
/// </summary>
public static class SyncServiceCollectionExtensions
{
    /// <summary>Prefix for the named <see cref="HttpClient"/> instances this registers.</summary>
    public const string HttpClientPrefix = "nexussync:";

    /// <summary>
    /// Registers one server connection under a key.
    /// <para>Keyed rather than plain, because talking to several servers is the normal case
    /// once authors start publishing their client bindings as packages — a plugin may speak to
    /// its own server and to somebody else's at the same time. Resolve with
    /// <c>[FromKeyedServices("acme.venuetracker")] ISyncProtocol</c> or
    /// <c>GetRequiredKeyedService&lt;ISyncProtocol&gt;(key)</c>.</para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="key">
    /// Connection key. Using the contract id is the obvious choice and keeps registration and
    /// resolution obviously in step.
    /// </param>
    /// <param name="configure">Configures the connection's address, key and agent.</param>
    public static IServiceCollection AddNexusKitSync(
        this IServiceCollection services,
        string key,
        Action<SyncConnectionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(configure);

        var httpClientName = HttpClientPrefix + key;

        services.AddHttpClient(httpClientName, http =>
        {
            var options = Resolve(key, configure);
            http.BaseAddress = options.ServerUrl;
            http.Timeout = options.Timeout;
        });

        services.AddKeyedSingleton<ISyncProtocol>(key, (provider, _) =>
        {
            var options = Resolve(key, configure);
            var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(httpClientName);
            var log = provider.GetService<ILoggerFactory>()?.CreateLogger<RestSyncProtocol>();

            return new RestSyncProtocol(http, options, log);
        });

        return services;
    }

    /// <summary>
    /// Registers a single, unkeyed connection — the simple case of one plugin, one server.
    /// </summary>
    public static IServiceCollection AddNexusKitSync(
        this IServiceCollection services,
        Action<SyncConnectionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        const string key = "default";
        const string httpClientName = HttpClientPrefix + key;

        services.AddHttpClient(httpClientName, http =>
        {
            var options = Resolve(key, configure);
            http.BaseAddress = options.ServerUrl;
            http.Timeout = options.Timeout;
        });

        services.AddSingleton<ISyncProtocol>(provider =>
        {
            var options = Resolve(key, configure);
            var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(httpClientName);
            var log = provider.GetService<ILoggerFactory>()?.CreateLogger<RestSyncProtocol>();

            return new RestSyncProtocol(http, options, log);
        });

        return services;
    }

    private static SyncConnectionOptions Resolve(string key, Action<SyncConnectionOptions> configure)
    {
        var options = new SyncConnectionOptions();
        configure(options);

        try
        {
            options.Validate();
        }
        catch (InvalidOperationException ex)
        {
            // Name the connection in the message. With several registered, "ServerUrl is
            // required" on its own does not say which one.
            throw new InvalidOperationException($"Sync connection '{key}' is misconfigured: {ex.Message}", ex);
        }

        return options;
    }
}
