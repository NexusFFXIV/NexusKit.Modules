using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Modules.InternalData.Persistence;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Persistence;

namespace NexusKit.Modules.InternalData.History;

/// <summary>Persists normalized observation snapshots. Recent rows are kept
/// intact; the database maintenance contributor compacts older rows to one
/// representative per month.</summary>
public sealed class PlayerSnapshotStore : IDisposable
{
    private const int MaxSnapshotsPerPlayer = 2000;
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly INexusDbContextFactory mDb;
    private readonly ILogger<PlayerSnapshotStore> mLog;
    private readonly object mGate = new();
    private readonly Dictionary<ulong, (PlayerSnapshot Previous, PlayerSnapshot Current)> mLatest = new();
    private readonly SemaphoreSlim mPersistGate = new(1, 1);
    private bool mDisposed;

    public PlayerSnapshotStore(IInternalDataPlayerWatcher watcher, INexusDbContextFactory db,
        ILogger<PlayerSnapshotStore> log)
    {
        mWatcher = watcher;
        mDb = db;
        mLog = log;
        mWatcher.ObservationProcessed += OnObservationProcessed;
    }

    /// <summary>Returns the newest normalized snapshots for a character. The
    /// bounded result protects the UI from pathological data sets; maintenance
    /// compacts older rows so normal history remains available.</summary>
    public async Task<IReadOnlyList<PlayerSnapshotRecord>> GetSnapshotsAsync(
        ulong contentId, int limit = MaxSnapshotsPerPlayer, CancellationToken ct = default)
    {
        if (contentId == 0 || limit <= 0) return Array.Empty<PlayerSnapshotRecord>();
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await ctx.Set<InternalPlayerSnapshotEntity>()
                .AsNoTracking()
                .Where(x => x.ContentId == contentId)
                .OrderByDescending(x => x.CapturedAt)
                .ThenByDescending(x => x.Id)
                .Take(Math.Clamp(limit, 1, MaxSnapshotsPerPlayer))
                .ToListAsync(ct).ConfigureAwait(false);

            var result = new List<PlayerSnapshotRecord>(rows.Count);
            foreach (var row in rows)
            {
                var values = NormalizeValues(JsonSerializer.Deserialize<Dictionary<string, string?>>(row.ValuesJson));
                if (values is null) continue;
                result.Add(new(row.Id, new(row.ContentId, row.CapturedAt, values)));
            }

            if (result.Count >= 2)
            {
                lock (mGate)
                    mLatest[contentId] = (result[1].Snapshot, result[0].Snapshot);
            }
            return result;
        }
        catch (OperationCanceledException) { return Array.Empty<PlayerSnapshotRecord>(); }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Player snapshot read failed for {ContentId}", contentId);
            return Array.Empty<PlayerSnapshotRecord>();
        }
    }

    public bool TryGetDiff(ulong contentId, PlayerSnapshotDiffService diffService,
        out PlayerSnapshotDiff? diff)
    {
        lock (mGate)
        {
            if (!mLatest.TryGetValue(contentId, out var pair))
            {
                diff = null;
                return false;
            }
            diff = diffService.Compare(pair.Previous, pair.Current);
            return true;
        }
    }

    private void OnObservationProcessed(PlayerObservationEvent evt)
    {
        var previous = evt.Previous is null
            ? null
            : ToSnapshot(evt.Previous, evt.Previous.LastSeen);
        var current = ToSnapshot(evt.Current, evt.ObservedAt);
        if (previous is not null)
            lock (mGate) mLatest[evt.Current.ContentId] = (previous, current);
        _ = PersistAsync(current);
    }

    private async Task PersistAsync(PlayerSnapshot current)
    {
        if (mDisposed || mDb.IsStopping) return;
        try
        {
            await mPersistGate.WaitAsync(mDb.LifetimeToken).ConfigureAwait(false);
            try
            {
                await using var ctx = await mDb.CreateDbContextAsync(mDb.LifetimeToken)
                    .ConfigureAwait(false);
                var valuesJson = SerializeValues(NormalizeValues(current.Values));
                var latest = await ctx.Set<InternalPlayerSnapshotEntity>()
                    .Where(x => x.ContentId == current.ContentId)
                    .OrderByDescending(x => x.CapturedAt)
                    .ThenByDescending(x => x.Id)
                    .FirstOrDefaultAsync(mDb.LifetimeToken).ConfigureAwait(false);
                if (latest is not null && string.Equals(latest.ValuesJson, valuesJson,
                        StringComparison.Ordinal)) return;

                ctx.Set<InternalPlayerSnapshotEntity>().Add(new()
                {
                    ContentId = current.ContentId,
                    CapturedAt = current.CapturedAt.ToUniversalTime(),
                    ValuesJson = valuesJson,
                });
                await ctx.SaveChangesAsync(mDb.LifetimeToken).ConfigureAwait(false);

            }
            finally { mPersistGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Player snapshot write failed for {ContentId}", current.ContentId);
        }
    }

    private static string SerializeValues(IReadOnlyDictionary<string, string?> values)
        => JsonSerializer.Serialize(values.OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));

    private static PlayerSnapshot ToSnapshot(ObservedPlayer player, DateTime capturedAt)
        => new(player.ContentId, capturedAt, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["name"] = player.Name,
            ["home_world_id"] = player.HomeWorldId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["active_class_job"] = $"{player.ClassJobId.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{player.Level.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            ["race"] = player.Race.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["gender"] = player.Gender.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["company_tag"] = player.CompanyTag,
        });

    private static Dictionary<string, string?> NormalizeValues(IReadOnlyDictionary<string, string?>? values)
    {
        var normalized = values is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(values, StringComparer.Ordinal);
        if (!normalized.ContainsKey("active_class_job")
            && normalized.TryGetValue("class_job_id", out var job)
            && normalized.TryGetValue("level", out var level))
        {
            normalized["active_class_job"] = $"{job}:{level}";
            normalized.Remove("class_job_id");
            normalized.Remove("level");
        }
        return normalized;
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mWatcher.ObservationProcessed -= OnObservationProcessed;
        mPersistGate.Dispose();
    }
}
