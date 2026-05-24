using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Modules.ExternalData.Players;
using NexusKit.Modules.InternalData.History;
using NexusKit.Modules.InternalData.Persistence;
using NexusKit.Persistence;

namespace NexusKit.Modules.PlayerEnrichment.Bridges;

/// <summary>
/// Bridges Lodestone-detected player changes (emitted from
/// <see cref="ExternalData.Players.ExternalDataPlayerService"/>'s upsert path)
/// into the InternalData history log. Lives here in PlayerEnrichment because
/// it's the only assembly that's allowed to reference both module bundles —
/// InternalData and ExternalData themselves stay decoupled, talking only via
/// the <see cref="IPlayerChangeRecorder"/> seam defined in ExternalData.
/// <para>The history service applies its own dedup (last-row-per-kind comparison
/// of <c>NewValue</c>), so this recorder writing the same effective change a
/// few seconds after the live watcher already logged it is harmless — the
/// second insert is silently dropped.</para>
/// </summary>
internal sealed class HistoryChangeRecorder : IPlayerChangeRecorder
{
    private readonly INexusDbContextFactory mDb;
    private readonly IInternalDataHistoryService mHistory;
    private readonly ILogger<HistoryChangeRecorder> mLog;

    public HistoryChangeRecorder(
        INexusDbContextFactory db,
        IInternalDataHistoryService history,
        ILogger<HistoryChangeRecorder> log)
    {
        mDb = db;
        mHistory = history;
        mLog = log;
    }

    public async Task RecordPlayerChangeAsync(
        ulong lodestoneId,
        PlayerChangeKind kind,
        string? oldValue,
        string? newValue,
        DateTime detectedAt,
        CancellationToken ct = default)
    {
        var historyKind = kind switch
        {
            PlayerChangeKind.Name        => PlayerHistoryKind.NameChange,
            PlayerChangeKind.HomeWorld   => PlayerHistoryKind.HomeWorldChange,
            PlayerChangeKind.FreeCompany => PlayerHistoryKind.FreeCompanyChange,
            _ => (PlayerHistoryKind?)null,
        };
        if (historyKind is null) return;

        try
        {
            // ContentId is the history log's primary axis. We resolve it from
            // observed_player by lodestone_id. If we've never seen the player
            // live (Lodestone-only enrichment, e.g. typed Lodestone id), skip:
            // history is per-observed-character.
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var contentId = await ctx.Set<InternalObservedPlayerEntity>()
                .Where(p => p.LodestoneId == lodestoneId)
                .Select(p => (ulong?)p.ContentId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (contentId is null or 0UL) return;

            await mHistory.InsertIfNewAsync(
                contentId.Value, historyKind.Value, detectedAt, oldValue, newValue, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex,
                "HistoryChangeRecorder: failed to record {Kind} change for lodestoneId={Lid}",
                kind, lodestoneId);
        }
    }
}
