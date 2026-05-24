namespace NexusKit.Modules.PlayerEnrichment;

/// <summary>
/// Supplies the freshness TTL used by <see cref="IPlayerRefreshQueueService"/>.
/// Defined as a service so the plugin layer can read it from its own settings
/// type without the module needing to reference plugin-specific types. The
/// provider is consulted on every stale check, so caching reads or watching
/// for setting changes is up to the impl.
/// </summary>
public interface IRefreshTtlProvider
{
    TimeSpan GetTtl();
}

/// <summary>Default fallback used when no plugin-specific provider is registered.
/// Returns 7 days — matches the documented default and the old plugin's sweep
/// freshness window.</summary>
public sealed class DefaultRefreshTtlProvider : IRefreshTtlProvider
{
    public TimeSpan GetTtl() => TimeSpan.FromDays(7);
}
