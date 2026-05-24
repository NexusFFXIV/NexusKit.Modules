using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Core.Cache;
using NexusKit.Modules.Lodestone.Persistence;
using NexusKit.Persistence;

namespace NexusKit.Modules.Lodestone.Cache;

/// <summary>
/// <see cref="IExternalDataCacheResetter"/> implementation for the
/// Lodestone module. Owns the per-character cache-key shapes —
/// callers don't construct keys themselves — and runs one LIKE-filtered
/// <c>ExecuteDeleteAsync</c> per resource family, since SQLite's LIKE
/// can't express an alternation in a single pattern and EF Core can't
/// translate a runtime list of LIKEs into one DELETE.
/// </summary>
internal sealed class LodestoneCacheResetter : IExternalDataCacheResetter
{
    // Six per-character resource families share the same suffix shape
    // ":{lid}". Search keys end in "|world" and FC keys end in fc_lid;
    // neither carries the failing player's id, so they're naturally
    // excluded by the per-family LIKE patterns below.
    private static readonly string[] ResourceFamilies =
    {
        "char", "classjobs", "gear", "mounts", "minions", "achievements",
    };

    private readonly INexusDbContextFactory mDb;
    private readonly ILogger<LodestoneCacheResetter> mLog;

    public LodestoneCacheResetter(INexusDbContextFactory db, ILogger<LodestoneCacheResetter> log)
    {
        mDb = db;
        mLog = log;
    }

    public async Task<int> ResetAsync(ResetContext ctx, CancellationToken ct = default)
    {
        try
        {
            await using var db = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var total = 0;
            // Per-character families — keyed on the Lodestone id.
            foreach (var family in ResourceFamilies)
            {
                var pattern = $"lodestone:%:{family}:{ctx.LodestoneId}";
                total += await db.Set<LodestoneCacheEntity>()
                    .Where(e => EF.Functions.Like(e.Key, pattern))
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }
            // Search family — keyed on (name|world) instead of the id, so
            // the heal can only target it when the caller supplied both.
            // Without this row, a stale empty-Results search pins the
            // LodestoneId-resolution path into an instant fast-fail loop
            // for the same character even after the id-keyed caches are
            // clean.
            if (!string.IsNullOrEmpty(ctx.Name) && !string.IsNullOrEmpty(ctx.HomeWorldName))
            {
                var searchPattern = $"lodestone:%:search:{ctx.Name!.ToLowerInvariant()}|{ctx.HomeWorldName!.ToLowerInvariant()}";
                total += await db.Set<LodestoneCacheEntity>()
                    .Where(e => EF.Functions.Like(e.Key, searchPattern))
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }
            return total;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "Lodestone cache reset failed for lid {Lid}", ctx.LodestoneId);
            return 0;
        }
    }
}
