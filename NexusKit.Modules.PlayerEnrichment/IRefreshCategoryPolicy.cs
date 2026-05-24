using NexusKit.Modules.InternalData.Refresh;

namespace NexusKit.Modules.PlayerEnrichment;

/// <summary>
/// User-facing on/off gate for the refresh queue's per-category fetch work.
/// Consulted by <see cref="IPlayerRefreshQueueService"/> at every enqueue,
/// stale-check, and worker-pick boundary so a disabled category is never
/// fetched — not even via the explicit "Refresh" path. Re-enabling a category
/// is a passive operation: the next stale check (next session, next detail
/// selection, or next LodestoneId cascade) sees the row as missing/aged and
/// queues a catch-up fetch.
/// <para><see cref="RefreshCategory.LodestoneId"/> and
/// <see cref="RefreshCategory.FreeCompany"/> are mandatory and always report
/// enabled — the worker needs the id for every sub-resource, and the FC link
/// powers the FreeCompanyChange history pipeline that is too load-bearing to
/// switch off.</para>
/// </summary>
public interface IRefreshCategoryPolicy
{
    /// <summary>True when the category is allowed to be queued and fetched.
    /// Always true for mandatory categories regardless of stored settings.</summary>
    bool IsEnabled(RefreshCategory category);
}

/// <summary>Fallback policy used when no settings-backed implementation has
/// been registered — every category is enabled. Matches the historical
/// behavior of plugins that don't expose the toggles in their settings UI.</summary>
public sealed class DefaultRefreshCategoryPolicy : IRefreshCategoryPolicy
{
    public bool IsEnabled(RefreshCategory category) => true;
}
