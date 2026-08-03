using Microsoft.Extensions.Logging;
using NexusKit.GameData.ObjectTables;
using NexusKit.Modules.InternalData.History;

namespace NexusKit.Modules.InternalData.Players;

/// <summary>
/// Bridges the Examine-time search comment into the observation store and the
/// change history. Subscribes to <see cref="IInspectSearchCommentWatcher"/>,
/// persists what came in, and logs a <see cref="PlayerHistoryKind.SearchCommentChange"/>
/// row when the value actually moved.
/// <para>The diff lives here rather than in
/// <c>InternalDataHistoryService.OnObservationProcessed</c> because the search
/// comment never travels through the observation pipeline — the prev/current
/// pair in <see cref="PlayerObservationEvent"/> simply does not carry it. The
/// write already reads the previous value, so it hands it back and this class
/// records the change without a second query.</para>
/// <para>Same shape as <c>LiveTagChangeRefreshTrigger</c>: a thin subscriber
/// that owns one cross-cutting reaction and nothing else.</para>
/// </summary>
public sealed class SearchCommentCaptureService : IDisposable
{
    private readonly IInspectSearchCommentWatcher mSource;
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly IInternalDataHistoryService mHistory;
    private readonly ILogger<SearchCommentCaptureService> mLog;
    private bool mDisposed;

    public SearchCommentCaptureService(
        IInspectSearchCommentWatcher source,
        IInternalDataPlayerWatcher watcher,
        IInternalDataHistoryService history,
        ILogger<SearchCommentCaptureService> log)
    {
        mSource = source;
        mWatcher = watcher;
        mHistory = history;
        mLog = log;

        mSource.SearchCommentReceived += OnSearchCommentReceived;
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mSource.SearchCommentReceived -= OnSearchCommentReceived;
    }

    private void OnSearchCommentReceived(ulong contentId, string? comment)
    {
        // Fires on the framework thread — get off it before touching the DB.
        _ = Task.Run(() => CaptureAsync(contentId, comment));
    }

    private async Task CaptureAsync(ulong contentId, string? comment)
    {
        try
        {
            var result = await mWatcher.SetSearchCommentAsync(contentId, comment).ConfigureAwait(false);
            if (!result.Applied || !result.ValueChanged) return;

            // A first capture on a character we have never examined before is
            // indistinguishable from them having just written the comment, so it
            // is recorded as "set" either way. That is the honest reading: all we
            // can say is that this is the first value we know of.
            await mHistory.InsertIfNewAsync(
                contentId,
                PlayerHistoryKind.SearchCommentChange,
                DateTime.UtcNow,
                result.Previous,
                result.Current).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "InternalData: search-comment capture failed for ContentId {Cid}", contentId);
        }
    }
}
