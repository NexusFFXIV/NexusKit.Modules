using NexusKit.Modules.InternalData.Refresh;
using NexusKit.Persistence.Settings;

namespace NexusKit.Modules.PlayerEnrichment;

/// <summary>
/// Settings-backed <see cref="IRefreshCategoryPolicy"/>. Loads the persisted
/// per-category enable flags from <see cref="ISettingsStore"/> on construction
/// and exposes the live <see cref="RefreshCategorySettings"/> instance for the
/// settings UI to mutate in place. <see cref="PersistAsync"/> writes the same
/// instance back to the store — consumers reading via <see cref="IsEnabled"/>
/// see the toggled value on their next call without restart.
/// <para>The cache is a single reference assignment from a background load.
/// Per-field bool reads are atomic, so no lock is needed across the worker /
/// UI threads.</para>
/// </summary>
public sealed class RefreshCategoryPolicy : IRefreshCategoryPolicy
{
    /// <summary>Stable storage key — keep stable; renaming orphans the user's
    /// existing toggle state.</summary>
    internal const string StoreKey = "playerenrichment.refresh.categories";

    private readonly ISettingsStore mStore;
    private RefreshCategorySettings mSettings = new();

    public RefreshCategoryPolicy(ISettingsStore store)
    {
        mStore = store;
        _ = LoadAsync();
    }

    /// <summary>Live mutable settings instance. The settings section reads
    /// and writes individual fields here and then calls <see cref="PersistAsync"/>.
    /// Producers should consult <see cref="IsEnabled"/> instead, which folds in
    /// the mandatory-category override.</summary>
    public RefreshCategorySettings Settings => mSettings;

    public bool IsEnabled(RefreshCategory category)
    {
        // Single source of truth for "always on" categories — adding a new
        // mandatory one means updating RefreshCategoryClassification.IsMandatory
        // and nothing else.
        if (RefreshCategoryClassification.IsMandatory(category)) return true;

        return category switch
        {
            RefreshCategory.Profile => mSettings.Profile,
            RefreshCategory.ClassJobs => mSettings.ClassJobs,
            RefreshCategory.Gear => mSettings.Gear,
            RefreshCategory.Mounts => mSettings.Mounts,
            RefreshCategory.Minions => mSettings.Minions,
            RefreshCategory.Achievements => mSettings.Achievements,

            // Unknown future category that isn't mandatory and has no POCO
            // field yet: opt-in by default so the new code's enqueue path
            // isn't silently dropped before its UI exists.
            _ => true,
        };
    }

    public Task PersistAsync() => mStore.SetAsync(StoreKey, mSettings);

    private async Task LoadAsync()
    {
        try
        {
            var loaded = await mStore.GetAsync<RefreshCategorySettings>(StoreKey).ConfigureAwait(false);
            if (loaded is not null) mSettings = loaded;
        }
        catch { /* defaults (all enabled) are a safe fallback */ }
    }
}

/// <summary>Persisted POCO holding the user's per-category enable flags.
/// Only the togglable categories appear here — <see cref="RefreshCategory.LodestoneId"/>
/// and <see cref="RefreshCategory.FreeCompany"/> are mandatory and not user-controllable.
/// Defaults are "all enabled" to match the pre-policy behavior.</summary>
public sealed class RefreshCategorySettings
{
    public bool Profile { get; set; } = true;
    public bool ClassJobs { get; set; } = true;
    public bool Gear { get; set; } = true;
    public bool Mounts { get; set; } = true;
    public bool Minions { get; set; } = true;
    public bool Achievements { get; set; } = true;
}
