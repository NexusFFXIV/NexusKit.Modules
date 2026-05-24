using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.GameData;
using NexusKit.Modules.InternalData.Persistence;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Persistence;

namespace NexusKit.Modules.InternalData.History;

internal sealed class InternalDataHistoryService : IInternalDataHistoryService, IDisposable
{
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly IGameDataLookups mLookups;
    private readonly INexusDbContextFactory mDb;
    private readonly ILogger<InternalDataHistoryService> mLog;
    private bool mDisposed;

    public event Action<ulong, IReadOnlyList<PlayerHistoryEntry>>? HistoryAdded;
    public event Action<ulong>? HistoryRead;
    public event Action? AllHistoryRead;

    public InternalDataHistoryService(
        IInternalDataPlayerWatcher watcher,
        IGameDataLookups lookups,
        INexusDbContextFactory db,
        ILogger<InternalDataHistoryService> log)
    {
        mWatcher = watcher;
        mLookups = lookups;
        mDb = db;
        mLog = log;

        mWatcher.ObservationProcessed += OnObservationProcessed;
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mWatcher.ObservationProcessed -= OnObservationProcessed;
    }

    public async Task<IReadOnlyList<PlayerHistoryEntry>> GetForContentIdAsync(
        ulong contentId, int limit = 200, CancellationToken ct = default)
    {
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await ctx.Set<InternalPlayerHistoryEntity>()
                .Where(r => r.ContentId == contentId)
                .OrderByDescending(r => r.ChangedAt)
                .Take(limit)
                .AsNoTracking()
                .ToListAsync(ct).ConfigureAwait(false);

            return rows
                .Select(r => new PlayerHistoryEntry(
                    Id: r.Id,
                    ContentId: r.ContentId,
                    Kind: r.Kind,
                    ChangedAt: r.ChangedAt,
                    OldValue: r.OldValue,
                    NewValue: r.NewValue,
                    IsRead: r.IsRead))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "History read failed for {ContentId}", contentId);
            return Array.Empty<PlayerHistoryEntry>();
        }
    }

    public async Task<IReadOnlyDictionary<ulong, IReadOnlySet<PlayerHistoryKind>>> GetUnreadHistoryKindsByContentIdAsync(
        CancellationToken ct = default)
    {
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            // Projection to a primitive pair disables tracking implicitly — no
            // need for .AsNoTracking(), which would fail to compile here.
            var pairs = await ctx.Set<InternalPlayerHistoryEntity>()
                .Where(r => !r.IsRead)
                .Select(r => new { r.ContentId, r.Kind })
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);

            var byId = new Dictionary<ulong, IReadOnlySet<PlayerHistoryKind>>();
            foreach (var p in pairs)
            {
                if (!byId.TryGetValue(p.ContentId, out var existing))
                {
                    existing = new HashSet<PlayerHistoryKind> { p.Kind };
                    byId[p.ContentId] = existing;
                }
                else
                {
                    ((HashSet<PlayerHistoryKind>)existing).Add(p.Kind);
                }
            }
            return byId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Unread history kind-index read failed.");
            return new Dictionary<ulong, IReadOnlySet<PlayerHistoryKind>>();
        }
    }

    public async Task<PlayerHistoryEntry?> InsertIfNewAsync(
        ulong contentId,
        PlayerHistoryKind kind,
        DateTime changedAt,
        string? oldValue,
        string? newValue,
        CancellationToken ct = default)
    {
        if (mDb.IsStopping) return null;
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);

            // Dedup: skip if the most recent row of the same kind already
            // matches NewValue. The two-pipeline split (live watcher +
            // Lodestone-refresh) can otherwise observe the same effective
            // change at different times — the Lodestone path would log a
            // second row carrying the same target value once external_player
            // catches up to what the watcher already saw live.
            var latest = await ctx.Set<InternalPlayerHistoryEntity>()
                .Where(r => r.ContentId == contentId && r.Kind == kind)
                .OrderByDescending(r => r.ChangedAt)
                .Select(r => r.NewValue)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (latest is not null && string.Equals(
                    NormaliseForCompare(latest),
                    NormaliseForCompare(newValue), StringComparison.Ordinal))
                return null;

            var entity = Build(contentId, kind, changedAt, oldValue, newValue);
            ctx.Set<InternalPlayerHistoryEntity>().Add(entity);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

            var entry = new PlayerHistoryEntry(
                Id: entity.Id,
                ContentId: entity.ContentId,
                Kind: entity.Kind,
                ChangedAt: entity.ChangedAt,
                OldValue: entity.OldValue,
                NewValue: entity.NewValue,
                IsRead: entity.IsRead);
            HistoryAdded?.Invoke(contentId, new[] { entry });
            return entry;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "InsertIfNewAsync failed for {ContentId} kind={Kind}", contentId, kind);
            return null;
        }
    }

    /// <summary>Treats null and empty as the same so a stale Lodestone-cache
    /// read of "" doesn't get logged as a fresh "—" → X transition right after
    /// the live watcher already logged the real one.</summary>
    private static string NormaliseForCompare(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value;

    public async Task MarkAllReadForContentIdAsync(ulong contentId, CancellationToken ct = default)
    {
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            // ExecuteUpdateAsync issues a single UPDATE — no entity tracking,
            // no row materialization. Returns the affected row count; only fire
            // the event when something actually changed (avoids spurious UI
            // refreshes when the tab is re-opened with nothing unread).
            var affected = await ctx.Set<InternalPlayerHistoryEntity>()
                .Where(r => r.ContentId == contentId && !r.IsRead)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRead, true), ct)
                .ConfigureAwait(false);
            if (affected > 0) HistoryRead?.Invoke(contentId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Mark-all-read failed for {ContentId}", contentId);
        }
    }

    public async Task MarkAllReadAsync(CancellationToken ct = default)
    {
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var affected = await ctx.Set<InternalPlayerHistoryEntity>()
                .Where(r => !r.IsRead)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRead, true), ct)
                .ConfigureAwait(false);
            if (affected > 0) AllHistoryRead?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Mark-all-read (global) failed.");
        }
    }

    private void OnObservationProcessed(PlayerObservationEvent evt)
    {
        // First observation has nothing to diff against — no history rows.
        if (evt.Previous is null) return;

        var rows = new List<InternalPlayerHistoryEntity>();
        var prev = evt.Previous;
        var curr = evt.Current;

        if (!string.Equals(prev.Name, curr.Name, StringComparison.Ordinal))
            rows.Add(Build(curr.ContentId, PlayerHistoryKind.NameChange, evt.ObservedAt,
                prev.Name, curr.Name));

        if (!string.Equals(prev.HomeWorld, curr.HomeWorld, StringComparison.Ordinal))
            rows.Add(Build(curr.ContentId, PlayerHistoryKind.HomeWorldChange, evt.ObservedAt,
                prev.HomeWorld, curr.HomeWorld));

        // Customize: only the race (byte 0) / gender (byte 1) pair matters. Hair, face,
        // eye color etc. flip too often to be worth logging — they'd flood the timeline.
        if (prev.Race != curr.Race || prev.Gender != curr.Gender)
            rows.Add(Build(curr.ContentId, PlayerHistoryKind.CustomizeChange, evt.ObservedAt,
                FormatRaceGender(prev.Race, prev.Gender),
                FormatRaceGender(curr.Race, curr.Gender)));

        // FC change detection lives exclusively in the Lodestone-refresh path
        // (ExternalDataPlayerService.UpsertProfileAsync → HistoryChangeRecorder),
        // keyed on the profile's free_company_lodestone_id. The live object-table
        // tag is intentionally not used: a single (tag, world) pair can belong
        // to multiple distinct FCs, so a tag-only diff can't distinguish a real
        // swap from "different FC, same tag" — and it produced phantom rows on
        // every duty/cutscene/zoning bounce where the tag string hides and
        // rehydrates.

        if (rows.Count == 0) return;

        _ = Task.Run(() => PersistAsync(rows));
    }

    private async Task PersistAsync(IReadOnlyList<InternalPlayerHistoryEntity> rows)
    {
        if (mDb.IsStopping) return;
        var ct = mDb.LifetimeToken;
        try
        {
            await using var ctx = await mDb.CreateDbContextAsync().ConfigureAwait(false);

            // Dedup against the latest existing row per (contentId, kind) so a
            // live diff doesn't duplicate a change that the Lodestone-refresh
            // path already logged a few seconds earlier (and vice versa). The
            // batch comes from one OnObservationProcessed call, so all rows
            // share the same ContentId by construction.
            var contentId = rows[0].ContentId;
            var kinds = rows.Select(r => r.Kind).ToHashSet();
            var latestByKind = await ctx.Set<InternalPlayerHistoryEntity>()
                .Where(r => r.ContentId == contentId && kinds.Contains(r.Kind))
                .GroupBy(r => r.Kind)
                .Select(g => new
                {
                    g.Key,
                    NewValue = g.OrderByDescending(r => r.ChangedAt)
                                .Select(r => r.NewValue)
                                .FirstOrDefault(),
                })
                .ToDictionaryAsync(x => x.Key, x => x.NewValue, ct)
                .ConfigureAwait(false);

            var toInsert = rows.Where(r =>
                !latestByKind.TryGetValue(r.Kind, out var prevNew)
                || !string.Equals(NormaliseForCompare(prevNew),
                                  NormaliseForCompare(r.NewValue), StringComparison.Ordinal))
                .ToList();

            if (toInsert.Count == 0) return;

            ctx.Set<InternalPlayerHistoryEntity>().AddRange(toInsert);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            rows = toInsert;
            // All rows in a single PersistAsync call share the same ContentId by
            // construction (one OnObservationProcessed → at most one batch). Map
            // the entities to the public record so subscribers don't see the
            // persistence type — and so chat / UI consumers have direct access
            // to old/new values without re-querying.
            var entries = rows.Select(r => new PlayerHistoryEntry(
                Id: r.Id,
                ContentId: r.ContentId,
                Kind: r.Kind,
                ChangedAt: r.ChangedAt,
                OldValue: r.OldValue,
                NewValue: r.NewValue,
                IsRead: r.IsRead)).ToArray();
            HistoryAdded?.Invoke(rows[0].ContentId, entries);
        }
        catch (OperationCanceledException) { /* plugin shutdown */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "History write failed ({Count} rows)", rows.Count);
        }
    }

    private static InternalPlayerHistoryEntity Build(
        ulong contentId, PlayerHistoryKind kind, DateTime at, string? oldValue, string? newValue)
        => new()
        {
            ContentId = contentId,
            Kind = kind,
            ChangedAt = at,
            OldValue = oldValue,
            NewValue = newValue,
        };

    private string FormatRaceGender(byte race, byte gender)
    {
        // Race id 0 is the slim-projection sentinel for "we never saw a
        // Customize snapshot for this row" (e.g. legacy rows hydrated before
        // appearance tracking landed). Don't try to surface a name in that
        // case — it would read as "Hyur · Male" by Lumina default and
        // misrepresent the actual unknown state.
        if (race == 0) return "—";
        var feminine = gender == 1;
        var raceName = mLookups.GetRaceName(race, feminine) ?? $"#{race}";
        var genderLabel = feminine ? "Female" : "Male";
        return $"{raceName} · {genderLabel}";
    }
}
