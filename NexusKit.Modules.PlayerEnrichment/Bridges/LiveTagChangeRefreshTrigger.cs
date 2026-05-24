using Microsoft.Extensions.Logging;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Modules.InternalData.Refresh;

namespace NexusKit.Modules.PlayerEnrichment.Bridges;

/// <summary>
/// Live FC-tag changes are not eligible history events on their own — a single
/// <c>(tag, world)</c> pair can legitimately belong to multiple distinct FCs,
/// so a tag diff can't pin down which actual FC was joined or left. They are
/// however a strong <i>hint</i> that the player's Lodestone profile is now
/// out of date.
/// <para>This subscriber listens for tag changes coming out of the live
/// observation pipeline and immediately enqueues a high-priority Profile +
/// FreeCompany refresh for the affected character. The refresh worker pulls
/// the fresh Lodestone profile, which lets
/// <see cref="ExternalData.Players.ExternalDataPlayerService"/>'s upsert path
/// emit a precise <c>FreeCompanyChange</c> history row keyed on the unambiguous
/// FC Lodestone id — the strong-match signal — without the user having to
/// wait for the TTL sweep.</para>
/// <para>Suppression mirrors the rules the retired tag-diff history writer
/// used so we don't fire bursts of useless refreshes:</para>
/// <list type="bullet">
///   <item><see cref="PlayerObservationEvent.CanTrackChange"/> must be true —
///   inside duties / cutscenes / zoning the game hides the tag, and a missing
///   tag there is noise, not a real change.</item>
///   <item>Both prev and curr tags must be non-empty AND different — only the
///   <c>tagA → tagB</c> case fires. A <c>tag → null</c> transient is suppressed
///   (Square Enix briefly blanks the tag during transfers and zone bounces);
///   a <c>null → tag</c> first-sighting is suppressed (the profile fetch will
///   land that on its own via the normal stale-sweep path without the
///   priority bump).</item>
/// </list>
/// </summary>
public sealed class LiveTagChangeRefreshTrigger : IDisposable
{
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly IPlayerRefreshQueueService mQueue;
    private readonly ILogger<LiveTagChangeRefreshTrigger> mLog;
    private bool mDisposed;

    public LiveTagChangeRefreshTrigger(
        IInternalDataPlayerWatcher watcher,
        IPlayerRefreshQueueService queue,
        ILogger<LiveTagChangeRefreshTrigger> log)
    {
        mWatcher = watcher;
        mQueue = queue;
        mLog = log;
        mWatcher.ObservationProcessed += OnObservationProcessed;
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mWatcher.ObservationProcessed -= OnObservationProcessed;
    }

    private void OnObservationProcessed(PlayerObservationEvent evt)
    {
        if (!evt.CanTrackChange) return;
        if (evt.Previous is null) return;

        var prevTag = string.IsNullOrEmpty(evt.Previous.CompanyTag) ? null : evt.Previous.CompanyTag;
        var currTag = string.IsNullOrEmpty(evt.Current.CompanyTag) ? null : evt.Current.CompanyTag;
        if (prevTag is null || currTag is null) return;
        if (string.Equals(prevTag, currTag, StringComparison.Ordinal)) return;

        // Profile carries free_company_lodestone_id — that's the upsert that
        // produces the FreeCompanyChange history row via HistoryChangeRecorder.
        // FreeCompany follows so the catalog row for the NEW FC lands in the
        // same refresh wave; the worker enforces ORDER BY priority, category,
        // so Profile finishes before FreeCompany picks the freshly-written
        // link.
        _ = EnqueueAsync(evt.Current.ContentId, RefreshCategory.Profile);
        _ = EnqueueAsync(evt.Current.ContentId, RefreshCategory.FreeCompany);
    }

    private async Task EnqueueAsync(ulong contentId, RefreshCategory category)
    {
        try
        {
            await mQueue.EnqueueAsync(contentId, category, RefreshPriority.Immediate)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex,
                "LiveTagChangeRefreshTrigger: enqueue failed for {ContentId} category={Category}",
                contentId, category);
        }
    }
}
